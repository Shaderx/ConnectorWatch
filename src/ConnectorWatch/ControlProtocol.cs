using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The small, versioned wire contract shared by ConnectorWatch and its GUI.
/// This file deliberately has no dependency on either application shell.
/// </summary>
public static class ControlProtocol
{
    public const int Version = 1;
    public const int ProtocolVersion = Version;
    public const int LeaseSeconds = 5;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static string Serialize(ControlRequest request) => JsonSerializer.Serialize(request, Json);

    public static string Serialize(ControlResponse response) => JsonSerializer.Serialize(response, Json);

    public static bool TryParseRequest(string line, out ControlRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(line) || line.Length > 16 * 1024) return false;
        try
        {
            request = JsonSerializer.Deserialize<ControlRequest>(line, Json);
            return request is not null &&
                   !string.IsNullOrWhiteSpace(request.Command) &&
                   !string.IsNullOrWhiteSpace(request.ClientId);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record ControlRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("expected_instance_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ExpectedInstanceId = null);

public sealed record ControlResponse(
    [property: JsonPropertyName("protocol")] int Protocol,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("data_directory")] string DataDirectory,
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("live")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LiveTelemetry? Live = null);

public sealed record LiveTelemetry(string Status, string Header, string[] Rows);

/// <summary>
/// Derives the per-user/configuration control endpoint from a normalized path.
/// The GUI links this file so it cannot accidentally choose a different pipe.
/// </summary>
public static class ControlEndpoint
{
    public static string NormalizeDataDirectory(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));

        var full = Path.GetFullPath(dataDirectory);
        full = Path.TrimEndingDirectorySeparator(full);
        if (full.Length == 0) full = Path.DirectorySeparatorChar.ToString();

        return full;
    }

    public static string Name(string dataDirectory)
    {
        var normalized = NormalizeDataDirectory(dataDirectory);
        // Windows paths are case-insensitive. Canonicalizing only the hash input
        // means C:\\Data and c:\\DATA address the same endpoint while the
        // response can retain the readable absolute path supplied by the daemon.
        var hashInput = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return "ConnectorWatch-" + Convert.ToHexString(digest.AsSpan(0, 20));
    }
}
