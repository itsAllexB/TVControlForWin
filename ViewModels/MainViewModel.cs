// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TVControlForWin.Models;
using TVControlForWin.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TVControlForWin.ViewModels;

/// <summary>Top-level view model: TVs, schedules, settings and hotkey routing — shared across pages.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly DeviceStore _store;
    private readonly DispatcherQueue _dispatcher;

    public ObservableCollection<TvViewModel> Tvs { get; } = new();
    public ObservableCollection<ScheduleViewModel> Schedules { get; } = new();

    /// <summary>Power page chooser: "All TVs" (the shared rules) and each TV by name.</summary>
    public ObservableCollection<TvChoice> PowerScopes { get; } = new();

    /// <summary>Hotkeys page chooser: the TV under the cursor, all TVs, or one dimmable TV.</summary>
    public ObservableCollection<TvChoice> HotkeyTargets { get; } = new();

    /// <summary>Connected displays, for pairing each TV to the screen it drives. Refreshed on demand.</summary>
    public ObservableCollection<MonitorInfo> Monitors { get; } = new();

    /// <summary>App updates: the Settings page's "Updates" section and the daily check.</summary>
    public UpdateViewModel Updates { get; }

    public AppSettings Settings { get; }

    /// <summary>Raised when the user changes the theme (0=system,1=light,2=dark); App applies it.</summary>
    public event Action<int>? ThemeChanged;

    /// <summary>Raised when the hotkey modifier combo changes; App re-registers the global hotkeys.</summary>
    public event Action<uint>? HotkeyModifiersChanged;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _store = new DeviceStore();
        Settings = _store.LoadSettings();
        Updates = new UpdateViewModel(Settings, SaveSettings, dispatcher);

        foreach (var device in _store.LoadDevices())
            Add(device, start: true);
        SaveAssignedIds();

        foreach (var schedule in Settings.Schedules)
            Schedules.Add(NewScheduleViewModel(schedule));
        SortSchedules();

        RefreshMonitors();

        PowerRules =new PowerRulesViewModel(Settings.Power, SaveSettings, isEditable: true);
        RefreshTvLists();

        Tvs.CollectionChanged += (_, e) =>
        {
            // Persist a new TV order when the user drag-reorders the list.
            if (e.Action == NotifyCollectionChangedAction.Move) Persist();
            RefreshTvLists();
        };
    }

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newHost = "";

    // Settings surfaced for the UI; each setter persists immediately.
    public bool StartWithWindows
    {
        get => Settings.StartWithWindows;
        set
        {
            if (value == Settings.StartWithWindows) return;
            Settings.StartWithWindows = value;
            AutostartService.Apply(value);
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>How the window opens on launch: 0 = maximized, 1 = minimized, 2 = system tray.</summary>
    public int LaunchBehavior
    {
        get => Settings.LaunchBehavior;
        set
        {
            if (value == Settings.LaunchBehavior) return;
            Settings.LaunchBehavior = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public double HotkeyStep
    {
        get => Settings.HotkeyStep;
        set
        {
            var step = (int)Math.Clamp(value, 1, 25);
            if (step == Settings.HotkeyStep) return;
            Settings.HotkeyStep = step;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // Hotkey modifiers (Ctrl/Alt/Shift/Win), combined with the Up/Down arrows. At least one is
    // always kept — a bare arrow hotkey would swallow every arrow press system-wide.
    private const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    public bool HotkeyCtrl { get => HasMod(ModCtrl); set => SetMod(ModCtrl, value); }
    public bool HotkeyAlt { get => HasMod(ModAlt); set => SetMod(ModAlt, value); }
    public bool HotkeyShift { get => HasMod(ModShift); set => SetMod(ModShift, value); }
    public bool HotkeyWin { get => HasMod(ModWin); set => SetMod(ModWin, value); }

    /// <summary>Human-readable combo for display, e.g. "Ctrl + Alt + ↑ / ↓".</summary>
    public string HotkeyComboText
    {
        get
        {
            var parts = new List<string>();
            if (HotkeyCtrl) parts.Add("Ctrl");
            if (HotkeyWin) parts.Add("Win");
            if (HotkeyAlt) parts.Add("Alt");
            if (HotkeyShift) parts.Add("Shift");
            return parts.Count == 0 ? "↑ / ↓" : string.Join(" + ", parts) + " + ↑ / ↓";
        }
    }

    private bool HasMod(int bit) => (Settings.HotkeyModifiers & bit) != 0;

    private void SetMod(int bit, bool on)
    {
        var cur = Settings.HotkeyModifiers;
        var next = on ? cur | bit : cur & ~bit;
        if (next == 0) next = cur; // don't allow clearing the last modifier
        if (next != cur)
        {
            Settings.HotkeyModifiers = next;
            SaveSettings();
            HotkeyModifiersChanged?.Invoke((uint)next);
        }
        // Re-raise all four (so a rejected "clear the last one" reverts its checkbox) plus the label.
        OnPropertyChanged(nameof(HotkeyCtrl));
        OnPropertyChanged(nameof(HotkeyAlt));
        OnPropertyChanged(nameof(HotkeyShift));
        OnPropertyChanged(nameof(HotkeyWin));
        OnPropertyChanged(nameof(HotkeyComboText));
    }

    /// <summary>0 = follow system, 1 = light, 2 = dark.</summary>
    public int ThemeIndex
    {
        get => Settings.ThemeIndex;
        set
        {
            if (value == Settings.ThemeIndex) return;
            Settings.ThemeIndex = value;
            SaveSettings();
            OnPropertyChanged();
            ThemeChanged?.Invoke(value);
        }
    }

    // ----- On-screen display (OSD) -----

    /// <summary>Whether the brightness OSD appears when hotkeys change brightness.</summary>
    public bool OsdEnabled
    {
        get => Settings.Osd.Enabled;
        set
        {
            if (value == Settings.Osd.Enabled) return;
            Settings.Osd.Enabled = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Screen anchor (0=bottom-centre … 5=top-right). See <see cref="OsdSettings"/>.</summary>
    public int OsdPosition
    {
        get => Settings.Osd.Position;
        set
        {
            if (value == Settings.Osd.Position) return;
            Settings.Osd.Position = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Where the OSD shows (0=primary, 1=adjusted TV's screen, 2=cursor).</summary>
    public int OsdMonitor
    {
        get => Settings.Osd.Monitor;
        set
        {
            if (value == Settings.Osd.Monitor) return;
            Settings.Osd.Monitor = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>How long the OSD lingers before fading, in seconds (0.5–5.0).</summary>
    public double OsdTimeoutSeconds
    {
        get => Settings.Osd.TimeoutSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 5.0);
            if (Math.Abs(clamped - Settings.Osd.TimeoutSeconds) < 0.001) return;
            Settings.Osd.TimeoutSeconds = clamped;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the targeted TV's name is shown in the OSD.</summary>
    public bool OsdShowTvName
    {
        get => Settings.Osd.ShowTvName;
        set
        {
            if (value == Settings.Osd.ShowTvName) return;
            Settings.Osd.ShowTvName = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Whether the OSD also appears when a schedule applies brightness.</summary>
    public bool OsdOnSchedule
    {
        get => Settings.Osd.ShowOnSchedule;
        set
        {
            if (value == Settings.Osd.ShowOnSchedule) return;
            Settings.Osd.ShowOnSchedule = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    // ----- TV power automation -----
    //
    // Each TV follows its own rules (TvDevice.Power) when it has them, otherwise the shared ones
    // (Settings.Power). The handlers below are called by App on the UI thread as Windows reports
    // each event; the returned task completes once every TV's command has been sent.

    /// <summary>The app just started: wake the TVs whose rules say so. WoL reaches TVs that are off;
    /// ones already on ignore it.</summary>
    public Task OnAppStartedAsync() => ApplyPowerEventAsync(PcPowerEvent.AppStart);

    /// <summary>The PC resumed from sleep or hibernation.</summary>
    public Task OnPcResumedAsync() => ApplyPowerEventAsync(PcPowerEvent.Resume);

    /// <summary>The PC is about to sleep. Windows gives only a couple of seconds, so the caller
    /// waits on this briefly and then lets go.</summary>
    public Task OnPcSuspendingAsync() => ApplyPowerEventAsync(PcPowerEvent.Suspend);

    /// <summary>Shutdown, restart or sign-out. Like sleep, the caller only waits briefly.</summary>
    public Task OnSessionEndingAsync() => ApplyPowerEventAsync(PcPowerEvent.SessionEnding);

    /// <summary>Windows turned the displays off after the idle timeout (false) or back on (true).</summary>
    public Task OnDisplaysChangedAsync(bool on) =>
        ApplyPowerEventAsync(on ? PcPowerEvent.DisplayOn : PcPowerEvent.DisplayOff);

    private Task ApplyPowerEventAsync(PcPowerEvent e)
    {
        var plan = RuleTargets.PlanPowerEvent(e, Tvs.ToList(), t => t.Device.Power, Settings.Power);
        if (plan.Count == 0) return Task.CompletedTask;

        Log.Write($"power: {e} -> {string.Join(", ", plan.Select(p => $"{p.Tv.Name}: {p.Action}"))}");
        return Task.WhenAll(plan.Select(p => p.Action switch
        {
            TvPowerAction.TurnOff => p.Tv.TurnOffAsync(),
            // Blanks the panel (webOS keeps running, resume is instant); TVs that can't blank just
            // the panel go to standby instead.
            TvPowerAction.ScreenOff => p.Tv.ScreenOffAsync(),
            // Un-blanks connected TVs, wakes the rest via WoL.
            _ => p.Tv.PowerOnAsync(),
        }));
    }

    // ----- Power page: whose rules are being edited -----

    /// <summary>The rule controls on the Power page: the shared rules, or the selected TV's own.</summary>
    public PowerRulesViewModel PowerRules { get; private set; }

    private TvChoice? _selectedPowerScope;

    /// <summary>What the Power page is editing: "All TVs" (id "") or one TV.</summary>
    public TvChoice? SelectedPowerScope
    {
        get => _selectedPowerScope;
        set
        {
            // Null comes from the list being rebuilt underneath the ComboBox; keep the selection.
            if (value is null || value == _selectedPowerScope) return;
            _selectedPowerScope = value;
            OnPropertyChanged();
            LoadPowerRules();
        }
    }

    // The TV selected on the Power page, or null for "All TVs".
    private TvViewModel? PowerScopeTv =>
        string.IsNullOrEmpty(_selectedPowerScope?.Id) ? null : TvById(_selectedPowerScope.Id);

    /// <summary>
    /// Whether the selected TV has rules of its own. Switching it on starts them as a copy of the
    /// shared rules; switching it off discards them, so the TV follows the shared rules again.
    /// </summary>
    public bool PowerUseSeparateRules
    {
        get => PowerScopeTv?.Device.Power is not null;
        set
        {
            var tv = PowerScopeTv;
            if (tv is null || value == (tv.Device.Power is not null)) return;
            tv.Device.Power = value ? Settings.Power.Clone() : null;
            Persist();
            LoadPowerRules();
        }
    }

    /// <summary>
    /// The chooser shows with more than one TV. With a single TV it stays visible only if that TV
    /// still has rules of its own (left over from when there were more), so they can be undone.
    /// </summary>
    public Visibility PowerScopeVisibility =>
        Tvs.Count > 1 || Tvs.Any(t => t.Device.Power is not null) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Use separate rules" only applies to a single TV.</summary>
    public Visibility PowerSeparateVisibility => PowerScopeTv is null ? Visibility.Collapsed : Visibility.Visible;

    private void LoadPowerRules()
    {
        // With the chooser hidden there'd be no way back from one TV's (read-only) view, e.g. after
        // the other TV was removed: go back to the shared rules.
        if (PowerScopeVisibility == Visibility.Collapsed && PowerScopeTv is not null
            && PowerScopes.FirstOrDefault(c => c.Id == "") is { } allTvs)
        {
            _selectedPowerScope = allTvs;
            OnPropertyChanged(nameof(SelectedPowerScope));
        }

        var tv = PowerScopeTv;
        PowerRules = tv?.Device.Power is { } own
            ? new PowerRulesViewModel(own, Persist, isEditable: true)
            : new PowerRulesViewModel(Settings.Power, SaveSettings, isEditable: tv is null);
        OnPropertyChanged(nameof(PowerRules));
        OnPropertyChanged(nameof(PowerUseSeparateRules));
        OnPropertyChanged(nameof(PowerSeparateVisibility));
        OnPropertyChanged(nameof(PowerScopeVisibility));
    }

    // ----- Adding TVs -----

    public bool CanAdd => !string.IsNullOrWhiteSpace(NewHost);

    partial void OnNewHostChanged(string value)
    {
        AddTvCommand.NotifyCanExecuteChanged();
        if (value != _unreachableHost) AddError = ""; // typing a different address clears the message
    }

    /// <summary>Why the last "Add TV" didn't add anything; empty when there's nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddErrorVisibility))]
    private string _addError = "";

    public Visibility AddErrorVisibility => AddError.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string? _unreachableHost;

    /// <summary>True when a TV with this host/IP is already in the list (used by discovery).</summary>
    public bool HasTvWithHost(string host) =>
        Tvs.Any(t => string.Equals(t.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a TV found by network discovery (no-op if its IP is already added).</summary>
    public void AddDiscoveredTv(string name, string host, TvKind kind)
    {
        if (HasTvWithHost(host)) return;
        Add(new TvDevice { Name = name, Host = host, Kind = kind }, start: true, pair: true);
        Persist();
    }

    // Adding by IP: which protocol the TV speaks is worked out from the ports it answers on,
    // so there's no brand to pick.
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddTvAsync()
    {
        var name = NewName.Trim();
        var host = NewHost.Trim();
        NewName = "";
        NewHost = "";
        AddError = "";
        if (HasTvWithHost(host)) return;

        var detected = await TvControllerFactory.DetectKindAsync(host);
        if (HasTvWithHost(host)) return; // added some other way while we were probing
        if (detected is not TvKind kind)
        {
            // Nothing answered, so there's no telling what kind of TV it is. Put the entry back
            // for another go rather than save a card that could never connect.
            _unreachableHost = host;
            NewName = name;
            NewHost = host;
            AddError = "No TV found at that address. Turn the TV on and try again";
            return;
        }
        if (name.Length == 0) name = kind == TvKind.AndroidTv ? "Google TV" : "LG TV";

        Add(new TvDevice { Name = name, Host = host, Kind = kind }, start: true, pair: true);
        Persist();
    }

    [RelayCommand]
    private void RemoveTv(TvViewModel? vm)
    {
        if (vm is null) return;
        Tvs.Remove(vm);
        vm.Dispose();
        Persist();
        ForgetTvInRules(vm.Id);
    }

    // A removed TV's own power rules go with its device entry; schedules and the hotkey target
    // name it by id and are cleaned up here.
    private void ForgetTvInRules(string tvId)
    {
        var remaining = Tvs.Select(t => t.Id).ToList();
        var changed = false;
        foreach (var schedule in Schedules.ToList()) // a change persists, which re-sorts the list
            changed |= schedule.ForgetTv(tvId, remaining);
        if (changed) PersistSchedules();

        if (Settings.HotkeyTarget == tvId)
        {
            Settings.HotkeyTarget = "";
            SaveSettings();
        }
        RefreshTvLists();
    }

    // <paramref name="pair"/> is set when the user has just added the TV: they're at the PC and
    // expecting it, so a TV that pairs by on-screen code may show that code straight away. A TV
    // loaded from disk unpaired waits for the Pair button instead.
    private void Add(TvDevice device, bool start, bool pair = false)
    {
        if (string.IsNullOrEmpty(device.Id))
        {
            device.Id = Guid.NewGuid().ToString("N");
            _idsAssigned = true;
        }

        var vm = new TvViewModel(device, _dispatcher, Persist, Monitors, StartupBrightnessFor,
            capabilitiesChanged: RefreshTvLists);
        Tvs.Add(vm);
        if (pair) vm.RequestPairing();
        if (start) _ = vm.StartAsync();
    }

    // TVs saved before ids existed get one as they're loaded; write it back straight away,
    // or anything that refers to a TV by id would point at nothing after a restart.
    private bool _idsAssigned;

    private void SaveAssignedIds()
    {
        if (!_idsAssigned) return;
        _idsAssigned = false;
        Persist();
    }

    /// <summary>Re-enumerates connected displays and refreshes each TV's screen picker.</summary>
    public void RefreshMonitors()
    {
        var list = MonitorService.List();
        Monitors.Clear();
        foreach (var m in list) Monitors.Add(m);
        foreach (var tv in Tvs) tv.RefreshScreens();
    }

    private TvViewModel? TvById(string id) => Tvs.FirstOrDefault(t => t.Id == id);

    // The TVs as rule targeting sees them right now. UI thread.
    private IReadOnlyList<TvInfo> TvInfos() => Tvs.Select(t => t.Info).ToList();

    /// <summary>
    /// Rebuilds everything on the pages that depends on which TVs exist and which of them can be
    /// dimmed: the Power and Hotkeys choosers, their visibility, and each schedule's TV picker.
    /// Runs whenever TVs are added or removed; call it too when a TV gains or loses brightness
    /// control while the app runs. UI thread.
    /// </summary>
    public void RefreshTvLists()
    {
        // Power page: "All TVs" plus every TV (power rules apply to all kinds).
        var scopeId = _selectedPowerScope?.Id ?? "";
        ReplaceIfDifferent(PowerScopes,
            new[] { new TvChoice("", "All TVs") }.Concat(Tvs.Select(t => new TvChoice(t.Id, t.Name))));
        _selectedPowerScope = PowerScopes.FirstOrDefault(c => c.Id == scopeId) ?? PowerScopes[0];
        OnPropertyChanged(nameof(SelectedPowerScope));
        LoadPowerRules();

        // Hotkeys page: the cursor, all TVs, then each TV that can be dimmed. A TV picked earlier
        // that can't be dimmed right now stays listed, so the choice still shows as made.
        ReplaceIfDifferent(HotkeyTargets,
            new[] { new TvChoice("", "TV under the cursor"), new TvChoice(RuleTargets.AllTvs, "All TVs") }
                .Concat(Tvs.Where(t => t.SupportsBrightness || t.Id == Settings.HotkeyTarget)
                           .Select(t => new TvChoice(t.Id, t.Name))));
        OnPropertyChanged(nameof(SelectedHotkeyTarget));
        OnPropertyChanged(nameof(HotkeyTargetVisibility));

        foreach (var schedule in Schedules) schedule.RefreshTargets();
    }

    // Only touches the collection when its contents actually change, so a bound ComboBox keeps
    // its selection (and doesn't flicker) on the many refreshes that change nothing.
    private static void ReplaceIfDifferent(ObservableCollection<TvChoice> target, IEnumerable<TvChoice> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var item in list) target.Add(item);
    }

    // Schedule catch-up on a TV's first connect: the schedule in effect for that TV now, or null
    // when catch-up is off, the TV can't be dimmed, or no schedule targets it. Called off the UI
    // thread, so it reads the settings' schedule list (replaced whole on every edit).
    private int? StartupBrightnessFor(TvViewModel tv) =>
        Settings.ApplyScheduleOnStartup && tv.SupportsBrightness
            ? RuleTargets.ScheduledBrightnessFor(Settings.Schedules.ToList(), tv.Id, DateTime.Now)
            : null;

    /// <summary>
    /// Applies one schedule that just fired to the TVs it targets whose brightness is set up. One
    /// that's off or unreachable holds the value and applies it when it's back.
    /// Returns the TVs it changed. UI thread.
    /// </summary>
    public IReadOnlyList<TvViewModel> ApplySchedule(BrightnessSchedule schedule)
    {
        var changed = new List<TvViewModel>();
        foreach (var tv in Tvs.Where(t => t.AcceptsBrightness && RuleTargets.Targets(schedule, t.Id)))
        {
            tv.Backlight = Math.Clamp(schedule.Brightness, 0, 100);
            changed.Add(tv);
        }
        return changed;
    }

    /// <summary>
    /// After the PC slept through (from, to]: sets each dimmable TV that had a schedule fire in that
    /// gap to the schedule now in effect for it (wrapping to last night's). TVs whose schedules all
    /// stayed quiet are left alone. Returns the TVs it changed. UI thread.
    /// </summary>
    public IReadOnlyList<TvViewModel> CatchUpSchedules(DateTime from, DateTime to)
    {
        var schedules = Settings.Schedules.ToList();
        var changed = new List<TvViewModel>();
        foreach (var tv in Tvs.Where(t => t.AcceptsBrightness))
        {
            if (RuleTargets.CatchUpBrightnessFor(schedules, tv.Id, from, to) is not int value) continue;
            tv.Backlight = Math.Clamp(value, 0, 100);
            changed.Add(tv);
        }
        return changed;
    }

    // ----- Brightness hotkeys -----

    /// <summary>
    /// Nudges brightness by a signed step from a global hotkey, on the TV(s) chosen on the Hotkeys
    /// page (see <see cref="RuleTargets.PlanHotkey"/> for the rules). Returns what to show: the
    /// name (only when a single TV changed), new value and screen of the TV the overlay is about.
    /// <see cref="HotkeyTargetOutcome.Unpaired"/> means the cursor was on a screen no TV is paired
    /// to, so the caller can prompt the user to pair. Must be invoked on the UI thread.
    /// </summary>
    public (HotkeyTargetOutcome Kind, string? Name, int Value, string? ScreenId) NudgeFromHotkey(int step)
    {
        var plan = RuleTargets.PlanHotkey(Settings.HotkeyTarget, TvInfos(), MonitorService.MonitorIdAtCursor);
        if (plan.Outcome != HotkeyTargetOutcome.Adjusted || plan.ShownTvId is null)
            return (plan.Outcome, null, 0, null);

        foreach (var id in plan.TvIds) TvById(id)?.Nudge(step);

        var shown = TvById(plan.ShownTvId);
        if (shown is null) return (HotkeyTargetOutcome.None, null, 0, null);
        return (HotkeyTargetOutcome.Adjusted, plan.TvIds.Count == 1 ? shown.Name : null,
            shown.Backlight, shown.Device.PairedScreenId);
    }

    /// <summary>The Hotkeys page "Adjusts" choice. An id of a TV that's gone shows as the cursor.</summary>
    public TvChoice? SelectedHotkeyTarget
    {
        get
        {
            var (mode, tvId) = RuleTargets.ResolveHotkeyTarget(Settings.HotkeyTarget, Tvs.Select(t => t.Id));
            var id = mode switch { HotkeyMode.All => RuleTargets.AllTvs, HotkeyMode.Tv => tvId!, _ => "" };
            return HotkeyTargets.FirstOrDefault(c => c.Id == id);
        }
        set
        {
            // Null comes from the list being rebuilt underneath the ComboBox; keep the setting.
            if (value is null || value.Id == Settings.HotkeyTarget) return;
            Settings.HotkeyTarget = value.Id;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>The "Which TV" chooser only matters with more than one TV.</summary>
    public Visibility HotkeyTargetVisibility => Tvs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    // ----- Schedules -----

    /// <summary>When on, the in-effect schedule is applied to each TV on startup.</summary>
    public bool ApplyScheduleOnStartup
    {
        get => Settings.ApplyScheduleOnStartup;
        set
        {
            if (value == Settings.ApplyScheduleOnStartup) return;
            Settings.ApplyScheduleOnStartup = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool CanAddSchedule => Schedules.Count < 10;

    [RelayCommand(CanExecute = nameof(CanAddSchedule))]
    private void AddSchedule()
    {
        var model = new BrightnessSchedule();
        Schedules.Add(NewScheduleViewModel(model));
        PersistSchedules();
        AddScheduleCommand.NotifyCanExecuteChanged();
    }

    private ScheduleViewModel NewScheduleViewModel(BrightnessSchedule model) =>
        new(model, PersistSchedules, TvInfos);

    [RelayCommand]
    private void RemoveSchedule(ScheduleViewModel? schedule)
    {
        if (schedule is null) return;
        Schedules.Remove(schedule);
        PersistSchedules();
        AddScheduleCommand.NotifyCanExecuteChanged();
    }

    private void PersistSchedules()
    {
        SortSchedules();
        Settings.Schedules = Schedules.Select(s => s.Model).ToList();
        SaveSettings();
    }

    // Reorders the list by time in place (preserving item instances) so it stays chronological.
    private void SortSchedules()
    {
        var sorted = Schedules
            .OrderBy(s => s.Model.Hour * 60 + s.Model.Minute)
            .ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var current = Schedules.IndexOf(sorted[i]);
            if (current != i) Schedules.Move(current, i);
        }
    }

    // ----- Backup / restore -----

    /// <summary>Returns the full configuration (TVs + settings) as a single JSON string.</summary>
    public string ExportSettings() => _store.SerializeBackup(Tvs.Select(t => t.Device), Settings);

    /// <summary>Replaces all TVs and settings from a backup string and reloads live state.</summary>
    public void ImportSettings(string json)
    {
        var data = _store.DeserializeBackup(json); // throws on invalid input (before we touch anything)
        _store.SaveDevices(data.Devices!);
        _store.SaveSettings(data.Settings!);
        ReloadFromStore();
    }

    private void ReloadFromStore()
    {
        // Rebuild TVs.
        foreach (var tv in Tvs) tv.Dispose();
        Tvs.Clear();
        foreach (var device in _store.LoadDevices())
            Add(device, start: true);
        SaveAssignedIds();
        RefreshMonitors();

        // Copy settings into the existing instance and refresh bound proxies.
        var s = _store.LoadSettings();
        Settings.HotkeyStep = s.HotkeyStep;
        Settings.HotkeyModifiers = s.HotkeyModifiers;
        Settings.HotkeyTarget = s.HotkeyTarget;
        Settings.StartWithWindows = s.StartWithWindows;
        Settings.LaunchBehavior = s.LaunchBehavior;
        Settings.WindowWidth = s.WindowWidth;
        Settings.WindowHeight = s.WindowHeight;
        Settings.ThemeIndex = s.ThemeIndex;
        Settings.ApplyScheduleOnStartup = s.ApplyScheduleOnStartup;
        Settings.CheckForUpdates = s.CheckForUpdates;
        Settings.InstallUpdatesAutomatically = s.InstallUpdatesAutomatically;
        Settings.Osd = s.Osd;
        Settings.Power = s.Power;
        Settings.Schedules = s.Schedules;

        Schedules.Clear();
        foreach (var schedule in Settings.Schedules)
            Schedules.Add(NewScheduleViewModel(schedule));
        SortSchedules();
        AddScheduleCommand.NotifyCanExecuteChanged();

        // The Power page goes back to the shared rules, now the restored ones; the TV lists,
        // pickers and hotkey choice are rebuilt for the restored TVs and settings.
        _selectedPowerScope = null;
        RefreshTvLists();

        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(LaunchBehavior));
        OnPropertyChanged(nameof(HotkeyStep));
        OnPropertyChanged(nameof(ThemeIndex));
        OnPropertyChanged(nameof(ApplyScheduleOnStartup));
        OnPropertyChanged(nameof(OsdEnabled));
        OnPropertyChanged(nameof(OsdPosition));
        OnPropertyChanged(nameof(OsdMonitor));
        OnPropertyChanged(nameof(OsdTimeoutSeconds));
        OnPropertyChanged(nameof(OsdShowTvName));
        OnPropertyChanged(nameof(OsdOnSchedule));
        OnPropertyChanged(nameof(HotkeyCtrl));
        OnPropertyChanged(nameof(HotkeyAlt));
        OnPropertyChanged(nameof(HotkeyShift));
        OnPropertyChanged(nameof(HotkeyWin));
        OnPropertyChanged(nameof(HotkeyComboText));
        Updates.SettingsReloaded();

        AutostartService.Apply(Settings.StartWithWindows);
        ThemeChanged?.Invoke(Settings.ThemeIndex);
        HotkeyModifiersChanged?.Invoke((uint)Settings.HotkeyModifiers);
    }

    public void Persist() => _store.SaveDevices(Tvs.Select(t => t.Device));
    public void SaveSettings() => _store.SaveSettings(Settings);

    public void Dispose()
    {
        Updates.Dispose();
        foreach (var tv in Tvs) tv.Dispose();
    }
}
