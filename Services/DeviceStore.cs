// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using TVControlForWin.Models;

namespace TVControlForWin.Services;

/// <summary>
/// Loads and saves the list of TVs (incl. their client-keys) plus app settings to
/// %LOCALAPPDATA%\TVControlForWin. Client-key reuse is what avoids re-pairing every launch.
/// </summary>
public sealed class DeviceStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _devicesPath;
    private readonly string _settingsPath;

    public DeviceStore()
    {
        _dir = AppPaths.DataDir;
        _devicesPath = Path.Combine(_dir, "devices.json");
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public List<TvDevice> LoadDevices()
    {
        try
        {
            if (!File.Exists(_devicesPath)) return new List<TvDevice>();
            var json = File.ReadAllText(_devicesPath);
            var devices = JsonSerializer.Deserialize<List<TvDevice>>(json) ?? new List<TvDevice>();
            devices.RemoveAll(d => d is null); // "[null]" deserializes to a list holding null
            return devices;
        }
        catch
        {
            return new List<TvDevice>();
        }
    }

    public void SaveDevices(IEnumerable<TvDevice> devices)
    {
        try
        {
            WriteReplacing(_devicesPath, JsonSerializer.Serialize(devices, JsonOpts));
        }
        catch
        {
            // Best-effort; a transient IO failure shouldn't crash the app.
        }
    }

    /// <summary>Serializes the full configuration (TVs + settings) to a single JSON backup string.</summary>
    public string SerializeBackup(IEnumerable<TvDevice> devices, AppSettings settings) =>
        JsonSerializer.Serialize(new BackupData { Devices = devices.ToList(), Settings = settings }, JsonOpts);

    /// <summary>Parses a backup string; throws if the file isn't a valid TVControlForWin backup.</summary>
    public BackupData DeserializeBackup(string json)
    {
        var data = JsonSerializer.Deserialize<BackupData>(json)
                   ?? throw new InvalidDataException("Not a valid TVControlForWin backup file.");
        // Importing replaces every TV and setting. Any other JSON file reads as an object with
        // neither section, which would otherwise import as "no TVs, default settings". A real
        // backup always has both.
        if (data.Devices is null || data.Settings is null)
            throw new InvalidDataException("That file isn't a settings backup.");
        return data;
    }

    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new AppSettings();
            var json = File.ReadAllText(_settingsPath);
            return FillGaps(JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    // An explicit null in the file ("Osd": null, written by hand or by another tool) replaces
    // the initializer's value, and the rest of the app reads these sections without checking:
    // a null schedule list crashed the app on every start, and a null entry in it crashed the
    // schedule timer. Loading is the one place that has to cope with whatever is on disk.
    private static AppSettings FillGaps(AppSettings settings)
    {
        settings.Osd ??= new OsdSettings();
        settings.Power ??= new PowerSettings();
        settings.Schedules = settings.Schedules?.Where(s => s is not null).ToList() ?? new List<BrightnessSchedule>();
        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        try
        {
            WriteReplacing(_settingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { }
    }

    // Written to a file next to the real one, then moved over it in one step. Writing in place
    // truncates the file first, so a save cut short (the process ended at shutdown or sign-out
    // mid-write, a full disk) left an empty or half-written devices.json. That loads as "no
    // TVs", and the next save then wrote the empty list over it for good.
    private static void WriteReplacing(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A single-file backup bundling all TVs and app settings.</summary>
public sealed class BackupData
{
    // No initializers: a section missing from the file has to stay null, so that
    // DeserializeBackup can tell a backup from some other JSON file.
    public List<TvDevice>? Devices { get; set; }
    public AppSettings? Settings { get; set; }
}

/// <summary>App-wide preferences (hotkey step, autostart, theme, schedules).</summary>
public sealed class AppSettings
{
    /// <summary>How many points the brightness hotkeys nudge per press.</summary>
    public int HotkeyStep { get; set; } = 10;

    /// <summary>Modifier bitmask for the brightness hotkeys (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4,
    /// MOD_WIN=8). Combined with the Up/Down arrow keys. Default = Ctrl+Alt (3).</summary>
    public int HotkeyModifiers { get; set; } = 3;

    private string _hotkeyTarget = "";

    /// <summary>
    /// Which TV the brightness hotkeys change: "" (default) = the TV on the screen under the mouse
    /// cursor, "all" (<see cref="RuleTargets.AllTvs"/>) = every TV whose brightness can be set,
    /// otherwise a <see cref="TvDevice.Id"/> = always that TV. An id that no longer exists
    /// behaves like "".
    /// </summary>
    public string HotkeyTarget
    {
        get => _hotkeyTarget;
        set => _hotkeyTarget = value ?? "";
    }

    /// <summary>Whether the app registers itself to launch at Windows sign-in.</summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>How the window opens on an AUTOSTART (sign-in) launch: 0 = normal window (default),
    /// 1 = minimized, 2 = system tray. Manual launches always open a normal window.</summary>
    public int LaunchBehavior { get; set; } = 0;

    /// <summary>Last window size in physical pixels, restored on a normal launch. 0 = use the default.</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }

    /// <summary>App theme: 0 = follow system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex { get; set; } = 0;

    /// <summary>Up to 10 daily brightness schedules.</summary>
    public List<BrightnessSchedule> Schedules { get; set; } = new();

    /// <summary>
    /// When true, on startup each TV is set to the schedule that's currently in effect
    /// (the most recent past schedule), catching up rules that fired while the PC was off.
    /// </summary>
    public bool ApplyScheduleOnStartup { get; set; } = false;

    /// <summary>On-screen display (brightness OSD) preferences. Never null after load.</summary>
    public OsdSettings Osd { get; set; } = new();

    /// <summary>Look on GitHub for a newer version of the app, about once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Download a newer version when one is found and install it while the window is
    /// closed. Only does anything while <see cref="CheckForUpdates"/> is on.</summary>
    public bool InstallUpdatesAutomatically { get; set; } = true;

    /// <summary>When the last update check got an answer, so restarts don't each trigger one.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>The version the last unattended install was for. If the app is still older than
    /// this, that install failed, and it isn't tried again without being asked for.</summary>
    public string? UnattendedInstallTried { get; set; }

    /// <summary>TV power automation (follow PC startup/sleep/shutdown/display sleep), shared by every
    /// TV without rules of its own (<see cref="TvDevice.Power"/>). Never null after load.</summary>
    public PowerSettings Power { get; set; } = new();
}

/// <summary>
/// Rules for automatically turning the TVs on and off with the PC. One set is shared
/// (<see cref="AppSettings.Power"/>); a TV can also carry its own (<see cref="TvDevice.Power"/>).
/// Everything is off by default — pure opt-in.
/// </summary>
public sealed class PowerSettings
{
    /// <summary>An independent copy, e.g. to start a TV's own rules from the shared ones.</summary>
    public PowerSettings Clone() => (PowerSettings)MemberwiseClone();

    /// <summary>Wake the TVs (WoL) whenever the app starts — with autostart on, that's sign-in.</summary>
    public bool OnAppStart { get; set; }

    /// <summary>Wake the TVs (WoL) when the PC resumes from sleep or hibernation.</summary>
    public bool OnResume { get; set; }

    /// <summary>Turn the TVs off when the PC goes to sleep or hibernates.</summary>
    public bool OffOnSleep { get; set; }

    /// <summary>Turn the TVs off when the PC shuts down, restarts or the user signs out.</summary>
    public bool OffOnShutdown { get; set; }

    /// <summary>React when Windows turns the displays off after the idle timeout.</summary>
    public bool FollowDisplayOff { get; set; }

    /// <summary>
    /// What <see cref="FollowDisplayOff"/> does: 0 = screen off (panel blanked, instant resume),
    /// 1 = full power off (needs Wake-on-LAN to come back).
    /// </summary>
    public int DisplayOffAction { get; set; } = 0;

    /// <summary>Wake the TVs when Windows turns the displays back on.</summary>
    public bool FollowDisplayOn { get; set; }
}

/// <summary>
/// Preferences for the on-screen display shown when brightness changes via hotkeys.
/// </summary>
public sealed class OsdSettings
{
    /// <summary>Whether the OSD is shown at all when brightness changes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Screen anchor: 0 = bottom-centre, 1 = bottom-left, 2 = bottom-right,
    /// 3 = top-centre, 4 = top-left, 5 = top-right.
    /// </summary>
    public int Position { get; set; } = 0;

    /// <summary>
    /// Where to show the OSD: 0 = the primary monitor (default), 1 = the screen of the TV being
    /// adjusted (see <see cref="TvDevice.PairedScreenId"/>), 2 = the screen under the mouse cursor.
    /// </summary>
    public int Monitor { get; set; } = 0;

    /// <summary>How long the OSD lingers before fading out, in seconds.</summary>
    public double TimeoutSeconds { get; set; } = 1.6;

    /// <summary>When true, the targeted TV's name is shown above the brightness bar.</summary>
    public bool ShowTvName { get; set; } = false;

    /// <summary>When true, the OSD also pops up when a schedule applies brightness (off by default).</summary>
    public bool ShowOnSchedule { get; set; } = false;
}
