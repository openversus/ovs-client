using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using OpenVersus.Native;

namespace OpenVersus.P2P;

/// <summary>Where the rollback node's executables live: node/win-x64/ and node/linux-x64/ inside the mod's folder.</summary>
public static class NodeFiles
{
    /// <summary>The folder inside the mod's folder.</summary>
    public const string Folder = "node";
    /// <summary>The Windows build's file name.</summary>
    public const string WindowsExe = "OVS.Rollback.Node.exe";
    /// <summary>The Linux build's file name, for Proton and Steam Deck players.</summary>
    public const string LinuxExe = "OVS.Rollback.Node";

    /// <summary>The Windows node for a mod in <paramref name="modDirectory"/>.</summary>
    public static string WindowsPath(string modDirectory) => Path.Combine(modDirectory, Folder, "win-x64", WindowsExe);
    /// <summary>The Linux node for a mod in <paramref name="modDirectory"/>.</summary>
    public static string LinuxPath(string modDirectory) => Path.Combine(modDirectory, Folder, "linux-x64", LinuxExe);
}

/// <summary>What the mod tells the node on its command line (see the node's Program.cs).</summary>
/// <param name="Port">The UDP port to bind; 0 for any free one.</param>
/// <param name="PortFile">Where the node writes the port it took, as a Windows path.</param>
/// <param name="ParentToken">The watchdog token the keepalives carry.</param>
/// <param name="ParentTimeoutSeconds">Seconds without a keepalive before the node exits.</param>
/// <param name="ServerUrl">The OpenVersus server the node fetches match configs from and reports to.</param>
public sealed record NodeOptions(ushort Port, string PortFile, ulong ParentToken, int ParentTimeoutSeconds, string ServerUrl)
{
    /// <summary>
    /// The arguments, with <paramref name="portFile"/> in place of <see cref="PortFile"/> (the Linux
    /// path when the Linux node runs). Values are quoted; a value holding a quote cannot be passed and
    /// is refused by <see cref="NodeLaunch.Plan"/>.
    /// </summary>
    public string Arguments(string portFile) =>
        $"{Port.ToString(CultureInfo.InvariantCulture)} --port-file \"{portFile}\" --parent-token {ParentToken.ToString(CultureInfo.InvariantCulture)} --parent-timeout {ParentTimeoutSeconds.ToString(CultureInfo.InvariantCulture)} --server \"{ServerUrl}\"";
}

/// <summary>How the node is started: which executable, with which command line, from where.</summary>
/// <param name="Executable">The file CreateProcess is given.</param>
/// <param name="Arguments">Everything after the executable on the command line.</param>
/// <param name="WorkingDirectory">The node's folder.</param>
/// <param name="Description">For the log.</param>
/// <param name="IsNodeItself">True when the created process is the node (so ending it ends the node); false when it is Wine's start.exe, which hands the node to Linux and exits.</param>
public sealed record LaunchPlan(string Executable, string Arguments, string WorkingDirectory, string Description, bool IsNodeItself)
{
    /// <summary>The whole command line, executable quoted.</summary>
    public string CommandLine => $"\"{Executable}\" {Arguments}";
}

/// <summary>Chooses how to start the node for this install and runtime.</summary>
public static class NodeLaunch
{
    /// <summary>
    /// Under Wine or Proton the Linux build runs natively, started through Wine's <c>start /unix</c>
    /// (<paramref name="unixPath"/> turns a Windows path into the Linux one, for the node and for its
    /// port file); without a Linux build the Windows one runs under Wine. On Windows the Windows build
    /// runs. The node's settings travel on its command line, since Wine does not pass the Windows
    /// environment to a Linux program. Null, with <paramref name="why"/> set, when there is nothing to
    /// run; a non-empty <paramref name="why"/> with a plan is a note for the log.
    /// </summary>
    public static LaunchPlan? Plan(string modDirectory, bool underWine, NodeOptions options, Func<string, bool> exists, Func<string, string?> unixPath, string systemDirectory, out string why)
    {
        string windows = NodeFiles.WindowsPath(modDirectory);
        string linux = NodeFiles.LinuxPath(modDirectory);
        if (options.PortFile.Contains('"') || options.ServerUrl.Contains('"'))
        {
            why = "the port file path or the server URL contains a quote, which cannot be passed on a command line";
            return null;
        }

        if (underWine && exists(linux))
        {
            string? unix = unixPath(linux);
            string? unixPortFile = unix == null ? null : unixPath(options.PortFile);
            if (unix == null || unixPortFile == null)
            {
                why = $"Wine could not give a Linux path for {(unix == null ? linux : options.PortFile)}";
                return exists(windows) ? WindowsPlan(windows, options, " under Wine, since its Linux path is unknown") : null;
            }

            if (unix.Contains('"') || unixPortFile.Contains('"'))
            {
                why = $"the Linux path {(unix.Contains('"') ? unix : unixPortFile)} contains a quote, which start.exe cannot pass";
                return exists(windows) ? WindowsPlan(windows, options, " under Wine") : null;
            }

            why = "";
            return new LaunchPlan(Path.Combine(systemDirectory, "start.exe"), $"/unix \"{unix}\" {options.Arguments(unixPortFile)}", Path.GetDirectoryName(linux)!,
                $"the Linux node {unix} through Wine's start.exe", IsNodeItself: false);
        }

        if (exists(windows))
        {
            why = "";
            return WindowsPlan(windows, options, underWine ? " under Wine (no Linux build beside it)" : "");
        }

        why = $"no node at {windows}{(underWine ? $" or {linux}" : "")}";
        return null;
    }

    private static LaunchPlan WindowsPlan(string exe, NodeOptions options, string note) =>
        new(exe, options.Arguments(options.PortFile), Path.GetDirectoryName(exe)!, $"the Windows node {exe}{note}", IsNodeItself: true);
}

/// <summary>The file the node writes its UDP port to once bound: one line, the number.</summary>
public static class PortFile
{
    /// <summary>The port in <paramref name="text"/>, or null when it is not a port (0 is not one).</summary>
    public static ushort? Parse(string? text)
    {
        if (text == null)
        {
            return null;
        }

        return ushort.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort port) && port > 0 ? port : null;
    }

    /// <summary>
    /// This launch's file: named for the game's process id, so a file a crashed launch left behind is
    /// never read as this launch's, and two games on one machine never share one.
    /// </summary>
    public static string PathFor(string modDirectory, int processId) => Path.Combine(modDirectory, NodeFiles.Folder, $"port-{processId}.txt");

    /// <summary>Deletes port files older than <paramref name="olderThan"/> in <paramref name="modDirectory"/>'s node folder (leftovers of crashed launches); returns how many.</summary>
    public static int Sweep(string modDirectory, DateTime now, TimeSpan olderThan)
    {
        string folder = Path.Combine(modDirectory, NodeFiles.Folder);
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        int swept = 0;
        foreach (string file in Directory.EnumerateFiles(folder, "port-*.txt"))
        {
            try
            {
                if (now - File.GetLastWriteTimeUtc(file) > olderThan)
                {
                    File.Delete(file);
                    swept++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return swept;
    }
}

/// <summary>
/// The keepalive the mod sends its node: the node's own protocol header ("OVSP2P", version 1, kind 6:
/// Parent) and the token the node was started with. The node exits when none arrives for its
/// Node.ParentTimeoutSeconds, which is how a node outlives a crashed game by seconds, not forever.
/// </summary>
public static class ParentKeepAlive
{
    /// <summary>The datagram for <paramref name="token"/>: 16 bytes.</summary>
    public static byte[] Encode(ulong token)
    {
        var bytes = new byte[16];
        "OVSP2P"u8.CopyTo(bytes);
        bytes[6] = 1;
        bytes[7] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), token);
        return bytes;
    }

    /// <summary>How often the mod sends one.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    /// <summary>How long the node waits for one before exiting, passed to it as Node__ParentTimeoutSeconds.</summary>
    public const int TimeoutSeconds = 10;
}

/// <summary>
/// The rollback node the mod runs beside the game: the process on this machine that the game connects
/// to as its rollback server when the OpenVersus server puts a match on the players' own machines
/// (one player's node runs the match, the others forward to it). Started with the game, told to take
/// any free UDP port, kept alive by a keepalive a second, stopped with the game. The port it took is
/// read from the file it writes and reported to the server with the player's identity.
/// </summary>
public sealed class RollbackNode : IDisposable
{
    private readonly string _modDirectory;
    private readonly string _serverUrl;
    private readonly bool _underWine;
    private readonly ILogger _log;
    private readonly Func<string, string?> _unixPath;
    private readonly string _portFile;
    private readonly ulong _token;
    private nint _process;
    private nint _job;
    private bool _isNodeItself;
    private volatile bool _stopping;
    private Thread? _keepAlive;

    /// <summary>A node for the mod in <paramref name="modDirectory"/>, told to reach the OpenVersus server at <paramref name="serverUrl"/>.</summary>
    /// <param name="unixPath">Turns a Windows path into Wine's Linux path; null outside Wine.</param>
    public RollbackNode(string modDirectory, string serverUrl, bool underWine, ILogger log, Func<string, string?>? unixPath = null)
    {
        _modDirectory = modDirectory;
        _serverUrl = serverUrl;
        _underWine = underWine;
        _log = log;
        _unixPath = unixPath ?? (_ => null);
        _portFile = PortFile.PathFor(modDirectory, Environment.ProcessId);
        _token = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>The UDP port the node listens on; 0 until it has reported one (<see cref="WaitForPort"/>).</summary>
    public ushort Port { get; private set; }

    /// <summary>Whether <see cref="Start"/> succeeded and <see cref="Stop"/> has not run.</summary>
    public bool Running => _process != 0 && !_stopping;

    /// <summary>
    /// Starts the node. False, with the reason logged, when there is no node to run or Windows would
    /// not start it; the game then plays matches the server hosts, as before.
    /// </summary>
    public unsafe bool Start()
    {
        var options = new NodeOptions(Port: 0, _portFile, _token, ParentKeepAlive.TimeoutSeconds, _serverUrl);
        var plan = NodeLaunch.Plan(_modDirectory, _underWine, options, File.Exists, _unixPath, Environment.SystemDirectory, out string why);
        if (plan == null)
        {
            _log.Info($"[Node] Not started: {why}");
            return false;
        }

        if (why.Length > 0)
        {
            _log.Warn($"[Node] {why}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_portFile)!);
            File.Delete(_portFile);
            int swept = PortFile.Sweep(_modDirectory, DateTime.UtcNow, TimeSpan.FromHours(1));
            if (swept > 0)
            {
                _log.Debug($"[Node] Removed {swept} port file(s) left by earlier launches");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"[Node] Not started: cannot prepare {_portFile} ({e.Message})");
            return false;
        }

        var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
        // CreateProcessW may write into the command line, so it gets its own buffer.
        char[] commandLine = (plan.CommandLine + "\0").ToCharArray();
        bool created;
        PROCESS_INFORMATION info;
        fixed (char* p = commandLine)
        {
            created = Kernel32.CreateProcess(null, p, 0, 0, inheritHandles: false, Kernel32.CREATE_NO_WINDOW, 0, plan.WorkingDirectory, in startup, out info);
        }

        if (!created)
        {
            _log.Warn($"[Node] Not started: CreateProcess failed for {plan.CommandLine} (error {Marshal.GetLastPInvokeError()})");
            return false;
        }

        Kernel32.CloseHandle(info.hThread);
        _process = info.hProcess;
        _isNodeItself = plan.IsNodeItself;
        _log.Info($"[Node] Started {plan.Description} as process {info.dwProcessId}; it reports its port in {_portFile}");
        _log.Debug($"[Node] Command line: {plan.CommandLine}");

        // On Windows the node is our child and dies with the game through the job, even when the game
        // crashes. Through start.exe it is not (start.exe exits at once), so there the keepalive is
        // what ends it: it stops when this process does, and the node exits ten seconds later.
        if (_isNodeItself && !_underWine)
        {
            _job = Kernel32.CreateJobObject(0, 0);
            if (_job != 0)
            {
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = Kernel32.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                if (!Kernel32.SetInformationJobObject(_job, Kernel32.JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))
                    || !Kernel32.AssignProcessToJobObject(_job, _process))
                {
                    _log.Warn($"[Node] The node is not in a kill-on-close job (error {Marshal.GetLastPInvokeError()}); its keepalive timeout covers a crash instead");
                    Kernel32.CloseHandle(_job);
                    _job = 0;
                }
            }
        }

        _keepAlive = new Thread(KeepAlive) { IsBackground = true, Name = "OVS node keepalive" };
        _keepAlive.Start();
        return true;
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the node to report its port, and returns it (0 when
    /// it has not, or the node is gone). The first launch of a build unpacks itself, which can take a
    /// few seconds.
    /// </summary>
    public ushort WaitForPort(TimeSpan timeout)
    {
        if (Port != 0)
        {
            return Port;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ushort? port = ReadPortFile();
            if (port != null)
            {
                // The keepalive thread and the identity thread both wait; the first to see the file logs it.
                lock (this)
                {
                    if (Port == 0)
                    {
                        Port = port.Value;
                        _log.Info($"[Node] Listening on UDP {Port}");
                    }
                }

                return Port;
            }

            if (_stopping || _process == 0 || HasExited())
            {
                return 0;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return 0;
            }

            Thread.Sleep(100);
        }
    }

    private ushort? ReadPortFile()
    {
        try
        {
            return File.Exists(_portFile) ? PortFile.Parse(File.ReadAllText(_portFile)) : null;
        }
        catch (IOException)
        {
            // Being written or renamed into place; the next read sees it.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the process we created has ended. Through start.exe that says nothing about the node, which is why it is only read for the node itself.</summary>
    private bool HasExited()
    {
        if (!_isNodeItself)
        {
            return false;
        }

        if (Kernel32.GetExitCodeProcess(_process, out uint code) && code != Kernel32.STILL_ACTIVE)
        {
            _log.Warn($"[Node] The node exited with code {code} before reporting a port; see its log in {Path.Combine(_modDirectory, NodeFiles.Folder)}");
            return true;
        }

        return false;
    }

    private void KeepAlive()
    {
        if (WaitForPort(TimeSpan.FromMinutes(2)) == 0)
        {
            if (!_stopping)
            {
                _log.Warn("[Node] No port reported in two minutes; the node is not kept alive and will exit on its own");
            }

            return;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var node = new IPEndPoint(IPAddress.Loopback, Port);
        byte[] message = ParentKeepAlive.Encode(_token);
        bool failed = false;
        while (!_stopping)
        {
            try
            {
                socket.SendTo(message, node);
                failed = false;
            }
            catch (SocketException e)
            {
                if (!failed)
                {
                    _log.Warn($"[Node] Keepalive to 127.0.0.1:{Port} failed ({e.SocketErrorCode}); the node exits if this keeps up");
                }

                failed = true;
            }

            Thread.Sleep(ParentKeepAlive.Interval);
        }
    }

    /// <summary>
    /// Stops the node: the keepalives end, the node process (when it is our child) is terminated,
    /// and the port file is removed. A node started through start.exe exits by itself within
    /// <see cref="ParentKeepAlive.TimeoutSeconds"/> of the last keepalive.
    /// </summary>
    public void Stop()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        if (_process != 0)
        {
            if (_isNodeItself)
            {
                bool ended = Firmware.TerminateProcess(_process, 0);
                _log.Info(ended ? "[Node] Stopped" : $"[Node] TerminateProcess failed (error {Marshal.GetLastPInvokeError()}); the node exits when its keepalives stop");
            }
            else
            {
                _log.Info($"[Node] Keepalives stopped; the node exits within {ParentKeepAlive.TimeoutSeconds} s");
            }

            Kernel32.CloseHandle(_process);
            _process = 0;
        }

        if (_job != 0)
        {
            Kernel32.CloseHandle(_job);
            _job = 0;
        }

        try
        {
            File.Delete(_portFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <inheritdoc cref="Stop"/>
    public void Dispose() => Stop();
}
