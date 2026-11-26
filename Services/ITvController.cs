// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.Models;

namespace TVControlForWin.Services;

public enum TvStatus
{
    Disconnected,
    Connecting,

    /// <summary>webOS: the TV is showing an accept prompt; nothing to do in the app.</summary>
    AwaitingPairing,

    Connected,
    Error,

    /// <summary>Not paired, and pairing only starts when the user asks for it
    /// (<see cref="ITvController.RequestPairing"/>) — it puts a dialog on the TV.</summary>
    NeedsPairing,

    /// <summary>The TV is showing a code that has to be typed into the app
    /// (<see cref="ITvController.SubmitPairingCode"/>).</summary>
    AwaitingCode,
}

/// <summary>
/// Where a TV stands on brightness control. LG TVs are simply <see cref="Ready"/>; a Google TV
/// gets there through a one-time setup, because its brightness is reached over ADB, which the
/// owner has to switch on and approve on the TV.
/// </summary>
public enum BrightnessSetup
{
    /// <summary>Not known yet: the TV hasn't connected since the app started.</summary>
    Unknown,

    /// <summary>No way to control this TV's brightness: it's a power-only TV.</summary>
    Unavailable,

    /// <summary>Could work on this TV; the user hasn't set it up (or it stopped being approved).</summary>
    Offered,

    /// <summary>Setup is running.</summary>
    Connecting,

    /// <summary>Setup couldn't reach ADB: USB debugging isn't switched on on the TV.</summary>
    NeedsDebugging,

    /// <summary>The TV is showing "Allow USB debugging?" and waiting for an answer.</summary>
    AwaitingApproval,

    /// <summary>Brightness can be read and set.</summary>
    Ready,

    /// <summary>ADB works, but this model doesn't expose a brightness control we know.</summary>
    Unsupported,
}

/// <summary>
/// One TV, whatever protocol it speaks: owns the connection, keeps it alive, and exposes the
/// operations the rest of the app is built on. Brands differ in what they can do, so callers
/// check the Supports* flags rather than the concrete type.
/// </summary>
public interface ITvController : IDisposable
{
    TvDevice Device { get; }
    TvStatus Status { get; }

    /// <summary>Whether this TV's brightness can be read and set over the network right now.
    /// Can change while the app runs; see <see cref="CapabilitiesChanged"/>.</summary>
    bool SupportsBrightness { get; }

    /// <summary>Whether a value given to <see cref="SetBacklight"/> gets to the TV, now or once
    /// it's back. Wider than <see cref="SupportsBrightness"/>: a TV that's set up but off or
    /// unreachable still takes a schedule's value and applies it later.</summary>
    bool AcceptsBrightness { get; }

    /// <summary>How far along brightness control is for this TV.</summary>
    BrightnessSetup BrightnessSetup { get; }

    /// <summary>Fires when <see cref="SupportsBrightness"/> or <see cref="BrightnessSetup"/> changes.</summary>
    event Action? CapabilitiesChanged;

    /// <summary>Starts (or retries) brightness setup for a TV that offers it. It may put an
    /// approval dialog on the TV, so it only ever runs because the user asked. No-op otherwise.</summary>
    void SetUpBrightness();

    /// <summary>True when the TV can be woken over the network (MAC already learned).</summary>
    bool CanWake { get; }

    /// <summary>
    /// Optional: returns the brightness to apply once, on the first successful connect this
    /// session (schedule catch-up). Return null to skip.
    /// </summary>
    Func<int?>? GetStartupBrightness { get; set; }

    /// <summary>Fires (status, optional message) whenever the connection state changes.</summary>
    event Action<TvStatus, string?>? StatusChanged;

    /// <summary>Fires when something persisted on <see cref="Device"/> changed — pairing
    /// credentials, the learned MAC address — so the owner can save it.</summary>
    event Action? DeviceChanged;

    /// <summary>Fires when the TV's current backlight is read, so the UI can sync the slider.</summary>
    event Action<int>? BacklightReported;

    /// <summary>Fires when the TV reports a new power state ("Active", "Screen Off", "Standby", …),
    /// so the UI can distinguish a live connection from a TV that's actually switched on.</summary>
    event Action<string>? PowerStateReported;

    /// <summary>Connects and keeps the connection alive, retrying with backoff until disposed.</summary>
    Task StartAsync();

    /// <summary>Queues a brightness change. No-op when <see cref="SupportsBrightness"/> is false.</summary>
    void SetBacklight(int value);

    /// <summary>Turns the TV off. Returns false if it isn't connected or the TV refused.</summary>
    Task<bool> TurnOffAsync(CancellationToken ct = default);

    /// <summary>Blanks the panel while the TV keeps running. TVs that can't do that turn off instead.</summary>
    Task<bool> ScreenOffAsync(CancellationToken ct = default);

    /// <summary>Powers the TV on (or un-blanks it), retrying until it answers.</summary>
    Task PowerOnAsync(CancellationToken ct = default);

    /// <summary>Turns the TV off if it's awake, wakes it if it isn't.</summary>
    Task TogglePowerAsync(CancellationToken ct = default);

    /// <summary>Starts pairing for a TV in <see cref="TvStatus.NeedsPairing"/>. No-op otherwise.</summary>
    void RequestPairing();

    /// <summary>Submits the code the TV is showing while in <see cref="TvStatus.AwaitingCode"/>.</summary>
    void SubmitPairingCode(string code);
}

public static class TvControllerFactory
{
    public static ITvController Create(TvDevice device) => device.Kind switch
    {
        TvKind.AndroidTv => new AndroidTvController(device),
        _ => new WebOsController(device),
    };

    /// <summary>
    /// Works out which protocol the TV at <paramref name="host"/> speaks by seeing which control
    /// port answers. Falls back to webOS when nothing does (the TV may simply be off).
    /// </summary>
    /// <summary>Which protocol the TV at <paramref name="host"/> speaks, judged by the ports it
    /// answers on. Null when none answers (the TV is off, or the address is wrong): guessing
    /// then would save the TV as the wrong kind for good.</summary>
    public static async Task<TvKind?> DetectKindAsync(string host, CancellationToken ct = default)
    {
        // Probe all at once and take the first port that answers: no TV listens on both
        // families, and the ports it doesn't have may be silently dropped rather than refused.
        var android = IsPortOpenAsync(host, AndroidTvTls.RemotePort, ct);
        var pending = new List<Task<bool>> { android, IsPortOpenAsync(host, 3000, ct), IsPortOpenAsync(host, 3001, ct) };
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            if (await finished.ConfigureAwait(false))
                return finished == android ? TvKind.AndroidTv : TvKind.WebOs;
        }
        return null;
    }

    public static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(1500);
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
