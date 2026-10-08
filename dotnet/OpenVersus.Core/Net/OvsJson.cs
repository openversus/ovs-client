using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVersus.Config;

namespace OpenVersus.Net;

/// <summary>What the server sends back from /ovs/client-version.</summary>
/// <param name="LatestVersion">latest_version: the newest client the server offers.</param>
/// <param name="DownloadUrl">download_url: where that client is downloaded from.</param>
/// <param name="IsLatest">is_latest: whether the server considers the running client current.</param>
/// <param name="ReleaseName">release_name: the release's display name.</param>
public sealed record VersionInfo(
    [property: JsonPropertyName("latest_version"), JsonConverter(typeof(LenientStringConverter))] string? LatestVersion,
    [property: JsonPropertyName("download_url"), JsonConverter(typeof(LenientStringConverter))] string? DownloadUrl,
    [property: JsonPropertyName("is_latest"), JsonConverter(typeof(LenientBoolConverter))] bool IsLatest,
    [property: JsonPropertyName("release_name"), JsonConverter(typeof(LenientStringConverter))] string? ReleaseName);

/// <summary>One file of a release as /ovs/client-version lists it under "files".</summary>
/// <param name="Name">name: the asset's file name.</param>
/// <param name="Kind">kind: "plugin" (the .asi) or "paks" (an OVS_* pak, utoc, ucas or sig).</param>
/// <param name="Size">size: bytes, as a number or a string of digits.</param>
/// <param name="Sha256">sha256: the SHA-256 GitHub records for the asset, lowercase hex.</param>
/// <param name="DownloadUrl">download_url: where it downloads from.</param>
public sealed record UpdateFile(
    [property: JsonConverter(typeof(LenientStringConverter))] string? Name,
    [property: JsonConverter(typeof(LenientStringConverter))] string? Kind,
    [property: JsonConverter(typeof(LenientInt64Converter))] long Size,
    [property: JsonConverter(typeof(LenientStringConverter))] string? Sha256,
    [property: JsonPropertyName("download_url"), JsonConverter(typeof(LenientStringConverter))] string? DownloadUrl);

/// <summary>The "files" list of /ovs/client-version; null when the server sent none.</summary>
/// <param name="Files">files: every asset of the release the updater may install.</param>
public sealed record ReleaseFiles(List<UpdateFile>? Files);

/// <summary>
/// manifest.json in the pak backup folder: what one pak install moved there. Written to disk
/// before the first file moves, so an install the process did not live to finish can be put
/// back and checked on the next launch.
/// </summary>
/// <param name="State">"installing" until every file is in place, then "complete"; "rolledback" once put back.</param>
/// <param name="Release">The release being installed.</param>
/// <param name="Started">When the install began, UTC, ISO 8601.</param>
/// <param name="Files">Every file the install touches, in the order it moves them.</param>
public sealed record PakInstallManifest(string State, string Release, string Started, List<PakInstallEntry> Files);

/// <summary>One file of a <see cref="PakInstallManifest"/>.</summary>
/// <param name="Name">The pak file's name, in both the pak folder and the backup folder.</param>
/// <param name="HadOriginal">Whether a copy was installed before, which then sits in the backup folder.</param>
/// <param name="OldSha256">That copy's SHA-256, lowercase hex; null when there was none.</param>
/// <param name="OldSize">That copy's size in bytes; 0 when there was none.</param>
/// <param name="NewSha256">The release file's SHA-256.</param>
public sealed record PakInstallEntry(string Name, bool HadOriginal, string? OldSha256, long OldSize, string NewSha256);

/// <summary>A string that takes a number or a boolean as its text, and an object, an array or null as null.</summary>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    /// <inheritdoc/>
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return System.Text.Encoding.UTF8.GetString(reader.ValueSpan);
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>
/// A whole number, also taken from a string of its digits ("12345"). Anything else fails the
/// parse rather than standing in as some number: a size the updater trusted wrongly would fail
/// its download instead of saying why.
/// </summary>
public sealed class LenientInt64Converter : JsonConverter<long>
{
    /// <inheritdoc/>
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long number))
        {
            return number;
        }

        if (reader.TokenType == JsonTokenType.String
            && long.TryParse(reader.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long text))
        {
            return text;
        }

        throw new JsonException($"expected a whole number, not {(reader.TokenType == JsonTokenType.String ? $"\"{reader.GetString()}\"" : reader.TokenType.ToString())}");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>
/// A boolean that forgives the server: JSON true/false as they are, the numbers 1 and 0, a string
/// in any spelling the ini accepts (true/false, on/off, 1/0, any case), and anything else,
/// including null, is false rather than a failed response.
/// </summary>
public sealed class LenientBoolConverter : JsonConverter<bool>
{
    /// <inheritdoc/>
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return true;
            case JsonTokenType.Number:
                return reader.TryGetInt64(out long n) && n == 1;
            case JsonTokenType.String:
                return ConfigFile.TryParseBool(reader.GetString(), out bool value) && value;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                // A nested value must be consumed whole, or the reader is left inside it.
                reader.Skip();
                return false;
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

/// <summary>One item of /ovs/notifications. Type is null when the server sent none, and such items are skipped.</summary>
/// <param name="Type">type: match_cancel, toast_received or admin_banner.</param>
/// <param name="Title">title: the banner's caption.</param>
/// <param name="Message">message: the banner's text.</param>
/// <param name="Timeout">timeout: seconds an admin banner stays up; 8 when absent.</param>
public sealed record Notification(string? Type, string Title = "", string Message = "", double? Timeout = null);

/// <summary>The body of the identity POST, in the C++ client's field order.</summary>
/// <param name="SteamId">steamId: the Steam id, or "Unknown".</param>
/// <param name="EpicId">epicId: the Epic id, or "Unknown".</param>
/// <param name="HardwareId">hardwareId: the V2 hardware fingerprint, or "".</param>
/// <param name="HardwareIdVersion">hardwareIdVersion: "2" with a fingerprint, else "".</param>
/// <param name="HardwareIdQuality">hardwareIdQuality: "strong" with a fingerprint, else "".</param>
/// <param name="InstallId">installId: this install's random id, or "".</param>
/// <param name="SteamTicket">steamTicket: the Steam session ticket as hex, or "" (the server believes the Steam id only with it).</param>
/// <param name="ClientVersion">clientVersion: the running client's version.</param>
/// <param name="NodePort">nodePort: the UDP port of this machine's rollback node, 0 for none.</param>
public sealed record IdentityBody(string SteamId, string SteamTicket, string EpicId, string HardwareId, string HardwareIdVersion, string HardwareIdQuality, string InstallId, string ClientVersion, int NodePort);

/// <summary>What /api/identify sends back. The server also sends accountId, which the client has no use for.</summary>
/// <param name="Ok">ok: whether the identity was registered.</param>
/// <param name="Token">token: a token the server resolves the player from on the client's own calls.</param>
/// <param name="Error">error: why not, such as "client_update_required".</param>
public sealed record IdentifyResponse(
    [property: JsonConverter(typeof(LenientBoolConverter))] bool Ok,
    [property: JsonConverter(typeof(LenientStringConverter))] string? Token,
    [property: JsonConverter(typeof(LenientStringConverter))] string? Error);

/// <summary>
/// The JSON the client reads and writes, compiled ahead of time: NativeAOT has no reflection
/// for System.Text.Json, so every type goes through this context. Property names are camelCase
/// unless a <see cref="JsonPropertyNameAttribute"/> says otherwise; unknown fields are ignored.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(Notification))]
[JsonSerializable(typeof(IdentityBody))]
[JsonSerializable(typeof(IdentifyResponse))]
[JsonSerializable(typeof(ReleaseFiles))]
[JsonSerializable(typeof(PakInstallManifest))]
internal sealed partial class OvsJson : JsonSerializerContext
{
    /// <summary><paramref name="json"/> as a <typeparamref name="T"/>, or null, with <paramref name="problem"/> saying why when it is not that JSON.</summary>
    public static T? TryParse<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, out string? problem) where T : class
    {
        try
        {
            problem = null;
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException e)
        {
            problem = e.Message;
            return null;
        }
    }
}
