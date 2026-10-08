using System.Text.Json;
using OpenVersus.Config;
using OpenVersus.Identity;
using OpenVersus.Net;

namespace OpenVersus.Tests;

public class IdentityTests : IDisposable
{
    private const string InstallId = "0123456789abcdef0123456789abcdef";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.eyJpZCI6IiJ9.c2lnbmF0dXJl";
    private readonly string _dir = Directory.CreateTempSubdirectory("ovs-identity-").FullName;

    public void Dispose()
    {
        UnixPermissions.Restore(_dir);
        Directory.Delete(_dir, recursive: true);
    }

    private string In(string name) => Path.Combine(_dir, name);

    // Install id

    [Fact]
    public void AnInstallIdIsCreatedOnceAndKept()
    {
        var state = new State(_dir).Load();
        Assert.Equal("", state.InstallId);

        string id = state.LoadOrCreateInstallId();
        Assert.True(State.IsValidInstallId(id));
        Assert.Equal(id, id.ToLowerInvariant());
        Assert.Equal(id, state.LoadOrCreateInstallId());
        Assert.Equal(id, new State(_dir).Load().InstallId);
        Assert.Equal(id, new State(_dir).Load().LoadOrCreateInstallId());
        Assert.Contains($"[Identity]\r\nInstallId = \"{id}\"", File.ReadAllText(In(State.FileName)));
    }

    [Fact]
    public void TheInstallIdSitsBesideTheAgreement()
    {
        var state = new State(_dir).Load();
        string id = state.LoadOrCreateInstallId();
        state.MarkPaidModWarned();

        var again = new State(_dir).Load();
        Assert.True(again.PaidModWarned);
        Assert.Equal(id, again.InstallId);
    }

    [Theory]
    [InlineData("[Identity]\r\nInstallId=" + InstallId + "\r\n", false)]
    [InlineData("[FirstRun]\r\nPaidModWarned=true\r\n[Identity]\r\nInstallId=" + InstallId + "\r\n", true)]
    public void TheCppInstallIdIsCarriedOver(string ini, bool warned)
    {
        File.WriteAllText(In(State.LegacyFileName), ini);

        var state = new State(_dir).Load();
        Assert.Equal(InstallId, state.InstallId);
        Assert.Equal(warned, state.PaidModWarned);
        Assert.Equal(InstallId, state.LoadOrCreateInstallId());
        Assert.False(File.Exists(In(State.LegacyFileName)));
        Assert.Equal(InstallId, new State(_dir).Load().InstallId);
    }

    [Fact]
    public void TheCppInstallIdIsCarriedIntoAnEarlierBuildsToml()
    {
        // An earlier C# build wrote OVSState.toml without an install id.
        File.WriteAllText(In(State.FileName), "[FirstRun]\r\nPaidModWarned = true\r\n");
        File.WriteAllText(In(State.LegacyFileName), "[Identity]\r\nInstallId=" + InstallId.ToUpperInvariant() + "\r\n");

        var state = new State(_dir).Load();
        Assert.True(state.PaidModWarned);
        Assert.Equal(InstallId.ToUpperInvariant(), state.InstallId);
        Assert.False(File.Exists(In(State.LegacyFileName)));
    }

    [Fact]
    public void TheTomlsOwnInstallIdWinsOverALeftoverIni()
    {
        const string Own = "fedcba9876543210fedcba9876543210";
        File.WriteAllText(In(State.FileName), $"[Identity]\r\nInstallId = \"{Own}\"\r\n");
        File.WriteAllText(In(State.LegacyFileName), "[Identity]\r\nInstallId=" + InstallId + "\r\n");

        Assert.Equal(Own, new State(_dir).Load().InstallId);
        Assert.False(File.Exists(In(State.LegacyFileName)));
    }

    [Theory]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void ABrokenInstallIdIsReplaced(string broken)
    {
        File.WriteAllText(In(State.LegacyFileName), "[Identity]\r\nInstallId=" + broken + "\r\n");

        var state = new State(_dir).Load();
        Assert.Equal("", state.InstallId);
        string id = state.LoadOrCreateInstallId();
        Assert.True(State.IsValidInstallId(id));
        Assert.NotEqual(broken, id);
    }

    [SkippableFact]
    public void AnInstallIdThatCannotBeStoredIsNotUsed()
    {
        UnixPermissions.SkipUnlessUnix();
        Directory.CreateDirectory(In(State.FileName) + ".tmp");

        Assert.Equal("", new State(_dir).Load().LoadOrCreateInstallId());
    }

    [Theory]
    [InlineData("[Identity]\r\nInstallId = \"" + InstallId + "\"\r\n[FirstRun\r\nPaidModWarned = true\r\n")]
    [InlineData("InstallId=" + InstallId + "\r\n= = =\r\n")]
    [InlineData("[Identity]\nInstallId = '" + InstallId + "'\n[Identity]\nInstallId = '" + InstallId + "'\n")]
    public void ABrokenStateFileKeepsItsInstallId(string broken)
    {
        File.WriteAllText(In(State.FileName), broken);

        var state = new State(_dir).Load();
        Assert.Equal(InstallId, state.InstallId);
        Assert.Equal(InstallId, state.LoadOrCreateInstallId());
        Assert.Equal(broken, File.ReadAllText(In(State.BrokenFileName)));
        Assert.Empty(TomlConfig.Load(In(State.FileName)).Errors);
        Assert.Equal(InstallId, new State(_dir).Load().InstallId);
    }

    [Fact]
    public void AnInstallIdIsSalvagedFromAFileThatIsNotUtf8()
    {
        byte[] broken = [.. System.Text.Encoding.ASCII.GetBytes($"[Identity]\r\nInstallId = \"{InstallId}\"\r\nName = \""), 0xFF, 0xFE, .. "\"\r\n"u8];
        File.WriteAllBytes(In(State.FileName), broken);

        Assert.Equal(InstallId, new State(_dir).Load().InstallId);
        Assert.Equal(broken, File.ReadAllBytes(In(State.BrokenFileName)));
    }

    [Theory]
    [InlineData("= = =\r\n")]
    [InlineData("InstallId = \"0123456789abcdef0123456789abcdef\"\r\nInstallId = \"fedcba9876543210fedcba9876543210\"\r\n= = =\r\n")]
    [InlineData("InstallId = \"0123456789abcdef\"\r\n= = =\r\n")]
    public void ABrokenStateFileWithoutOneClearIdIsKeptBeforeANewIdIsWritten(string broken)
    {
        File.WriteAllText(In(State.FileName), broken);

        var state = new State(_dir).Load();
        Assert.Equal("", state.InstallId);
        Assert.True(State.IsValidInstallId(state.LoadOrCreateInstallId()));
        Assert.Equal(broken, File.ReadAllText(In(State.BrokenFileName)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ABrokenStateFileThatCannotBeKeptIsNeverReplaced(bool hasId)
    {
        string broken = (hasId ? $"InstallId = \"{InstallId}\"\r\n" : "") + "= = =\r\n";
        File.WriteAllText(In(State.FileName), broken);
        Directory.CreateDirectory(In(State.BrokenFileName));

        var state = new State(_dir).Load();
        Assert.Equal(hasId ? InstallId : "", state.LoadOrCreateInstallId());
        Assert.Equal(broken, File.ReadAllText(In(State.FileName)));
    }

    [SkippableFact]
    public void AStateFileThatCannotBeReadIsNeverReplaced()
    {
        UnixPermissions.SkipUnlessUnix();
        string text = $"[Identity]\r\nInstallId = \"{InstallId}\"\r\n";
        File.WriteAllText(In(State.FileName), text);
        UnixPermissions.MakeUnreadable(In(State.FileName));

        var state = new State(_dir).Load();
        Assert.Equal("", state.LoadOrCreateInstallId());
        state.MarkPaidModWarned();
        UnixPermissions.Restore(In(State.FileName));
        Assert.Equal(text, File.ReadAllText(In(State.FileName)));
    }

    // Hardware id V2

    [Fact]
    public void TheHardwareIdIsTheCppV2Hash()
    {
        const string Guid = "4c4c4544-0032-3910-8053-b4c04f4b4d32";
        string id = EnvInfo.ComputeHardwareIdV2("PF2ABC12", Guid);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("openversus-device-v2|windows|PF2ABC12|" + Guid))), id);
    }

    [Theory]
    [InlineData("PF2ABC12", true)]
    [InlineData("  /7XK3PQ2/CNFCW0045B00FA/  ", true)]
    [InlineData("To Be Filled By O.E.M.", false)]
    [InlineData("Default string", false)]
    [InlineData("System Serial Number", false)]
    [InlineData("Not Specified", false)]
    [InlineData("Unknown", false)]
    [InlineData("SN-00000000-17", false)]
    [InlineData("FFFFFFFF99", false)]
    [InlineData("AB12", false)]
    [InlineData("A-B-1-2-", false)]
    [InlineData("111111111", false)]
    [InlineData("", false)]
    public void OnlyRealSerialsAreFingerprinted(string serial, bool strong) => Assert.Equal(strong, EnvInfo.IsStrongSerial(serial));

    [Theory]
    [InlineData("4c4c4544-0032-3910-8053-b4c04f4b4d32", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("4c4c4544-0032", false)]
    [InlineData("", false)]
    public void OnlyRealMachineGuidsAreFingerprinted(string guid, bool strong) => Assert.Equal(strong, EnvInfo.IsStrongMachineGuid(guid));

    [Theory]
    [InlineData(RuntimeEnvironment.CrossOver)]
    [InlineData(RuntimeEnvironment.Proton)]
    [InlineData(RuntimeEnvironment.Wine)]
    public void CompatibilityRuntimesSendNoFingerprintAndIgnoreTheSteamVariable(RuntimeEnvironment runtime)
    {
        var env = new EnvInfo(runtime);
        Assert.Equal("", env.HardwareId);
        Assert.Equal("", env.HardwareIdVersion);
        Assert.Equal("", env.HardwareIdQuality);
        Assert.Equal("Unknown", env.SteamId);
        Assert.False(env.IsSteam);
    }

    [Fact]
    public void TheSteamUserAccessorIsLookedUpByItsVersionedNames()
    {
        // MultiVersus ships Steamworks 1.53, whose steam_api64.dll exports only the versioned
        // SteamAPI_SteamUser_v021; with the bare name alone the API route never ran on Proton and
        // loginusers.vdf named someone else's account.
        Assert.Contains("SteamAPI_SteamUser_v021", SteamId.UserAccessorNames);
        Assert.Equal("SteamAPI_SteamUser", SteamId.UserAccessorNames[^1]);
    }

    // Runtime

    [Fact]
    public void RuntimesAreToldApartAsTheCppDid()
    {
        static Func<string, bool> Vars(params string[] set) => name => set.Contains(name);

        Assert.Equal(RuntimeEnvironment.NativeWindows, Runtime.Classify(false, Vars("CX_BOTTLE", "PROTON_VERSION"), true, "crossover"));
        Assert.Equal(RuntimeEnvironment.CrossOver, Runtime.Classify(true, Vars("CX_BOTTLE"), false, null));
        Assert.Equal(RuntimeEnvironment.CrossOver, Runtime.Classify(true, Vars("CX_ROOT", "STEAM_COMPAT_DATA_PATH"), false, null));
        Assert.Equal(RuntimeEnvironment.CrossOver, Runtime.Classify(true, Vars(), true, null));
        Assert.Equal(RuntimeEnvironment.CrossOver, Runtime.Classify(true, Vars(), false, "wine-8.0 (CrossOver FOSS 24.0.0)"));
        Assert.Equal(RuntimeEnvironment.Proton, Runtime.Classify(true, Vars("STEAM_COMPAT_DATA_PATH"), false, "wine-9.0"));
        Assert.Equal(RuntimeEnvironment.Proton, Runtime.Classify(true, Vars("STEAM_COMPAT_CLIENT_INSTALL_PATH"), false, null));
        Assert.Equal(RuntimeEnvironment.Proton, Runtime.Classify(true, Vars("PROTON_VERSION"), false, null));
        Assert.Equal(RuntimeEnvironment.Wine, Runtime.Classify(true, Vars(), false, "wine-9.0"));
    }

    // Registration

    [Fact]
    public void TheBodyCarriesEveryFieldTheServerReads()
    {
        var env = new EnvInfo(RuntimeEnvironment.CrossOver) { SteamId = "7656119\"quoted\\", InstallId = InstallId, SteamTicket = "14000000AABB" };
        using var doc = JsonDocument.Parse(IdentityRegistration.Body(env));
        Assert.Equal("14000000AABB", doc.RootElement.GetProperty("steamTicket").GetString());
        var root = doc.RootElement;
        Assert.Equal(
            ["steamId", "steamTicket", "epicId", "hardwareId", "hardwareIdVersion", "hardwareIdQuality", "installId", "clientVersion", "nodePort"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(0, root.GetProperty("nodePort").GetInt32());
        env.NodePort = 51561;
        using var withPort = JsonDocument.Parse(IdentityRegistration.Body(env));
        Assert.Equal(51561, withPort.RootElement.GetProperty("nodePort").GetInt32());
        Assert.Equal("7656119\"quoted\\", root.GetProperty("steamId").GetString());
        Assert.Equal(InstallId, root.GetProperty("installId").GetString());
        Assert.Equal("", root.GetProperty("hardwareId").GetString());
        Assert.Equal(OvsVersion.Current, root.GetProperty("clientVersion").GetString());
    }

    [Fact]
    public void RegistrationRetriesUntilAnswered_AndKeepsTheToken()
    {
        var http = new ScriptedPosts(
            HttpResult.Failed("WinHttpSendRequest failed (12029)"),
            new HttpResult(false, 502, [], null),
            Json($"{{\"ok\":true,\"token\":\"{Token}\",\"accountId\":null}}"));
        var identity = new ServerIdentity { InstallId = InstallId };
        var env = SteamEnv();
        env.InstallId = InstallId;
        var slept = new List<TimeSpan>();

        IdentityRegistration.Run(env, "http://ovs.test", http, identity, new ListLogger(), slept.Add);

        Assert.Equal(3, http.Posts.Count);
        Assert.Equal(IdentityRegistration.RetryDelays[..2], slept);
        Assert.Equal("http://ovs.test/api/identify", http.Posts[0].Url);
        Assert.Contains(InstallId, http.Posts[0].Body);
        Assert.Equal(Token, identity.Token);
    }

    [Fact]
    public void TheRegistrationWaitsForTheNodesPortAndSendsIt()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = SteamEnv();
        var waits = new List<TimeSpan>();

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), _ => Assert.Fail("slept"), waitForNodePort: t => { waits.Add(t); return 41234; });

        Assert.Equal([IdentityRegistration.NodePortWait], waits);
        Assert.Single(http.Posts);
        Assert.Contains("\"nodePort\":41234", http.Posts[0].Body);
    }

    [Fact]
    public void APortReportedLateIsRegisteredAgain()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"), Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = SteamEnv();
        var waits = new List<TimeSpan>();
        var log = new ListLogger();

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), log, _ => Assert.Fail("slept"), waitForNodePort: t => { waits.Add(t); return waits.Count == 1 ? 0 : 50000; });

        Assert.Equal([IdentityRegistration.NodePortWait, IdentityRegistration.LateNodePortWait], waits);
        Assert.Equal(2, http.Posts.Count);
        Assert.Contains("\"nodePort\":0", http.Posts[0].Body);
        Assert.Contains("\"nodePort\":50000", http.Posts[1].Body);
        Assert.Contains(log.Lines, l => l.Contains("registering again"));
    }

    [Fact]
    public void ANodeThatNeverReportsAPortLeavesOneRegistrationAndAWarning()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var log = new ListLogger();

        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, new ServerIdentity(), log, _ => Assert.Fail("slept"), waitForNodePort: _ => 0);

        Assert.Single(http.Posts);
        Assert.Contains("\"nodePort\":0", http.Posts[0].Body);
        Assert.Contains(log.Lines, l => l.Contains("never reported a port"));
    }

    [Fact]
    public void WithoutANodeNothingWaitsAndThePortIsZero()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, new ServerIdentity(), new ListLogger(), _ => Assert.Fail("slept"));
        Assert.Contains("\"nodePort\":0", http.Posts[0].Body);
    }

    [Fact]
    public void RegistrationGivesUpAfterTheCppRetries()
    {
        var http = new ScriptedPosts();
        var log = new ListLogger();
        var slept = new List<TimeSpan>();

        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, new ServerIdentity(), log, slept.Add);

        Assert.Equal(IdentityRegistration.RetryDelays.Length + 1, http.Posts.Count);
        Assert.Equal(IdentityRegistration.RetryDelays, slept);
        Assert.Contains(log.Lines, l => l.Contains("no answer after retries"));
    }

    [Fact]
    public void ARefusalIsNotRetried()
    {
        var http = new ScriptedPosts(new HttpResult(false, 426, System.Text.Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"client_update_required\"}"), null));
        var log = new ListLogger();
        var identity = new ServerIdentity { Token = Token };

        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, identity, log, _ => Assert.Fail("slept"));

        Assert.Single(http.Posts);
        Assert.Contains(log.Lines, l => l.Contains("HTTP 426, client_update_required"));
        Assert.Equal(Token, identity.Token);
    }

    [Fact]
    public void ASteamLaunchSendsTheSteamIdInItsOnlyRegistration()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = new EnvInfo(RuntimeEnvironment.Proton) { GameId = "1818750", AppId = "1818750" };
        int resolved = 0;

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), resolveSteamId: () =>
        {
            Assert.Empty(http.Posts);
            resolved++;
            return "76561197960573826";
        });

        Assert.Equal(1, resolved);
        Assert.Contains("\"steamId\":\"76561197960573826\"", Assert.Single(http.Posts).Body);
    }

    [Fact]
    public void ASteamLaunchWithoutASteamIdRegistersOnceAndWaitsOnce()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = new EnvInfo(RuntimeEnvironment.Proton) { AppId = "1818750" };
        int resolved = 0;

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), resolveSteamId: () =>
        {
            resolved++;
            return "";
        });

        Assert.Equal(1, resolved);
        Assert.Contains("\"steamId\":\"Unknown\"", Assert.Single(http.Posts).Body);
    }

    [Theory]
    [InlineData("76561197960573826", 2)]
    [InlineData("", 1)]
    public void ALaunchNotThroughSteamRegistersAtOnce(string steamId, int posts)
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"), Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = new EnvInfo(RuntimeEnvironment.Proton) { GameId = "Unknown", AppId = "" };

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), resolveSteamId: () =>
        {
            Assert.Single(http.Posts);
            return steamId;
        });

        Assert.Equal(posts, http.Posts.Count);
        if (posts == 2)
        {
            Assert.Contains($"\"steamId\":\"{steamId}\"", http.Posts[1].Body);
        }
    }

    [Fact]
    public void TheInstallIdIsNeverLogged()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var log = new ListLogger();
        var env = SteamEnv();
        env.InstallId = InstallId;

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), log);

        Assert.DoesNotContain(log.Lines, l => l.Contains(InstallId) || l.Contains(Token));
    }

    // Headers on the client's own server calls

    [Fact]
    public void TheServerCallsCarryTheInstallIdAndToken()
    {
        var identity = new ServerIdentity();
        Assert.Equal("", identity.Headers());

        identity.InstallId = InstallId;
        Assert.Equal($"X-Install-Id: {InstallId}\r\n", identity.Headers());

        identity.Token = Token;
        Assert.Equal($"X-Install-Id: {InstallId}\r\nx-hydra-access-token: {Token}\r\n", identity.Headers());
    }

    [Theory]
    [InlineData("abc\r\nX-Evil: 1")]
    [InlineData("abc def")]
    [InlineData("")]
    public void ATokenThatIsNotAJwtIsDropped(string token)
    {
        var identity = new ServerIdentity { Token = Token };
        identity.Token = token;
        Assert.Equal("", identity.Token);
        Assert.Equal("", identity.Headers());
    }

    [Fact]
    public void AnInvalidInstallIdIsNotSent()
    {
        var identity = new ServerIdentity { InstallId = "not-an-install-id\r\nX-Evil: 1" };
        Assert.Equal("", identity.Headers());
    }

    [Fact]
    public void TheIdentifyResponseForgivesTheServer()
    {
        Assert.Equal(Token, IdentityRegistration.ParseResponse($"{{\"ok\":1,\"token\":\"{Token}\",\"accountId\":\"abc\"}}")!.Token);
        Assert.Null(IdentityRegistration.ParseResponse("{\"ok\":true,\"token\":null}")!.Token);
        Assert.Null(IdentityRegistration.ParseResponse("<html>502</html>"));
    }

    /// <summary>An env with a Steam id, so registration does not wait for the Steam API.</summary>
    [Fact]
    public void ASteamClientSendsItsSessionTicketWithEveryRegistration()
    {
        var http = new ScriptedPosts(HttpResult.Failed("WinHttpSendRequest failed (12029)"), Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        int fetched = 0;

        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, new ServerIdentity(), new ListLogger(), _ => { }, resolveSteamTicket: () => { fetched++; return "14000000AABBCCDD"; });

        Assert.Equal(1, fetched);
        Assert.All(http.Posts, p => Assert.Contains("\"steamTicket\":\"14000000AABBCCDD\"", p.Body));
    }

    [Fact]
    public void ASteamIdThatTurnsUpLateBringsItsTicket()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"), Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var env = new EnvInfo(RuntimeEnvironment.Wine) { InstallId = InstallId };
        var fetched = new List<string>();

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), resolveSteamId: () => "76561198000000002",
            resolveSteamTicket: () => { fetched.Add(env.SteamId); return "14000000EEFF"; });

        Assert.Equal(["76561198000000002"], fetched);
        Assert.Equal(2, http.Posts.Count);
        Assert.Contains("\"steamTicket\":\"\"", http.Posts[0].Body);
        Assert.Contains("\"steamTicket\":\"14000000EEFF\"", http.Posts[1].Body);
    }

    [Fact]
    public void WithoutATicketTheRegistrationWarnsAndGoesOn()
    {
        var http = new ScriptedPosts(Json($"{{\"ok\":true,\"token\":\"{Token}\"}}"));
        var log = new ListLogger();

        IdentityRegistration.Run(SteamEnv(), "http://ovs.test", http, new ServerIdentity(), log, _ => Assert.Fail("slept"), resolveSteamTicket: () => "");

        Assert.Single(http.Posts);
        Assert.Contains(log.Lines, l => l.Contains("no Steam session ticket"));
    }

    private static EnvInfo SteamEnv() => new(RuntimeEnvironment.Wine) { SteamId = "76561198000000001", IsSteam = true };

    private static HttpResult Json(string body) => new(true, 200, System.Text.Encoding.UTF8.GetBytes(body), null);

    /// <summary>Answers each POST with the next scripted result, then with no response at all.</summary>
    private sealed class ScriptedPosts(params HttpResult[] results) : IHttpTransport
    {
        private int _next;

        public List<(string Url, string Body)> Posts { get; } = [];

        public HttpResult Get(Uri url, TimeSpan timeout) => throw new NotSupportedException();

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout)
        {
            Posts.Add((url.ToString(), System.Text.Encoding.UTF8.GetString(body)));
            return _next < results.Length ? results[_next++] : HttpResult.Failed("no response");
        }
    }
}
