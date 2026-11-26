// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TVControlForWin.Services;

/// <summary>How this copy of the app got onto the PC, which decides how it can be updated.</summary>
public enum InstallKind
{
    /// <summary>Unzipped somewhere (or a development build): there's no installer to run, so an
    /// update means downloading the new version by hand.</summary>
    Portable,

    /// <summary>Installed for this user only: the installer can run without asking for permission.</summary>
    PerUser,

    /// <summary>Installed for all users: the installer needs administrator permission (a UAC prompt).</summary>
    PerMachine,
}

/// <summary>A published release, as far as updating is concerned.</summary>
/// <param name="PageUrl">The release's page, for downloading by hand.</param>
/// <param name="InstallerUrl">Where its installer is, or null when the release has none this
/// app can verify (then <paramref name="PageUrl"/> is the only way).</param>
public sealed record AppUpdate(
    Version Version, string PageUrl, string? InstallerUrl, string? InstallerName, long InstallerSize, string? InstallerSha256);

/// <summary>
/// Finds, downloads and starts app updates. Releases are published on GitHub; "the latest
/// release" there is the newest one that isn't a draft or pre-release.
///
/// What gets run is an installer downloaded from the internet, so the download is held to what
/// GitHub says about it: it has to come from the repository's release downloads over HTTPS, and
/// match the size and SHA-256 GitHub lists for the file. A release without a listed hash is
/// treated as having no installer.
/// </summary>
public sealed partial class UpdateService : IDisposable
{
    private const long MaxInstallerBytes = 400L * 1024 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly Uri _feed;
    private readonly string _downloadDir;
    private readonly Func<Uri, bool> _isTrustedDownload;

    /// <param name="feed">Where to ask for the latest release. Default: the app's GitHub repository.</param>
    /// <param name="downloadDir">Where installers are downloaded to.</param>
    /// <param name="isTrustedDownload">Which installer addresses are acceptable. Default: GitHub
    /// release downloads over HTTPS.</param>
    public UpdateService(Uri? feed = null, string? downloadDir = null, Func<Uri, bool>? isTrustedDownload = null)
    {
        _feed = feed ?? new Uri(AppInfo.LatestReleaseApi);
        _downloadDir = downloadDir ?? Path.Combine(AppPaths.DataDir, "updates");
        _isTrustedDownload = isTrustedDownload ?? IsGitHubReleaseDownload;

        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // each call sets its own limit
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// The updater for this copy of the app, and how the copy was installed.
    ///
    /// A trial run (one started on its own data folder, see <see cref="AppPaths.DataDirVariable"/>)
    /// can name a stand-in feed and install kind in TVCONTROL_UPDATE_FEED and
    /// TVCONTROL_INSTALL_KIND, to rehearse an update without publishing a release.
    /// </summary>
    public static (UpdateService Service, InstallKind Kind, bool IsRehearsal) ForThisCopy()
    {
        if (AppPaths.OverrideDir is not null
            && Uri.TryCreate(Environment.GetEnvironmentVariable("TVCONTROL_UPDATE_FEED"), UriKind.Absolute, out var feed))
        {
            Enum.TryParse(Environment.GetEnvironmentVariable("TVCONTROL_INSTALL_KIND"), ignoreCase: true, out InstallKind kind);
            return (new UpdateService(feed, isTrustedDownload: url => url.Host == feed.Host && url.Port == feed.Port), kind, true);
        }
        return (new UpdateService(), DetectInstallKind(), false);
    }

    // ----- Checking -----

    /// <summary>The latest published release, or null when there isn't one that can be read as a
    /// version. Throws when the answer can't be had (offline, GitHub unreachable or refusing).</summary>
    public async Task<AppUpdate?> GetLatestAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var response = await _http.GetAsync(_feed, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // nothing published yet
        response.EnsureSuccessStatusCode();

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        return Parse(json.RootElement);
    }

    /// <summary>Reads GitHub's release description. Null for drafts, pre-releases and tags that
    /// aren't a version.</summary>
    public static AppUpdate? Parse(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (Flag(release, "draft") || Flag(release, "prerelease")) return null;
        if (ParseVersion(Text(release, "tag_name")) is not { } version) return null;

        // The page is opened in the browser, so only ever a GitHub web address.
        var page = Text(release, "html_url");
        if (!Uri.TryCreate(page, UriKind.Absolute, out var pageUri) || !IsGitHubPage(pageUri))
            page = AppInfo.ReleasesPage;

        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name is null || !InstallerNamePattern().IsMatch(name)) continue;

                var digest = Text(asset, "digest");
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) break;
                if (size is <= 0 or > MaxInstallerBytes) break;

                return new AppUpdate(version, page!, Text(asset, "browser_download_url"), name, size, digest[7..].Trim());
            }
        }
        return new AppUpdate(version, page!, null, null, 0, null);
    }

    /// <summary>"v3.1.0" or "3.1" as a three-part version; null if it isn't one.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        var end = text.IndexOfAny(new[] { '-', '+' }); // drop "-beta", "+build"
        if (end >= 0) text = text[..end];
        return Version.TryParse(text, out var version) ? AppInfo.ThreeParts(version) : null;
    }

    private static bool Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsGitHubPage(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsGitHubReleaseDownload(Uri url) =>
        IsGitHubPage(url) && url.AbsolutePath.Contains("/releases/download/", StringComparison.Ordinal);

    // The installer build.ps1 -Package produces. Nothing else in a release is ever run.
    [GeneratedRegex(@"^TVControlForWin-\d+(\.\d+){1,3}-setup\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstallerNamePattern();

    // ----- Downloading -----

    /// <summary>
    /// Downloads the release's installer and checks it against the size and hash GitHub lists
    /// for it. Returns the path of the verified file. <paramref name="progress"/> gets 0..1.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(AppUpdate update, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (update.InstallerUrl is null || update.InstallerName is null || update.InstallerSha256 is null)
            throw new InvalidOperationException("This release has no installer.");
        if (!Uri.TryCreate(update.InstallerUrl, UriKind.Absolute, out var url) || !_isTrustedDownload(url))
            throw new InvalidOperationException("The installer isn't where a release's files belong.");

        Directory.CreateDirectory(_downloadDir);
        var target = Path.Combine(_downloadDir, update.InstallerName);
        if (await MatchesAsync(target, update, ct).ConfigureAwait(false))
        {
            progress?.Report(1);
            return target; // already downloaded, e.g. before a restart
        }

        // Only ever one installer on disk: whatever was here is for an older update, or broken.
        foreach (var old in Directory.EnumerateFiles(_downloadDir))
        {
            try { File.Delete(old); } catch { /* in use or protected: leave it */ }
        }

        var part = target + ".part";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > update.InstallerSize)
                        throw new InvalidDataException("The download is bigger than the release says.");
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    progress?.Report((double)total / update.InstallerSize);
                }
            }

            if (!await MatchesAsync(part, update, ct).ConfigureAwait(false))
                throw new InvalidDataException("The download doesn't match the release.");
            File.Move(part, target, overwrite: true);
            return target;
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
        }
    }

    /// <summary>Clears out downloads that have served their purpose: installers for this version
    /// or older (the update went in), and anything half-downloaded.</summary>
    public void DeleteUsedInstallers()
    {
        try
        {
            if (!Directory.Exists(_downloadDir)) return;
            foreach (var file in Directory.EnumerateFiles(_downloadDir))
            {
                var name = Path.GetFileName(file);
                var version = InstallerNamePattern().IsMatch(name) ? ParseVersion(name.Split('-')[1]) : null;
                if (version is not null && version > AppInfo.Version) continue; // still waiting to go in
                try { File.Delete(file); } catch { /* in use or protected: next time */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task<bool> MatchesAsync(string path, AppUpdate update, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != update.InstallerSize) return false;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            var hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            return Convert.ToHexString(hash).Equals(update.InstallerSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ----- Installing -----

    /// <summary>
    /// Where the installer put this copy, judged by the uninstall entry it registers: a copy
    /// running from the folder named there is an installed one.
    /// </summary>
    public static InstallKind DetectInstallKind()
    {
        var here = TrimPath(AppContext.BaseDirectory);
        if (string.Equals(InstalledAt(RegistryHive.CurrentUser), here, StringComparison.OrdinalIgnoreCase))
            return InstallKind.PerUser;
        if (string.Equals(InstalledAt(RegistryHive.LocalMachine), here, StringComparison.OrdinalIgnoreCase))
            return InstallKind.PerMachine;
        return InstallKind.Portable;
    }

    private static string? InstalledAt(RegistryHive hive)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppInfo.InstallerAppId}_is1");
            return key?.GetValue("Inno Setup: App Path") is string path ? TrimPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TrimPath(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The command that installs an update and brings the app back afterwards. The caller starts
    /// it and then exits the app, so the installer finds nothing in use.
    ///
    /// It's one hidden command prompt that runs the installer, waits for it, and starts the app
    /// again whatever the installer's outcome. So if the installer fails, or its permission
    /// prompt is declined, the version that was running comes back and the TVs aren't left
    /// without their rules.
    /// </summary>
    /// <param name="quiet">True for an update nobody is watching: no installer window, and the
    /// app comes back as a tray icon. False shows the installer's progress and reopens the window.</param>
    /// <exception cref="NotSupportedException">A path has a character the command prompt would
    /// misread. The caller can start the installer by itself instead.</exception>
    public static ProcessStartInfo InstallCommand(string installer, string appExe, bool quiet)
    {
        foreach (var path in new[] { installer, appExe })
        {
            if (path.IndexOfAny(new[] { '"', '%' }) >= 0)
                throw new NotSupportedException("A path contains a character that can't be passed on safely.");
        }

        // Inno Setup: /SILENT shows only progress, /VERYSILENT nothing at all.
        var setupArgs = quiet ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" : "/SILENT /NORESTART";
        var appArgs = quiet ? $"{AppInfo.UpdatedArg} {AppInfo.TrayArg}" : AppInfo.UpdatedArg;

        return new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            // /d: no AutoRun commands, /v:off: "!" stays literal, /s + outer quotes: the rest is taken as written.
            Arguments = $"/d /v:off /s /c \"start \"\" /wait \"{installer}\" {setupArgs} & start \"\" \"{appExe}\" {appArgs}\"",
            WorkingDirectory = Environment.SystemDirectory, // not the app's folder: it's about to be replaced
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    public void Dispose() => _http.Dispose();
}
