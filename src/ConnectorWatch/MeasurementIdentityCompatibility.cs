using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ConnectorWatch;

/// <summary>Compares the measurement semantics that survive an approved
/// direct NVIDIA driver transition. Reference identity remains stricter.</summary>
public sealed record MeasurementIdentitySnapshot(
    int IdentityVersion,
    string GpuUuid,
    string Board,
    string Driver,
    string Source,
    string AbiProfile,
    string AnalysisLoadSource,
    string FeatureVersion,
    string ModelVersion,
    int SchemaVersion,
    string QualificationJson,
    string CanonicalMeasurementKey,
    string CanonicalSourceKey,
    bool IsDirectNvidia);

public static class MeasurementIdentityCompatibility
{
    static readonly Regex DirectNvidiaSource = new(
        @"^direct NVIDIA rails \((?<channel>[^()]+); driver (?<driver>[^;()]+); UUID (?<uuid>[^;()]+)\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool TryParse(string? json, out MeasurementIdentitySnapshot? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParse(document.RootElement, out identity);
        }
        catch (JsonException) { return false; }
    }

    public static bool TryParse(JsonElement value, out MeasurementIdentitySnapshot? identity)
    {
        identity = null;
        if (value.ValueKind != JsonValueKind.Object ||
            !TryInt(value, "identity_version", out int identityVersion) ||
            !TryInt(value, "schema_version", out int schemaVersion) ||
            !TryString(value, "gpu_uuid", out var gpuUuid) ||
            !TryString(value, "board", out var board) ||
            !TryString(value, "driver", out var driver) ||
            !TryString(value, "source", out var source) ||
            !TryString(value, "abi_profile", out var abiProfile) ||
            !TryString(value, "analysis_load_source", out var analysisLoadSource) ||
            !TryString(value, "feature_version", out var featureVersion) ||
            !TryString(value, "model_version", out var modelVersion) ||
            !TryProperty(value, "qualification", out var qualification) ||
            qualification.ValueKind != JsonValueKind.Object)
            return false;

        string qualificationJson = CanonicalJson(qualification);
        bool isDirect = TryDirectSource(source, gpuUuid, driver,
            out _, out var canonicalDirectSource);
        string measurementSource = isDirect ? canonicalDirectSource : source;
        string canonicalMeasurement = string.Join("\n",
            identityVersion.ToString(CultureInfo.InvariantCulture),
            gpuUuid.ToUpperInvariant(), board, measurementSource,
            abiProfile, analysisLoadSource, featureVersion, modelVersion,
            schemaVersion.ToString(CultureInfo.InvariantCulture), qualificationJson,
            isDirect ? "direct-nvidia" : driver);
        string sourceKey = ComposeTelemetrySourceIdentity(gpuUuid,
            measurementSource, analysisLoadSource,
            UnitForLoadSource(analysisLoadSource));
        identity = new MeasurementIdentitySnapshot(identityVersion,
            gpuUuid, board, driver, source, abiProfile, analysisLoadSource,
            featureVersion, modelVersion, schemaVersion, qualificationJson,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalMeasurement))),
            sourceKey, isDirect);
        return true;
    }

    public static bool MatchesMeasurement(string? leftJson, string? rightJson) =>
        TryParse(leftJson, out var left) && TryParse(rightJson, out var right) &&
        MatchesMeasurement(left!, right!);

    public static bool MatchesMeasurement(MeasurementIdentitySnapshot left,
        MeasurementIdentitySnapshot right)
    {
        if (!string.Equals(left.GpuUuid, right.GpuUuid,
                StringComparison.OrdinalIgnoreCase) ||
            left.IdentityVersion != right.IdentityVersion ||
            !string.Equals(left.Board, right.Board, StringComparison.Ordinal) ||
            !string.Equals(left.AbiProfile, right.AbiProfile, StringComparison.Ordinal) ||
            !string.Equals(left.AnalysisLoadSource, right.AnalysisLoadSource, StringComparison.Ordinal) ||
            !string.Equals(left.FeatureVersion, right.FeatureVersion, StringComparison.Ordinal) ||
            !string.Equals(left.ModelVersion, right.ModelVersion, StringComparison.Ordinal) ||
            left.SchemaVersion != right.SchemaVersion ||
            !string.Equals(left.QualificationJson, right.QualificationJson, StringComparison.Ordinal))
            return false;

        if (left.IsDirectNvidia || right.IsDirectNvidia)
            return left.IsDirectNvidia && right.IsDirectNvidia &&
                string.Equals(left.CanonicalSourceKey, right.CanonicalSourceKey,
                    StringComparison.Ordinal);

        return string.Equals(left.Driver, right.Driver, StringComparison.Ordinal) &&
            string.Equals(left.Source, right.Source, StringComparison.Ordinal);
    }

    /// <summary>Matches legacy telemetry that has no full identity column.
    /// It checks the available provider, GPU, and load-source evidence.</summary>
    public static bool MatchesTelemetrySource(string acceptedGpuUuid,
        string acceptedSource, string acceptedLoadSource, string rowGpuUuid,
        string rowSource, string rowLoadSource, string rowLoadUnit,
        out string canonicalSourceKey, out string acceptedDriver,
        out string rowDriver)
    {
        canonicalSourceKey = "";
        acceptedDriver = "";
        rowDriver = "";
        if (!string.Equals(acceptedGpuUuid, rowGpuUuid,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(acceptedLoadSource, rowLoadSource, StringComparison.Ordinal) ||
            !string.Equals(UnitForLoadSource(acceptedLoadSource), rowLoadUnit,
                StringComparison.OrdinalIgnoreCase))
            return false;

        bool acceptedDirect = TryDirectSource(acceptedSource, acceptedGpuUuid,
            driver: null, out var acceptedParts, out var acceptedCanonical);
        bool rowDirect = TryDirectSource(rowSource, rowGpuUuid,
            driver: null, out var rowParts, out var rowCanonical);
        if (acceptedDirect || rowDirect)
        {
            if (!acceptedDirect || !rowDirect ||
                !string.Equals(acceptedCanonical, rowCanonical, StringComparison.Ordinal))
                return false;
            acceptedDriver = acceptedParts.Driver;
            rowDriver = rowParts.Driver;
            canonicalSourceKey = ComposeTelemetrySourceIdentity(acceptedGpuUuid,
                acceptedCanonical, acceptedLoadSource, rowLoadUnit);
            return true;
        }

        if (!string.Equals(acceptedSource, rowSource, StringComparison.Ordinal))
            return false;
        canonicalSourceKey = ComposeTelemetrySourceIdentity(acceptedGpuUuid,
            acceptedSource, acceptedLoadSource, rowLoadUnit);
        return true;
    }

    public static string ComposeTelemetrySourceIdentity(string gpuUuid,
        string electricalSource, string loadSource, string loadUnit) =>
        string.Join(" | ", new[] { gpuUuid, electricalSource, loadSource, loadUnit }
            .Select(value => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim()));

    static string UnitForLoadSource(string loadSource) =>
        string.Equals(loadSource, "CONNECTOR_CURRENT", StringComparison.Ordinal)
            ? "A" : "W";

    static bool TryDirectSource(string source, string gpuUuid, string? driver,
        out (string Channel, string Driver, string Uuid) parts,
        out string canonicalSource)
    {
        parts = default;
        canonicalSource = "";
        var match = DirectNvidiaSource.Match(source);
        if (!match.Success) return false;
        string channel = match.Groups["channel"].Value.Trim();
        string sourceDriver = match.Groups["driver"].Value.Trim();
        string sourceUuid = match.Groups["uuid"].Value.Trim();
        if (channel.Length == 0 || sourceDriver.Length == 0 || sourceUuid.Length == 0 ||
            !string.Equals(sourceUuid, gpuUuid, StringComparison.OrdinalIgnoreCase) ||
            driver is not null && !string.Equals(sourceDriver, driver, StringComparison.Ordinal))
            return false;
        parts = (channel, sourceDriver, sourceUuid);
        canonicalSource = "direct NVIDIA rails (" + channel + "; UUID " +
            sourceUuid.ToUpperInvariant() + ")";
        return true;
    }

    static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = default;
        return TryProperty(parent, name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    static bool TryString(JsonElement parent, string name, out string value)
    {
        value = "";
        if (!TryProperty(parent, name, out var property) ||
            property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()?.Trim() ?? "";
        return value.Length > 0;
    }

    static bool TryProperty(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        return false;
    }

    static string CanonicalJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(
                    property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var integer)) writer.WriteNumberValue(integer);
                else if (value.TryGetDecimal(out var decimalValue)) writer.WriteNumberValue(decimalValue);
                else writer.WriteNumberValue(value.GetDouble());
                break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: writer.WriteNullValue(); break;
        }
    }
}
