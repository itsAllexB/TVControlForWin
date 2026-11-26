// SPDX-License-Identifier: AGPL-3.0-or-later
using TVControlForWin.ViewModels;
using Microsoft.UI.Dispatching;

namespace TVControlForWin.Services;

/// <summary>
/// Polls once per ~20s and applies any enabled schedule whose time matches the current
/// minute, exactly once per occurrence, to the TVs that schedule targets.
///
/// The minute-match means a schedule whose time passes while the timer isn't ticking —
/// PC asleep or hibernating — would silently never fire. So each tick also watches for
/// a gap since the previous tick, and sets each TV to the schedule that should be in effect
/// for it after one. (If a TV is still off at that moment, WebOsController holds the value
/// and delivers it when the TV comes back.)
/// </summary>
public sealed class ScheduleService : IDisposable
{
    // Ticks are 20s apart; anything much longer means the process was suspended.
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(90);

    private readonly MainViewModel _vm;
    private readonly DispatcherQueue _dispatcher;
    private Timer? _timer;

    private string _minuteKey = "";
    private readonly HashSet<int> _firedThisMinute = new();
    private DateTime _lastTick = DateTime.MinValue;

    /// <summary>Raised on the UI thread after a schedule changed brightness, with the TVs it changed
    /// (never empty; their new values are already set).</summary>
    public event Action<IReadOnlyList<TvViewModel>>? ScheduleApplied;

    public ScheduleService(MainViewModel vm, DispatcherQueue dispatcher)
    {
        _vm = vm;
        _dispatcher = dispatcher;
    }

    public void Start() => _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(20));

    private void Tick()
    {
        var now = DateTime.Now;

        var previousTick = _lastTick;
        _lastTick = now;
        if (previousTick != DateTime.MinValue && now - previousTick > GapThreshold
            && RuleTargets.AnyFiresBetween(_vm.Settings.Schedules, previousTick, now))
        {
            Log.Write($"schedule: catching up after a {(int)(now - previousTick).TotalMinutes} min gap (PC was asleep)");
            _dispatcher.TryEnqueue(() => Report(_vm.CatchUpSchedules(previousTick, now)));
        }

        var minute = now.ToString("yyyyMMddHHmm");
        if (minute != _minuteKey)
        {
            _minuteKey = minute;
            _firedThisMinute.Clear();
        }

        var schedules = _vm.Settings.Schedules;
        for (var i = 0; i < schedules.Count; i++)
        {
            var s = schedules[i];
            if (s.Enabled && s.Hour == now.Hour && s.Minute == now.Minute && _firedThisMinute.Add(i))
            {
                _dispatcher.TryEnqueue(() => Report(_vm.ApplySchedule(s)));
                var targets = s.TvIds.Count == 0 ? "all TVs" : $"{s.TvIds.Count} chosen TV(s)";
                Log.Write($"schedule {s.Hour:00}:{s.Minute:00} fired -> {s.Brightness}% ({targets})");
            }
        }
    }

    private void Report(IReadOnlyList<TvViewModel> changed)
    {
        if (changed.Count > 0) ScheduleApplied?.Invoke(changed);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
