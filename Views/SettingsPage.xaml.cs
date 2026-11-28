// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using TVControlForWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace TVControlForWin.Views;

public sealed partial class SettingsPage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public SettingsPage() => InitializeComponent();

    // The Updates section's button: checks, updates or installs, by what's known so far.
    private async void OnUpdateClick(object sender, RoutedEventArgs e) => await VM.Updates.RunAsync();

    private async void OnExportClick(object sender, RoutedEventArgs e) => await RunAloneAsync(ExportAsync);

    private async void OnImportClick(object sender, RoutedEventArgs e) => await RunAloneAsync(ImportAsync);

    // Export and import both end in a dialog, and only one dialog can be open at a time: a
    // second one throws. So a click while either is still under way does nothing.
    private bool _busy;

    private async System.Threading.Tasks.Task RunAloneAsync(Func<System.Threading.Tasks.Task> action)
    {
        if (_busy) return;
        _busy = true;
        try { await action(); }
        finally { _busy = false; }
    }

    private async System.Threading.Tasks.Task ExportAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "TVControlForWin-backup"
        };
        picker.FileTypeChoices.Add("TVControlForWin backup", new List<string> { ".json" });
        InitializeWithWindow(picker);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await FileIO.WriteTextAsync(file, VM.ExportSettings());
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Export failed", ex.Message);
        }
    }

    private async System.Threading.Tasks.Task ImportAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        // The file is read and checked first, so one that isn't a backup is refused without
        // asking whether to replace everything with it.
        TVControlForWin.Services.BackupData data;
        try
        {
            data = VM.ReadBackup(await FileIO.ReadTextAsync(file));
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Import failed", $"That file couldn't be imported.\n\n{ex.Message}");
            return;
        }

        // An import replaces every TV, pairing and setting, so it asks first, with Cancel as
        // the button Enter presses.
        var confirm = new ContentDialog
        {
            Title = "Replace your TVs and settings?",
            Content = "Everything here is replaced by the backup.",
            PrimaryButtonText = "Import",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            VM.ImportSettings(data);
        }
        catch (Exception ex)
        {
            // The file itself was fine, so the message is the whole story: most likely the
            // copy of the current settings couldn't be made, and nothing was replaced.
            await ShowDialogAsync("Import failed", ex.Message);
            return;
        }
        await ShowDialogAsync("Import complete", "Your TVs and settings are restored.");
    }

    private static void InitializeWithWindow(object picker)
    {
        var hwnd = ((App)Application.Current).MainWindowHandle;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private async System.Threading.Tasks.Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
            // A dialog built in code is shown outside the page, so it doesn't pick up a theme
            // chosen in the app that differs from the Windows one.
            RequestedTheme = ActualTheme
        };
        await dialog.ShowAsync();
    }
}
