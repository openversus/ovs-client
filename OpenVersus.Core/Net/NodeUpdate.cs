using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenVersus.Native;
using OpenVersus.P2P;

namespace OpenVersus.Net;

/// <summary>
/// The rollback node's version, as its build stamps it into the executable (the rollback repo's
/// OVSRollbackNode.csproj: "OVS-NODE-VERSION:&lt;version&gt;;" in the assembly's metadata, which the
/// published single file carries as plain bytes). Read by scanning the file, not by running it, and
/// not from a file beside it that anyone could edit. "" when there is no executable or no marker:
/// a node from before the marker, which the server then tells the client to replace.
/// </summary>
public static class NodeVersion
{
    /// <summary>What precedes the version in the file.</summary>
    public const string Marker = "OVS-NODE-VERSION:";
    /// <summary>The most characters a version has; the marker's ';' must come within them.</summary>
    public const int MaxLength = 64;
    private const int ChunkSize = 1 << 20;

    /// <summary>The version in <paramref name="bytes"/>, or "" when no well-formed marker is there.</summary>
    public static string Find(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> marker = Encoding.ASCII.GetBytes(Marker);
        int at = bytes.IndexOf(marker);
        while (at >= 0)
        {
            var rest = bytes[(at + marker.Length)..];
            int end = rest.IndexOf((byte)';');
            if (end > 0 && end <= MaxLength && rest[..end].ToArray().All(b => char.IsAsciiLetterOrDigit((char)b) || b is (byte)'.' or (byte)'-' or (byte)'_'))
            {
                return Encoding.ASCII.GetString(rest[..end]);
            }

            int next = rest.IndexOf(marker);
            at = next < 0 ? -1 : at + marker.Length + next;
        }

        return "";
    }

    /// <summary>The version of the node at <paramref name="path"/>, scanning it a megabyte at a time; "" when it is missing, unreadable or unmarked.</summary>
    public static string Read(string path, ILogger? log = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                return "";
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            int keep = Marker.Length + MaxLength + 1;
            byte[] buffer = new byte[ChunkSize + keep];
            int carried = 0;
            while (true)
            {
                int read = stream.Read(buffer, carried, buffer.Length - carried);
                if (read == 0)
                {
                    return "";
                }

                int filled = carried + read;
                string version = Find(buffer.AsSpan(0, filled));
                if (version.Length > 0)
                {
                    return version;
                }

                carried = Math.Min(keep, filled);
                Buffer.BlockCopy(buffer, filled - carried, buffer, 0, carried);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Warn($"[Node] Cannot read {path} for its version ({e.Message})");
            return "";
        }
    }
}

/// <summary>Which node builds this install needs, by what the game runs on.</summary>
public static class NodePlatform
{
    public const string Windows = "win-x64";
    public const string Linux = "linux-x64";
    public const string MacArm = "osx-arm64";
    public const string MacIntel = "osx-x64";

    /// <summary>
    /// The platforms to keep installed: the Windows node on Windows, the Linux node under Proton or
    /// Wine, the Mac's node on a Mac (CrossOver), or both Mac builds when which Mac it is could not be
    /// learned, so that the launcher finds one.
    /// </summary>
    public static IReadOnlyList<string> Wanted(bool underWine, bool onMac, string? macArchitecture) =>
        !underWine ? [Windows] : !onMac ? [Linux] : macArchitecture is MacArm or MacIntel ? [macArchitecture] : [MacArm, MacIntel];

    /// <summary>The node executable of <paramref name="platform"/> in the mod's node folder.</summary>
    public static string ExecutablePath(string modDirectory, string platform) =>
        Path.Combine(modDirectory, NodeFiles.Folder, platform, platform == Windows ? NodeFiles.WindowsExe : NodeFiles.LinuxExe);

    /// <summary>The version of the first wanted platform's node that has one; "" when none does (missing, or from before versions).</summary>
    public static string InstalledVersion(string modDirectory, IReadOnlyList<string> wanted, ILogger? log = null)
    {
        foreach (string platform in wanted)
        {
            string version = NodeVersion.Read(ExecutablePath(modDirectory, platform), log);
            if (version.Length > 0)
            {
                return version;
            }
        }

        return "";
    }
}

/// <summary>
/// Which Mac the game runs on, asked from inside Wine, where it is not visible: a shell script the
/// mod writes beside the node is run through Wine's <c>start /unix</c> and writes the answer to a
/// file, which the mod waits for (start.exe's own exit code says nothing about a Unix program).
/// Apple silicon is read from sysctl, since a shell under Rosetta reports x86_64 from uname.
/// </summary>
public static class MacArchitecture
{
    /// <summary>The script's name in the node folder.</summary>
    public const string ScriptName = "mac-arch.sh";

    /// <summary>The script: writes osx-arm64 or osx-x64 to the file named by its one argument.</summary>
    public const string Script = "#!/bin/sh\n" +
        "if [ x\"$(sysctl -n hw.optional.arm64 2>/dev/null)\" = x1 ] || [ x\"$(sysctl -n sysctl.proc_translated 2>/dev/null)\" = x1 ]; then echo osx-arm64; else echo osx-x64; fi > \"$1\"\n";

    /// <summary>The command line that runs the script.</summary>
    public static string CommandLine(string systemDirectory, string unixScript, string unixAnswer) =>
        $"\"{Path.Combine(systemDirectory, "start.exe")}\" /unix /bin/sh \"{unixScript}\" \"{unixAnswer}\"";

    /// <summary>The platform the answer names, or null for anything else.</summary>
    public static string? Parse(string? answer) => answer?.Trim() switch { NodePlatform.MacArm => NodePlatform.MacArm, NodePlatform.MacIntel => NodePlatform.MacIntel, _ => null };

    /// <summary>
    /// Writes the script and starts it (<paramref name="start"/>: a command line and a working
    /// directory; true when the process was created), then waits up to twenty seconds for the
    /// answer. Null when the Mac could not be asked.
    /// </summary>
    public static string? Probe(string modDirectory, Func<string, string?> unixPath, string systemDirectory, Func<string, string, bool> start, ILogger log, Action<TimeSpan>? sleep = null)
    {
        string folder = Path.Combine(modDirectory, NodeFiles.Folder);
        string script = Path.Combine(folder, ScriptName);
        string answer = Path.Combine(folder, $"arch-{Environment.ProcessId}.txt");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(script, Script);
            File.Delete(answer);
            string? unixScript = unixPath(script);
            string? unixAnswer = unixPath(answer);
            if (unixScript == null || unixAnswer == null || unixScript.Contains('"') || unixAnswer.Contains('"'))
            {
                log.Warn("[Node] Wine gave no usable Linux path for the architecture check");
                return null;
            }

            if (!start(CommandLine(systemDirectory, unixScript, unixAnswer), folder))
            {
                log.Warn("[Node] The architecture check did not start");
                return null;
            }

            string? platform = Parse(FileAppears.Read(answer, TimeSpan.FromSeconds(20), sleep));
            if (platform == null)
            {
                log.Warn("[Node] The architecture check gave no answer");
            }

            return platform;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Node] The architecture check failed ({e.Message})");
            return null;
        }
        finally
        {
            try
            { File.Delete(answer); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Starts a process and lets it go: Wine's start.exe for the Unix-side steps of a node install. Its exit
/// code is not read (start.exe's says nothing about the Unix program it handed off); each step writes a
/// file the mod waits for instead (<see cref="FileAppears"/>).
/// </summary>
public static class StartedProcess
{
    /// <summary>Starts <paramref name="commandLine"/> in <paramref name="workingDirectory"/>; true when the process was created.</summary>
    public static unsafe bool Start(string commandLine, string workingDirectory)
    {
        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        char[] buffer = (commandLine + "\0").ToCharArray();
        PROCESS_INFORMATION info;
        bool created;
        fixed (char* p = buffer)
        {
            created = Kernel32.CreateProcess(null, p, 0, 0, inheritHandles: false, Kernel32.CREATE_NO_WINDOW, 0, workingDirectory, in startup, out info);
        }

        if (!created)
        {
            return false;
        }

        Kernel32.CloseHandle(info.hThread);
        Kernel32.CloseHandle(info.hProcess);
        return true;
    }
}

/// <summary>Waits for a file a started Unix step writes.</summary>
public static class FileAppears
{
    /// <summary>The file's text once it exists with content, polling every tenth of a second up to <paramref name="timeout"/>; null when it never appears.</summary>
    public static string? Read(string path, TimeSpan timeout, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var step = TimeSpan.FromMilliseconds(100);
        for (TimeSpan waited = TimeSpan.Zero; ; waited += step)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return File.ReadAllText(path);
                }
            }
            catch (IOException)
            {
                // Being written; the next look sees it whole.
            }

            if (waited >= timeout)
            {
                return null;
            }

            sleep(step);
        }
    }
}

/// <summary>
/// Keeps the rollback node beside the game current. The server's version answer says whether the
/// node this install runs is the release's (the client tells it the version read from the node's
/// executable; none counts as out of date) and lists the release's node archives, one per platform.
/// The one for this platform is downloaded into a staging folder under the node folder, checked
/// against the size and SHA-256 the release publishes, unpacked, and swapped into place, the previous
/// build kept beside it for one launch. Under Wine or CrossOver the unpacked files get their execute
/// bits through a chmod script started with Wine's start.exe, since files written through Wine have
/// none; the script's marker file says it ran. All of this runs at launch before the node is started,
/// so no restart is needed.
/// </summary>
/// <param name="modDirectory">The mod's folder (plugins/OpenVersus).</param>
/// <param name="download">The transport archives download through.</param>
/// <param name="log">Where everything is logged.</param>
/// <param name="wanted">The platforms this install needs (<see cref="NodePlatform.Wanted"/>).</param>
/// <param name="unixPath">Turns a Windows path into Wine's Linux path; null outside Wine.</param>
/// <param name="systemDirectory">Windows' system folder, where start.exe is.</param>
public sealed class NodeUpdate(string modDirectory, IHttpTransport download, ILogger log, IReadOnlyList<string> wanted, Func<string, string?>? unixPath, string systemDirectory)
{
    /// <summary>Tries per archive before a download counts as failed.</summary>
    public const int Attempts = 3;
    /// <summary>The launcher's name inside a Unix archive and in the node folder.</summary>
    public const string Launcher = NodeFiles.Launcher;

    /// <summary>The wait between tries; tests replace it.</summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>Starts a command line; tests replace it.</summary>
    public Func<string, string, bool> StartProcess { get; init; } = StartedProcess.Start;

    /// <summary>The chmod script's name in the node folder.</summary>
    public const string ChmodScript = "chmod.sh";

    /// <summary>The chmod script: sets the execute bit on every path after the first, then writes ok to the first.</summary>
    public const string ChmodScriptText = "#!/bin/sh\nmarker=$1; shift\nchmod 755 \"$@\" && echo ok > \"$marker\"\n";

    /// <summary>The platforms this install keeps.</summary>
    public IReadOnlyList<string> Wanted { get; } = wanted;

    /// <summary>The node folder.</summary>
    public string Folder => Path.Combine(modDirectory, NodeFiles.Folder);

    /// <summary>Where downloads and unpacked archives go: inside the node folder, so the final move is a rename.</summary>
    public string StagingDirectory => Path.Combine(Folder, ".update");

    /// <summary>
    /// The version of the installed node to report to the server; "" when there is none to report. Also the moment
    /// the previous build (node/&lt;platform&gt;.old, kept for one launch by <see cref="Install"/>) is removed, once
    /// the one in place has a version.
    /// </summary>
    public string InstalledVersion()
    {
        foreach (string platform in Wanted)
        {
            string old = Path.Combine(Folder, platform + ".old");
            if (Directory.Exists(old) && NodeVersion.Read(NodePlatform.ExecutablePath(modDirectory, platform), log).Length > 0)
            {
                try
                {
                    Directory.Delete(old, recursive: true);
                    log.Debug($"[Node] Removed the previous {platform} node ({old})");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"[Node] The previous {platform} node could not be removed ({e.Message}); it is in {old}");
                }
            }
        }

        return NodePlatform.InstalledVersion(modDirectory, Wanted, log);
    }

    /// <summary>
    /// The archives to install: nothing when the server says the node is current (or lists no node);
    /// else the release's archive for each wanted platform, provided it downloads from the release
    /// owner's GitHub releases and carries a SHA-256. A wanted platform the release lacks is logged.
    /// </summary>
    public List<UpdateFile> Needed(VersionInfo info, ReleaseFiles? files, string owner = PakUpdate.DefaultOwner)
    {
        if (info.NodeIsLatest)
        {
            return [];
        }

        var nodes = (files?.Files ?? []).Where(f => f != null && f.Kind == "node").ToList();
        if (nodes.Count == 0)
        {
            return [];
        }

        List<UpdateFile> needed = [];
        foreach (string platform in Wanted)
        {
            var file = nodes.FirstOrDefault(f => string.Equals(f.Platform, platform, StringComparison.OrdinalIgnoreCase));
            if (file == null)
            {
                log.Warn($"[Node] The release has no node for {platform}");
                continue;
            }

            if (string.IsNullOrEmpty(file.Name) || file.Name.Contains('/') || file.Name.Contains('\\') || file.Size <= 0)
            {
                log.Warn($"[Node] The release's node for {platform} has no usable name or size");
                continue;
            }

            if (!PakUpdate.IsAllowedUrl(file.DownloadUrl, file.Name, owner))
            {
                log.Warn($"[Node] {file.Name} would download from {file.DownloadUrl}, which is not a GitHub release of {owner}");
                continue;
            }

            if (string.IsNullOrEmpty(file.Sha256))
            {
                log.Warn($"[Node] {file.Name} has no SHA-256");
                continue;
            }

            needed.Add(file);
        }

        return needed;
    }

    /// <summary>
    /// Downloads <paramref name="files"/> into the staging folder, checking each one's size and
    /// SHA-256. An archive already there that checks out (left by an install that could not swap it
    /// in, say while the previous node was still running) is used as it is, so a node that cannot be
    /// replaced never costs the download again. False, with the reason logged, when any fails.
    /// </summary>
    public bool Download(IReadOnlyList<UpdateFile> files, Action<string, int, int>? onFile = null, Action<int>? onBytes = null)
    {
        try
        {
            Directory.CreateDirectory(StagingDirectory);
            foreach (string stale in Directory.EnumerateFileSystemEntries(StagingDirectory).Where(e => !files.Any(f => string.Equals(Path.GetFileName(e), f.Name, StringComparison.OrdinalIgnoreCase))))
            {
                if (Directory.Exists(stale))
                {
                    Directory.Delete(stale, recursive: true);
                }
                else
                {
                    File.Delete(stale);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Node] Cannot prepare the staging folder {StagingDirectory} ({e.Message})");
            return false;
        }

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            onFile?.Invoke(file.Name!, i + 1, files.Count);
            string staged = Path.Combine(StagingDirectory, file.Name!);
            if (File.Exists(staged) && Verify(staged, file) == null)
            {
                log.Info($"[Node] {file.Name} is already downloaded and verified");
                onBytes?.Invoke((int)Math.Min(file.Size, int.MaxValue));
                continue;
            }

            string? problem = null;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                log.Info($"[Node] Downloading {file.Name} ({file.Size} bytes) from {file.DownloadUrl}{(attempt > 1 ? $", try {attempt} of {Attempts}" : "")}");
                long received = 0;
                try
                {
                    HttpResult result;
                    using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        result = download.Download(new Uri(file.DownloadUrl!), output, TimeSpan.FromSeconds(120), bytes =>
                        {
                            received += bytes;
                            onBytes?.Invoke(bytes);
                        });
                    }

                    problem = !result.Ok ? $"the download failed ({result.Error ?? $"HTTP {result.Status}"})" : Verify(staged, file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problem = $"it cannot be written ({e.Message})";
                }

                if (problem == null)
                {
                    break;
                }

                log.Warn($"[Node] {file.Name}, try {attempt} of {Attempts}: {problem}");
                onBytes?.Invoke((int)-received);
                if (attempt < Attempts)
                {
                    Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            if (problem != null)
            {
                log.Warn($"[Node] Not installing {file.Name}: {problem}");
                try
                { File.Delete(staged); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return false;
            }
        }

        log.Info($"[Node] {files.Count} archive(s) downloaded and verified");
        return true;
    }

    private static string? Verify(string path, UpdateFile file)
    {
        var info = new FileInfo(path);
        if (info.Length != file.Size)
        {
            return $"it is {info.Length} bytes, but the release says {file.Size}";
        }

        using var stream = File.OpenRead(path);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase) ? null : $"its SHA-256 is {sha256}, but the release says {file.Sha256!.ToLowerInvariant()}";
    }

    /// <summary>
    /// Unpacks each downloaded archive and swaps its node folder into place: the previous
    /// node/&lt;platform&gt; becomes node/&lt;platform&gt;.old (the last .old is removed first), the
    /// launcher in a Unix archive replaces the one in the node folder, and under Wine the node and
    /// the launcher get their execute bits. A failure puts the previous folder back and leaves the
    /// rest as it was. The staging folder is removed on success; on a failure the verified archives
    /// stay for the next launch (<see cref="Download"/> reuses them) and only the unpacked files go.
    /// </summary>
    public bool Install(IReadOnlyList<UpdateFile> files)
    {
        bool ok = true;
        foreach (var file in files)
        {
            string platform = file.Platform!.ToLowerInvariant();
            try
            {
                if (!InstallOne(Path.Combine(StagingDirectory, file.Name!), platform))
                {
                    ok = false;
                    break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                log.Warn($"[Node] Not installed ({platform}): {e.Message}");
                ok = false;
                break;
            }
        }

        if (ok)
        {
            DeleteStaging();
        }
        else
        {
            DeleteUnpacked();
        }

        return ok;
    }

    /// <summary>Whether the installed node of <paramref name="platform"/> can be replaced now: on Windows a running node holds its executable open.</summary>
    private bool CanReplace(string platform)
    {
        string exe = NodePlatform.ExecutablePath(modDirectory, platform);
        if (!File.Exists(exe))
        {
            return true;
        }

        try
        {
            using var _ = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            log.Warn($"[Node] The {platform} node is still in use (a node from the last launch may still be closing); installing it next launch");
            return false;
        }
        catch (UnauthorizedAccessException e)
        {
            log.Warn($"[Node] The {platform} node cannot be replaced ({e.Message}); installing it next launch");
            return false;
        }
    }

    private bool InstallOne(string archive, string platform)
    {
        if (!CanReplace(platform))
        {
            return false;
        }

        string unpacked = Path.Combine(StagingDirectory, $"unpacked-{platform}");
        Extract(archive, unpacked);
        string source = Path.Combine(unpacked, NodeFiles.Folder, platform);
        string exe = platform == NodePlatform.Windows ? NodeFiles.WindowsExe : NodeFiles.LinuxExe;
        if (!File.Exists(Path.Combine(source, exe)))
        {
            log.Warn($"[Node] {Path.GetFileName(archive)} holds no node/{platform}/{exe}");
            return false;
        }

        string target = Path.Combine(Folder, platform);
        string old = target + ".old";
        if (Directory.Exists(old))
        {
            Directory.Delete(old, recursive: true);
        }

        bool hadOld = Directory.Exists(target);
        if (hadOld)
        {
            Directory.Move(target, old);
        }

        try
        {
            Directory.Move(source, target);
            string launcher = Path.Combine(unpacked, NodeFiles.Folder, Launcher);
            if (File.Exists(launcher))
            {
                File.Copy(launcher, Path.Combine(Folder, Launcher), overwrite: true);
            }

            if (platform != NodePlatform.Windows && !MakeExecutable([Path.Combine(target, exe), Path.Combine(Folder, Launcher)]))
            {
                throw new IOException("the execute bits could not be set");
            }

            string version = NodeVersion.Read(Path.Combine(target, exe), log);
            log.Info($"[Node] Installed the {platform} node{(version.Length > 0 ? $" {version}" : "")}{(hadOld ? $"; the previous one is in {old}" : "")}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Node] Installing the {platform} node failed ({e.Message}); putting the previous one back");
            try
            {
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }

                if (hadOld)
                {
                    Directory.Move(old, target);
                }
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                log.Error($"[Node] The previous node could not be put back ({rollback.Message}); it is in {old}");
            }

            return false;
        }
    }

    /// <summary>
    /// Sets the execute bit on <paramref name="paths"/>: outside Wine nothing to do; under Wine the chmod
    /// script is started through start.exe and its marker file waited for. False when it did not run.
    /// </summary>
    private bool MakeExecutable(IReadOnlyList<string> paths) => MakeExecutable(Folder, paths, unixPath, systemDirectory, StartProcess, log, Sleep);

    /// <summary>
    /// <see cref="MakeExecutable(IReadOnlyList{string})"/> for any caller: the mod also runs it before starting the node
    /// under Wine, since a node unpacked from the install zip (by hand or by Windows tools) has no execute bits either.
    /// </summary>
    public static bool MakeExecutable(string folder, IReadOnlyList<string> paths, Func<string, string?>? unixPath, string systemDirectory, Func<string, string, bool> startProcess, ILogger log, Action<TimeSpan>? sleep = null)
    {
        if (unixPath == null || !paths.Any(File.Exists))
        {
            return true;
        }

        string script = Path.Combine(folder, ChmodScript);
        string marker = Path.Combine(folder, $"chmod-{Environment.ProcessId}.txt");
        File.WriteAllText(script, ChmodScriptText);
        File.Delete(marker);
        var unix = new List<string>();
        foreach (string path in new[] { script, marker }.Concat(paths.Where(File.Exists)))
        {
            string? converted = unixPath(path);
            if (converted == null || converted.Contains('"'))
            {
                log.Warn($"[Node] Wine gave no usable Linux path for {path}");
                return false;
            }

            unix.Add(converted);
        }

        string commandLine = $"\"{Path.Combine(systemDirectory, "start.exe")}\" /unix /bin/sh {string.Join(" ", unix.Select(u => $"\"{u}\""))}";
        try
        {
            if (!startProcess(commandLine, folder))
            {
                log.Warn("[Node] chmod through Wine's start.exe did not start");
                return false;
            }

            if (FileAppears.Read(marker, TimeSpan.FromSeconds(20), sleep)?.Trim() != "ok")
            {
                log.Warn("[Node] chmod through Wine's start.exe did not report back");
                return false;
            }

            return true;
        }
        finally
        {
            try
            { File.Delete(marker); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Unpacks a zip or a gzipped tar into <paramref name="into"/>, refusing entries that would land outside it.</summary>
    public static void Extract(string archive, string into)
    {
        if (Directory.Exists(into))
        {
            Directory.Delete(into, recursive: true);
        }

        Directory.CreateDirectory(into);
        string root = Path.GetFullPath(into + Path.DirectorySeparatorChar);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                string destination = Destination(root, entry.FullName);
                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            return;
        }

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            string destination = Destination(root, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(destination);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    break;
                default:
                    throw new InvalidDataException($"{entry.Name} is a {entry.EntryType}, which a node archive never holds");
            }
        }
    }

    private static string Destination(string root, string entryName)
    {
        string relative = entryName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{entryName} would land outside the unpack folder");
        }

        return full;
    }

    private void DeleteUnpacked()
    {
        try
        {
            if (!Directory.Exists(StagingDirectory))
            {
                return;
            }

            foreach (string dir in Directory.EnumerateDirectories(StagingDirectory, "unpacked-*"))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Node] The unpacked files in {StagingDirectory} could not be removed ({e.Message})");
        }
    }

    private void DeleteStaging()
    {
        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Node] The staging folder {StagingDirectory} could not be removed ({e.Message})");
        }
    }
}
