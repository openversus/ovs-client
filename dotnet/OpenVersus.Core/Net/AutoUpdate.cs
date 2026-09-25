using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVersus.Native;

namespace OpenVersus.Net;

/// <summary>
/// Asks the server for the latest client and installs it: the running .asi is renamed to a
/// .bak, the download takes its place, and the game is closed so the next launch loads it.
/// The C++ did the version check over a raw socket without TLS, which cannot reach an https
/// server; this goes through the transport, so the check works against production.
/// </summary>
public sealed class AutoUpdate(string serverUrl, string pluginPath, IHttpTransport http, IHttpTransport download, ILogger log, Action beforeExit)
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
    /// Where a downloaded <paramref name="version"/> goes: "OpenVersus_&lt;version&gt;.asi" beside the
    /// running plugin, whatever that one is called (a plain "OpenVersus.asi" from an earlier
    /// release included), so the file name says what it is. The running file is renamed to
    /// ".bak" first, since the ASI loader would otherwise load both.
    /// </summary>
    public static string InstallPath(string pluginPath, string version) =>
        Path.Combine(Path.GetDirectoryName(pluginPath)!, $"{OvsVersion.Name}_{version.Trim()}.asi");

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

    private void Install(VersionInfo info)
    {
        var url = Urls.Parse(info.DownloadUrl!);
        if (url == null)
        {
            log.Warn($"[AutoUpdate] Bad download URL {info.DownloadUrl}");
            return;
        }
        log.Info($"[AutoUpdate] Starting download from: {url}");
        var result = download.Get(url, TimeSpan.FromSeconds(60));
        if (!result.Ok)
        {
            log.Warn($"[AutoUpdate] Download failed ({result.Error ?? $"HTTP {result.Status}"})");
            return;
        }
        log.Info($"[AutoUpdate] Downloaded {result.Body.Length} bytes");
        if (Validate(result.Body) is { } problem)
        {
            log.Warn($"[AutoUpdate] Download is {problem}; not installing it");
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "OpenVersus_update.asi");
        string backup = pluginPath + ".bak";
        string target = InstallPath(pluginPath, info.LatestVersion!);
        try
        {
            File.WriteAllBytes(temp, result.Body);
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
