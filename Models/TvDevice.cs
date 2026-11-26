// SPDX-License-Identifier: AGPL-3.0-or-later
namespace TVControlForWin.Models;

/// <summary>
/// Which control protocol a TV speaks. Stored as a number in devices.json; 0 is webOS so
/// files written before other kinds existed still load as LG TVs.
/// </summary>
public enum TvKind
{
    /// <summary>LG webOS — SSAP over a WebSocket. Brightness, screen off and power.</summary>
    WebOs = 0,

    /// <summary>Android TV / Google TV (TCL, Sony, Philips, Hisense, Xiaomi, Chromecast…) —
    /// the Android TV Remote protocol. Power only: it has no picture-settings channel.</summary>
    AndroidTv = 1,
}

/// <summary>
/// Persisted description of a single TV plus its last-known state.
/// Stored as JSON in %LOCALAPPDATA%\TVControlForWin\devices.json.
/// </summary>
public sealed class TvDevice
{
    /// <summary>
    /// Stable identity for this TV, so rules (schedules, power, hotkeys) can name the TVs they
    /// apply to and survive a rename or a new IP address. Assigned when the TV is first added
    /// or loaded (see MainViewModel) — empty only in files written before it existed.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>Friendly name shown in the UI (e.g. "Living Room C3").</summary>
    public string Name { get; set; } = "LG TV";

    /// <summary>LAN host/IP of the TV, e.g. "192.168.1.42".</summary>
    public string Host { get; set; } = "";

    /// <summary>The protocol this TV is controlled with.</summary>
    public TvKind Kind { get; set; } = TvKind.WebOs;

    /// <summary>
    /// webOS: pairing key returned by the TV on first accept. Reused on every later
    /// connection so the on-screen prompt never appears again. Empty until paired.
    /// </summary>
    public string ClientKey { get; set; } = "";

    /// <summary>
    /// Android TV: the certificate (base64 PKCS#12, private key included) this app paired with.
    /// The TV trusts whoever presents it, so it plays the role <see cref="ClientKey"/> does for
    /// webOS. Only saved once pairing succeeds — empty means "not paired".
    /// </summary>
    public string ClientCertificate { get; set; } = "";

    /// <summary>
    /// Android TV: the RSA key (base64 PKCS#8) the TV has approved for ADB, which is how its
    /// picture brightness is reached. Saved once brightness setup succeeds; empty until then.
    /// </summary>
    public string AdbKey { get; set; } = "";

    /// <summary>Last OLED Light (backlight) value 0-100 we set, for UI restore.</summary>
    public int LastBacklight { get; set; } = 80;

    /// <summary>
    /// Stable id (<see cref="TVControlForWin.Services.MonitorInfo.Id"/>) of the Windows display this TV
    /// drives, so the OSD can appear on that screen when the "TV's screen" mode is chosen. Empty
    /// until the user pairs it on the Home page.
    /// </summary>
    public string PairedScreenId { get; set; } = "";

    /// <summary>
    /// webOS: true once a secure wss://:3001 connection succeeded (2022+ models). Remembered so
    /// reconnects try the working transport first instead of timing out on ws://:3000.
    /// </summary>
    public bool Secure { get; set; }

    /// <summary>
    /// The TV's MAC address ("AA:BB:CC:DD:EE:FF"), learned via ARP the first time we connect
    /// and needed to wake the TV over the network (Wake-on-LAN) once it's off. Empty until learned.
    /// </summary>
    public string MacAddress { get; set; } = "";

    /// <summary>
    /// This TV's own power rules (Power page). Null, the default, means it follows the shared
    /// rules in <see cref="TVControlForWin.Services.AppSettings.Power"/>.
    /// </summary>
    public TVControlForWin.Services.PowerSettings? Power { get; set; }
}
