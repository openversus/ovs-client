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

/// <summary>The body of the identity POST.</summary>
/// <param name="SteamId">steamId: the Steam id, or "Unknown".</param>
/// <param name="EpicId">epicId: the Epic id, or "Unknown".</param>
/// <param name="HardwareId">hardwareId: the hardware fingerprint.</param>
/// <param name="ClientVersion">clientVersion: the running client's version.</param>
public sealed record IdentityBody(string SteamId, string EpicId, string HardwareId, string ClientVersion);

/// <summary>
/// The JSON the client reads and writes, compiled ahead of time: NativeAOT has no reflection
/// for System.Text.Json, so every type goes through this context. Property names are camelCase
/// unless a <see cref="JsonPropertyNameAttribute"/> says otherwise; unknown fields are ignored.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(Notification))]
[JsonSerializable(typeof(IdentityBody))]
internal sealed partial class OvsJson : JsonSerializerContext
{
}
