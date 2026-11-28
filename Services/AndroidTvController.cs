// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TVControlForWin.Models;

namespace TVControlForWin.Services;

/// <summary>
/// Controller for one Android TV / Google TV (TCL, Sony, Philips, Hisense…): pairs with a code,
/// keeps the remote-control connection alive, and turns the TV on and off. The protocol carries
/// key presses and power state but no picture settings, so there is no brightness here.
/// </summary>
public sealed class AndroidTvController : ITvController
{
    /// <summary>How many times, and how far apart, PowerOnAsync retries waking an unreachable TV.</summary>
    private const int WakeAttempts = 4;
    private const int WakeRetryDelayMs = 3000;

    /// <summary>How long the TV gets to report its new power state after a power key press.</summary>
    private const int PowerSettleMs = 8000;

    /// <summary>How long the first power report may trail the handshake.</summary>
    private const int PowerReportMs = 2000;

    private const int PairingStartTimeoutMs = 8000;
    private const int CodeEntryTimeoutMs = 180_000;

    /// <summary>The TV must refuse our certificate this many times running before we believe it
    /// (attempts that couldn't reach the TV in between start the count again).</summary>
    private const int RejectionsBeforeUnpairing = 2;

    // Windows' names for the TLS alerts a TV sends when it doesn't know our certificate.
    private const int SecCertUnknown = unchecked((int)0x80090327);
    private const int SecUntrustedRoot = unchecked((int)0x80090325);

    private readonly TvDevice _device;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();

    private AndroidTvRemoteClient? _client;
    private bool _running;
    private int _rejections;
    private bool _loadFailureLogged;

    // Pairing puts a dialog on the TV, so it only ever starts because the user asked.
    private TaskCompletionSource _pairRequested = NewSignal();
    private TaskCompletionSource<string>? _codeEntry;
    private string? _needsPairingMessage;

    // KEYCODE_POWER is a toggle: two presses racing each other would cancel out.
    private readonly SemaphoreSlim _powerLock = new(1, 1);

    // Released to cut the reconnect backoff short when we have reason to think the TV is back.
    private readonly SemaphoreSlim _retryNow = new(0, 1);

    // Brightness isn't part of the remote protocol; where it's possible at all it goes over ADB.
    private readonly AdbBrightness _brightness;
    private bool _startupApplied;

    public AndroidTvController(TvDevice device)
    {
        _device = device;
        _brightness = new AdbBrightness(device,
            deviceChanged: () => DeviceChanged?.Invoke(),
            tvIsOn: () => _client is { IsConnected: true, IsOn: not false });
        _brightness.StateChanged += OnBrightnessStateChanged;
        _brightness.Reported += value =>
        {
            _device.LastBacklight = value;
            BacklightReported?.Invoke(value);
        };
    }

    public TvDevice Device => _device;
    public TvStatus Status { get; private set; } = TvStatus.Disconnected;
    public bool SupportsBrightness => _brightness.State == BrightnessSetup.Ready;
    public bool AcceptsBrightness => SupportsBrightness || _brightness.Holds;
    public BrightnessSetup BrightnessSetup => _brightness.State;
    public bool CanWake => !string.IsNullOrEmpty(_device.MacAddress);
    public Func<int?>? GetStartupBrightness { get; set; }

    public event Action<TvStatus, string?>? StatusChanged;
    public event Action? DeviceChanged;
    public event Action? CapabilitiesChanged;
    public event Action<string>? PowerStateReported;
    public event Action<int>? BacklightReported;

    public void SetUpBrightness() => _ = _brightness.SetUpAsync();

    private void OnBrightnessStateChanged()
    {
        CapabilitiesChanged?.Invoke();

        // Schedule catch-up: once per session, the first time brightness is usable.
        if (_brightness.State == BrightnessSetup.Ready && !_startupApplied)
        {
            _startupApplied = true;
            if (GetStartupBrightness?.Invoke() is int target)
            {
                _device.LastBacklight = target;
                BacklightReported?.Invoke(target); // sync the slider
                _brightness.Set(target);           // push to the TV
            }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SetStatus(TvStatus s, string? message = null)
    {
        Status = s;
        StatusChanged?.Invoke(s, message);
    }

    // ----- Connection lifecycle -----

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
        }

        var token = _lifetime.Token;
        var delay = 1000;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrEmpty(_device.ClientCertificate))
                {
                    await PairAsync(token).ConfigureAwait(false);
                    continue; // paired → connect right away; not paired → wait for the next request
                }

                if (TryLoadCertificate() is not { } loaded)
                {
                    // Without a certificate the TV can't be reached, so offer pairing again
                    // rather than reconnect for ever. The saved one is kept, and tried again
                    // meanwhile in case the failure passes, until a new pairing replaces it.
                    Task requested;
                    string? message;
                    lock (_gate)
                    {
                        requested = _pairRequested.Task;
                        message = _needsPairingMessage;
                    }
                    if (requested.IsCompleted)
                    {
                        await PairAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    SetStatus(TvStatus.NeedsPairing, message ?? "Needs pairing again");
                    await Task.WhenAny(requested, Task.Delay(delay, token)).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    delay = Math.Min(delay * 2, 15000);
                    continue;
                }
                using var certificate = loaded;
                SetStatus(TvStatus.Connecting);
                var client = new AndroidTvRemoteClient();
                client.PowerChanged += OnPowerChanged;
                _client = client;

                await client.ConnectAsync(_device.Host, certificate, token).ConfigureAwait(false);
                Log.Write($"Connected to {_device.Host} via Android TV Remote ({client.Vendor} {client.Model})");
                SetStatus(TvStatus.Connected);
                delay = 1000; // reset backoff on success
                _rejections = 0;

                ResolveMacInBackground(token);
                _ = _brightness.ResumeAsync(AdbTclBacklight.MightSupport(client.Vendor));
                await client.RunAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (IsCertificateRejected(ex))
            {
                // The TV was reset or had its paired remotes cleared. Don't re-pair on our own —
                // that would pop a code dialog on a TV nobody may be sitting at.
                if (++_rejections >= RejectionsBeforeUnpairing)
                {
                    Log.Write($"{_device.Host}: the TV no longer accepts our certificate — pairing again is needed");
                    _rejections = 0;
                    _device.ClientCertificate = "";
                    DeviceChanged?.Invoke();
                    lock (_gate) _needsPairingMessage = "Needs pairing again";

                    // Straight on to offering Pair. Waiting out the reconnect backoff first left
                    // the card on "Connecting", and a click on Pair unanswered, for up to 15 s.
                    CleanupClient();
                    delay = 1000;
                    continue;
                }
                else
                {
                    SetStatus(TvStatus.Disconnected, "Reconnecting");
                }
            }
            catch (Exception ex)
            {
                // Two refusals have to come in a row. A TV that couldn't be reached at all said
                // nothing about our certificate, so it starts the count again: otherwise one odd
                // refusal and another days later would erase the pairing. A connection that was
                // closed some other way neither counts nor resets, in case a refusal sometimes
                // shows up as a plain close.
                if (IsUnreachable(ex)) _rejections = 0;

                var wasConnected = Status == TvStatus.Connected;
                if (wasConnected) Log.Write($"{_device.Host}: connection lost ({ex.Message})");
                SetStatus(TvStatus.Disconnected, wasConnected || !IsUnreachable(ex)
                    ? "Reconnecting"
                    : "Can't reach the TV");
            }

            CleanupClient();
            if (token.IsCancellationRequested) break;

            // Back off, but come straight back if a wake attempt says the TV should be up.
            try { await _retryNow.WaitAsync(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 15000);
        }

        lock (_gate) _running = false;
    }

    // The saved certificate, or null when it can't be loaded (damaged by hand, or written by
    // something this copy can't read).
    private X509Certificate2? TryLoadCertificate()
    {
        try
        {
            return AndroidTvTls.LoadClientCertificate(_device.ClientCertificate);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            if (!_loadFailureLogged)
                Log.Write($"{_device.Host}: the saved pairing can't be loaded ({ex.Message}) — pairing again is needed");
            _loadFailureLogged = true; // it's retried every few seconds; once in the log is enough
            return null;
        }
    }

    private static bool IsCertificateRejected(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is Win32Exception { NativeErrorCode: SecCertUnknown or SecUntrustedRoot }) return true;
        return false;
    }

    // A refused/timed-out TCP connect, as opposed to a connection that broke later.
    private static bool IsUnreachable(Exception ex) => ex is SocketException or OperationCanceledException;

    private void OnPowerChanged(bool on)
    {
        Log.Write($"{_device.Host}: power = {(on ? "on" : "standby")}");
        PowerStateReported?.Invoke(on ? "Active" : "Standby");
        _brightness.TvPowerChanged(on);
    }

    // ----- Pairing -----

    public void RequestPairing()
    {
        lock (_gate) _pairRequested.TrySetResult();
    }

    public void SubmitPairingCode(string code)
    {
        lock (_gate) _codeEntry?.TrySetResult(code);
    }

    /// <summary>
    /// Waits for the user to ask, then runs one pairing attempt: the TV shows a code, the user
    /// types it into the app, and on success the certificate we used is saved as the credential.
    /// Returns either way; the caller checks whether a certificate now exists.
    /// </summary>
    private async Task PairAsync(CancellationToken token)
    {
        Task requested;
        string? message;
        lock (_gate)
        {
            requested = _pairRequested.Task;
            message = _needsPairingMessage;
        }
        if (!requested.IsCompleted)
        {
            SetStatus(TvStatus.NeedsPairing, message ?? "Not paired");
            await requested.WaitAsync(token).ConfigureAwait(false);
        }
        lock (_gate)
        {
            _pairRequested = NewSignal();
            _needsPairingMessage = null;
        }

        SetStatus(TvStatus.Connecting);
        var (certificate, pfx) = AndroidTvTls.CreateClientCertificate();
        try
        {
            using var startTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            startTimeout.CancelAfter(PairingStartTimeoutMs);
            using var session = await AndroidTvPairingSession
                .StartAsync(_device.Host, certificate, $"TVControlForWin ({Environment.MachineName})", startTimeout.Token)
                .ConfigureAwait(false);

            using var entry = CancellationTokenSource.CreateLinkedTokenSource(token);
            entry.CancelAfter(CodeEntryTimeoutMs);
            var prompt = "Enter the code from the TV";
            while (true)
            {
                var typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _codeEntry = typed;
                SetStatus(TvStatus.AwaitingCode, prompt);

                var code = await typed.Task.WaitAsync(entry.Token).ConfigureAwait(false);
                try
                {
                    await session.FinishAsync(code, entry.Token).ConfigureAwait(false);
                    break;
                }
                catch (PairingCodeException ex)
                {
                    prompt = ex.Message; // a typo — the code on the TV is still valid
                }
            }

            _device.ClientCertificate = pfx;
            DeviceChanged?.Invoke();
            Log.Write($"{_device.Host}: paired");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"{_device.Host}: pairing failed: {ex.Message}");
            lock (_gate)
            {
                _needsPairingMessage = ex is OperationCanceledException
                    ? "Pairing timed out"
                    : "Pairing failed. Is the TV on?";
            }
        }
        finally
        {
            lock (_gate) _codeEntry = null;
            certificate.Dispose();
        }
    }

    // ----- Power -----

    public async Task<bool> TurnOffAsync(CancellationToken ct = default)
    {
        var client = _client;
        if (client is not { IsConnected: true }) return false;
        try
        {
            return await SetPowerAsync(client, on: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Write($"{_device.Host}: turn off FAILED: {ex.Message}");
            return false;
        }
    }

    // The remote protocol can't blank just the panel. Standby is the nearest thing, and a
    // Google TV resumes from it in a second or two.
    public Task<bool> ScreenOffAsync(CancellationToken ct = default) => TurnOffAsync(ct);

    /// <summary>
    /// Powers the TV on, by whichever route reaches it.
    ///
    /// A TV in ordinary standby keeps its network up, holds the connection open and takes a
    /// power key (a TCL C765 answers in ~0.2 s). One that shuts down fully instead (seen on the
    /// same TCL when it had a system update pending) falls off Wi-Fi seconds after going dark,
    /// yet the connection goes on looking open until the missing pings give it away — so a key
    /// press can vanish without an error, and Wake-on-LAN didn't reach it either. Hence both routes on
    /// every attempt: a magic packet, which is harmless to a TV that's awake, and the key; and a
    /// key that gets no answer is taken as proof the connection is dead.
    /// </summary>
    public async Task PowerOnAsync(CancellationToken ct = default)
    {
        var hasMac = !string.IsNullOrEmpty(_device.MacAddress);
        try
        {
            for (var attempt = 0; attempt < WakeAttempts && !ct.IsCancellationRequested; attempt++)
            {
                if (hasMac) await WakeOnLan.SendAsync(_device.MacAddress, ct).ConfigureAwait(false);

                if (_client is { IsConnected: true } client)
                {
                    // A send that throws is the same news as one that gets no answer: this
                    // connection is dead (typical after the PC wakes), so drop it and go round.
                    bool answered;
                    try { answered = await SetPowerAsync(client, on: true, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.Write($"{_device.Host}: power key not sent ({ex.Message})");
                        answered = false;
                    }
                    if (answered) return;

                    // Reconnecting also means the next key press acts on a freshly reported
                    // state, so a TV that was merely slow to answer isn't toggled back off.
                    Log.Write($"{_device.Host}: no answer to the power key — dropping the connection");
                    client.Dispose();
                }
                else if (!hasMac)
                {
                    Log.Write($"{_device.Host}: can't wake — not connected, and the MAC address isn't learned yet");
                    return;
                }

                // Give the TV a moment to bring its network up, nudging the reconnect loop
                // rather than waiting out its backoff.
                for (var waited = 0; waited < WakeRetryDelayMs && _client is not { IsConnected: true }; waited += 500)
                {
                    if (_retryNow.CurrentCount == 0)
                    {
                        try { _retryNow.Release(); } catch (SemaphoreFullException) { }
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }

            if (_client is { IsConnected: true } last && await SetPowerAsync(last, on: true, ct).ConfigureAwait(false))
                return;
            Log.Write($"{_device.Host}: didn't come back after {WakeAttempts} wake attempts — " +
                      "to be woken remotely the TV has to stay on the network in standby " +
                      "(Google TV: Power & Energy > Energy Modes > Increased)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write($"{_device.Host}: power on FAILED: {ex.Message}");
        }
    }

    public async Task TogglePowerAsync(CancellationToken ct = default)
    {
        if (_client is { IsConnected: true } client
            && await client.WaitForPowerReportAsync(PowerReportMs, ct).ConfigureAwait(false) == true)
        {
            await TurnOffAsync(ct).ConfigureAwait(false);
            return;
        }
        await PowerOnAsync(ct).ConfigureAwait(false);
    }

    // Presses the power key only if the TV isn't already in the wanted state, then waits for
    // it to confirm. The key is a toggle, so with the state unknown we'd be guessing which way
    // it goes — better to do nothing than to switch a TV on at shutdown.
    private async Task<bool> SetPowerAsync(AndroidTvRemoteClient client, bool on, CancellationToken ct)
    {
        await _powerLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await client.WaitForPowerReportAsync(PowerReportMs, ct).ConfigureAwait(false);
            if (current == on) return true;
            if (current is null)
            {
                Log.Write($"{_device.Host}: power state unknown — not sending the power toggle");
                return false;
            }

            await client.SendKeyAsync(AndroidTvRemoteClient.KeyPower, ct).ConfigureAwait(false);
            var done = await client.WaitForPowerAsync(on, PowerSettleMs, ct).ConfigureAwait(false);
            Log.Write($"{_device.Host}: power key -> {(done ? (on ? "on" : "standby") : "no change reported")}");
            return done;
        }
        finally
        {
            _powerLock.Release();
        }
    }

    // ARP the TV's MAC once per device while it's reachable, so WoL works after it goes off.
    private void ResolveMacInBackground(CancellationToken token)
    {
        if (!string.IsNullOrEmpty(_device.MacAddress)) return;
        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;
            var mac = WakeOnLan.TryResolveMac(_device.Host);
            if (mac is null) return;
            _device.MacAddress = mac;
            Log.Write($"{_device.Host}: MAC learned {mac}");
            DeviceChanged?.Invoke();
        }, token);
    }

    public void SetBacklight(int value)
    {
        if (!AcceptsBrightness) return;
        _device.LastBacklight = Math.Clamp(value, 0, 100);
        _brightness.Set(value);
    }

    private void CleanupClient()
    {
        if (_client is { } client)
        {
            client.PowerChanged -= OnPowerChanged;
            client.Dispose();
            _client = null;
            _brightness.Suspend();
        }
    }

    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch { }
        CleanupClient();
        _brightness.Dispose();
        _lifetime.Dispose();
    }
}
