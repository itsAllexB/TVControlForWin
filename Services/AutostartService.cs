// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Win32;

namespace TVControlForWin.Services;

/// <summary>Opt-in "launch at sign-in" via the per-user HKCU Run key.</summary>
public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TVControlForWin";

    // The Run entry written while the app was called LGforWin; it points at the old exe.
    private const string LegacyValueName = "LGforWin";

    /// <summary>Command-line argument the autostart entry passes, so the app can tell an
    /// at-sign-in launch from a manual one (manual launches always open the window).</summary>
    public const string AutostartArg = "--autostart";

    public static void Apply(bool enabled)
    {
        // An instance running on its own data folder is a side-by-side trial; it must not
        // take over (or remove) the real installation's sign-in entry.
        if (AppPaths.OverrideDir is not null) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\" {AutostartArg}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* registry access denied — non-fatal */ }
    }
}
