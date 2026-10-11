using OpenVersus.Identity;
using OpenVersus.Memory;

namespace OpenVersus.Tests;

public class EpicIdentityTests
{
    private static string GameExe => Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", ".local/share/Steam/steamapps/common/MultiVersus/MultiVersus/Binaries/Win64/MultiVersus-Win64-Shipping.exe");

    // The delay-load import slot

    [SkippableFact]
    public void TheGamesTickSlotIsFoundByName()
    {
        Skip.If(!File.Exists(GameExe), "game exe not installed here");
        byte[] mapped = PeImage.MapFile(File.ReadAllBytes(GameExe));

        // The slot pefile reports for EOS_Platform_Tick in the final build, inside .data.
        Assert.Equal(0x800DCC0u, PeImage.DelayImportSlot(mapped, EpicIdentity.ModuleName, "EOS_Platform_Tick"));
        Assert.Equal(0x800DFA0u, PeImage.DelayImportSlot(mapped, "eossdk-win64-shipping.DLL", "EOS_Platform_Create"));

        // Not delay-imported by the game (the SDK exports it; the probe binds it itself) and not a module of the game's.
        Assert.Equal(0u, PeImage.DelayImportSlot(mapped, EpicIdentity.ModuleName, "EOS_Auth_CopyIdToken"));
        Assert.Equal(0u, PeImage.DelayImportSlot(mapped, "nosuch.dll", "EOS_Platform_Tick"));
    }

    [Fact]
    public void AnImageWithoutDelayImportsHasNoSlot()
    {
        // A PE32+ header with 16 empty data directories: no delay-load directory, so nothing to search.
        byte[] image = new byte[0x400];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(image, 0x3C);
        image[0x80] = (byte)'P';
        image[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)0x20B).CopyTo(image, 0x80 + 24);
        BitConverter.GetBytes(16u).CopyTo(image, 0x80 + 24 + 108);
        Assert.Equal(0u, PeImage.DelayImportSlot(image, EpicIdentity.ModuleName, "EOS_Platform_Tick"));
    }

    // The JWT decoder

    [Fact]
    public void AJwtDecodesToItsHeaderAndPayload()
    {
        // {"alg":"RS256","kid":"k1"} . {"sub":"0123456789abcdef0123456789abcdef","aud":"client","exp":1} . (not a real signature)
        const string token = "eyJhbGciOiJSUzI1NiIsImtpZCI6ImsxIn0.eyJzdWIiOiIwMTIzNDU2Nzg5YWJjZGVmMDEyMzQ1Njc4OWFiY2RlZiIsImF1ZCI6ImNsaWVudCIsImV4cCI6MX0.c2ln";
        var decoded = Jwt.Decode(token);
        Assert.NotNull(decoded);
        Assert.Equal("""{"alg":"RS256","kid":"k1"}""", decoded.Value.Header);
        Assert.Equal("""{"sub":"0123456789abcdef0123456789abcdef","aud":"client","exp":1}""", decoded.Value.Payload);
    }

    [Fact]
    public void Base64UrlCharactersAndMissingPaddingAreRead()
    {
        // Payload bytes 0xFB 0xFF 0xBF decode from "-_-_" (base64url) where standard base64 would need "+/+/".
        string header = Convert.ToBase64String("{}"u8.ToArray()).TrimEnd('=');
        var decoded = Jwt.Decode($"{header}.-_-_.x");
        Assert.NotNull(decoded);
        Assert.Equal("{}", decoded.Value.Header);
        Assert.Equal(System.Text.Encoding.UTF8.GetString([0xFB, 0xFF, 0xBF]), decoded.Value.Payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("onlyone")]
    [InlineData("two.parts")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.e30.sig")]
    public void AnythingElseIsNotAJwt(string token)
    {
        Assert.Null(Jwt.Decode(token));
    }
}
