using OpenVersus.Config;

namespace OpenVersus.Tests;

public class StateAndCacheTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-state-").FullName;

    public void Dispose()
    {
        UnixPermissions.Restore(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    private string In(string name) => Path.Combine(_dir, name);

    [Fact]
    public void TheAgreementIsRememberedInToml()
    {
        Assert.False(new State(_dir).Load().PaidModWarned);
        Assert.False(File.Exists(In(State.FileName)));

        new State(_dir).Load().MarkPaidModWarned();
        Assert.Equal("[FirstRun]\r\nPaidModWarned = true\r\n", File.ReadAllText(In(State.FileName)));
        Assert.True(new State(_dir).Load().PaidModWarned);
    }

    [Theory]
    [InlineData("[FirstRun]\r\nPaidModWarned=true\r\n", true)]
    [InlineData("[FirstRun]\r\nPaidModWarned=false\r\n", false)]
    [InlineData("", false)]
    public void TheLegacyStateIsCarriedAndDeleted(string ini, bool warned)
    {
        File.WriteAllText(In(State.LegacyFileName), ini);

        Assert.Equal(warned, new State(_dir).Load().PaidModWarned);
        Assert.False(File.Exists(In(State.LegacyFileName)));
        Assert.Equal(warned, File.Exists(In(State.FileName)));
        Assert.Equal(warned, new State(_dir).Load().PaidModWarned);
    }

    [SkippableFact]
    public void AnAgreementThatCannotBeWrittenKeepsTheIni()
    {
        UnixPermissions.SkipUnlessUnix();
        File.WriteAllText(In(State.LegacyFileName), "[FirstRun]\r\nPaidModWarned=true\r\n");
        Directory.CreateDirectory(In(State.FileName) + ".tmp");

        Assert.True(new State(_dir).Load().PaidModWarned);
        Assert.True(File.Exists(In(State.LegacyFileName)));
    }

    [Fact]
    public void ABrokenStateFileStartsOverSoTheAgreementCanBeSaved()
    {
        File.WriteAllText(In(State.FileName), "this is not toml = = =\n");
        var state = new State(_dir).Load();
        Assert.False(state.PaidModWarned);

        state.MarkPaidModWarned();
        Assert.True(new State(_dir).Load().PaidModWarned);
    }

    [Fact]
    public void ALeftoverIniBesideTheTomlIsDeleted()
    {
        File.WriteAllText(In(State.FileName), "[FirstRun]\r\nPaidModWarned = true\r\n");
        File.WriteAllText(In(State.LegacyFileName), "[FirstRun]\r\nPaidModWarned=false\r\n");

        Assert.True(new State(_dir).Load().PaidModWarned);
        Assert.False(File.Exists(In(State.LegacyFileName)));
    }

    [Fact]
    public void TheCacheIsForOneExeAndClient()
    {
        const ulong hash = 0xABCD1234_00000000;
        var cache = new PatternCache(_dir, hash, "2026.09.24.12");
        Assert.Equal("ABCD1234.2026.09.24.12", cache.Section);
        Assert.Equal(0u, cache.Load("48 8D ? ?"));
        cache.Save("48 8D ? ?", 4096);
        cache.Save("4C 8B ?", 0);
        cache.Save("48 8D ? ?", 8192);
        Assert.Equal(8192u, new PatternCache(_dir, hash, "2026.09.24.12").Load("48 8D ? ?"));
        Assert.StartsWith("Exe = \"ABCD1234\"\r\nClient = \"2026.09.24.12\"\r\n\r\n[Patterns]\r\n", File.ReadAllText(In(PatternCache.FileName)));

        // Another client version, or another exe, starts over.
        var next = new PatternCache(_dir, hash, "2026.09.25.01");
        Assert.Equal(0u, next.Load("48 8D ? ?"));
        next.Save("E8 ? ?", 12);
        Assert.Equal(0u, new PatternCache(_dir, 0x11111111_00000000, "2026.09.25.01").Load("E8 ? ?"));
        Assert.Equal(12u, new PatternCache(_dir, hash, "2026.09.25.01").Load("E8 ? ?"));
        Assert.DoesNotContain("2026.09.24.12", File.ReadAllText(In(PatternCache.FileName)));
    }

    [Fact]
    public void TheLegacyCacheIsDeletedAndABrokenOneStartsOver()
    {
        File.WriteAllText(In(PatternCache.LegacyFileName), "[ABCD1234.2026.09.24.12]\r\n48 8D ? ?=4096\r\n");
        File.WriteAllText(In(PatternCache.FileName), "this is not toml = = =\n");

        var cache = new PatternCache(_dir, 0xABCD1234_00000000, "2026.09.24.12");
        Assert.False(File.Exists(In(PatternCache.LegacyFileName)));
        Assert.Equal(0u, cache.Load("48 8D ? ?"));
        cache.Save("48 8D ? ?", 4096);
        Assert.Equal(4096u, new PatternCache(_dir, 0xABCD1234_00000000, "2026.09.24.12").Load("48 8D ? ?"));
    }

    [Fact]
    public void AZeroHashTurnsTheCacheOff()
    {
        var cache = new PatternCache(_dir, 0, "2026.09.24.12");
        Assert.Null(cache.Section);
        cache.Save("48 8D ? ?", 4096);
        Assert.Equal(0u, cache.Load("48 8D ? ?"));
        Assert.False(File.Exists(In(PatternCache.FileName)));
    }
}
