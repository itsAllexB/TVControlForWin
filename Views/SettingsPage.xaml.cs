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

    private async void OnExportClick(object sender, RoutedEventArgs e)
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

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow(picker);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var json = await FileIO.ReadTextAsync(file);
            VM.ImportSettings(json);
            await ShowDialogAsync("Import complete", "Your TVs and settings are restored.");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Import failed", $"That file couldn't be imported.\n\n{ex.Message}");
        }
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
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }
}
