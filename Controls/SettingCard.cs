// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace TVControlForWin.Controls;

/// <summary>
/// One setting row: an optional icon, a short label (with an optional <see cref="InfoTip"/>),
/// an optional one-line description, and the control itself (the content) on the right.
/// Styled in App.xaml: the default look is a card; <c>SettingRowStyle</c> drops the card
/// chrome for rows that sit inside another card.
/// </summary>
public sealed partial class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnPartChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnPartChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnPartChanged));

    public static readonly DependencyProperty InfoTextProperty = DependencyProperty.Register(
        nameof(InfoText), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnPartChanged));

    public static readonly DependencyProperty InfoTitleProperty = DependencyProperty.Register(
        nameof(InfoTitle), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnPartChanged));

    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(
        nameof(GlyphSize), typeof(double), typeof(SettingCard), new PropertyMetadata(20.0));

    private FrameworkElement? _icon, _description, _info;

    /// <summary>Icon size: 20 on page cards, 16 on rows inside a TV card.</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public SettingCard()
    {
        IsEnabledChanged += (_, _) => UpdateEnabledState(true);
    }

    /// <summary>The label: a few words, sentence case.</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>Optional single short line under the label. Hidden when empty.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Optional Segoe Fluent Icons glyph shown before the label.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Optional longer explanation, behind an "i" button after the label.</summary>
    public string InfoText
    {
        get => (string)GetValue(InfoTextProperty);
        set => SetValue(InfoTextProperty, value);
    }

    /// <summary>Optional heading for <see cref="InfoText"/>.</summary>
    public string InfoTitle
    {
        get => (string)GetValue(InfoTitleProperty);
        set => SetValue(InfoTitleProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _icon = GetTemplateChild("IconPart") as FrameworkElement;
        _description = GetTemplateChild("DescriptionPart") as FrameworkElement;
        _info = GetTemplateChild("InfoPart") as FrameworkElement;
        UpdateParts();
        UpdateEnabledState(false);
    }

    private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SettingCard)d).UpdateParts();

    private void UpdateParts()
    {
        if (_icon is not null) _icon.Visibility = Show(Glyph);
        if (_description is not null) _description.Visibility = Show(Description);
        if (_info is not null) _info.Visibility = Show(InfoText);
        NameContent();
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        NameContent();
    }

    // Screen readers name the row's control (a switch, a list) after the label, unless the page
    // already gave it a name of its own.
    private string? _givenName;

    private void NameContent()
    {
        if (Content is not Control control) return;
        var current = AutomationProperties.GetName(control);
        if (!string.IsNullOrEmpty(current) && current != _givenName) return;
        _givenName = Header;
        AutomationProperties.SetName(control, Header ?? "");
    }

    private static Visibility Show(string? value) =>
        string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    // The label greys out with the control when the row is disabled.
    private void UpdateEnabledState(bool animate) =>
        VisualStateManager.GoToState(this, IsEnabled ? "Normal" : "Disabled", animate);
}
