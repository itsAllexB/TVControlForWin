// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TVControlForWin.Controls;

/// <summary>
/// A quiet "i" button that opens a flyout with a longer explanation. Put it right after the label
/// it explains. <see cref="Text"/> may hold line breaks for short steps.
/// </summary>
public sealed partial class InfoTip : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnTextChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InfoTip), new PropertyMetadata("", OnTextChanged));

    public InfoTip()
    {
        InitializeComponent();
        Update();
    }

    /// <summary>The explanation shown in the flyout.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Optional heading above the text.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((InfoTip)d).Update();

    private void Update()
    {
        if (BodyText is null) return; // before InitializeComponent
        BodyText.Text = Text ?? "";
        TitleText.Text = Title ?? "";
        TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
    }
}
