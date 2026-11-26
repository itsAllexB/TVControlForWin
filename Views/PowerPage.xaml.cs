// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVControlForWin.Views;

public sealed partial class PowerPage : Page
{
    public MainViewModel VM => ((App)Application.Current).ViewModel!;

    public PowerPage() => InitializeComponent();
}
