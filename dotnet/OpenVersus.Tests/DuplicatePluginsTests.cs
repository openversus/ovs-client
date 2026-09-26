namespace OpenVersus.Tests;

public class DuplicatePluginsTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("ovs-dupes-").FullName;

    public void Dispose() => Directory.Delete(_game, recursive: true);

    private string Put(string relative)
    {
        string path = Path.Combine(_game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return Path.GetFullPath(path);
    }

    [Fact]
    public void FindsCopiesAnywhereUnderThePluginAndGameFolders()
    {
        string self = Put("Win64/plugins/OpenVersus_2026.10.01.01.asi");
        string cpp = Put("Win64/plugins/OpenVersus.asi");
        string nested = Put("Win64/plugins/old stuff/backup/OpenVersus_2026.09.25.06.asi");
        string shouted = Put("Win64/scripts/OPENVERSUS.ASI");
        string root = Put("Win64/OpenVersus_2026.08.01.01.asi");
        Put("Win64/plugins/OpenVersus_2026.09.25.06.asi.bak");
        Put("Win64/plugins/SomethingElse.asi");
        Put("Elsewhere/OpenVersus.asi");

        var found = DuplicatePlugins.Find(self, Path.Combine(_game, "Win64", "plugins"), Path.Combine(_game, "Win64"));
        Assert.Equal(new[] { cpp, nested, shouted, root }.Order(), found.Order());
    }

    /// <summary>A minimal PE32+ whose only section is .rsrc holding a version resource for
    /// <paramref name="version"/>, laid out as the linker lays it out; no resources for null.</summary>
    internal static byte[] PeWithVersion(Version? version)
    {
        var file = new byte[0x400];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BitConverter.TryWriteBytes(file.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(file.AsSpan(0x80));
        BitConverter.TryWriteBytes(file.AsSpan(0x84), (ushort)0x8664);
        BitConverter.TryWriteBytes(file.AsSpan(0x86), (ushort)1);        // one section
        BitConverter.TryWriteBytes(file.AsSpan(0x94), (ushort)240);      // PE32+ optional header
        const int optional = 0x98;
        BitConverter.TryWriteBytes(file.AsSpan(optional), (ushort)0x20B);
        BitConverter.TryWriteBytes(file.AsSpan(optional + 108), 16u);   // data directories
        const int section = optional + 240;
        ".rsrc"u8.CopyTo(file.AsSpan(section));
        BitConverter.TryWriteBytes(file.AsSpan(section + 8), 0x200u);    // virtual size
        BitConverter.TryWriteBytes(file.AsSpan(section + 12), 0x1000u);  // RVA
        BitConverter.TryWriteBytes(file.AsSpan(section + 16), 0x200u);  // raw size
        BitConverter.TryWriteBytes(file.AsSpan(section + 20), 0x200u);   // raw offset
        if (version == null)
        {
            return file;
        }

        BitConverter.TryWriteBytes(file.AsSpan(optional + 112 + 16), 0x1000u); // resources RVA
        BitConverter.TryWriteBytes(file.AsSpan(optional + 112 + 20), 0x100u);  // and size
        var r = file.AsSpan(0x200);
        // Root: one id entry, type 16, pointing at the name directory; then name 1, language 0x409.
        BitConverter.TryWriteBytes(r[14..], (ushort)1);
        BitConverter.TryWriteBytes(r[16..], 16u);
        BitConverter.TryWriteBytes(r[20..], 0x8000_0018u);
        BitConverter.TryWriteBytes(r[(0x18 + 14)..], (ushort)1);
        BitConverter.TryWriteBytes(r[(0x18 + 16)..], 1u);
        BitConverter.TryWriteBytes(r[(0x18 + 20)..], 0x8000_0030u);
        BitConverter.TryWriteBytes(r[(0x30 + 14)..], (ushort)1);
        BitConverter.TryWriteBytes(r[(0x30 + 16)..], 0x409u);
        BitConverter.TryWriteBytes(r[(0x30 + 20)..], 0x48u);            // a data entry
        BitConverter.TryWriteBytes(r[0x48..], 0x1000u + 0x58);          // its RVA
        BitConverter.TryWriteBytes(r[0x4C..], 0x80u);
        var info = r[0x58..];
        BitConverter.TryWriteBytes(info, (ushort)0x80);
        BitConverter.TryWriteBytes(info[2..], (ushort)52);
        System.Text.Encoding.Unicode.GetBytes("VS_VERSION_INFO").CopyTo(info[6..]);
        var fixedInfo = info[40..];                                      // 6 + 32, padded to 4
        BitConverter.TryWriteBytes(fixedInfo, 0xFEEF04BDu);
        BitConverter.TryWriteBytes(fixedInfo[8..], (uint)((version.Major << 16) | version.Minor));
        BitConverter.TryWriteBytes(fixedInfo[12..], (uint)((version.Build << 16) | version.Revision));
        return file;
    }

    [Fact]
    public void TheVersionIsReadFromTheFilesVersionResource()
    {
        Assert.Equal(new Version(2026, 9, 25, 8), DuplicatePlugins.EmbeddedVersion(PeWithVersion(new Version(2026, 9, 25, 8))));
        Assert.Null(DuplicatePlugins.EmbeddedVersion(PeWithVersion(null)));
        Assert.Null(DuplicatePlugins.EmbeddedVersion("[Settings]\n"u8));
        Assert.Null(DuplicatePlugins.EmbeddedVersion(PeWithVersion(new Version(2026, 9, 25, 8))[..0x250]));

        byte[] wrongSignature = PeWithVersion(new Version(2026, 9, 25, 8));
        wrongSignature[0x200 + 0x58 + 40] ^= 0xFF;
        Assert.Null(DuplicatePlugins.EmbeddedVersion(wrongSignature));
    }

    [Fact]
    public void TheNewestBuiltInVersionStaysAndNoVersionIsOldest()
    {
        var versions = new Dictionary<string, Version?>
        {
            ["/g/plugins/OpenVersus/OpenVersus_2026.09.25.08.asi"] = new(2026, 9, 25, 8),
            ["/g/plugins/OpenVersus.asi"] = null,                               // the C++ client
            ["/g/OpenVersus_2026.09.25.06.asi"] = new(2026, 9, 25, 6),
            ["/g/scripts/OpenVersus_2026.10.01.01.asi"] = new(2026, 10, 1, 1),
            ["/g/sub/OpenVersus_2026.09.25.08.asi"] = new(2026, 9, 25, 8),
        };
        Version? Of(string p) => versions[p];
        const string self = "/g/plugins/OpenVersus/OpenVersus_2026.09.25.08.asi";

        var (keep, retire) = DuplicatePlugins.Decide(self, ["/g/plugins/OpenVersus.asi", "/g/OpenVersus_2026.09.25.06.asi"], Of);
        Assert.Equal(self, keep);
        Assert.Equal(["/g/plugins/OpenVersus.asi", "/g/OpenVersus_2026.09.25.06.asi"], retire);

        // A newer copy elsewhere wins, and the running one retires itself.
        (keep, retire) = DuplicatePlugins.Decide(self, ["/g/scripts/OpenVersus_2026.10.01.01.asi", "/g/plugins/OpenVersus.asi"], Of);
        Assert.Equal("/g/scripts/OpenVersus_2026.10.01.01.asi", keep);
        Assert.Equal(["/g/plugins/OpenVersus.asi", self], retire);

        // The same version twice: the running copy wins the tie.
        (keep, _) = DuplicatePlugins.Decide(self, ["/g/sub/OpenVersus_2026.09.25.08.asi"], Of);
        Assert.Equal(self, keep);

        // A running copy without a version loses to any copy with one.
        (keep, _) = DuplicatePlugins.Decide("/g/plugins/OpenVersus.asi", ["/g/OpenVersus_2026.09.25.06.asi"], Of);
        Assert.Equal("/g/OpenVersus_2026.09.25.06.asi", keep);
    }

    /// <summary>The name says nothing: a newer build renamed to look old still wins.</summary>
    [Fact]
    public void ARenamedFileIsJudgedByWhatIsInIt()
    {
        string self = Put("plugins/OpenVersus/OpenVersus_2026.10.01.01.asi");
        File.WriteAllBytes(self, PeWithVersion(new Version(2026, 9, 25, 6)));
        string renamed = Put("plugins/OpenVersus.asi");
        File.WriteAllBytes(renamed, PeWithVersion(new Version(2026, 10, 1, 1)));

        var (keep, retire) = DuplicatePlugins.Decide(self, [renamed], DuplicatePlugins.EmbeddedVersion);
        Assert.Equal(renamed, keep);
        Assert.Equal([self], retire);
    }

    [Fact]
    public void RetiredCopiesBecomeBakFilesReplacingOldOnes()
    {
        string cpp = Put("plugins/OpenVersus.asi");
        File.WriteAllText(cpp, "new");
        File.WriteAllText(cpp + ".bak", "old");

        Assert.Empty(DuplicatePlugins.Retire([cpp]));
        Assert.False(File.Exists(cpp));
        Assert.Equal("new", File.ReadAllText(cpp + ".bak"));

        var failed = DuplicatePlugins.Retire([Path.Combine(_game, "plugins", "missing.asi")]);
        Assert.Single(failed);
    }
}
