// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace TVControlForWin.Services;

/// <summary>Resolves whether Windows is set to 12- or 24-hour time, for the TimePicker.</summary>
public static class ClockHelper
{
    /// <summary>"24HourClock" or "12HourClock", matching the user's Windows time format.</summary>
    public static string ClockIdentifier =>
        CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H')
            ? "24HourClock"
            : "12HourClock";
}
