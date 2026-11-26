// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;

namespace TVControlForWin.Services;

/// <summary>Facts about the app itself: its name, version, and where its releases are published.</summary>
public static class AppInfo
{
    public const string Name = "TVControlForWin";

    /// <summary>The running version, three parts (the .csproj's Version).</summary>
    public static Version Version { get; } = ThreeParts(Assembly.GetExecutingAssembly().GetName().Version);

    // The GitHub repository releases are published to.
    private const string Repository = "itsAllexB/TVControlForWin";

    /// <summary>GitHub's description of the newest published release (not drafts or pre-releases).</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>The page a person downloads the newest release from.</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases/latest";

    /// <summary>The installer's AppId (installer.iss): names the app's entry under Windows' Uninstall key.</summary>
    public const string InstallerAppId = "{8E6F2A41-2C7B-4D19-9F3E-1B7A5C9D4E20}";

    /// <summary>Launch argument: start hidden, as a tray icon only.</summary>
    public const string TrayArg = "--tray";

    /// <summary>Launch argument: the app is coming back after installing an update, not being
    /// started by the user or by Windows.</summary>
    public const string UpdatedArg = "--updated";

    /// <summary>A version with exactly three parts, so "3.1" and "3.1.0" compare as equal.</summary>
    public static Version ThreeParts(Version? version) => version is null
        ? new Version(0, 0, 0)
        : new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
}
