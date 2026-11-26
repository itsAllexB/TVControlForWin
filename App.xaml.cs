// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.Services;
using TVControlForWin.ViewModels;
using TVControlForWin.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace TVControlForWin;

public partial class App : Application
{
    private MainWindow? _window;
    private MainViewModel? _vm;
    private HotkeyService? _hotkeys;
    private ScheduleService? _schedules;
    private DispatcherQueue? _dispatcher;
    private BrightnessOverlay? _overlay;
    private PowerEventService? _powerEvents;

    /// <summary>Shared application view model, accessed by the navigation pages.</summary>
    public MainViewModel? ViewModel => _vm;

    /// <summary>HWND of the main window, needed to initialize file pickers in an unpackaged app.</summary>
    public IntPtr MainWindowHandle =>
        _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _vm = new MainViewModel(_dispatcher);

        AutostartService.Apply(_vm.Settings.StartWithWindows);

        _hotkeys = new HotkeyService();
        _hotkeys.HotkeyPressed += action =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                var step = _vm!.Settings.HotkeyStep;
                var outcome = _vm.NudgeFromHotkey(action == HotkeyAction.BrightnessUp ? step : -step);
                switch (outcome.Kind)
                {
                    case HotkeyTargetOutcome.Adjusted:
                        ShowOverlay(outcome.Name, outcome.Value, outcome.ScreenId);
                        break;
                    case HotkeyTargetOutcome.Unpaired:
                        ShowUnpairedOverlay();
                        break;
                }
            });
        };
        _hotkeys.Start((uint)_vm.Settings.HotkeyModifiers);
        _vm.HotkeyModifiersChanged += mods => _hotkeys?.UpdateModifiers(mods);

        _schedules = new ScheduleService(_vm, _dispatcher);
        _schedules.ScheduleApplied += ShowScheduleOverlay;
        _schedules.Start();

        _window = new MainWindow(_vm);
        _vm.ThemeChanged += i => _window?.ApplyTheme(i);
        _window.ApplyTheme(_vm.Settings.ThemeIndex);

        // Manual launches always open the window; the launch-behavior preference applies only to the
        // at-sign-in autostart launch (tagged with --autostart on the Run entry). Coming back
        // from a background update, the app returns to the tray it was in.
        var launchArgs = Environment.GetCommandLineArgs();
        var behavior = launchArgs.Contains(AppInfo.TrayArg) ? 2
            : launchArgs.Contains(AutostartService.AutostartArg) ? _vm.Settings.LaunchBehavior
            : 0;
        ApplyLaunchBehavior(_window, behavior);

        WirePowerAutomation(afterUpdate: launchArgs.Contains(AppInfo.UpdatedArg));

        _vm.Updates.IsWindowVisible = () => _window?.AppWindow.IsVisible == true;
        _vm.Updates.InstallRequested += InstallUpdate;
        _vm.Updates.Start();
    }

    // Hands over to the installer: start it (with the app's own restart queued behind it, see
    // UpdateService.InstallCommand), then exit so none of the app's files are in use.
    private void InstallUpdate(string installer, bool quiet)
    {
        try
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's own path is unknown.");
            System.Diagnostics.ProcessStartInfo command;
            try
            {
                command = UpdateService.InstallCommand(installer, exe, quiet);
            }
            catch (NotSupportedException)
            {
                // Can't queue the restart behind it: run the installer as a person would. Its
                // last page offers to start the app.
                command = new System.Diagnostics.ProcessStartInfo(installer) { UseShellExecute = true };
            }
            System.Diagnostics.Process.Start(command)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write($"update: couldn't start the installer ({ex.Message})");
            _vm?.Updates.InstallNotStarted();
            return;
        }
        _window?.Quit();
    }

    // Connects the Windows power lifecycle to the TV power rules (Power page); MainViewModel
    // decides what each TV does by its own or the shared rules. The main window's HWND outlives
    // hide-to-tray, so it can carry the power notifications.
    private void WirePowerAutomation(bool afterUpdate)
    {
        _powerEvents = new PowerEventService(MainWindowHandle);

        // An installer run by hand asks the running app to close. Quit properly (off this
        // window message), without treating it as the PC shutting down.
        _powerEvents.CloseRequested += () => _dispatcher?.TryEnqueue(() => _window?.Quit());

        // Sleep and shutdown give handlers only a couple of seconds — send the off
        // commands over the already-open sockets and wait briefly, then let go.
        _powerEvents.Suspending += () => _vm?.OnPcSuspendingAsync().Wait(1500);
        _powerEvents.SessionEnding += () => _vm?.OnSessionEndingAsync().Wait(2000);

        _powerEvents.Resumed += () => _ = _vm?.OnPcResumedAsync();
        _powerEvents.DisplayStateChanged += on => _ = _vm?.OnDisplaysChangedAsync(on);

        // "Turn on when the PC starts": the app just started, so wake the TVs now. WoL
        // reaches TVs that are off; ones already on simply ignore it (screen-on is a no-op
        // here because no connection exists yet — the reconnect loop is racing in parallel).
        // Not after an update: the app only blinked, and a TV someone switched off in the
        // meantime should stay off.
        if (!afterUpdate) _ = _vm!.OnAppStartedAsync();
    }

    // Opens the window per the launch preference (0 = normal window, 1 = minimized, 2 = tray).
    private static void ApplyLaunchBehavior(MainWindow window, int behavior)
    {
        var presenter = window.AppWindow.Presenter as OverlappedPresenter;
        switch (behavior)
        {
            case 2: // system tray: load the window once (so the tray icon initialises), then hide it
                window.Activate();
                window.AppWindow.Hide();
                break;
            case 1: // minimized to the taskbar
                window.Activate();
                presenter?.Minimize();
                break;
            default: // 0 = normal window at its last size
                window.Activate();
                break;
        }
    }

    // Created lazily on the UI thread the first time a hotkey fires.
    private void ShowOverlay(string? tvName, int value, string? screenId)
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled) return;
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowBrightness(tvName, value, _vm.Settings.Osd, screenId);
    }

    // A hotkey targeted the cursor's TV, but that screen isn't paired to one — prompt to pair
    // instead of silently doing nothing. (Only when the OSD is enabled at all.)
    private void ShowUnpairedOverlay()
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled) return;
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowUnpaired(_vm.Settings.Osd);
    }

    // Shows the OSD after a schedule applied brightness, if the user opted in. One confirmation
    // for the TVs it changed: the value of the first of them in Home page order, on the
    // configured screen (always shown — falls back to primary if "TV's screen" can't
    // resolve), named only when a single TV changed.
    private void ShowScheduleOverlay(IReadOnlyList<TvViewModel> changed)
    {
        if (_vm is null || !_vm.Settings.Osd.Enabled || !_vm.Settings.Osd.ShowOnSchedule) return;
        var tv = changed[0];
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        _overlay.ShowBrightness(changed.Count == 1 ? tv.Name : null, tv.Backlight, _vm.Settings.Osd,
            tv.Device.PairedScreenId, allowFallback: true);
    }

    /// <summary>Shows the OSD on demand (from the settings page Preview button), ignoring the enable toggle.</summary>
    public void PreviewOverlay()
    {
        if (_vm is null) return;
        var tv = _vm.Tvs.FirstOrDefault();
        _overlay ??= new BrightnessOverlay();
        _overlay.ApplyTheme(_vm.Settings.ThemeIndex);
        // Preview should always show something even if this TV isn't paired to a screen yet.
        _overlay.ShowBrightness(tv?.Name ?? "TV", tv?.Backlight ?? 50, _vm.Settings.Osd,
            tv?.Device.PairedScreenId, allowFallback: true);
    }

    /// <summary>Called when a second launch redirects activation here — surface the window.</summary>
    public void OnRedirected()
    {
        _dispatcher?.TryEnqueue(() => _window?.ShowFromTray());
    }

    /// <summary>Releases hotkeys, sockets and persists state before exit.</summary>
    public void ShutdownServices()
    {
        _powerEvents?.Dispose();
        _powerEvents = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _schedules?.Dispose();
        _schedules = null;
        _overlay?.Close();
        _overlay = null;
        _vm?.Persist();
        _vm?.Dispose();
        _vm = null;
    }
}
