using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConnectorWatch.Gui;

/// <summary>
/// The immutable accepted reference used to score historical rows against the
/// model that is available now.  This type deliberately owns no output files:
/// callers can apply it while reducing a telemetry file, leaving the source
/// bytes untouched.
/// </summary>
internal sealed class AcceptedReferenceReplay
{
    const long MaximumReferenceBytes = 1L * 1024 * 1024;
    readonly string gpuUuid;
    readonly string source;
    readonly string loadSource;
    readonly double acceptedBinWatts;
    readonly double currentBinWatts;
    readonly MeasurementIdentitySnapshot? measurementIdentity;
    readonly IReadOnlyDictionary<int, double> bins;
    int appliedRows;

    AcceptedReferenceReplay(string gpuUuid, string source, string loadSource,
        double acceptedBinWatts, double currentBinWatts,
        MeasurementIdentitySnapshot? measurementIdentity,
        IReadOnlyDictionary<int, double> bins, string cacheKey)
    {
        this.gpuUuid = gpuUuid;
        this.source = source;
        this.loadSource = loadSource;
        this.acceptedBinWatts = acceptedBinWatts;
        this.currentBinWatts = currentBinWatts;
        this.measurementIdentity = measurementIdentity;
        this.bins = bins;
        CacheKey = cacheKey;
    }

    public string CacheKey { get; }
    public int AppliedRows => appliedRows;
    public int BinCount => bins.Count;

    /// <summary>
    /// Loads the frozen accepted model independently of the current lifecycle
    /// state. The lifecycle can be pending, degraded, or invalid while the
    /// accepted snapshot remains valid historical evidence.
    /// </summary>
    public static bool TryLoad(string path, double binWatts,
        out AcceptedReferenceReplay? replay, out string error)
    {
        replay = null;
        error = "";
        if (!File.Exists(path))
        {
            error = "accepted reference is missing";
            return false;
        }

        try
        {
            if (new FileInfo(path).Length > MaximumReferenceBytes)
            {
                error = "accepted reference exceeds the bounded read limit";
                return false;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(path,
                new UTF8Encoding(false, true)));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "accepted reference is not an object";
                return false;
            }

            if (!TryInt(document.RootElement, "schema_version", out int schemaVersion) ||
                schemaVersion != 1 ||
                !TryString(document.RootElement, "state", out var state) ||
                state.ToUpperInvariant() is not ("REFERENCE_UNVERIFIED" or
                    "REFERENCE_ACCEPTED" or "REFERENCE_STALE" or "REFERENCE_INVALID"))
            {
                error = "accepted reference lifecycle schema or state is invalid";
                return false;
            }

            if (!TryString(document.RootElement, "compatibility", out var compatibility) ||
                compatibility.ToUpperInvariant() is not ("LEGACY" or "COMPATIBLE" or
                    "MISMATCH" or "DEGRADED" or "RESTART"))
            {
                error = "accepted reference lifecycle compatibility is invalid" +
                    (compatibility.Length == 0 ? "" : " (" + compatibility + ")");
                return false;
            }

            if (!TryObject(document.RootElement, "accepted", out var accepted) ||
                !TryObject(accepted, "identity", out var identity) ||
                !TryObject(accepted, "bins", out var binsElement))
            {
                error = "accepted reference has no complete accepted identity and bins";
                return false;
            }

            if (!TryString(identity, "gpu_uuid", out var gpuUuid) ||
                !TryString(identity, "source", out var source) ||
                !TryString(identity, "analysis_load_source", out var loadSource))
            {
                error = "accepted reference identity is incomplete";
                return false;
            }

            if (!TryObject(identity, "qualification", out var qualification) ||
                !TryNumber(qualification, "bin_watts", out var persistedBinWatts) ||
                !double.IsFinite(persistedBinWatts) || persistedBinWatts <= 0)
            {
                error = "accepted reference bin width is invalid";
                return false;
            }

            if (!TryString(accepted, "accepted_by", out _) ||
                !TryString(accepted, "accepted_at_utc", out _))
            {
                error = "accepted reference has no frozen acceptance record";
                return false;
            }

            var bins = new SortedDictionary<int, double>();
            foreach (var bin in binsElement.EnumerateObject())
            {
                if (!int.TryParse(bin.Name, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var key) || key < 0 ||
                    !TryNumber(bin.Value, "reference_volts", out var reference) &&
                    !TryNumber(bin.Value, "Reference", out reference))
                    continue;

                if (!double.IsFinite(reference) || reference < 6 || reference > 16)
                    continue;
                if (TryBoolean(bin.Value, "is_qualified", out var qualified) && !qualified ||
                    TryBoolean(bin.Value, "IsQualified", out qualified) && !qualified)
                    continue;
                bins[key] = reference;
            }

            if (bins.Count == 0)
            {
                error = "accepted reference has no finite qualified bins";
                return false;
            }

            // Only identity and accepted bin values participate in cache
            // identity.  accepted_at_utc/updated_at_utc and other lifecycle
            // timestamps intentionally cannot make an equivalent replay stale.
            string canonical = CanonicalObject(identity) + "|bins=" +
                CanonicalObject(binsElement);
            string cacheKey = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical)));
            MeasurementIdentityCompatibility.TryParse(identity,
                out var measurementIdentity);
            replay = new AcceptedReferenceReplay(gpuUuid, source, loadSource,
                persistedBinWatts, binWatts, measurementIdentity, bins, cacheKey);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or DecoderFallbackException or FormatException)
        {
            error = "accepted reference could not be read: " + ex.Message;
            return false;
        }
    }

    /// <summary>Applies one accepted value when all replay identity gates match.</summary>
    public bool TryApply(string rowGpuUuid, string rowSource, string rowLoadSource,
        string rowLoadUnit, int? rowBin, string? rowIdentityJson,
        bool? rowDriverUnvalidated, out double reference,
        out string canonicalSourceIdentity)
    {
        reference = default;
        canonicalSourceIdentity = "";
        if (!string.Equals(rowLoadSource, loadSource, StringComparison.Ordinal) ||
            !rowBin.HasValue ||
            !MeasurementIdentityCompatibility.MatchesTelemetrySource(gpuUuid,
                source, loadSource, rowGpuUuid, rowSource, rowLoadSource,
                rowLoadUnit, out var legacySourceIdentity,
                out var acceptedDriver, out var rowDriver))
            return false;

        bool hasRowIdentity = MeasurementIdentityCompatibility.TryParse(
            rowIdentityJson, out var rowMeasurement);
        if (!hasRowIdentity && !string.IsNullOrWhiteSpace(rowIdentityJson))
            return false;
        if (measurementIdentity is not null && hasRowIdentity)
        {
            if (!MeasurementIdentityCompatibility.MatchesMeasurement(
                    measurementIdentity, rowMeasurement!) ||
                rowDriverUnvalidated == true && measurementIdentity.IsDirectNvidia &&
                    !string.Equals(measurementIdentity.Driver, rowMeasurement!.Driver,
                        StringComparison.Ordinal))
                return false;
            canonicalSourceIdentity = measurementIdentity.CanonicalSourceKey;
        }
        else
        {
            // Old CSV files do not carry qualification and profile identity.
            // Keep their prior gates when the active bin width still matches.
            if (Math.Abs(currentBinWatts - acceptedBinWatts) > 1e-9 ||
                rowDriverUnvalidated == true && acceptedDriver.Length > 0 &&
                    rowDriver.Length > 0 && !string.Equals(acceptedDriver, rowDriver,
                        StringComparison.Ordinal))
                return false;
            canonicalSourceIdentity = legacySourceIdentity;
        }

        if (!bins.TryGetValue(rowBin.Value, out reference)) return false;

        appliedRows++;
        return true;
    }

    static bool TryObject(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return value.ValueKind == JsonValueKind.Object;
            }
        return false;
    }

    static bool TryString(JsonElement parent, string name, out string value)
    {
        value = "";
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString()?.Trim() ?? "";
                return value.Length > 0;
            }
        return false;
    }

    static bool TryInt(JsonElement parent, string name, out int value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetInt32(out value);
        return false;
    }

    static bool TryNumber(JsonElement parent, string name, out double value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Number &&
                    property.Value.TryGetDouble(out value)) return true;
                if (property.Value.ValueKind == JsonValueKind.String &&
                    double.TryParse(property.Value.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out value)) return true;
                return false;
            }
        return false;
    }

    static bool TryBoolean(JsonElement parent, string name, out bool value)
    {
        value = default;
        if (parent.ValueKind != JsonValueKind.Object) return false;
        foreach (var property in parent.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.True) { value = true; return true; }
                if (property.Value.ValueKind == JsonValueKind.False) { value = false; return true; }
                return false;
            }
        return false;
    }

    static string CanonicalObject(JsonElement value)
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
