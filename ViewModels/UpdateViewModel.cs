// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TVControlForWin.Services;

namespace TVControlForWin.ViewModels;

public enum UpdateState
{
    /// <summary>Nothing known yet this session.</summary>
    Idle,
    Checking,
    UpToDate,

    /// <summary>A newer version is published; nothing downloaded.</summary>
    Available,
    Downloading,

    /// <summary>The installer is downloaded and verified, waiting to be run.</summary>
    Ready,
    Installing,
    Failed,
}

/// <summary>
/// App updates: the "Updates" section of the Settings page, and the automatic check behind it.
///
/// With both settings on, a new version is found within a day of being published, downloaded in
/// the background, and installed the next moment the window isn't open. The app is away for the
/// few seconds the installer runs and comes back as a tray icon. Two cases stop short of that:
/// a copy installed for all users needs Windows' permission prompt, which is never sprung on
/// anyone unasked, so it waits for the Install button; and a portable copy has no installer, so
/// it only points to the download page.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TimerInterval = TimeSpan.FromHours(1);

    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly UpdateService _service;
    private readonly bool _automatic; // automatic checks are off for trial runs on their own data folder
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _lifetime = new();

    private AppUpdate? _update;
    private string? _installer;
    private string _failure = "";

    public UpdateViewModel(AppSettings settings, Action saveSettings, DispatcherQueue dispatcher)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        (_service, Kind, var rehearsal) = UpdateService.ForThisCopy();
        _automatic = rehearsal || AppPaths.OverrideDir is null;

        _timer = dispatcher.CreateTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Interval = TimerInterval; // the first tick comes sooner
            if (IsCheckDue) _ = CheckAsync(manual: false);
        };
    }

    /// <summary>How this copy was installed, which decides what an update can do by itself.</summary>
    public InstallKind Kind { get; }

    /// <summary>Asked before installing unattended: an update never restarts the app under
    /// someone who has its window open. Set by the app.</summary>
    public Func<bool>? IsWindowVisible { get; set; }

    /// <summary>Run this verified installer and restart. Bool: quiet (nobody is watching).</summary>
    public event Action<string, bool>? InstallRequested;

    /// <summary>Begins the daily automatic check. The first one waits until startup has settled.</summary>
    public void Start()
    {
        _service.DeleteUsedInstallers();
        if (!_automatic) return;
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    private bool IsCheckDue
    {
        get
        {
            if (!_settings.CheckForUpdates) return false;
            if (_settings.LastUpdateCheckUtc is not { } last) return true;
            var since = DateTime.UtcNow - last;
            return since >= CheckEvery || since < TimeSpan.Zero; // the clock went backwards: don't wait it out
        }
    }

    // ----- What the Settings page shows -----

    public string VersionLabel => $"Version {AppInfo.Version.ToString(3)}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(ButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private UpdateState _state = UpdateState.Idle;

    /// <summary>Download progress, in percent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _progress;

    private string NewVersion => _update?.Version.ToString(3) ?? "";

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking",
        UpdateState.UpToDate => "Up to date",
        UpdateState.Available => $"{NewVersion} is available",
        UpdateState.Downloading => $"Downloading {NewVersion}, {Progress}%",
        UpdateState.Ready => $"{NewVersion} is ready to install",
        UpdateState.Installing => $"Installing {NewVersion}",
        UpdateState.Failed => _failure,
        _ => "",
    };

    // A portable copy, or a release without an installer this app can verify, is updated by
    // hand: the button opens the download page.
    private bool ByHand => Kind == InstallKind.Portable || _update?.InstallerUrl is null;

    public string ButtonText => State switch
    {
        UpdateState.Available => ByHand ? "Download" : "Update",
        UpdateState.Ready => "Install",
        _ => "Check now",
    };

    public bool ButtonEnabled => !IsBusy;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    /// <summary>A newer version is waiting on the user: drives the dot on the Settings tab.</summary>
    public bool HasUpdate => State is UpdateState.Available or UpdateState.Ready;

    public bool CheckAutomatically
    {
        get => _settings.CheckForUpdates;
        set
        {
            if (value == _settings.CheckForUpdates) return;
            _settings.CheckForUpdates = value;
            _saveSettings();
            OnPropertyChanged();
            if (value && _automatic && IsCheckDue) _ = CheckAsync(manual: false);
        }
    }

    public bool InstallAutomatically
    {
        get => _settings.InstallUpdatesAutomatically;
        set
        {
            if (value == _settings.InstallUpdatesAutomatically) return;
            _settings.InstallUpdatesAutomatically = value;
            _saveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>A portable copy has no installer, so nothing to install automatically.</summary>
    public Visibility InstallAutomaticallyVisibility =>
        Kind == InstallKind.Portable ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Settings were replaced wholesale (a backup was imported).</summary>
    public void SettingsReloaded()
    {
        OnPropertyChanged(nameof(CheckAutomatically));
        OnPropertyChanged(nameof(InstallAutomatically));
    }

    // ----- Actions -----

    /// <summary>The section's one button: check, update, or install, by what's known so far.</summary>
    public async Task RunAsync()
    {
        switch (State)
        {
            case UpdateState.Available when ByHand:
                OpenDownloadPage();
                break;
            case UpdateState.Available:
                if (await DownloadAsync()) Install(quiet: false);
                break;
            case UpdateState.Ready:
                Install(quiet: false);
                break;
            default:
                await CheckAsync(manual: true);
                break;
        }
    }

    /// <summary>
    /// Asks whether a newer version is published. An automatic check that finds one carries on
    /// to download it, and to install it if that can be done unattended; a failed automatic
    /// check stays quiet and is tried again an hour later.
    /// </summary>
    public async Task CheckAsync(bool manual)
    {
        if (IsBusy || (State == UpdateState.Ready && !manual)) return;

        var before = State;
        State = UpdateState.Checking;
        try
        {
            var latest = await _service.GetLatestAsync(_lifetime.Token);
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _saveSettings();

            if (latest is null || latest.Version <= AppInfo.Version)
            {
                _update = null;
                State = UpdateState.UpToDate;
                return;
            }

            if (latest.Version != _update?.Version) _installer = null;
            _update = latest;
            OnPropertyChanged(nameof(ButtonText));
            Log.Write($"update: {latest.Version.ToString(3)} is available");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Write($"update: check failed ({ex.Message})");
            if (manual)
            {
                _failure = "Couldn't check. Try again later";
                State = UpdateState.Failed;
            }
            else
            {
                // Nothing to tell anyone: put back what was showing.
                State = before is UpdateState.Checking or UpdateState.Failed ? UpdateState.Idle : before;
            }
            return;
        }

        State = _installer is null ? UpdateState.Available : UpdateState.Ready;

        if (!ByHand && _settings.InstallUpdatesAutomatically && _settings.CheckForUpdates)
        {
            if (_installer is not null || await DownloadAsync()) InstallIfUnattended();
        }
    }

    private async Task<bool> DownloadAsync()
    {
        if (_update is not { } update) return false;

        Progress = 0;
        State = UpdateState.Downloading;
        try
        {
            // Created here, on the UI thread, so its reports arrive on it.
            var progress = new Progress<double>(fraction =>
            {
                var percent = (int)Math.Clamp(fraction * 100, 0, 100);
                if (percent != Progress && State == UpdateState.Downloading) Progress = percent;
            });
            _installer = await _service.DownloadInstallerAsync(update, progress, _lifetime.Token);
            State = UpdateState.Ready;
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Write($"update: download failed ({ex.Message})");
            _installer = null;
            _failure = "Download failed. Try again later";
            State = UpdateState.Failed;
            return false;
        }
    }

    /// <summary>The window was closed to the tray: a downloaded update can go in now.</summary>
    public void NotifyWindowHidden() => InstallIfUnattended();

    // Unattended means: the user asked for automatic installs, this copy's installer needs no
    // permission prompt, and nobody has the window open.
    private void InstallIfUnattended()
    {
        if (State != UpdateState.Ready || Kind != InstallKind.PerUser) return;
        if (!_settings.InstallUpdatesAutomatically || !_settings.CheckForUpdates) return;
        if (IsWindowVisible?.Invoke() == true) return;

        // One unattended try per version. If the installer fails, the old version comes back
        // (see UpdateService.InstallCommand) and would otherwise restart itself into the same
        // failure every day; from then on this version waits for the Install button.
        if (_settings.UnattendedInstallTried == NewVersion) return;
        _settings.UnattendedInstallTried = NewVersion;
        _saveSettings();

        Install(quiet: true);
    }

    private void Install(bool quiet)
    {
        if (_installer is not { } installer || !File.Exists(installer))
        {
            _installer = null;
            _failure = "Download failed. Try again later";
            State = UpdateState.Failed;
            return;
        }

        Log.Write($"update: installing {NewVersion}{(quiet ? " in the background" : "")}");
        State = UpdateState.Installing;
        InstallRequested?.Invoke(installer, quiet);
    }

    /// <summary>The installer couldn't be started; the app is still running.</summary>
    public void InstallNotStarted()
    {
        _failure = "Couldn't start the installer";
        State = UpdateState.Failed;
    }

    private void OpenDownloadPage()
    {
        try
        {
            // Always a GitHub web address: UpdateService.Parse makes sure of it.
            Process.Start(new ProcessStartInfo(_update?.PageUrl ?? AppInfo.ReleasesPage) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"update: couldn't open the download page ({ex.Message})");
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        try { _lifetime.Cancel(); } catch { }
        _service.Dispose();
        _lifetime.Dispose();
    }
}
