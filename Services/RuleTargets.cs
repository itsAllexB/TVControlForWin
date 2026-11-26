// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.Models;

namespace TVControlForWin.Services;

/// <summary>Outcome of a brightness hotkey: a TV was adjusted, no paired TV was under the cursor
/// (prompt to pair), or there was nothing to adjust.</summary>
public enum HotkeyTargetOutcome { Adjusted, Unpaired, None }

/// <summary>Something the PC did that the power rules (Power page) can react to.</summary>
public enum PcPowerEvent { AppStart, Resume, Suspend, SessionEnding, DisplayOff, DisplayOn }

/// <summary>What the power rules ask of one TV for one <see cref="PcPowerEvent"/>.</summary>
public enum TvPowerAction { None, PowerOn, TurnOff, ScreenOff }

/// <summary>How the brightness hotkeys pick their TV (see <see cref="AppSettings.HotkeyTarget"/>).</summary>
public enum HotkeyMode { Cursor, All, Tv }

/// <summary>The facts about one TV that rule targeting needs, free of any UI types.</summary>
public sealed record TvInfo(string Id, string Name, bool SupportsBrightness, string PairedScreenId);

/// <summary>What one hotkey press should do: which TVs to nudge, and whose new value the overlay shows.</summary>
public sealed record HotkeyPlan(HotkeyTargetOutcome Outcome, IReadOnlyList<string> TvIds, string? ShownTvId)
{
    public static readonly HotkeyPlan Nothing = new(HotkeyTargetOutcome.None, Array.Empty<string>(), null);
    public static readonly HotkeyPlan Unpaired = new(HotkeyTargetOutcome.Unpaired, Array.Empty<string>(), null);

    public static HotkeyPlan Adjust(IReadOnlyList<string> tvIds, string shownTvId) =>
        new(HotkeyTargetOutcome.Adjusted, tvIds, shownTvId);
}

/// <summary>
/// Which TVs each rule applies to: schedules, power rules and the brightness hotkeys can each
/// work for all TVs or for chosen ones. Pure logic on models and plain values, so it can be
/// exercised without the UI. Whether a TV can be dimmed can change while the app runs, so
/// callers pass it in fresh every time rather than this class remembering it.
/// </summary>
public static class RuleTargets
{
    /// <summary><see cref="AppSettings.HotkeyTarget"/> value meaning "every TV that can be dimmed".</summary>
    public const string AllTvs = "all";

    // ----- Schedules -----

    /// <summary>
    /// True when <paramref name="schedule"/> applies to the TV with <paramref name="tvId"/>. An empty
    /// list means all TVs. Ids of TVs that no longer exist simply never match, so a schedule naming
    /// only removed TVs targets nothing rather than widening to all of them.
    /// </summary>
    public static bool Targets(BrightnessSchedule schedule, string tvId) =>
        schedule.TvIds.Count == 0 || schedule.TvIds.Contains(tvId);

    /// <summary>The enabled schedules that apply to one TV, in list order.</summary>
    public static List<BrightnessSchedule> EnabledFor(IEnumerable<BrightnessSchedule> schedules, string tvId) =>
        schedules.Where(s => s.Enabled && Targets(s, tvId)).ToList();

    /// <summary>
    /// The schedule in effect at <paramref name="nowMinutes"/> (minutes since midnight) among
    /// <paramref name="schedules"/>: the latest one at or before now, wrapping to the latest of the
    /// day (last night's) when none has passed yet today. Two at the same time: the later one in
    /// the list wins, as it would when they fire together. Null for an empty list.
    /// </summary>
    public static BrightnessSchedule? InEffect(IReadOnlyList<BrightnessSchedule> schedules, int nowMinutes)
    {
        BrightnessSchedule? passed = null, latest = null;
        foreach (var s in schedules)
        {
            var at = Minutes(s);
            if (latest is null || at >= Minutes(latest)) latest = s;
            if (at <= nowMinutes && (passed is null || at >= Minutes(passed))) passed = s;
        }
        return passed ?? latest;
    }

    /// <summary>The brightness the schedules want for one TV right now, or null if none target it.</summary>
    public static int? ScheduledBrightnessFor(IEnumerable<BrightnessSchedule> schedules, string tvId, DateTime now) =>
        InEffect(EnabledFor(schedules, tvId), now.Hour * 60 + now.Minute)?.Brightness;

    /// <summary>
    /// True when at least one enabled schedule's daily time falls inside (from, to]: it would have
    /// fired in that window had the PC been awake.
    /// </summary>
    public static bool AnyFiresBetween(IEnumerable<BrightnessSchedule> schedules, DateTime from, DateTime to)
    {
        var enabled = schedules.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return false;
        if (to - from >= TimeSpan.FromDays(1)) return true; // a day or more passed: every schedule did

        foreach (var s in enabled)
        {
            // First occurrence of this schedule's time strictly after `from`.
            var occurrence = from.Date.AddHours(s.Hour).AddMinutes(s.Minute);
            if (occurrence <= from) occurrence = occurrence.AddDays(1);
            if (occurrence <= to) return true;
        }
        return false;
    }

    /// <summary>
    /// After the app slept through (from, to]: the brightness one TV should catch up to, or null
    /// when no schedule targeting that TV fired in the gap (so a TV is never touched because of
    /// another TV's schedule).
    /// </summary>
    public static int? CatchUpBrightnessFor(IEnumerable<BrightnessSchedule> schedules, string tvId, DateTime from, DateTime to)
    {
        var mine = EnabledFor(schedules, tvId);
        if (!AnyFiresBetween(mine, from, to)) return null;
        return InEffect(mine, to.Hour * 60 + to.Minute)?.Brightness;
    }

    /// <summary>
    /// A schedule's TV list after one click in its TV picker. <paramref name="tvId"/> null is the
    /// "All TVs" entry. A TV is added or removed; un-ticking the last one goes back to all TVs.
    /// Returns a new list, so a reader on another thread never sees one half-changed.
    /// </summary>
    public static List<string> ToggleTarget(IReadOnlyList<string> current, string? tvId)
    {
        if (tvId is null) return new List<string>();
        var next = current.ToList();
        if (next.RemoveAll(id => id == tvId) == 0) next.Add(tvId);
        return next;
    }

    /// <summary>The picker's label: "All TVs", the TV's name for one, "2 TVs" for several.</summary>
    public static string TargetLabel(IReadOnlyList<string> tvIds, IReadOnlyList<TvInfo> tvs)
    {
        if (tvIds.Count == 0) return "All TVs";
        var chosen = tvs.Where(t => tvIds.Contains(t.Id)).ToList();
        return chosen.Count switch
        {
            0 => "No TVs",
            1 => chosen[0].Name,
            var n => $"{n} TVs",
        };
    }

    /// <summary>
    /// Takes a removed TV out of a schedule's list (dropping ids of TVs already gone with it).
    /// A schedule that named specific TVs and is left naming none is switched off rather than
    /// left to widen to all TVs. Returns true when the schedule changed.
    /// </summary>
    public static bool ForgetTv(BrightnessSchedule schedule, string removedId, IReadOnlyCollection<string> remainingIds)
    {
        if (!schedule.TvIds.Contains(removedId)) return false;
        var kept = schedule.TvIds.Where(id => id != removedId && remainingIds.Contains(id)).ToList();
        schedule.TvIds = kept;
        if (kept.Count == 0) schedule.Enabled = false;
        return true;
    }

    private static int Minutes(BrightnessSchedule s) => s.Hour * 60 + s.Minute;

    // ----- Power rules -----

    /// <summary>A TV's own power rules when it has them, otherwise the shared ones.</summary>
    public static PowerSettings EffectivePower(PowerSettings? own, PowerSettings shared) => own ?? shared;

    /// <summary>What one set of power rules does for one PC power event.</summary>
    public static TvPowerAction ActionFor(PcPowerEvent e, PowerSettings rules) => e switch
    {
        PcPowerEvent.AppStart => rules.OnAppStart ? TvPowerAction.PowerOn : TvPowerAction.None,
        PcPowerEvent.Resume => rules.OnResume ? TvPowerAction.PowerOn : TvPowerAction.None,
        PcPowerEvent.Suspend => rules.OffOnSleep ? TvPowerAction.TurnOff : TvPowerAction.None,
        PcPowerEvent.SessionEnding => rules.OffOnShutdown ? TvPowerAction.TurnOff : TvPowerAction.None,
        PcPowerEvent.DisplayOff => !rules.FollowDisplayOff ? TvPowerAction.None
            : rules.DisplayOffAction == 1 ? TvPowerAction.TurnOff : TvPowerAction.ScreenOff,
        PcPowerEvent.DisplayOn => rules.FollowDisplayOn ? TvPowerAction.PowerOn : TvPowerAction.None,
        _ => TvPowerAction.None,
    };

    /// <summary>
    /// For one PC power event, the TVs that have something to do and what, each judged by its
    /// own rules or, when it has none (<paramref name="ownRules"/> returns null), the shared ones.
    /// </summary>
    public static List<(T Tv, TvPowerAction Action)> PlanPowerEvent<T>(
        PcPowerEvent e, IEnumerable<T> tvs, Func<T, PowerSettings?> ownRules, PowerSettings shared)
    {
        var plan = new List<(T, TvPowerAction)>();
        foreach (var tv in tvs)
        {
            var action = ActionFor(e, EffectivePower(ownRules(tv), shared));
            if (action != TvPowerAction.None) plan.Add((tv, action));
        }
        return plan;
    }

    // ----- Brightness hotkeys -----

    /// <summary>
    /// Reads <see cref="AppSettings.HotkeyTarget"/>: "" is the TV under the cursor, "all" every
    /// dimmable TV, anything else a TV's id; an id that no longer exists falls back to the cursor.
    /// </summary>
    public static (HotkeyMode Mode, string? TvId) ResolveHotkeyTarget(string? setting, IEnumerable<string> tvIds)
    {
        if (string.IsNullOrEmpty(setting)) return (HotkeyMode.Cursor, null);
        if (setting == AllTvs) return (HotkeyMode.All, null);
        return tvIds.Contains(setting) ? (HotkeyMode.Tv, setting) : (HotkeyMode.Cursor, null);
    }

    /// <summary>
    /// Works out what a brightness hotkey press changes. <paramref name="cursorScreen"/> returns the
    /// id of the display under the mouse cursor; it's only asked when there's more than one TV.
    /// <list type="bullet">
    /// <item>Cursor mode: only TVs whose brightness can be set count. With a single such TV it
    /// always targets that one, unless the cursor is on a screen paired to a TV without brightness
    /// control, where the hotkey does nothing. With several and the cursor on a screen no TV is
    /// paired to, nothing changes and the outcome is <see cref="HotkeyTargetOutcome.Unpaired"/>.</item>
    /// <item>All: every dimmable TV moves; the overlay shows the one under the cursor if it's
    /// dimmable, else the first dimmable TV.</item>
    /// <item>One TV: that TV, and nothing when it can't be dimmed right now.</item>
    /// </list>
    /// </summary>
    public static HotkeyPlan PlanHotkey(string? target, IReadOnlyList<TvInfo> tvs, Func<string?> cursorScreen)
    {
        var dimmable = tvs.Where(t => t.SupportsBrightness).ToList();
        if (dimmable.Count == 0) return HotkeyPlan.Nothing;

        var (mode, tvId) = ResolveHotkeyTarget(target, tvs.Select(t => t.Id));
        switch (mode)
        {
            case HotkeyMode.Tv:
                var chosen = tvs.First(t => t.Id == tvId);
                return chosen.SupportsBrightness ? HotkeyPlan.Adjust(new[] { chosen.Id }, chosen.Id) : HotkeyPlan.Nothing;

            case HotkeyMode.All:
                var under = TvUnderCursor(tvs, cursorScreen);
                var shown = under is { SupportsBrightness: true } ? under : dimmable[0];
                return HotkeyPlan.Adjust(dimmable.Select(t => t.Id).ToList(), shown.Id);

            default:
                var underCursor = TvUnderCursor(tvs, cursorScreen);
                if (underCursor is { SupportsBrightness: false }) return HotkeyPlan.Nothing;
                var tv = dimmable.Count == 1 ? dimmable[0] : underCursor;
                return tv is null ? HotkeyPlan.Unpaired : HotkeyPlan.Adjust(new[] { tv.Id }, tv.Id);
        }
    }

    // With one TV in total there's nothing to tell apart, so the cursor isn't consulted.
    private static TvInfo? TvUnderCursor(IReadOnlyList<TvInfo> tvs, Func<string?> cursorScreen)
    {
        var screen = tvs.Count > 1 ? cursorScreen() : null;
        return string.IsNullOrEmpty(screen) ? null : tvs.FirstOrDefault(t => t.PairedScreenId == screen);
    }
}
