// SPDX-License-Identifier: AGPL-3.0-or-later
namespace TVControlForWin.Models;

/// <summary>
/// A time-of-day rule: at <see cref="Hour"/>:<see cref="Minute"/> set the OLED Light of the TVs it
/// targets to <see cref="Brightness"/>. Fires daily. Up to 10 are allowed.
/// </summary>
public sealed class BrightnessSchedule
{
    public int Hour { get; set; } = 8;
    public int Minute { get; set; }
    public int Brightness { get; set; } = 50;
    public bool Enabled { get; set; } = true;

    private List<string> _tvIds = new();

    /// <summary>
    /// Ids (<see cref="TvDevice.Id"/>) of the TVs this schedule changes. Empty means all TVs, which
    /// is also what files written before this existed load as. Replaced as a whole on every edit
    /// (never changed in place), because the schedule timer reads it from another thread.
    /// </summary>
    public List<string> TvIds
    {
        get => _tvIds;
        set => _tvIds = value ?? new List<string>();
    }
}
