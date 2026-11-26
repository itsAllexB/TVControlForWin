// SPDX-License-Identifier: AGPL-3.0-or-later
namespace TVControlForWin.Services;

/// <summary>Where the app keeps its files (TVs, settings, log).</summary>
public static class AppPaths
{
    /// <summary>
    /// Environment variable that points the app at a different data folder. An instance started
    /// with it set also runs alongside the normal one instead of handing over to it — for
    /// trying a build against throwaway data without touching the real TVs and settings.
    /// </summary>
    public const string DataDirVariable = "TVCONTROL_DATA_DIR";

    // The app was called LGforWin up to v2.1; its data folder is carried over on first run.
    private const string LegacyFolder = "LGforWin";
    private const string Folder = "TVControlForWin";

    private static readonly Lazy<string> Resolved = new(Resolve);

    /// <summary>The data folder. Created (and migrated from the old name) on first use.</summary>
    public static string DataDir => Resolved.Value;

    /// <summary>The override folder when <see cref="DataDirVariable"/> is set, otherwise null.</summary>
    public static string? OverrideDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(DataDirVariable);
            return string.IsNullOrWhiteSpace(dir) ? null : Path.GetFullPath(dir);
        }
    }

    private static string Resolve()
    {
        if (OverrideDir is { } custom)
        {
            Directory.CreateDirectory(custom);
            return custom;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dir = Path.Combine(local, Folder);
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            CopyLegacyData(Path.Combine(local, LegacyFolder), dir);
        }
        return dir;
    }

    // Copied rather than moved: the old folder may be open in a still-running old version, and
    // leaving it lets that version keep working if someone goes back to it.
    private static void CopyLegacyData(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            foreach (var name in new[] { "devices.json", "settings.json" })
            {
                var source = Path.Combine(from, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(to, name), overwrite: false);
            }
        }
        catch { /* best effort: worst case the app starts with no TVs and they're re-added */ }
    }
}
