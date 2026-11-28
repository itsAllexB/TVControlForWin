// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Sockets;
using TVControlForWin.Models;

namespace TVControlForWin.Services;

/// <summary>
/// Brightness for one Android TV over ADB: the one-time setup, the connection, and debounced
/// writes. Owned by <see cref="AndroidTvController"/>, which tells it when the TV comes and goes.
///
/// ADB is a separate channel from the remote-control connection and, unlike it, has to be
/// switched on by the owner (USB debugging) and approved on the TV. So nothing here ever
/// contacts ADB with an unapproved key unless the user asked for setup — that's what would put
/// an "Allow USB debugging?" dialog on a TV nobody may be sitting at.
/// </summary>
internal sealed class AdbBrightness : IDisposable
{
    private const int DebounceMs = 150;
    private const int CommandTimeoutMs = 3000;
    private const int ResumeAttempts = 3;
    private const int ResumeRetryDelayMs = 3000;
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromSeconds(90);

    private readonly TvDevice _device;
    private readonly Action _deviceChanged;
    private readonly Func<bool> _tvIsOn;
    private readonly CancellationTokenSource _lifetime = new();

    // One ADB conversation at a time: the client runs a single command stream.
    private readonly SemaphoreSlim _adbLock = new(1, 1);
    private readonly object _gate = new();

    private const int MaxRefusals = 3;       // refused writes in a row before giving up on this TV
    private const int RefusalRetryMs = 2000;

    private AdbClient? _adb;
    private bool _verified; // a write on this connection has been read back
    private int _refusals;  // writes in a row the TV has turned down
    private bool _resumeFailed; // the last quiet reconnect couldn't reach ADB
    private int _resumeGen;     // goes up with every resume and every Suspend; an attempt holding an older number is void
    private string? _offeredKey; // made for setup this session, not approved (yet)
    private Timer? _debounce;
    private int _pendingValue;
    private bool _hasPending;

    // A value that couldn't be delivered because the TV was off or unreachable (typically a
    // schedule firing overnight). Pushed as soon as the TV is back.
    private int? _missedWhileOff;

    /// <param name="deviceChanged">Called after something persisted on the device changed.</param>
    /// <param name="tvIsOn">Whether the TV is connected and switched on right now.</param>
    public AdbBrightness(TvDevice device, Action deviceChanged, Func<bool> tvIsOn)
    {
        _device = device;
        _deviceChanged = deviceChanged;
        _tvIsOn = tvIsOn;
    }

    public BrightnessSetup State { get; private set; } = BrightnessSetup.Unknown;

    /// <summary>
    /// True when a value passed to <see cref="Set"/> won't be lost even though brightness isn't
    /// <see cref="BrightnessSetup.Ready"/>: the TV has approved us before, so the value is kept
    /// and delivered once ADB is back. That's how a schedule that fires while the TV is
    /// unreachable still lands.
    /// </summary>
    public bool Holds => !string.IsNullOrEmpty(_device.AdbKey) && State != BrightnessSetup.Unsupported;

    /// <summary>Fires whenever <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Fires with the TV's current brightness when it's read.</summary>
    public event Action<int>? Reported;

    private void SetState(BrightnessSetup state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke();
    }

    private string KeyName => "TVControlForWin@" + Environment.MachineName;

    // ----- Lifecycle, driven by the controller -----

    /// <summary>
    /// The TV just connected. With a key it has already approved, reconnect quietly and read the
    /// brightness; without one, offer setup if <paramref name="mightSupport"/> says it's worth it,
    /// and otherwise settle on <see cref="BrightnessSetup.Unavailable"/>.
    /// </summary>
    public async Task ResumeAsync(bool mightSupport)
    {
        var token = _lifetime.Token;

        // This attempt can outlive its purpose: it retries for several seconds, and meanwhile the
        // TV may go away (Suspend) or connect again (a newer resume). Finishing late it would
        // report a failure nobody is waiting for and put "Set up" back on a TV that works. So
        // every step below first checks it is still the latest.
        var gen = Interlocked.Increment(ref _resumeGen);
        bool Superseded() => gen != Volatile.Read(ref _resumeGen);

        if (string.IsNullOrEmpty(_device.AdbKey))
        {
            if (State is BrightnessSetup.Unknown or BrightnessSetup.Unavailable)
                SetState(mightSupport ? BrightnessSetup.Offered : BrightnessSetup.Unavailable);
            return;
        }
        lock (_gate)
        {
            if (Superseded()) return;
            _resumeFailed = false;
        }

        // A TV that's just woken may take a few seconds to bring ADB back up.
        for (var attempt = 0; attempt < ResumeAttempts && !token.IsCancellationRequested; attempt++)
        {
            try
            {
                await _adbLock.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (Superseded()) return; // before ConnectAsync closes a connection that isn't ours
                    try
                    {
                        await ConnectAsync(_device.AdbKey, onApprovalNeeded: null, token).ConfigureAwait(false);
                        if (!Superseded()) await ProbeAsync(token, Superseded).ConfigureAwait(false);
                    }
                    finally
                    {
                        // Suspend may have run while we were connecting, before there was anything
                        // for it to close. Under the lock the open connection can only be this
                        // attempt's own, so this is the place to make sure it doesn't stay behind.
                        if (Superseded()) CloseConnection();
                    }
                }
                finally { _adbLock.Release(); }

                if (Superseded()) return;
                ApplyMissedIfAny();
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception) when (Superseded())
            {
                // Not a failure: Suspend closing the connection under us ends up here as well.
                return;
            }
            catch (AdbException ex) when (ex.NeedsApproval)
            {
                // The TV forgot us (debugging authorisations revoked, or a factory reset).
                Log.Write($"{_device.Host}: ADB key no longer approved — brightness needs setting up again");
                CloseConnection();
                SetState(BrightnessSetup.Offered);
                return;
            }
            catch (Exception ex)
            {
                CloseConnection();
                if (attempt == ResumeAttempts - 1)
                {
                    // Checked together with the write, so a resume that has started since
                    // (and cleared the flag) can't have it set again behind its back.
                    lock (_gate)
                    {
                        if (Superseded()) return;
                        _resumeFailed = true;
                    }

                    // Most likely USB debugging was switched off. Keep the key: turning it
                    // back on and running setup again won't need a new approval. In case ADB
                    // was only slow to come up, the next power-on report tries once more.
                    Log.Write($"{_device.Host}: ADB not reachable ({ex.Message}) — brightness unavailable");
                    SetState(BrightnessSetup.Offered);
                    return;
                }
            }

            try { await Task.Delay(ResumeRetryDelayMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (Superseded()) return;
        }
    }

    /// <summary>
    /// The user asked for brightness: connect, getting the TV's approval if it doesn't know us.
    /// Every outcome lands in <see cref="State"/>.
    /// </summary>
    public async Task SetUpAsync()
    {
        var token = _lifetime.Token;
        if (State is BrightnessSetup.Connecting or BrightnessSetup.AwaitingApproval or BrightnessSetup.Ready) return;

        // Reuse a saved key (the TV may still trust it); otherwise the one already offered this
        // session, or a new one. A key is saved only on success, but one the TV was shown is kept
        // in memory: "Allow" pressed after the wait ran out still approves that key, and the next
        // attempt then gets in without a second dialog.
        string key;
        if (!string.IsNullOrEmpty(_device.AdbKey))
            key = _device.AdbKey;
        else
            lock (_gate) key = _offeredKey ??= AdbClient.CreateKey();
        // A quiet resume still retrying must not finish after this and overwrite its result.
        Interlocked.Increment(ref _resumeGen);
        SetState(BrightnessSetup.Connecting);
        try
        {
            await _adbLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await ConnectAsync(key, () => SetState(BrightnessSetup.AwaitingApproval), token).ConfigureAwait(false);
                if (_device.AdbKey != key)
                {
                    _device.AdbKey = key;
                    _deviceChanged();
                }
                await ProbeAsync(token).ConfigureAwait(false);
            }
            finally { _adbLock.Release(); }

            if (State == BrightnessSetup.Ready)
            {
                Log.Write($"{_device.Host}: brightness over ADB is set up");
                _resumeFailed = false;
                ApplyMissedIfAny();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AdbException ex)
        {
            Log.Write($"{_device.Host}: brightness setup failed: {ex.Message}");
            CloseConnection();
            SetState(BrightnessSetup.Unsupported);
        }
        catch (Exception ex)
        {
            // Nobody answered the dialog → back to "offer it". Anything else at this point is
            // the connection itself failing: ADB isn't listening, i.e. debugging is off.
            var unanswered = State == BrightnessSetup.AwaitingApproval;
            Log.Write($"{_device.Host}: brightness setup failed: {(unanswered ? "no answer on the TV" : ex.Message)}");
            CloseConnection();
            SetState(unanswered ? BrightnessSetup.Offered : BrightnessSetup.NeedsDebugging);
        }
    }

    /// <summary>The TV went away (connection lost or standby): the ADB link is dead weight now.</summary>
    public void Suspend()
    {
        // First, so a resume that sees its connection go from under it already knows why.
        Interlocked.Increment(ref _resumeGen);
        CloseConnection();
    }

    /// <summary>The TV reported its power state; coming on is when a held value is delivered.</summary>
    public void TvPowerChanged(bool on)
    {
        if (!on) CloseConnection();
        else if (_resumeFailed) _ = ResumeAsync(mightSupport: true); // delivers a held value itself
        else ApplyMissedIfAny();
    }

    // ----- Writing -----

    /// <summary>Queues a brightness change; only the latest value within the debounce window is sent.</summary>
    public void Set(int value)
    {
        value = Math.Clamp(value, 0, 100);
        lock (_gate)
        {
            _pendingValue = value;
            _hasPending = true;
            _debounce ??= new Timer(_ => _ = FlushAsync());
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private async Task FlushAsync()
    {
        int value;
        lock (_gate)
        {
            if (!_hasPending) return;
            value = _pendingValue;
            _hasPending = false;
        }
        if (State != BrightnessSetup.Ready)
        {
            // Set up, but not usable at the moment (typically the TV hasn't been reachable since
            // the app started): keep the value for when it is.
            if (Holds) lock (_gate) _missedWhileOff = value;
            return;
        }

        var token = _lifetime.Token;
        if (!_tvIsOn())
        {
            lock (_gate) _missedWhileOff = value;
            Log.Write($"{_device.Host}: TV is off — holding brightness {value} until it's back");
            return;
        }

        try
        {
            await _adbLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // An idle ADB connection can die without a trace; one reconnect covers that.
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (_adb is not { IsConnected: true })
                            await ConnectAsync(_device.AdbKey, onApprovalNeeded: null, token).ConfigureAwait(false);

                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(CommandTimeoutMs);
                        switch (await AdbTclBacklight.SetAsync(_adb!, value, verify: !_verified, timeout.Token).ConfigureAwait(false))
                        {
                            case AdbTclBacklight.SetResult.Applied:
                                _verified = true;
                                _refusals = 0;
                                lock (_gate) _missedWhileOff = null;
                                break;

                            case AdbTclBacklight.SetResult.Refused when ++_refusals < MaxRefusals:
                                // Seen as a passing thing while the TV is still waking: keep the
                                // value and try again shortly, unless a newer one has come in.
                                Log.Write($"{_device.Host}: brightness {value} refused by the TV — trying again shortly");
                                lock (_gate)
                                {
                                    if (!_hasPending)
                                    {
                                        _pendingValue = value;
                                        _hasPending = true;
                                        _debounce?.Change(RefusalRetryMs, Timeout.Infinite);
                                    }
                                }
                                break;

                            default:
                                // Accepted but not applied, or refused time after time: these
                                // aren't the calls we think they are on this firmware. Stop writing.
                                Log.Write($"{_device.Host}: brightness writes don't take — turning brightness off for this TV");
                                _refusals = 0;
                                CloseConnection();
                                SetState(BrightnessSetup.Unsupported);
                                break;
                        }
                        return;
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested && ex is not AdbException && attempt == 0)
                    {
                        CloseConnection();
                    }
                }
            }
            finally { _adbLock.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (AdbException ex) when (ex.NeedsApproval)
        {
            Log.Write($"{_device.Host}: ADB key no longer approved — brightness needs setting up again");
            CloseConnection();
            SetState(BrightnessSetup.Offered);
        }
        catch (Exception ex)
        {
            CloseConnection();
            lock (_gate) _missedWhileOff = value;
            Log.Write($"{_device.Host}: brightness {value} FAILED ({ex.Message}) — holding until the TV is back");
        }
    }

    private void ApplyMissedIfAny()
    {
        if (State != BrightnessSetup.Ready) return; // keep holding it until brightness works again

        int? missed;
        lock (_gate)
        {
            missed = _missedWhileOff;
            _missedWhileOff = null;
        }
        if (missed is not int value) return;

        Log.Write($"{_device.Host}: TV is back — applying held brightness {value}");
        Reported?.Invoke(value); // sync the slider
        Set(value);
    }

    // ----- Connection (callers hold _adbLock) -----

    private async Task ConnectAsync(string key, Action? onApprovalNeeded, CancellationToken token)
    {
        CloseConnection();
        var adb = new AdbClient();
        try
        {
            await adb.ConnectAsync(_device.Host, AdbClient.DefaultPort, key, KeyName, onApprovalNeeded,
                onApprovalNeeded is null ? TimeSpan.Zero : ApprovalTimeout, token).ConfigureAwait(false);
        }
        catch
        {
            adb.Dispose();
            throw;
        }
        _adb = adb;
        _verified = false;
    }

    // Checks this TV has a brightness control we know, and reads its current value.
    // The optional check lets a quiet resume stop short of reporting anything once it is no
    // longer the latest; the caller then closes the connection.
    private async Task ProbeAsync(CancellationToken token, Func<bool>? superseded = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CommandTimeoutMs);
        var available = await AdbTclBacklight.IsAvailableAsync(_adb!, timeout.Token).ConfigureAwait(false);
        if (superseded?.Invoke() == true) return;
        if (!available)
        {
            CloseConnection();
            SetState(BrightnessSetup.Unsupported);
            return;
        }

        // Report what the TV is at before announcing Ready: becoming Ready is what triggers the
        // schedule catch-up, and its value has to be the one that sticks, not this reading.
        var value = await AdbTclBacklight.GetAsync(_adb!, timeout.Token).ConfigureAwait(false);
        if (superseded?.Invoke() == true) return;
        if (value is int current) Reported?.Invoke(current);
        SetState(BrightnessSetup.Ready);
    }

    private void CloseConnection()
    {
        var adb = Interlocked.Exchange(ref _adb, null);
        adb?.Dispose();
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        CloseConnection();
        _lifetime.Dispose();
    }
}
