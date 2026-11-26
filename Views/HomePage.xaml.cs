// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TVControlForWin.Services;
using TVControlForWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVControlForWin.Views;

public sealed partial class HomePage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    private readonly ObservableCollection<DiscoveredTvItem> _discovered = new();
    private CancellationTokenSource? _scanCts;
    private bool _scanning;

    public HomePage() => InitializeComponent();

    // Re-enumerate displays each time the page is shown, so freshly (un)plugged screens appear.
    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        VM.RefreshMonitors();
        VM.Tvs.CollectionChanged += OnTvsChanged;
        UpdateEmptyState();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) =>
        VM.Tvs.CollectionChanged -= OnTvsChanged;

    private void OnTvsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        UpdateEmptyState();

    // The "No TVs yet" message, in place of the empty list.
    private void UpdateEmptyState() =>
        EmptyState.Visibility = VM.Tvs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TvViewModel tv })
            VM.RemoveTvCommand.Execute(tv);
    }

    // Power button on each card: turns a connected TV off, wakes a disconnected one via WoL.
    private async void OnPowerClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TvViewModel tv })
            await tv.TogglePowerAsync();
    }

    // Pairing strip (TVs that pair by on-screen code): the button asks the TV for a code, then
    // submits what was typed; Enter in the box does the same.
    private void OnPairClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TvViewModel tv }) tv.AdvancePairing();
    }

    private void OnPairingCodeKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && sender is TextBox { Tag: TvViewModel tv })
        {
            tv.AdvancePairing();
            e.Handled = true;
        }
    }

    // Brightness setup strip (Google TVs): connects over ADB; the TV may ask to allow it.
    private void OnSetUpBrightnessClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TvViewModel tv }) tv.SetUpBrightness();
    }

    // ----- SSDP auto-discovery -----

    private async void OnFindTvsClick(object sender, RoutedEventArgs e)
    {
        DiscoverDialog.XamlRoot = XamlRoot;
        DiscoveredList.ItemsSource = _discovered;
        var scan = ScanAsync();
        await DiscoverDialog.ShowAsync();
        _scanCts?.Cancel(); // closing the dialog stops an in-flight scan
        await scan;
    }

    // "Scan again" re-runs the search without closing the dialog.
    private void OnScanAgainClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _scanCts = new CancellationTokenSource();
        _discovered.Clear();
        ScanRing.IsActive = true;
        ScanStatus.Text = "Searching";
        ScanHint.Visibility = Visibility.Collapsed;

        // Progress is constructed on the UI thread, so found TVs appear in the list live.
        var found = new Progress<DiscoveredTv>(tv =>
            _discovered.Add(new DiscoveredTvItem(tv) { Added = VM.HasTvWithHost(tv.Host) }));

        try { await SsdpDiscovery.FindTvsAsync(found, ct: _scanCts.Token); }
        catch (OperationCanceledException) { /* dialog closed mid-scan */ }

        ScanRing.IsActive = false;
        ScanStatus.Text = _discovered.Count switch
        {
            0 => "No TVs found",
            1 => "1 TV found",
            var n => $"{n} TVs found"
        };
        ScanHint.Text = "Make sure the TV is on and on the same network";
        ScanHint.Visibility = _discovered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scanning = false;
    }

    private void OnAddDiscoveredClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DiscoveredTvItem item } && !item.Added)
        {
            VM.AddDiscoveredTv(item.Name, item.Host, item.Kind);
            item.Added = true;
        }
    }
}

/// <summary>One row of the "Find TVs" dialog: a discovered TV and whether it's been added.</summary>
public sealed partial class DiscoveredTvItem : ObservableObject
{
    public DiscoveredTvItem(DiscoveredTv tv)
    {
        Name = tv.Name;
        Host = tv.Host;
        Kind = tv.Kind;

        // "192.168.1.154 · TCL Smart TV Pro · Google TV" — the protocol tells LG and Google TVs apart.
        var details = string.IsNullOrEmpty(tv.Model) || tv.Model == tv.Name
            ? tv.Host
            : $"{tv.Host} · {tv.Model}";
        Details = $"{details} · {TvViewModel.KindLabelFor(tv.Kind)}";
    }

    public string Name { get; }
    public string Host { get; }
    public TVControlForWin.Models.TvKind Kind { get; }
    public string Details { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    private bool _added;

    public string ButtonText => Added ? "Added" : "Add";
    public bool CanAdd => !Added;
}
