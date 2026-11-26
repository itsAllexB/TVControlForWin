// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace TVControlForWin.Services;

/// <summary>
/// Picture brightness (the "Brightness" slider, 0–100, i.e. the backlight) on TCL Google TVs,
/// through ADB.
///
/// Android's own display-brightness setting does nothing on these sets; the panel is driven by
/// TCL's picture pipeline, reached through the system service "tcl_tv_display"
/// (com.tcl.tv.display.ITvDisplayManagerService). The TV's settings app calls its
/// setBacklight(applyType, value, actType) / getBacklight() — binder transactions 100 and 101 —
/// and `service call` lets the ADB shell make the same calls.
///
/// The transaction numbers are positions in TCL's interface definition, so another firmware
/// generation could number them differently. Hence the checks: the service has to exist under
/// that interface name, the value read has to be a plausible 0–100, and the first write of a
/// connection is read back (see <see cref="SetAsync"/>). Worked out and tested on a 65C765
/// (Android 14).
/// </summary>
internal static partial class AdbTclBacklight
{
    private const string Service = "tcl_tv_display";
    private const string Interface = "com.tcl.tv.display.ITvDisplayManagerService";
    private const int SetBacklight = 100;
    private const int GetBacklight = 101;

    // What the TV's own settings slider sends. applyType 1 + actType 2 is the combination that
    // drives the panel and saves; 0 + 1 (what the service then calls internally) only stores
    // the number.
    private const int ApplyType = 1;
    private const int ActType = 2;

    /// <summary>TVs worth offering this on, judged by the vendor name from the remote handshake.</summary>
    public static bool MightSupport(string vendor) =>
        vendor.Contains("TCL", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the TV has the service this relies on, under the expected interface.</summary>
    public static async Task<bool> IsAvailableAsync(AdbClient adb, CancellationToken ct)
    {
        var listing = await adb.ShellAsync($"service list | grep {Service}", ct).ConfigureAwait(false);
        return listing.Contains(Interface, StringComparison.Ordinal)
            && await GetAsync(adb, ct).ConfigureAwait(false) is not null;
    }

    /// <summary>The current brightness, or null if the TV's answer doesn't look like one.</summary>
    public static async Task<int?> GetAsync(AdbClient adb, CancellationToken ct)
    {
        var reply = await adb.ShellAsync($"service call {Service} {GetBacklight}", ct).ConfigureAwait(false);
        return ParseInt(reply) is int value and >= 0 and <= 100 ? value : null;
    }

    public enum SetResult
    {
        /// <summary>The TV took the value (and, when asked to verify, read it back).</summary>
        Applied,

        /// <summary>The service turned the call down. Can be passing: it also happens while the
        /// TV is still waking up.</summary>
        Refused,

        /// <summary>The call was accepted but the value read back is a different one: these
        /// aren't the calls we think they are on this firmware, so stop writing.</summary>
        NotApplied,
    }

    /// <summary>
    /// Sets the brightness. With <paramref name="verify"/> it also confirms the value took by
    /// reading it back.
    /// </summary>
    public static async Task<SetResult> SetAsync(AdbClient adb, int value, bool verify, CancellationToken ct)
    {
        value = Math.Clamp(value, 0, 100);
        var reply = await adb.ShellAsync(
            $"service call {Service} {SetBacklight} i32 {ApplyType} i32 {value} i32 {ActType}", ct).ConfigureAwait(false);
        if (ParseInt(reply) != 1) return SetResult.Refused;
        if (!verify) return SetResult.Applied;

        // The service applies the value a moment after the call returns (~90 ms measured), so
        // an immediate read can still see the old one. Give it a few tries.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(120, ct).ConfigureAwait(false);
            if (await GetAsync(adb, ct).ConfigureAwait(false) == value) return SetResult.Applied;
        }
        return SetResult.NotApplied;
    }

    // `service call` prints the reply parcel as hex words:
    //   Result: Parcel(00000000 0000003c   '....<...')
    // The first word is the exception code (0 = none), the second the return value.
    private static int? ParseInt(string reply)
    {
        var match = ParcelPattern().Match(reply);
        if (!match.Success || match.Groups[1].Value != "00000000") return null;
        return int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"Parcel\(\s*([0-9a-f]{8})\s+([0-9a-f]{8})")]
    private static partial Regex ParcelPattern();
}
