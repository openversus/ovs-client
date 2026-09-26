using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenVersus.Native;

namespace OpenVersus.Net;

/// <summary>
/// The required update, run once at launch before the game has loaded anything: asks the
/// server for the release, and when the game's paks are missing or differ from it (or, with
/// AutoUpdate on, a newer plugin is out), downloads everything behind a progress window,
/// installs it and closes the game, so the next launch runs the new files. The ASI loader calls
/// the plugin at the game's entry point, before the engine opens its paks, so they can be
/// replaced here. Paks download whatever AutoUpdate says: they are content the game needs.
/// A release without paks (Infinity War) leaves this with nothing to do.
/// </summary>
/// <param name="serverUrl">The OVS server.</param>
/// <param name="autoUpdate">Whether a newer plugin may be installed too.</param>
/// <param name="http">The server transport.</param>
/// <param name="plugins">The plugin updater, for its download checks and install.</param>
/// <param name="paks">The pak updater for the game's Content/Paks; null when that folder was not found.</param>
/// <param name="log">Where everything is logged.</param>
/// <param name="beforeExit">Run just before the game is closed.</param>
/// <param name="onUpdated">Told what was installed ("Version …", "New game content installed") before the game closes, for the toast after the relaunch.</param>
public sealed class StartupUpdate(string serverUrl, bool autoUpdate, IHttpTransport http, AutoUpdate plugins, PakUpdate? paks, ILogger log, Action beforeExit, Action<string> onUpdated)
{
    /// <summary>What happened.</summary>
    public enum Outcome
    {
        /// <summary>No usable answer from the server; the background plugin check should run as before.</summary>
        NoAnswer,
        /// <summary>Everything matches the release.</summary>
        UpToDate,
        /// <summary>An update was due but failed; nothing was changed and it is tried again next launch.</summary>
        Failed,
        /// <summary>Installed. Only tests see this: in the game the process has been closed.</summary>
        Installed,
    }

    /// <summary>Stands in for closing the game, for tests.</summary>
    public Action Exit { get; init; } = () => Firmware.TerminateProcess(Kernel32.GetCurrentProcess(), 0);
    /// <summary>Stands in for the message box, for tests.</summary>
    public Action<string, string, uint> Tell { get; init; } = (text, caption, icon) => User32.MessageBox(0, text, caption, icon);
    /// <summary>Opens the progress window, for tests.</summary>
    public Func<long, UpdateProgressWindow?> OpenWindow { get; init; } = total => UpdateProgressWindow.Open(total, "Downloading the required OpenVersus update");

    /// <summary>The "files" part of the version response, or null when it has none or is not JSON.</summary>
    public static ReleaseFiles? ParseFiles(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, OvsJson.Default.ReleaseFiles);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Checks, and when anything is due, updates and closes the game.</summary>
    public Outcome Run()
    {
        var url = Urls.Join(serverUrl, $"/ovs/client-version?v={Uri.EscapeDataString(OvsVersion.Current)}");
        if (url == null)
        {
            return Outcome.NoAnswer;
        }

        var result = http.Get(url, TimeSpan.FromSeconds(5));
        var info = result.Ok ? AutoUpdate.Parse(result.Text) : null;
        if (info == null)
        {
            log.Warn($"[Update] No usable answer from the server at launch ({result.Error ?? (result.Ok ? "not JSON" : $"HTTP {result.Status}")}); checking the plugin in the background instead");
            return Outcome.NoAnswer;
        }

        List<UpdateFile> due = [];
        if (paks != null)
        {
            var listed = PakUpdate.Paks(ParseFiles(result.Text), out string? problem);
            if (listed == null)
            {
                log.Warn($"[Update] Not using this release's paks: {problem}");
            }
            else if (listed.Count > 0)
            {
                due = paks.Needed(listed);
                log.Info($"[Update] Release paks: {listed.Count}; to download: {due.Count}");
            }
        }

        bool pluginDue = autoUpdate && !info.IsLatest && !string.IsNullOrEmpty(info.LatestVersion)
            && !string.IsNullOrEmpty(info.DownloadUrl) && AutoUpdate.IsNewer(info.LatestVersion, OvsVersion.Current);
        if (due.Count == 0 && !pluginDue)
        {
            log.Info($"[Update] Up to date ({OvsVersion.Current}{(info.IsLatest ? "" : $", server offers {info.LatestVersion}")})");
            return Outcome.UpToDate;
        }

        long total = due.Sum(f => f.Size);
        log.Info($"[Update] Downloading {(pluginDue ? $"OpenVersus {info.LatestVersion}" : "")}{(pluginDue && due.Count > 0 ? " and " : "")}{(due.Count > 0 ? $"{due.Count} pak file(s), {total / (1024 * 1024)} MB" : "")}");
        using var window = OpenWindow(total);

        byte[]? plugin = null;
        if (pluginDue)
        {
            window?.SetStatus($"Downloading OpenVersus {info.LatestVersion}. Keep the game open.");
            plugin = plugins.Fetch(info);
        }

        if (due.Count > 0 && !paks!.Download(due, (name, n, count) => window?.SetFile(name, n, count), bytes => window?.AddBytes(bytes)))
        {
            window?.Dispose();
            Tell("The OpenVersus update could not be downloaded. No game files were changed; the game will start as it is and try again next launch.", "Update failed", User32.MB_ICONERROR);
            return Outcome.Failed;
        }

        window?.SetStatus("Downloads verified. Installing the update...");
        if (due.Count > 0 && !paks!.Install(due))
        {
            window?.Dispose();
            Tell("OpenVersus downloaded the update, but Windows would not let one of the game files be replaced. The previous files were put back and the game will start as it is.", "Update not installed", User32.MB_ICONERROR);
            return Outcome.Failed;
        }

        bool pluginInstalled = plugin != null && plugins.InstallPlugin(plugin, info.LatestVersion!);
        if (due.Count == 0 && !pluginInstalled)
        {
            // Only the plugin was due and it did not install: nothing changed, so play on.
            window?.Dispose();
            log.Warn("[Update] The plugin update did not install; trying again next launch");
            return Outcome.Failed;
        }

        window?.Dispose();
        onUpdated(pluginInstalled ? $"Version {info.LatestVersion}{(due.Count > 0 ? " and new game content" : "")}" : "New game content installed");
        log.Info("[Update] Update installed; closing the game so the next launch runs it");
        Tell("The OpenVersus update is installed. MultiVersus will now close; please relaunch it to play.", "Update complete", User32.MB_ICONINFORMATION);
        beforeExit(); // TerminateProcess gives no exit moment, so the log is closed and archived here
        Exit();
        return Outcome.Installed;
    }
}
