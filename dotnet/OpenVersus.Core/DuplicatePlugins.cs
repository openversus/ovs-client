using OpenVersus.Memory;

namespace OpenVersus;

/// <summary>
/// Other copies of OpenVersus in the same install. Ultimate ASI Loader loads every .asi it finds,
/// so a player who extracts a new release over an old one (the C++ client's OpenVersus.asi, or an
/// older OpenVersus_&lt;version&gt;.asi) runs two clients that patch the same code. At startup the
/// plugin's own folder and the game executable's folder are searched, subfolders included, since
/// installs have been seen with copies in odd places. The newest copy by the version built into it
/// (<see cref="EmbeddedVersion"/>, read from the file, never by loading it) wins, whatever the file
/// is called; a file without one (the C++ client) counts as oldest, and a tie goes to the running
/// copy. Every other copy, the running one included when it lost, is renamed to .bak.
/// </summary>
public static class DuplicatePlugins
{
    /// <summary>How deep below each folder to look. Far more than a real install nests.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Every OpenVersus*.asi under <paramref name="roots"/> other than <paramref name="self"/>,
    /// regardless of case, each path once. A folder that cannot be read is skipped.
    /// </summary>
    public static List<string> Find(string self, params string?[] roots)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxDepth,
            MatchCasing = MatchCasing.CaseInsensitive,
            IgnoreInaccessible = true,
            // A junction or symlink could loop, or lead out of the install.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(self) };
        var found = new List<string>();
        foreach (string? root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(root, "OpenVersus*.asi", options))
            {
                string full = Path.GetFullPath(file);
                if (seen.Add(full))
                {
                    found.Add(full);
                }
            }
        }

        return found;
    }

    /// <summary>The most of a file read when looking for its version; the real plugin is a few MB.</summary>
    private const int MaxFileBytes = 64 * 1024 * 1024;

    /// <summary>
    /// The file version in <paramref name="path"/>'s version resource (VS_FIXEDFILEINFO, which the
    /// build stamps from VERSION), read from the file's bytes without loading it: loading a copy
    /// would run its code, and the C++ client patches the game from DllMain. Null when the file has
    /// no version resource, as the C++ client's does not, or cannot be read.
    /// </summary>
    public static Version? EmbeddedVersion(string path)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                return null;
            }

            return EmbeddedVersion(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>As above, for a file already read.</summary>
    public static Version? EmbeddedVersion(ReadOnlySpan<byte> file)
    {
        const int versionInfo = 16;          // RT_VERSION
        const uint fixedSignature = 0xFEEF04BD;
        try
        {
            var (rva, size) = PeImage.DataDirectory(file, 2);
            var sections = PeImage.Sections(file);
            long root = PeImage.FileOffset(sections, rva);
            if (size == 0 || root < 0)
            {
                return null;
            }

            // Type 16, then the first name and the first language under it.
            long entry = FindEntry(file, root, root, versionInfo);
            for (int level = 0; level < 2 && entry >= 0; level++)
            {
                entry = FindEntry(file, root, entry, null);
            }

            if (entry < 0 || (BitConverter.ToUInt32(file[(int)(entry + 4)..]) & 0x8000_0000) != 0)
            {
                return null;
            }

            uint dataEntry = BitConverter.ToUInt32(file[(int)(entry + 4)..]);
            long data = PeImage.FileOffset(sections, BitConverter.ToUInt32(file[(int)(root + dataEntry)..]));
            if (data < 0)
            {
                return null;
            }

            // VS_VERSIONINFO: three WORDs, the key "VS_VERSION_INFO" in UTF-16, padding to 4 bytes,
            // then VS_FIXEDFILEINFO.
            const string key = "VS_VERSION_INFO";
            var keyBytes = file.Slice((int)data + 6, key.Length * 2);
            if (System.Text.Encoding.Unicode.GetString(keyBytes) != key)
            {
                return null;
            }

            int value = (int)((data + 6 + ((key.Length + 1) * 2) + 3) & ~3L);
            if (BitConverter.ToUInt32(file[value..]) != fixedSignature)
            {
                return null;
            }

            uint ms = BitConverter.ToUInt32(file[(value + 8)..]);
            uint ls = BitConverter.ToUInt32(file[(value + 12)..]);
            return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
        }
        catch (Exception e) when (e is FormatException or ArgumentOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// In the resource directory at <paramref name="directory"/>, the entry with id
    /// <paramref name="id"/> (or the first entry for null), as the file offset of that entry; for a
    /// subdirectory, the offset of the directory it points to. -1 when there is none.
    /// </summary>
    private static long FindEntry(ReadOnlySpan<byte> file, long root, long directory, int? id)
    {
        int named = BitConverter.ToUInt16(file[(int)(directory + 12)..]);
        int ids = BitConverter.ToUInt16(file[(int)(directory + 14)..]);
        for (int i = 0; i < named + ids; i++)
        {
            long entry = directory + 16 + (i * 8);
            uint name = BitConverter.ToUInt32(file[(int)entry..]);
            if (id != null && (i < named || name != id))
            {
                continue;
            }

            uint offset = BitConverter.ToUInt32(file[(int)(entry + 4)..]);
            return (offset & 0x8000_0000) != 0 ? root + (offset & 0x7FFF_FFFF) : entry;
        }

        return -1;
    }

    /// <summary>
    /// Which copy stays: the newest by <paramref name="versionOf"/> (<see cref="EmbeddedVersion"/>),
    /// a copy without one counting as oldest, with <paramref name="self"/> winning a tie. Everything
    /// else is retired.
    /// </summary>
    public static (string Keep, List<string> Retire) Decide(string self, IReadOnlyList<string> others, Func<string, Version?> versionOf)
    {
        string keep = self;
        Version? kept = versionOf(self);
        foreach (string other in others)
        {
            Version? version = versionOf(other);
            if (version != null && (kept == null || version > kept))
            {
                keep = other;
                kept = version;
            }
        }

        var retire = others.Where(o => o != keep).ToList();
        if (keep != self)
        {
            retire.Add(self);
        }

        return (keep, retire);
    }

    /// <summary>
    /// Renames each file to &lt;name&gt;.bak, replacing an earlier .bak of the same name, so the
    /// loader no longer loads it. Windows allows renaming a loaded module. Returns the files that
    /// could not be renamed, with why.
    /// </summary>
    public static List<(string Path, string Error)> Retire(IEnumerable<string> files)
    {
        var failed = new List<(string, string)>();
        foreach (string file in files)
        {
            try
            {
                File.Move(file, file + ".bak", overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed.Add((file, e.Message));
            }
        }

        return failed;
    }
}
