// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TVControlForWin.Services;

namespace TVControlForWin.ViewModels;

/// <summary>
/// The Power page's rule controls, bound to whichever <see cref="PowerSettings"/> is being shown:
/// the shared rules, or one TV's own. Each change is saved straight away through the save action
/// that came with it (settings.json for the shared rules, devices.json for a TV's own).
/// </summary>
public sealed class PowerRulesViewModel : ObservableObject
{
    private readonly PowerSettings _rules;
    private readonly Action _save;

    /// <param name="isEditable">False while a TV that follows the shared rules is selected: the
    /// controls then show the shared rules without changing them.</param>
    public PowerRulesViewModel(PowerSettings rules, Action save, bool isEditable)
    {
        _rules = rules;
        _save = save;
        IsEditable = isEditable;
    }

    public bool IsEditable { get; }

    /// <summary>Wake the TV(s) whenever the app starts (with autostart, that's Windows sign-in).</summary>
    public bool OnAppStart
    {
        get => _rules.OnAppStart;
        set => Set(_rules.OnAppStart, value, v => _rules.OnAppStart = v);
    }

    /// <summary>Wake the TV(s) when the PC resumes from sleep.</summary>
    public bool OnResume
    {
        get => _rules.OnResume;
        set => Set(_rules.OnResume, value, v => _rules.OnResume = v);
    }

    /// <summary>Turn the TV(s) off when the PC goes to sleep.</summary>
    public bool OffOnSleep
    {
        get => _rules.OffOnSleep;
        set => Set(_rules.OffOnSleep, value, v => _rules.OffOnSleep = v);
    }

    /// <summary>Turn the TV(s) off when the PC shuts down, restarts or signs out.</summary>
    public bool OffOnShutdown
    {
        get => _rules.OffOnShutdown;
        set => Set(_rules.OffOnShutdown, value, v => _rules.OffOnShutdown = v);
    }

    /// <summary>React when Windows turns the displays off after the idle timeout.</summary>
    public bool FollowDisplayOff
    {
        get => _rules.FollowDisplayOff;
        set => Set(_rules.FollowDisplayOff, value, v => _rules.FollowDisplayOff = v);
    }

    /// <summary>0 = screen off (instant resume), 1 = full power off (wakes via WoL).</summary>
    public int DisplayOffAction
    {
        get => _rules.DisplayOffAction;
        set => Set(_rules.DisplayOffAction, value, v => _rules.DisplayOffAction = v);
    }

    /// <summary>Wake the TV(s) when Windows turns the displays back on.</summary>
    public bool FollowDisplayOn
    {
        get => _rules.FollowDisplayOn;
        set => Set(_rules.FollowDisplayOn, value, v => _rules.FollowDisplayOn = v);
    }

    private void Set<T>(T current, T value, Action<T> assign, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        if (IsEditable)
        {
            assign(value);
            _save();
        }
        OnPropertyChanged(name); // read-only view: re-raising puts the control back
    }
}

/// <summary>
/// One entry in a TV chooser (Power page, Hotkeys page): a TV by its id, or a special entry such
/// as "All TVs" with an id that no TV has.
/// </summary>
public sealed record TvChoice(string Id, string Name)
{
    // Screen readers and UI Automation name list items after ToString.
    public override string ToString() => Name;
}
