using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace OpenVersus.Net;

/// <summary>
/// The release's paks against the pak folder, %LOCALAPPDATA%\MultiVersus\Saved\Paks (one of the
/// folders the engine mounts paks from, always writable, and never touched by Steam's updates
/// or file verification): which ones are missing or differ, downloading those into a staging
/// folder with every size and SHA-256 checked, and installing them all or nothing. The previous
/// copy of each replaced file is kept in the backup folder. Staging and backup must be outside
/// the pak folder, which the engine mounts recursively, and staging should be on the same drive
/// so installing is a rename. Paks installed by hand into the game's own Content\Paks
/// (<see cref="LegacyDirectory"/>) outrank Saved\Paks, so they are moved over when they match the
/// release and moved out of the way when they don't.
/// </summary>
/// <param name="paksDirectory">The pak folder, Saved\Paks.</param>
/// <param name="stagingDirectory">Where downloads wait until every one is verified.</param>
/// <param name="backupDirectory">Where replaced files go.</param>
/// <param name="hashCachePath">The file remembering local hashes by size and modified time.</param>
/// <param name="download">The transport for the downloads (GitHub, so no server identity on it).</param>
/// <param name="log">Where progress and problems are logged.</param>
/// <param name="releaseOwner">The GitHub account paks may download from; <see cref="DefaultOwner"/> unless testing a fork.</param>
public sealed class PakUpdate(string paksDirectory, string stagingDirectory, string backupDirectory, string hashCachePath, IHttpTransport download, ILogger log, string releaseOwner = PakUpdate.DefaultOwner)
{
    /// <summary>The organization whose releases paks come from.</summary>
    public const string DefaultOwner = "openversus";
    /// <summary>Tries per file before a download counts as failed.</summary>
    public const int Attempts = 3;

    /// <summary>The account paks may download from: <paramref name="releaseOwner"/> when it is a plain GitHub name, else <see cref="DefaultOwner"/>.</summary>
    public string ReleaseOwner { get; } = releaseOwner.Length is > 0 and <= 39 && releaseOwner.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? releaseOwner : DefaultOwner;

    /// <summary>The wait between tries; tests replace it.</summary>
    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// <summary>The game's Content\Paks, where OVS paks used to be installed by hand; null when not known.</summary>
    public string? LegacyDirectory { get; init; }

    /// <summary>Where OVS paks taken out of <see cref="LegacyDirectory"/> go.</summary>
    public string? LegacyBackupDirectory { get; init; }

    /// <summary>
    /// Moves each release pak that the pak folder lacks from <see cref="LegacyDirectory"/> into
    /// it, when the old copy already matches the release, so a player coming from a hand install
    /// downloads nothing. A copy that differs or cannot be moved stays, and is downloaded instead.
    /// </summary>
    public void AdoptLegacy(IReadOnlyList<UpdateFile> paks)
    {
        if (LegacyDirectory == null || !Directory.Exists(LegacyDirectory))
        {
            return;
        }

        foreach (var file in paks)
        {
            string old = Path.Combine(LegacyDirectory, file.Name!);
            string target = Path.Combine(paksDirectory, file.Name!);
            if (File.Exists(target) || !File.Exists(old) || new FileInfo(old).Length != file.Size
                || !string.Equals(HashFile(old), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(paksDirectory);
                File.Move(old, target);
                log.Info($"[Paks] {file.Name}: moved from the game's Content\\Paks to {paksDirectory} (it matches the release)");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn($"[Paks] {file.Name}: could not be moved from {LegacyDirectory} ({e.Message}); downloading it instead");
            }
        }
    }

    /// <summary>
    /// Moves every OVS pak still in <see cref="LegacyDirectory"/> to <see cref="LegacyBackupDirectory"/>:
    /// the engine ranks the game's Content\Paks above Saved\Paks, so a stale copy there would win
    /// over the installed one. False, with the reason logged, when one cannot be moved.
    /// </summary>
    public bool RetireLegacy()
    {
        if (LegacyDirectory == null || !Directory.Exists(LegacyDirectory))
        {
            return true;
        }

        try
        {
            var stale = Directory.EnumerateFiles(LegacyDirectory).Where(f => IsPakName(Path.GetFileName(f))).ToList();
            if (stale.Count == 0)
            {
                return true;
            }

            string backup = LegacyBackupDirectory ?? Path.Combine(backupDirectory, "old-content-paks");
            Directory.CreateDirectory(backup);
            foreach (string path in stale)
            {
                File.Move(path, Path.Combine(backup, Path.GetFileName(path)), overwrite: true);
            }

            log.Info($"[Paks] Moved {string.Join(", ", stale.Select(Path.GetFileName))} out of the game's Content\\Paks into {backup}; OVS paks load from {paksDirectory} now");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Error($"[Paks] Could not move old OVS paks out of {LegacyDirectory} ({e.Message}); they would load instead of the current ones");
            return false;
        }
    }

    /// <summary>More files than a release would ever carry; a manifest with more is refused.</summary>
    public const int MaxFiles = 64;
    /// <summary>GitHub's limit for one release asset.</summary>
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024 - 1;

    private static readonly string[] s_extensions = [".pak", ".utoc", ".ucas", ".sig"];

    /// <summary>An OVS content file name: "OVS_", then letters, digits, dots, dashes or underscores, ending .pak, .utoc, .ucas or .sig. Never a path.</summary>
    public static bool IsPakName(string? name) =>
        name != null && name.Length > 4 && name.Length <= 128
        && name.StartsWith("OVS_", StringComparison.OrdinalIgnoreCase)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        && s_extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether <paramref name="url"/> is a release download of a repository of
    /// <paramref name="owner"/> on GitHub, over https, for the file <paramref name="name"/>. Any
    /// repository of the account, so the pak repository can be added on the server without a
    /// client release.
    /// </summary>
    public static bool IsAllowedUrl(string? url, string name, string owner = DefaultOwner)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // /openversus/<repo>/releases/download/<tag>/<file>
        string[] parts = uri.AbsolutePath.Split('/');
        return parts.Length == 7 && parts[0] == "" && string.Equals(parts[1], owner, StringComparison.OrdinalIgnoreCase) && parts[2].Length > 0
            && parts[3] == "releases" && parts[4] == "download" && parts[5].Length > 0
            && string.Equals(Uri.UnescapeDataString(parts[6]), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The "paks" files of <paramref name="manifest"/>: none when it lists none (a release without
    /// paks), null with <paramref name="problem"/> saying why when any entry is unsafe or
    /// incomplete, since a release is installed whole or not at all.
    /// </summary>
    public static List<UpdateFile>? Paks(ReleaseFiles? manifest, out string? problem, string owner = DefaultOwner)
    {
        problem = null;
        var paks = (manifest?.Files ?? []).Where(f => f != null && f.Kind == "paks").ToList();
        if (paks.Count > MaxFiles)
        {
            problem = $"the release lists {paks.Count} pak files (at most {MaxFiles})";
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in paks)
        {
            string name = file.Name ?? "";
            if (!IsPakName(name))
            {
                problem = $"the pak entry \"{name}\" is not an OVS content file name";
            }
            else if (!IsAllowedUrl(file.DownloadUrl, name, owner))
            {
                problem = $"{name} would download from {file.DownloadUrl}, which is not a GitHub release of {owner}";
            }
            else if (file.Sha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit))
            {
                problem = $"{name} has no SHA-256";
            }
            else if (file.Size is <= 0 or > MaxFileBytes)
            {
                problem = $"{name} has a size of {file.Size} bytes";
            }
            else if (!names.Add(name))
            {
                problem = $"{name} is listed twice";
            }

            if (problem != null)
            {
                return null;
            }
        }

        return paks;
    }

    /// <summary>
    /// The files whose copy in Content/Paks is missing, a different size, or a different SHA-256.
    /// A local file's hash is taken from the cache while its size and modified time are unchanged.
    /// </summary>
    public List<UpdateFile> Needed(IReadOnlyList<UpdateFile> paks)
    {
        var cache = HashCache.Load(hashCachePath);
        bool cacheChanged = false;
        var needed = new List<UpdateFile>();
        foreach (var file in paks)
        {
            string path = Path.Combine(paksDirectory, file.Name!);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                log.Info($"[Paks] {file.Name}: not installed");
                needed.Add(file);
                continue;
            }

            if (info.Length != file.Size)
            {
                log.Info($"[Paks] {file.Name}: {info.Length} bytes here, {file.Size} in the release");
                needed.Add(file);
                continue;
            }

            string? hash = cache.Find(file.Name!, info.Length, info.LastWriteTimeUtc.Ticks);
            if (hash == null)
            {
                hash = HashFile(path);
                if (hash == null)
                {
                    log.Warn($"[Paks] {file.Name}: cannot be read to check it; replacing it");
                    needed.Add(file);
                    continue;
                }

                cache.Set(file.Name!, info.Length, info.LastWriteTimeUtc.Ticks, hash);
                cacheChanged = true;
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                log.Info($"[Paks] {file.Name}: differs from the release");
                needed.Add(file);
            }
        }

        if (cacheChanged)
        {
            cache.Save(hashCachePath, log);
        }

        return needed;
    }

    /// <summary>
    /// Downloads <paramref name="files"/> into the staging folder, checking each one's size and
    /// SHA-256 as it arrives. <paramref name="onFile"/> gets each file's name, number and the count;
    /// <paramref name="onBytes"/> each chunk's size. False, with the staging folder removed and the
    /// reason logged, when any file fails; nothing outside the staging folder is touched.
    /// </summary>
    public bool Download(IReadOnlyList<UpdateFile> files, Action<string, int, int>? onFile = null, Action<int>? onBytes = null)
    {
        try
        {
            DeleteStaging();
            Directory.CreateDirectory(stagingDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Paks] Cannot create the staging folder {stagingDirectory} ({e.Message})");
            return false;
        }

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            onFile?.Invoke(file.Name!, i + 1, files.Count);
            string staged = Path.Combine(stagingDirectory, file.Name!);
            string? problem = null;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                log.Info($"[Paks] Downloading {file.Name} ({file.Size} bytes) from {file.DownloadUrl}{(attempt > 1 ? $", try {attempt} of {Attempts}" : "")}");
                long received = 0;
                try
                {
                    using var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
                    using var hashing = new HashingStream(output);
                    var result = download.Download(new Uri(file.DownloadUrl!), hashing, TimeSpan.FromSeconds(60), bytes =>
                    {
                        received += bytes;
                        onBytes?.Invoke(bytes);
                    });
                    problem = !result.Ok ? $"the download failed ({result.Error ?? $"HTTP {result.Status}"})"
                        : hashing.Length != file.Size ? $"it is {hashing.Length} bytes, but the release says {file.Size}"
                        : !string.Equals(hashing.Sha256(), file.Sha256, StringComparison.OrdinalIgnoreCase) ? $"its SHA-256 is {hashing.Sha256()}, but the release says {file.Sha256!.ToLowerInvariant()}"
                        : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problem = $"it cannot be written ({e.Message})";
                }

                if (problem == null)
                {
                    break;
                }

                log.Warn($"[Paks] {file.Name}, try {attempt} of {Attempts}: {problem}");
                onBytes?.Invoke((int)-received); // the bar goes back for the retry
                if (attempt < Attempts)
                {
                    Sleep(TimeSpan.FromSeconds(2 * attempt));
                }
            }

            if (problem != null)
            {
                log.Warn($"[Paks] Not installing {file.Name}: {problem}");
                DeleteStaging();
                return false;
            }
        }

        log.Info($"[Paks] {files.Count} file(s) downloaded and verified");
        return true;
    }

    /// <summary>
    /// Moves every downloaded file into Content/Paks, the file it replaces going to the backup
    /// folder first. When any move fails, everything already moved is put back and false is
    /// returned. On success the staging folder is removed and the hash cache learns the new files.
    /// </summary>
    public bool Install(IReadOnlyList<UpdateFile> files)
    {
        var done = new List<(string Target, string Backup, bool HadOriginal)>();
        try
        {
            Directory.CreateDirectory(backupDirectory);
            Directory.CreateDirectory(paksDirectory);
            foreach (var file in files)
            {
                string target = Path.Combine(paksDirectory, file.Name!);
                string backup = Path.Combine(backupDirectory, file.Name!);
                bool hadOriginal = File.Exists(target);
                if (hadOriginal)
                {
                    File.Move(target, backup, overwrite: true);
                }

                done.Add((target, backup, hadOriginal));
                File.Move(Path.Combine(stagingDirectory, file.Name!), target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Error($"[Paks] Installing failed ({e.Message}); putting the previous files back");
            for (int i = done.Count - 1; i >= 0; i--)
            {
                var (target, backup, hadOriginal) = done[i];
                try
                {
                    if (hadOriginal)
                    {
                        File.Move(backup, target, overwrite: true);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    log.Error($"[Paks] Could not put back {Path.GetFileName(target)} ({restore.Message}); the previous copy is in {backupDirectory}");
                }
            }

            DeleteStaging();
            return false;
        }

        var cache = HashCache.Load(hashCachePath);
        foreach (var file in files)
        {
            var info = new FileInfo(Path.Combine(paksDirectory, file.Name!));
            cache.Set(file.Name!, info.Length, info.LastWriteTimeUtc.Ticks, file.Sha256!.ToLowerInvariant());
        }

        cache.Save(hashCachePath, log);
        DeleteStaging();
        log.Info($"[Paks] Installed {string.Join(", ", files.Select(f => f.Name))}; replaced files are in {backupDirectory}");
        return true;
    }

    private void DeleteStaging()
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[Paks] Cannot remove the staging folder {stagingDirectory} ({e.Message})");
        }
    }

    /// <summary>The SHA-256 of a file as lowercase hex, or null when it cannot be read.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes through to another stream, hashing and counting what passes.</summary>
    private sealed class HashingStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;
        private string? _sha256;

        public string Sha256() => _sha256 ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            _hash.AppendData(buffer);
            _length += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Local pak hashes by name, size and modified time, one per line:
    /// "&lt;sha256&gt; &lt;size&gt; &lt;ticks&gt; &lt;name&gt;". A damaged file only costs a re-hash.
    /// </summary>
    private sealed class HashCache
    {
        private readonly Dictionary<string, (long Size, long Ticks, string Sha256)> _entries = new(StringComparer.OrdinalIgnoreCase);

        public static HashCache Load(string path)
        {
            var cache = new HashCache();
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    string[] parts = line.Split(' ', 4);
                    if (parts.Length == 4 && parts[0].Length == 64 && IsPakName(parts[3])
                        && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                        && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                    {
                        cache._entries[parts[3]] = (size, ticks, parts[0]);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }

            return cache;
        }

        public string? Find(string name, long size, long ticks) =>
            _entries.TryGetValue(name, out var entry) && entry.Size == size && entry.Ticks == ticks ? entry.Sha256 : null;

        public void Set(string name, long size, long ticks, string sha256) => _entries[name] = (size, ticks, sha256);

        public void Save(string path, ILogger log)
        {
            var lines = _entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => string.Create(CultureInfo.InvariantCulture, $"{e.Value.Sha256} {e.Value.Size} {e.Value.Ticks} {e.Key}"));
            try
            {
                File.WriteAllLines(path, lines);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Debug($"[Paks] Cannot save the hash cache {path} ({e.Message}); paks will be hashed again next launch");
            }
        }
    }
}
