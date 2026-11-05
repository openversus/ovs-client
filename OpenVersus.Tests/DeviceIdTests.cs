using System.Text.Json;
using OpenVersus.Identity;
using OpenVersus.Net;

namespace OpenVersus.Tests;

/// <summary>
/// The device identifiers of the registration: what is read, which values count, how each is hashed, and that they
/// reach the body (and the log only as present or unavailable).
/// </summary>
public class DeviceIdTests
{
    private const string MachineGuid = "4c4c4544-0032-3910-8053-b4c04f4b4d32";
    private const string UeMachineId = "0F1E2D3C4B5A69788796A5B4C3D2E1F0";
    private const string OsMachineId = "9f86d081884c7d659a2feaa0c55ad015";
    private const string BoardSerial = "PF2ABC12";
    private static readonly byte[] s_uuidBytes = [0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF];
    private const string Uuid = "33221100-5544-7766-8899-aabbccddeeff";

    private static string Sha(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void TheHashIsDomainSeparatedTrimmedAndLowercased()
    {
        Assert.Equal(Sha("openversus-device-v3|ueMachineId|0f1e2d3c4b5a69788796a5b4c3d2e1f0"), DeviceIdentity.Hash(DeviceIdentity.UeMachineIdKind, $"  {UeMachineId} \n"));
        // The registry spells the engine's id in upper case and a crash report's LoginId in lower case: one hash.
        Assert.Equal(DeviceIdentity.Hash(DeviceIdentity.UeMachineIdKind, UeMachineId), DeviceIdentity.Hash(DeviceIdentity.UeMachineIdKind, UeMachineId.ToLowerInvariant()));
        Assert.NotEqual(DeviceIdentity.Hash(DeviceIdentity.MachineGuidKind, MachineGuid), DeviceIdentity.Hash(DeviceIdentity.UeMachineIdKind, MachineGuid));
        Assert.NotEqual(DeviceIdentity.Hash(DeviceIdentity.MachineGuidKind, MachineGuid), EnvInfo.ComputeHardwareIdV2(BoardSerial, MachineGuid));
    }

    [Theory]
    [InlineData(Uuid, true)]
    [InlineData("4C4C4544-0032-3910-8053-B4C04F4B4D32", true)]
    [InlineData("00000000-0000-0000-0000-000000000000", false)]
    [InlineData("ffffffff-ffff-ffff-ffff-ffffffffffff", false)]
    [InlineData("03000200-0400-0500-0006-000700080009", false)]
    [InlineData("00020003-0004-0005-0006-000700080009", false)]
    [InlineData("11111111-1111-1111-1111-111111111111", false)]
    [InlineData("4c4c4544003239108053b4c04f4b4d32", false)]
    [InlineData("", false)]
    public void OnlyRealSystemUuidsAreSent(string uuid, bool strong) => Assert.Equal(strong, DeviceIdentity.IsStrongUuid(uuid));

    [Theory]
    [InlineData(UeMachineId, true)]
    [InlineData(OsMachineId, true)]
    [InlineData(" " + OsMachineId + "\n", true)]
    [InlineData("00000000000000000000000000000000", false)]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f", false)]
    [InlineData("0f1e2d3c4b5a69788796a5b4c3d2e1f0ff", false)]
    [InlineData("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", false)]
    [InlineData("uninitialized", false)]
    [InlineData("", false)]
    public void OnlyRealMachineIdsAreSent(string id, bool strong) => Assert.Equal(strong, DeviceIdentity.IsStrongHex32(id));

    [Fact]
    public void TheFirmwareTableIsParsed()
    {
        var info = Smbios.Parse(Table(Type1("VMware, Inc.", "VMware Virtual Platform", s_uuidBytes), Type2("Intel", "Board", BoardSerial), Type2("Intel", "Riser", "")));

        Assert.Equal("VMware, Inc.", info.Manufacturer);
        Assert.Equal("VMware Virtual Platform", info.Product);
        Assert.Equal(Uuid, info.SystemUuid);
        Assert.Equal([BoardSerial, ""], info.BoardSerials);
        Assert.True(DeviceIdentity.IsVirtualMachine(info));
    }

    [Fact]
    public void AnAbsentUuidAndAShortOrEmptyTableGiveNothing()
    {
        Assert.Equal("", Smbios.Parse(Table(Type1("Dell Inc.", "OptiPlex", new byte[16]))).SystemUuid);
        Assert.Equal("", Smbios.FormatUuid(Enumerable.Repeat((byte)0xFF, 16).ToArray()));
        Assert.Same(SmbiosInfo.Empty, Smbios.Parse([]));
        var empty = Smbios.Parse(Table());
        Assert.Equal(("", "", ""), (empty.Manufacturer, empty.Product, empty.SystemUuid));
        Assert.Empty(empty.BoardSerials);
        Assert.Null(Smbios.TryRead(() => throw new DllNotFoundException("kernel32")));
        Assert.Null(Smbios.TryRead(() => null));
    }

    [Theory]
    [InlineData("Dell Inc.", "OptiPlex 7090", false)]
    [InlineData("ASUS", "System Product Name", false)]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)", true)]
    [InlineData("innotek GmbH", "VirtualBox", true)]
    [InlineData("Microsoft Corporation", "Virtual Machine", true)]
    [InlineData("Parallels Software International Inc.", "Parallels Virtual Platform", true)]
    [InlineData("Xen", "HVM domU", true)]
    [InlineData("", "", false)]
    public void TheVmFlagComesFromTheFirmware(string manufacturer, string product, bool vm) =>
        Assert.Equal(vm, DeviceIdentity.IsVirtualMachine(new SmbiosInfo(manufacturer, product, "", [])));

    [Fact]
    public void ProtonSendsThePrefixsAndLinuxsIdsAndNoV2Fingerprint()
    {
        var read = new List<string>();
        var sources = new DeviceSources
        {
            ReadLocalMachine = (key, value) => key == DeviceIdentity.MachineGuidKey && value == DeviceIdentity.MachineGuidValue ? MachineGuid : "",
            ReadCurrentUser = (key, value) => key == DeviceIdentity.UeMachineIdKey && value == DeviceIdentity.UeMachineIdValue ? UeMachineId : "",
            // Wine's table: the host's machine id stands in for the serial and the UUID (seen with Wine 11.20).
            ReadFirmware = () => Table(Type1("ASUS", "System Product Name", System.Security.Cryptography.SHA256.HashData([1])[..16]), Type2("Asus", "PRIME", OsMachineId.ToUpperInvariant())),
            ReadTextFile = path => { read.Add(path); return OsMachineId + "\n"; },
        };

        var env = new EnvInfo(RuntimeEnvironment.Proton, sources);

        Assert.Equal([@"Z:\etc\machine-id"], read);
        Assert.Equal(DeviceIdentity.Hash("machineGuid", MachineGuid), env.DeviceIds.MachineGuid);
        Assert.Equal(DeviceIdentity.Hash("ueMachineId", UeMachineId), env.DeviceIds.UeMachineId);
        Assert.Equal(DeviceIdentity.Hash("osMachineId", OsMachineId), env.DeviceIds.OsMachineId);
        // Taken as the table carries them: unique per OS install, whatever Wine filled them from.
        Assert.Equal(DeviceIdentity.Hash("boardSerial", OsMachineId), env.DeviceIds.BoardSerial);
        Assert.NotNull(env.DeviceIds.SystemUuid);
        Assert.False(env.VirtualMachine);
        Assert.Equal("", env.HardwareId);
        Assert.Equal(OsMachineId.ToUpperInvariant(), env.MotherboardSerial);
    }

    [Fact]
    public void WindowsKeepsTheV2FingerprintAndNeverReadsTheLinuxId()
    {
        var sources = new DeviceSources
        {
            ReadLocalMachine = (_, _) => MachineGuid,
            ReadCurrentUser = (_, _) => UeMachineId,
            ReadFirmware = () => Table(Type1("Dell Inc.", "OptiPlex 7090", s_uuidBytes), Type2("Dell Inc.", "0K240Y", " " + BoardSerial + " ")),
            ReadTextFile = _ => throw new InvalidOperationException("Z: is a real drive on Windows"),
        };

        var env = new EnvInfo(RuntimeEnvironment.NativeWindows, sources);

        // Exactly as read, spaces included: the hash the server in production holds.
        Assert.Equal(" " + BoardSerial + " ", env.MotherboardSerial);
        Assert.Equal(EnvInfo.ComputeHardwareIdV2(" " + BoardSerial + " ", MachineGuid), env.HardwareId);
        Assert.Equal("2", env.HardwareIdVersion);
        Assert.Equal(DeviceIdentity.Hash("machineGuid", MachineGuid), env.DeviceIds.MachineGuid);
        Assert.Equal(DeviceIdentity.Hash("boardSerial", BoardSerial), env.DeviceIds.BoardSerial);
        Assert.Equal(DeviceIdentity.Hash("systemUuid", Uuid), env.DeviceIds.SystemUuid);
        Assert.Equal(DeviceIdentity.Hash("ueMachineId", UeMachineId), env.DeviceIds.UeMachineId);
        Assert.Null(env.DeviceIds.OsMachineId);
    }

    [Fact]
    public void APlaceholderSerialStillLeavesTheOtherIds()
    {
        var sources = new DeviceSources
        {
            ReadLocalMachine = (_, _) => MachineGuid,
            ReadFirmware = () => Table(Type1("ASUS", "System Product Name", s_uuidBytes), Type2("ASUSTeK", "PRIME", "Default string")),
        };

        var env = new EnvInfo(RuntimeEnvironment.NativeWindows, sources);

        Assert.Equal("", env.HardwareId);
        Assert.Null(env.DeviceIds.BoardSerial);
        Assert.NotNull(env.DeviceIds.MachineGuid);
        Assert.NotNull(env.DeviceIds.SystemUuid);
        Assert.Null(env.DeviceIds.UeMachineId);
    }

    [Fact]
    public void AMachineWithoutIdsSendsNoDeviceIds()
    {
        var env = new EnvInfo(RuntimeEnvironment.Wine, new DeviceSources());
        Assert.False(env.DeviceIds.HasAny());

        using var doc = JsonDocument.Parse(IdentityRegistration.Body(env));
        Assert.False(doc.RootElement.TryGetProperty("deviceIds", out _));
        Assert.Equal("wine", doc.RootElement.GetProperty("runtime").GetString());
        Assert.False(doc.RootElement.GetProperty("virtualMachine").GetBoolean());
    }

    /// <summary>Every property of the record reaches the body under its camelCase name (a Proton machine can have all five).</summary>
    [Fact]
    public void EveryDeviceIdReachesTheBodyUnderItsOwnName()
    {
        var sources = new DeviceSources
        {
            ReadLocalMachine = (_, _) => MachineGuid,
            ReadCurrentUser = (_, _) => UeMachineId,
            ReadFirmware = () => Table(Type1("QEMU", "Standard PC (Q35 + ICH9, 2009)", s_uuidBytes), Type2("QEMU", "Board", BoardSerial)),
            ReadTextFile = _ => OsMachineId,
        };
        var env = new EnvInfo(RuntimeEnvironment.Proton, sources);

        using var doc = JsonDocument.Parse(IdentityRegistration.Body(env));
        var ids = doc.RootElement.GetProperty("deviceIds");
        string[] expected = typeof(DeviceIds).GetProperties().Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]).ToArray();
        Assert.Equal(expected, ids.EnumerateObject().Select(p => p.Name));
        Assert.Equal(DeviceIdentity.Hash("osMachineId", OsMachineId), ids.GetProperty("osMachineId").GetString());
        Assert.Equal(DeviceIdentity.Hash("systemUuid", Uuid), ids.GetProperty("systemUuid").GetString());
        Assert.Equal("proton", doc.RootElement.GetProperty("runtime").GetString());
        Assert.True(doc.RootElement.GetProperty("virtualMachine").GetBoolean());
    }

    [Fact]
    public void TheEnginesMachineIdIsPickedUpOnALaterRegistration()
    {
        // Absent when the env is built and when the first registration looks again; there by the second one.
        int reads = 0;
        var sources = new DeviceSources { ReadCurrentUser = (_, _) => reads++ < 2 ? "" : UeMachineId };
        var env = new EnvInfo(RuntimeEnvironment.Wine, sources) { SteamId = "76561198000000001", IsSteam = true };
        Assert.Null(env.DeviceIds.UeMachineId);
        var http = new Posts(HttpResult.Failed("no response"), new HttpResult(true, 200, "{\"ok\":true,\"token\":\"t\"}"u8.ToArray(), null));

        IdentityRegistration.Run(env, "http://ovs.test", http, new ServerIdentity(), new ListLogger(), _ => { }, resolveSteamTicket: () => "");

        Assert.Equal(2, http.Bodies.Count);
        Assert.DoesNotContain("ueMachineId", http.Bodies[0]);
        Assert.Contains($"\"ueMachineId\":\"{DeviceIdentity.Hash("ueMachineId", UeMachineId)}\"", http.Bodies[1]);
    }

    [Fact]
    public void NeitherTheValuesNorTheHashesAreLogged()
    {
        var sources = new DeviceSources
        {
            ReadLocalMachine = (_, _) => MachineGuid,
            ReadCurrentUser = (_, _) => UeMachineId,
            ReadFirmware = () => Table(Type1("Dell Inc.", "OptiPlex 7090", s_uuidBytes), Type2("Dell Inc.", "0K240Y", BoardSerial)),
            ReadTextFile = _ => OsMachineId,
        };
        var env = new EnvInfo(RuntimeEnvironment.NativeWindows, sources) { SteamId = "76561198000000001", IsSteam = true };
        var log = new ListLogger();
        IdentityRegistration.Run(env, "http://ovs.test", new Posts(new HttpResult(true, 200, "{\"ok\":true}"u8.ToArray(), null)), new ServerIdentity(), log, _ => { }, resolveSteamTicket: () => "");

        string[] secrets = [MachineGuid, UeMachineId, OsMachineId, BoardSerial, Uuid, env.DeviceIds.MachineGuid!, env.DeviceIds.UeMachineId!, env.DeviceIds.BoardSerial!, env.DeviceIds.SystemUuid!];
        foreach (string line in log.Lines.Append(env.Print()))
        {
            Assert.DoesNotContain(secrets, s => line.Contains(s, StringComparison.OrdinalIgnoreCase));
        }

        Assert.Contains("DeviceIDs  : machineGuid present, boardSerial present, systemUuid present, ueMachineId present, osMachineId unavailable", env.Print());
    }

    // An SMBIOS table as GetSystemFirmwareTable returns it: the 8-byte header, the structures, then type 127.
    private static byte[] Table(params byte[][] structures)
    {
        byte[] body = [.. structures.SelectMany(s => s), 127, 4, 0, 0, 0, 0];
        return [0, 3, 2, 0, .. BitConverter.GetBytes(body.Length), .. body];
    }

    private static byte[] Type1(string manufacturer, string product, byte[] uuid) =>
        [1, 0x1B, 0, 0, 1, 2, 0, 0, .. uuid, 6, 0, 0, .. Strings(manufacturer, product)];

    private static byte[] Type2(string manufacturer, string product, string serial) =>
        [2, 0x0F, 0, 0, 1, 2, 0, (byte)(serial.Length > 0 ? 3 : 0), 0, 0, 0, 0, 0, 10, 0, .. Strings(manufacturer, product, serial)];

    private static byte[] Strings(params string[] values) =>
        [.. values.Where(v => v.Length > 0).SelectMany(v => System.Text.Encoding.UTF8.GetBytes(v).Append((byte)0)), 0];

    private sealed class Posts(params HttpResult[] results) : IHttpTransport
    {
        private int _next;

        public List<string> Bodies { get; } = [];

        public HttpResult Get(Uri url, TimeSpan timeout) => throw new NotSupportedException();

        public HttpResult Post(Uri url, string contentType, ReadOnlySpan<byte> body, TimeSpan timeout)
        {
            Bodies.Add(System.Text.Encoding.UTF8.GetString(body));
            return _next < results.Length ? results[_next++] : HttpResult.Failed("no response");
        }
    }
}
