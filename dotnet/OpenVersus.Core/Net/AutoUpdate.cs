using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVersus.Native;

namespace OpenVersus.Net;

/// <summary>
/// Asks the server for the latest client and installs it: the running .asi is renamed to a
/// .bak, the download takes its place, and the game is closed so the next launch loads it. The
/// download is the .asi itself, or a release zip when the release has no .asi asset (the server
/// falls back to it), in which case only the .asi inside is installed. A download is installed only
/// when it matches the SHA-256 the release publishes beside it (<c>&lt;download_url&gt;.sha256</c>).
/// A release without one is refused like a mismatch: every release this client could install comes
/// from the pipeline that publishes checksums (older ones are refused as not newer), so a missing
/// file means a hand-made release or something wrong. Any failure to check waits for the next launch.
/// The C++ did the version check over a raw socket without TLS, which cannot reach an https
/// server; this goes through the transport, so the check works against production.
/// </summary>
public sealed class AutoUpdate(string serverUrl, string pluginPath, string installDirectory, IHttpTransport http, IHttpTransport download, ILogger log, Action beforeExit)
{
    /// <summary>The version response, or null when the text is not that.</summary>
    public static VersionInfo? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, OvsJson.Default.VersionInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// After five seconds, asks the server for the latest version and, when the offer is newer than
    /// this client, installs it and closes the game. Every failure is logged and leaves the running
    /// client as it is.
    /// </summary>
    public void Run()
    {
        Thread.Sleep(5000); // let the game settle before checking
        if (string.IsNullOrEmpty(serverUrl))
        {
            return;
        }

        var url = Urls.Join(serverUrl, $"/ovs/client-version?v={Uri.EscapeDataString(OvsVersion.Current)}");
        if (url == null)
        {
            log.Warn("[AutoUpdate] Failed to parse server URL");
            return;
        }

        var result = http.Get(url, TimeSpan.FromSeconds(5));
        if (!result.Ok)
        {
            log.Warn($"[AutoUpdate] Version check failed ({result.Error ?? $"HTTP {result.Status}"})");
            return;
        }
        var info = Parse(result.Text);
        if (info == null)
        {
            log.Warn("[AutoUpdate] Version check returned something that is not JSON");
            return;
        }
        log.Debug($"[AutoUpdate] Current: {OvsVersion.Current}, Latest: {info.LatestVersion}");
        if (info.IsLatest)
        {
            log.Info("[AutoUpdate] Already up to date");
            return;
        }
        if (string.IsNullOrEmpty(info.LatestVersion) || string.IsNullOrEmpty(info.DownloadUrl))
        {
            log.Warn("[AutoUpdate] Missing version/URL in response, skipping");
            return;
        }

        if (!IsNewer(info.LatestVersion, OvsVersion.Current))
        {
            log.Info($"[AutoUpdate] Server offers {info.LatestVersion} and this is {OvsVersion.Current}; not installing");
            return;
        }

        log.Info($"[AutoUpdate] Update available ({info.LatestVersion}) — downloading automatically...");
        Install(info);
    }

    /// <summary>
    /// Whether <paramref name="offered"/> is a later version than <paramref name="running"/>.
    /// Versions are zero-padded dates ("2026.09.24.01"), so ordinal order is date order; the
    /// server cannot move a player backwards, which the C++ updater once did.
    /// </summary>
    public static bool IsNewer(string offered, string running) => string.CompareOrdinal(offered.Trim(), running.Trim()) > 0;

    /// <summary>
    /// Where a downloaded <paramref name="version"/> goes: "OpenVersus_&lt;version&gt;.asi" in
    /// <paramref name="installDirectory"/>, which the client sets to plugins/OpenVersus/ when the
    /// loader loads that folder and to the plugin's own folder otherwise, so an update never lands
    /// where it would not be loaded. Whatever the running plugin is called (a plain "OpenVersus.asi"
    /// included), it is renamed to ".bak" first, since the ASI loader would otherwise load both.
    /// </summary>
    public static string InstallPath(string installDirectory, string version) =>
        Path.Combine(installDirectory, $"{OvsVersion.Name}_{version.Trim()}.asi");

    /// <summary>Why <paramref name="body"/> must not be installed as the plugin, or null when it looks like one.</summary>
    public static string? Validate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 10000)
        {
            return $"too small ({body.Length} bytes)";
        }

        try
        {
            Memory.PeImage.SizeOfImage(body);
            return null;
        }
        catch (FormatException e)
        {
            return $"not a Windows binary ({e.Message})";
        }
        catch (ArgumentOutOfRangeException)
        {
            // "MZ" followed by a header offset that points outside the body: a DOS-era file, a
            // truncated download, or junk that happens to start with those two bytes.
            return "not a Windows binary (PE header offset is outside the file)";
        }
    }

    /// <summary>The most a plugin may be, uncompressed; the real one is a few MB.</summary>
    private const int MaxPluginBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The plugin in a download: the body itself when it is one, or the .asi inside a zip. A zip
    /// must hold exactly one .asi, or one named for <paramref name="version"/> among several.
    /// Nothing else in a zip is used: the ASI loader is in use while the game runs, and the
    /// settings file in a release must never replace the player's. Null, with
    /// <paramref name="problem"/> saying why, when there is no plugin to install;
    /// <paramref name="source"/> names what was taken.
    /// </summary>
    public static byte[]? PluginFrom(byte[] body, string version, out string source, out string? problem)
    {
        source = "the download";
        if (!body.AsSpan().StartsWith("PK\x03\x04"u8))
        {
            problem = Validate(body);
            return problem == null ? body : null;
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(body), ZipArchiveMode.Read);
            var plugins = zip.Entries.Where(e => e.Name.EndsWith(".asi", StringComparison.OrdinalIgnoreCase)).ToList();
            string wanted = $"{OvsVersion.Name}_{version.Trim()}.asi";
            ZipArchiveEntry? entry = plugins.Count == 1
                ? plugins[0]
                : plugins.FirstOrDefault(e => e.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                problem = plugins.Count == 0 ? "a zip with no .asi in it" : $"a zip with {plugins.Count} .asi files and none named {wanted}";
                return null;
            }

            source = $"{entry.FullName} from the zip";
            // The header's size can lie, so the read itself is bounded.
            using var stream = entry.Open();
            var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxPluginBytes)
                {
                    problem = $"a zip whose {entry.FullName} is over {MaxPluginBytes / (1024 * 1024)} MB";
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            byte[] plugin = buffer.ToArray();
            problem = Validate(plugin) is { } invalid ? $"a zip whose {entry.FullName} is {invalid}" : null;
            return problem == null ? plugin : null;
        }
        catch (InvalidDataException e)
        {
            problem = $"a damaged zip ({e.Message})";
            return null;
        }
    }

    /// <summary>
    /// Why <paramref name="body"/> does not match <paramref name="checksumFile"/> (the text of a
    /// <c>.sha256</c> file: "&lt;64 hex digits&gt; *&lt;file name&gt;", the name optional), or null when it
    /// does. A file that names a different file than <paramref name="fileName"/> is a mismatch.
    /// </summary>
    public static string? CheckSha256(string checksumFile, string fileName, ReadOnlySpan<byte> body)
    {
        string[] parts = checksumFile.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
        {
            return "its .sha256 file does not hold a SHA-256";
        }

        if (parts.Length > 1 && !string.Equals(parts[1].TrimStart('*').Trim(), fileName, StringComparison.Ordinal))
        {
            return $"its .sha256 file is for {parts[1].TrimStart('*').Trim()}, not {fileName}";
        }

        string actual = Convert.ToHexString(SHA256.HashData(body));
        return string.Equals(actual, parts[0], StringComparison.OrdinalIgnoreCase)
            ? null
            : $"its SHA-256 is {actual.ToLowerInvariant()}, but the release says {parts[0].ToLowerInvariant()}";
    }

    /// <summary>
    /// Why <paramref name="plugin"/> must not replace the running client, judged by the version
    /// built into it (<see cref="DuplicatePlugins.EmbeddedVersion"/>) rather than by what the server
    /// says: it must have one, it must be newer than <paramref name="running"/>, and it must be the
    /// <paramref name="offered"/> version. Null when it may be installed. Without this a server that
    /// mislabels a release would have the client reinstall itself on every launch.
    /// </summary>
    public static string? VersionProblem(ReadOnlySpan<byte> plugin, string offered, string running)
    {
        Version? inside = DuplicatePlugins.EmbeddedVersion(plugin);
        if (inside == null)
        {
            return "it has no version resource, so its version is unknown";
        }

        if (Version.TryParse(running.Trim(), out Version? current) && inside <= current)
        {
            return $"it is version {inside}, which is not newer than this client ({current})";
        }

        if (!Version.TryParse(offered.Trim(), out Version? label) || inside != label)
        {
            return $"it is version {inside}, but the server offered {offered.Trim()}";
        }

        return null;
    }

    /// <summary>
    /// Downloads the offered version and returns the plugin to install: checked against the
    /// release's <c>.sha256</c>, taken out of a zip if it is one, and checked for being the newer
    /// version it claims to be (<see cref="VersionProblem"/>). Null, with the reason logged,
    /// when there is nothing to install this time.
    /// </summary>
    internal byte[]? Fetch(VersionInfo info)
    {
        var url = Urls.Parse(info.DownloadUrl!);
        if (url == null)
        {
            log.Warn($"[AutoUpdate] Bad download URL {info.DownloadUrl}");
            return null;
        }
        log.Info($"[AutoUpdate] Starting download from: {url}");
        var result = download.Get(url, TimeSpan.FromSeconds(60));
        if (!result.Ok)
        {
            log.Warn($"[AutoUpdate] Download failed ({result.Error ?? $"HTTP {result.Status}"})");
            return null;
        }
        log.Info($"[AutoUpdate] Downloaded {result.Body.Length} bytes");

        var sumUrl = new UriBuilder(url) { Path = url.AbsolutePath + ".sha256" }.Uri;
        var sum = download.Get(sumUrl, TimeSpan.FromSeconds(15));
        if (sum.Status == 404)
        {
            log.Warn($"[AutoUpdate] Not installing the download: the release publishes no checksum ({sumUrl} is not there)");
            return null;
        }
        else if (!sum.Ok)
        {
            log.Warn($"[AutoUpdate] Could not fetch the checksum from {sumUrl} ({sum.Error ?? $"HTTP {sum.Status}"}); not installing, trying again next launch");
            return null;
        }
        else if (CheckSha256(sum.Text, Uri.UnescapeDataString(url.Segments[^1]), result.Body) is { } mismatch)
        {
            log.Warn($"[AutoUpdate] Not installing the download: {mismatch}");
            return null;
        }
        else
        {
            log.Info($"[AutoUpdate] SHA-256 matches {sumUrl}");
        }

        byte[]? plugin = PluginFrom(result.Body, info.LatestVersion!, out string source, out string? problem);
        if (plugin == null)
        {
            log.Warn($"[AutoUpdate] Download is {problem}; not installing it");
            return null;
        }

        if (VersionProblem(plugin, info.LatestVersion!, OvsVersion.Current) is { } wrongVersion)
        {
            log.Warn($"[AutoUpdate] Not installing the download: {wrongVersion}");
            return null;
        }

        log.Info($"[AutoUpdate] Installing {source} ({plugin.Length} bytes, version {DuplicatePlugins.EmbeddedVersion(plugin)})");
        return plugin;
    }

    private void Install(VersionInfo info)
    {
        byte[]? plugin = Fetch(info);
        if (plugin == null)
        {
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "OpenVersus_update.asi");
        string backup = pluginPath + ".bak";
        string target = InstallPath(installDirectory, info.LatestVersion!);
        try
        {
            File.WriteAllBytes(temp, plugin);
            // Before the running plugin is renamed, so a failure here leaves the install as it was.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            log.Info($"[AutoUpdate] Backing up {Path.GetFileName(pluginPath)} and installing {Path.GetFileName(target)}...");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(pluginPath, backup);
            try
            {
                File.Move(temp, target, overwrite: true);
            }
            catch (Exception e)
            {
                log.Warn($"[AutoUpdate] Failed to move new DLL into place ({e.Message}), restoring original asi file");
                File.Move(backup, pluginPath);
                return;
            }
        }
        catch (Exception e)
        {
            log.Warn($"[AutoUpdate] Install failed: {e.Message}");
            return;
        }
        log.Info("[AutoUpdate] New DLL installed! Restarting game...");
        User32.MessageBox(0, "A new version of OpenVersus has been released and an update has been applied. The game will now close; please relaunch the game to play.", "Game restarting", User32.MB_ICONINFORMATION);
        beforeExit(); // TerminateProcess gives no exit moment, so the log is closed and archived here
        Firmware.TerminateProcess(Kernel32.GetCurrentProcess(), 0);
    }
}
