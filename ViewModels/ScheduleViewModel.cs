// SPDX-License-Identifier: AGPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using TVControlForWin.Models;
using TVControlForWin.Services;
using Microsoft.UI.Xaml;

namespace TVControlForWin.ViewModels;

/// <summary>Editable wrapper around a <see cref="BrightnessSchedule"/>; persists on every change.</summary>
public sealed partial class ScheduleViewModel : ObservableObject
{
    private readonly Action _persist;
    private readonly Func<IReadOnlyList<TvInfo>> _tvs;

    /// <param name="tvs">The app's TVs as they are right now (whether each can be dimmed can change
    /// while the app runs, so it's asked every time rather than remembered).</param>
    public ScheduleViewModel(BrightnessSchedule model, Action persist, Func<IReadOnlyList<TvInfo>> tvs)
    {
        Model = model;
        _persist = persist;
        _tvs = tvs;
        _time = new TimeSpan(model.Hour, model.Minute, 0);
        _brightness = model.Brightness;
        _enabled = model.Enabled;
    }

    public BrightnessSchedule Model { get; }

    /// <summary>System-appropriate 12/24h format for the bound TimePicker.</summary>
    public string ClockId => ClockHelper.ClockIdentifier;

    [ObservableProperty] private TimeSpan _time;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private bool _enabled;

    partial void OnTimeChanged(TimeSpan value)
    {
        Model.Hour = value.Hours;
        Model.Minute = value.Minutes;
        _persist();
    }

    partial void OnBrightnessChanged(double value)
    {
        Model.Brightness = (int)value;
        _persist();
    }

    partial void OnEnabledChanged(bool value)
    {
        Model.Enabled = value;
        _persist();
    }

    // ----- Which TVs it changes -----

    /// <summary>The TV picker's label: "All TVs", one TV's name, or "2 TVs" etc.</summary>
    public string TargetLabel => RuleTargets.TargetLabel(Model.TvIds, _tvs());

    /// <summary>
    /// The picker only appears with at least two TVs that can be dimmed. The one exception: a
    /// schedule that names TVs none of which can be dimmed right now (say a Google TV that lost
    /// brightness control) keeps it, or it would silently do nothing with no way to see why.
    /// </summary>
    public Visibility TvPickerVisibility
    {
        get
        {
            var dimmable = _tvs().Where(t => t.SupportsBrightness).Select(t => t.Id).ToList();
            var stranded = Model.TvIds.Count > 0 && !Model.TvIds.Any(dimmable.Contains);
            return dimmable.Count >= 2 || stranded ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>The TVs the picker offers: those whose brightness can be set right now.</summary>
    public IReadOnlyList<TvInfo> PickerTvs => _tvs().Where(t => t.SupportsBrightness).ToList();

    public bool TargetsAll => Model.TvIds.Count == 0;

    public bool TargetsTv(string tvId) => Model.TvIds.Contains(tvId);

    /// <summary>One click in the picker: a TV's id toggles that TV, null picks all TVs.</summary>
    public void ToggleTarget(string? tvId)
    {
        Model.TvIds = RuleTargets.ToggleTarget(Model.TvIds, tvId);
        _persist();
        RefreshTargets();
    }

    /// <summary>A TV was removed: drops it from this schedule (switching the schedule off if that
    /// leaves it naming no TV). Returns true when the schedule changed.</summary>
    public bool ForgetTv(string tvId, IReadOnlyCollection<string> remainingIds)
    {
        if (!RuleTargets.ForgetTv(Model, tvId, remainingIds)) return false;
        Enabled = Model.Enabled;
        RefreshTargets();
        return true;
    }

    /// <summary>Re-reads the picker's label and visibility after the TV list or a TV's abilities changed.</summary>
    public void RefreshTargets()
    {
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(TvPickerVisibility));
    }
}
