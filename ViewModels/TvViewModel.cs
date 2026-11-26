// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TVControlForWin.Models;
using TVControlForWin.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TVControlForWin.ViewModels;

/// <summary>UI-facing wrapper around one <see cref="TvDevice"/> and its <see cref="ITvController"/>.</summary>
public sealed partial class TvViewModel : ObservableObject, IDisposable
{
    private readonly ITvController _controller;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _persist;
    private bool _suppressSend;

    /// <param name="startupBrightness">Schedule catch-up: asked once, on this TV's first connect, for
    /// the value this TV should start at (null to leave it). Called off the UI thread.</param>
    /// <param name="capabilitiesChanged">Called on the UI thread after this TV gains or loses
    /// brightness control, so lists of dimmable TVs elsewhere can be rebuilt.</param>
    public TvViewModel(TvDevice device, DispatcherQueue dispatcher, Action persist,
        ObservableCollection<MonitorInfo> screens, Func<TvViewModel, int?>? startupBrightness = null,
        Action? capabilitiesChanged = null)
    {
        _capabilitiesChanged = capabilitiesChanged;
        Device = device;
        _dispatcher = dispatcher;
        _persist = persist;
        AvailableScreens = screens;
        _backlight = device.LastBacklight;

        _controller = TvControllerFactory.Create(device);
        if (startupBrightness is not null)
            _controller.GetStartupBrightness = () => startupBrightness(this);
        _controller.StatusChanged += OnStatusChanged;
        // Pairing credentials, the learned MAC for WoL. Raised on background threads, and saving
        // walks the TV list, so it's handed to the UI thread like every other save.
        _controller.DeviceChanged += () => _dispatcher.TryEnqueue(() => _persist());
        _controller.BacklightReported += OnBacklightReported;
        _controller.PowerStateReported += OnPowerStateReported;
        _controller.CapabilitiesChanged += OnCapabilitiesChanged;
    }

    private readonly Action? _capabilitiesChanged;

    public TvDevice Device { get; }

    /// <summary>Stable id that rules (schedules, power, hotkeys) use to name this TV.</summary>
    public string Id => Device.Id;

    /// <summary>What rule targeting needs to know about this TV, as of now.</summary>
    public TvInfo Info => new(Device.Id, Device.Name, SupportsBrightness, Device.PairedScreenId ?? "");

    public string Name => Device.Name;
    public string Host => Device.Host;

    /// <summary>Short protocol name shown next to the address, e.g. "LG webOS".</summary>
    public string KindLabel => KindLabelFor(Device.Kind);

    public static string KindLabelFor(TvKind kind) => kind switch
    {
        TvKind.AndroidTv => "Google TV",
        _ => "LG webOS",
    };

    /// <summary>False for TVs whose brightness can't be set (yet) — they get power control only.
    /// Can change while the app runs: a Google TV gains it once ADB brightness is set up.</summary>
    public bool SupportsBrightness => _controller.SupportsBrightness;

    /// <summary>True when setting <see cref="Backlight"/> reaches the TV now or once it's back.
    /// What schedules go by, so one that fires while a TV is unreachable isn't lost.</summary>
    public bool AcceptsBrightness => _controller.AcceptsBrightness;

    public Visibility BrightnessVisibility => SupportsBrightness ? Visibility.Visible : Visibility.Collapsed;

    // ----- Brightness setup (Google TVs, where it goes over ADB and needs switching on once) -----

    private BrightnessSetup BrightnessSetup => _controller.BrightnessSetup;

    /// <summary>The setup strip shows while brightness could work but doesn't yet.</summary>
    public Visibility BrightnessSetupVisibility => BrightnessSetup is
        BrightnessSetup.Offered or BrightnessSetup.Connecting or BrightnessSetup.NeedsDebugging or
        BrightnessSetup.AwaitingApproval or BrightnessSetup.Unsupported
        ? Visibility.Visible : Visibility.Collapsed;

    public string BrightnessSetupText => BrightnessSetup switch
    {
        BrightnessSetup.Connecting => "Connecting",
        BrightnessSetup.NeedsDebugging => "Turn on USB debugging, then try again",
        BrightnessSetup.AwaitingApproval => "Select Allow on the TV",
        BrightnessSetup.Unsupported => "Not available on this TV",
        _ => "Needs USB debugging on the TV",
    };

    /// <summary>Added to the card's detail line for a TV whose brightness can't be controlled, so
    /// the missing slider doesn't look like a fault. Empty otherwise.</summary>
    public string PowerOnlyText => BrightnessSetup == BrightnessSetup.Unavailable ? " · Power only" : "";

    /// <summary>The steps on the TV, for the strip's info button.</summary>
    public string BrightnessSetupHelp =>
        "1. On the TV, open Settings > System > About.\n" +
        "2. Select \"Android TV OS build\" 7 times.\n" +
        "3. Open Settings > System > Developer options and turn on USB debugging.\n" +
        "4. Select Set up here. When the TV asks, tick \"Always allow\" and select Allow.";

    public string BrightnessSetupButtonText =>
        BrightnessSetup == BrightnessSetup.NeedsDebugging ? "Try again" : "Set up";

    public Visibility BrightnessSetupButtonVisibility => BrightnessSetup is
        BrightnessSetup.Offered or BrightnessSetup.NeedsDebugging
        ? Visibility.Visible : Visibility.Collapsed;

    public bool BrightnessSetupBusy => BrightnessSetup is
        BrightnessSetup.Connecting or BrightnessSetup.AwaitingApproval;

    /// <summary>The setup strip's button. The TV may ask for approval as a result.</summary>
    public void SetUpBrightness() => _controller.SetUpBrightness();

    private void OnCapabilitiesChanged()
    {
        _dispatcher.TryEnqueue(() =>
        {
            OnPropertyChanged(nameof(SupportsBrightness));
            OnPropertyChanged(nameof(BrightnessVisibility));
            OnPropertyChanged(nameof(BrightnessSetupVisibility));
            OnPropertyChanged(nameof(BrightnessSetupText));
            OnPropertyChanged(nameof(BrightnessSetupButtonText));
            OnPropertyChanged(nameof(BrightnessSetupButtonVisibility));
            OnPropertyChanged(nameof(BrightnessSetupBusy));
            OnPropertyChanged(nameof(PowerOnlyText));
            OnPropertyChanged(nameof(StatusHelp));
            _capabilitiesChanged?.Invoke();
        });
    }

    /// <summary>Shared list of connected displays (owned by MainViewModel), for the OSD-screen picker.</summary>
    public ObservableCollection<MonitorInfo> AvailableScreens { get; }

    /// <summary>The display this TV is paired to for its OSD; null when unpaired or disconnected.</summary>
    public MonitorInfo? SelectedScreen
    {
        get => AvailableScreens.FirstOrDefault(m => m.Id == Device.PairedScreenId);
        set
        {
            // Ignore null (transient clears while the list refreshes / a paired screen is unplugged)
            // so we never wipe a saved pairing.
            if (value is null || value.Id == Device.PairedScreenId) return;
            Device.PairedScreenId = value.Id;
            _persist();
            OnPropertyChanged();
        }
    }

    /// <summary>Only show the picker when there's a choice to make (more than one display).</summary>
    public Visibility ScreenPickerVisibility =>
        AvailableScreens.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Re-evaluate the picker after the shared display list changes.</summary>
    public void RefreshScreens()
    {
        OnPropertyChanged(nameof(SelectedScreen));
        OnPropertyChanged(nameof(ScreenPickerVisibility));
    }

    [ObservableProperty] private int _backlight;
    [ObservableProperty] private string _statusText = "Not connected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool _isConnected;

    [ObservableProperty] private bool _isBusy;

    /// <summary>Green dot when connected, muted grey otherwise.</summary>
    public Brush StatusBrush => new SolidColorBrush(
        IsConnected ? Color.FromArgb(255, 38, 194, 129) : Color.FromArgb(255, 120, 120, 120));

    /// <summary>Shown on hovering the card's detail line: what to check while the TV can't be
    /// reached, or why a power-only TV has no brightness. Null (no tooltip) the rest of the time.</summary>
    public string? StatusHelp => Status is TvStatus.Error or TvStatus.Disconnected
        ? Device.Kind == TvKind.WebOs
            ? "Check that the TV is on, on the same network as this PC, and that the IP address is right. " +
              "On older LG TVs, also turn on LG Connect Apps in the TV's network settings."
            : "Check that the TV is on, on the same network as this PC, and that the IP address is right."
        : BrightnessSetup == BrightnessSetup.Unavailable
            ? "Brightness isn't supported on this TV's brand yet. Turning it on and off works."
            : null;

    partial void OnBacklightChanged(int value)
    {
        if (_suppressSend) return;
        _controller.SetBacklight(value);
        _persist();
    }

    /// <summary>Nudge brightness by a signed step (used by global hotkeys).</summary>
    public void Nudge(int step) => Backlight = Math.Clamp(Backlight + step, 0, 100);

    public Task StartAsync() => _controller.StartAsync();

    // ----- Pairing by code (TVs that show a code to type in, rather than an accept prompt) -----

    /// <summary>Where the TV is in code pairing; drives the pairing strip on the card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairingVisibility))]
    [NotifyPropertyChangedFor(nameof(CodeEntryVisibility))]
    [NotifyPropertyChangedFor(nameof(PairButtonText))]
    [NotifyPropertyChangedFor(nameof(PairingHelp))]
    [NotifyPropertyChangedFor(nameof(StatusHelp))]
    private TvStatus _status = TvStatus.Disconnected;

    /// <summary>What the user has typed of the code on the TV.</summary>
    [ObservableProperty] private string _pairingCode = "";

    public Visibility PairingVisibility =>
        Status is TvStatus.NeedsPairing or TvStatus.AwaitingCode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CodeEntryVisibility =>
        Status == TvStatus.AwaitingCode ? Visibility.Visible : Visibility.Collapsed;

    public string PairButtonText => Status == TvStatus.AwaitingCode ? "Confirm" : "Pair";

    // While a code is awaited, the card's status line already says what to do (or what was wrong).
    public string PairingHelp => Status == TvStatus.AwaitingCode
        ? ""
        : "The TV will show a code to type here";

    /// <summary>Asks the TV to show its pairing code. Called for a just-added TV and by the Pair button.</summary>
    public void RequestPairing() => _controller.RequestPairing();

    /// <summary>The pairing strip's button: starts pairing, or submits the typed code.</summary>
    public void AdvancePairing()
    {
        if (Status == TvStatus.AwaitingCode)
        {
            if (string.IsNullOrWhiteSpace(PairingCode)) return;
            _controller.SubmitPairingCode(PairingCode);
            PairingCode = "";
        }
        else
        {
            _controller.RequestPairing();
        }
    }

    // ----- Power -----

    public Task<bool> TurnOffAsync() => _controller.TurnOffAsync();
    public Task<bool> ScreenOffAsync() => _controller.ScreenOffAsync();
    public Task PowerOnAsync() => _controller.PowerOnAsync();

    /// <summary>The Home-page power button: turns the TV off when it's awake, wakes it when it isn't.</summary>
    public async Task TogglePowerAsync()
    {
        if (!IsConnected && !_controller.CanWake)
        {
            _dispatcher.TryEnqueue(() =>
                StatusText = "Can't turn on until it has connected once");
            return;
        }
        await _controller.TogglePowerAsync();
    }

    private void OnStatusChanged(TvStatus status, string? message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Status = status;
            IsConnected = status == TvStatus.Connected;
            IsBusy = status is TvStatus.Connecting or TvStatus.AwaitingPairing;
            StatusText = message ?? status switch
            {
                TvStatus.Connected => "Connected",
                TvStatus.Connecting => "Connecting",
                TvStatus.AwaitingPairing => "Accept the prompt on the TV",
                TvStatus.NeedsPairing => "Not paired",
                TvStatus.AwaitingCode => "Enter the code from the TV",
                TvStatus.Error => "Can't connect",
                _ => "Not connected"
            };
        });
    }

    // The socket can be perfectly healthy while the TV itself is in standby, so say which it is
    // rather than a bare "Connected".
    private void OnPowerStateReported(string state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (!IsConnected) return; // a status change is already describing the connection
            StatusText = state switch
            {
                var s when s.Contains("Screen Off", StringComparison.OrdinalIgnoreCase) => "Screen off",
                var s when s.Contains("Standby", StringComparison.OrdinalIgnoreCase)
                        || s.Contains("Suspend", StringComparison.OrdinalIgnoreCase)
                        || s.Contains("Power Off", StringComparison.OrdinalIgnoreCase) => "TV is off",
                _ => "Connected"
            };
        });
    }

    private void OnBacklightReported(int value)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _suppressSend = true;
            Backlight = value;
            _suppressSend = false;
        });
    }

    public void Dispose() => _controller.Dispose();

    // Screen readers and UI Automation name the TV list's items after ToString.
    public override string ToString() => Name;
}
