using System.Text.Json;
using OpenVersus.Native;

namespace OpenVersus.Net;

/// <summary>
/// Asks the server for the latest client and installs it: the running .asi is renamed to a
/// .bak, the download takes its place, and the game is closed so the next launch loads it.
/// The C++ did the version check over a raw socket without TLS, which cannot reach an https
/// server; this goes through the transport, so the check works against production.
/// </summary>
public sealed class AutoUpdate(string serverUrl, string pluginPath, IHttpTransport http, IHttpTransport download, Log log)
{
    public sealed record VersionInfo(string? LatestVersion, string? DownloadUrl, bool IsLatest, string? ReleaseName);

    public static VersionInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new VersionInfo(
                root.TryGetProperty("latest_version", out var v) ? v.GetString() : null,
                root.TryGetProperty("download_url", out var d) ? d.GetString() : null,
                root.TryGetProperty("is_latest", out var l) && l.ValueKind == JsonValueKind.True,
                root.TryGetProperty("release_name", out var r) ? r.GetString() : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

        log.Info($"[AutoUpdate] Update available ({info.LatestVersion}) — downloading automatically...");
        Install(info);
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
        if (result.Body.Length < 10000)
        {
            log.Warn($"[AutoUpdate] Download update file too small ({result.Body.Length} bytes), aborting");
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), "OpenVersus_update.asi");
        string backup = $"{pluginPath}.v{OvsVersion.Current}.bak";
        try
        {
            File.WriteAllBytes(temp, result.Body);
            log.Info("[AutoUpdate] Backing up current asi file and installing update...");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(pluginPath, backup);
            try
            {
                File.Move(temp, pluginPath);
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
        log.Close(); // TerminateProcess gives no exit moment, so the log is archived here
        Firmware.TerminateProcess(Kernel32.GetCurrentProcess(), 0);
    }
}
