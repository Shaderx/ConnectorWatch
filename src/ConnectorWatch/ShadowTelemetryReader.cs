using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace ConnectorWatch;

/// <summary>
/// Reads retained telemetry for offline shadow analysis.  This adapter is
/// deliberately read only: it never opens a hardware source and never writes
/// to the telemetry directory.
/// </summary>
public static class ShadowTelemetryReader
{
    // A telemetry row is normally less than 2 KiB.  Keep a generous limit for
    // the persisted JSON columns while making a hostile or truncated file
    // unable to grow a StringBuilder without bound.
    internal const int MaximumRecordCharacters = 256 * 1024;
    const int MaximumIdentityCharacters = 1024;
    const int MaximumSamplesPerMinute = 512;
    const int MaximumAggregateMinutes = 200_000;
    const int MaximumWarnings = 256;
    // A daily file normally has at most 1,440 UTC minutes.  This second
    // ceiling protects the reader if a file contains a hostile number of
    // distinct identities within one day, while still leaving room for
    // several stable cohorts per minute.
    const int MaximumAccumulatorsPerFile = 4096;
    const double MinimumVoltageV = 6;
    const double MaximumVoltageV = 16;
    const string CsvExtension = ".csv";
    const string GzipExtension = ".csv.gz";

    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Reads completed UTC-day telemetry files in date order.  A plain CSV is
    /// preferred over its gzip archive when both represent the same day.
    /// </summary>
    public static ShadowReadResult ReadDirectory(string directory,
        ShadowReadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A telemetry directory is required.", nameof(directory));

        var supplied = options ?? new ShadowReadOptions();
        Validate(supplied);
        var cutoffUtc = supplied.InputCutoffUtc.ToUniversalTime();
        var cutoffDate = DateOnly.FromDateTime(cutoffUtc.UtcDateTime.Date);
        var state = new ReadState(supplied, cutoffUtc);

        var sources = SelectSources(directory, cutoffDate, state.Warnings);
        foreach (var source in sources)
        {
            if (state.Truncated && state.Rows.Count >= supplied.MaximumMinutes)
            {
                state.AddWarning("Aggregate minute cap reached; remaining source files were not materialized.");
                break;
            }

            ReadSource(source, cutoffDate, state);
        }

        state.Rows.Sort(static (left, right) =>
        {
            var timestamp = left.TimestampUtc.CompareTo(right.TimestampUtc);
            return timestamp != 0
                ? timestamp
                : StringComparer.Ordinal.Compare(left.CohortKey, right.CohortKey);
        });

        return new(
            state.Rows.ToArray(),
            state.Files.ToArray(),
            state.RowsRead,
            state.EligibleRawRows,
            state.InvalidRows,
            state.RejectedRows,
            state.DuplicateRows,
            state.ConflictingRows,
            state.PartialRows,
            state.Truncated,
            cutoffUtc,
            state.Warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    static void Validate(ShadowReadOptions options)
    {
        if (options.MaximumMinutes <= 0 || options.MaximumMinutes > MaximumAggregateMinutes)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumMinutes),
                $"MaximumMinutes must be between 1 and {MaximumAggregateMinutes}.");
        if (options.MinimumObservationsPerMinute <= 0 ||
            options.MinimumObservationsPerMinute > MaximumSamplesPerMinute)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumObservationsPerMinute),
                $"MinimumObservationsPerMinute must be between 1 and {MaximumSamplesPerMinute}.");
        if (!double.IsFinite(options.MinimumConnectorPowerW) || options.MinimumConnectorPowerW < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumConnectorPowerW),
                "MinimumConnectorPowerW must be finite and non-negative.");
        if (!double.IsFinite(options.MaximumAgeSeconds) || options.MaximumAgeSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumAgeSeconds),
                "MaximumAgeSeconds must be finite and non-negative.");
    }

    static IReadOnlyList<SelectedSource> SelectSources(string directory,
        DateOnly cutoffDate, List<string> warnings)
    {
        if (!Directory.Exists(directory))
        {
            AddWarning(warnings, "Telemetry directory was not found; no source files were read.");
            return Array.Empty<SelectedSource>();
        }

        var byDate = new Dictionary<DateOnly, SelectedSource>();
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFiles(directory, "telemetry-*",
                SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            AddWarning(warnings, "Telemetry directory could not be enumerated: " + exception.Message);
            return Array.Empty<SelectedSource>();
        }

        foreach (var path in names)
        {
            if (!TryTelemetryDate(Path.GetFileName(path), out var date, out var isCsv))
                continue;
            if (date >= cutoffDate)
                continue;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                var candidate = new SelectedSource(path, date, isCsv,
                    new ShadowSourceFile(info.Name, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc)));
                if (!byDate.TryGetValue(date, out var current) ||
                    (isCsv && !current.IsCsv) ||
                    (isCsv == current.IsCsv && StringComparer.OrdinalIgnoreCase.Compare(
                        candidate.Path, current.Path) < 0))
                    byDate[date] = candidate;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                AddWarning(warnings, $"Could not inventory {Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return byDate.Values.OrderBy(x => x.Date).ThenBy(x => x.Path,
            StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static bool TryTelemetryDate(string name, out DateOnly date, out bool isCsv)
    {
        date = default;
        isCsv = false;
        if (!name.StartsWith("telemetry-", StringComparison.OrdinalIgnoreCase)) return false;

        string dateText;
        if (name.EndsWith(CsvExtension, StringComparison.OrdinalIgnoreCase))
        {
            dateText = name["telemetry-".Length..^CsvExtension.Length];
            isCsv = true;
        }
        else if (name.EndsWith(GzipExtension, StringComparison.OrdinalIgnoreCase))
        {
            dateText = name["telemetry-".Length..^GzipExtension.Length];
        }
        else return false;

        return dateText.Length == 10 &&
            DateOnly.TryParseExact(dateText, "yyyy-MM-dd", Invariant,
                DateTimeStyles.None, out date);
    }

    static void ReadSource(SelectedSource source, DateOnly cutoffDate, ReadState state)
    {
        state.Files.Add(source.Metadata);
        var accumulators = new Dictionary<MinuteCohortKey, MinuteAccumulator>();
        try
        {
            using var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192,
                FileOptions.SequentialScan);
            using Stream content = source.IsCsv
                ? file
                : new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
            using var reader = new StreamReader(content, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 8192);

            var headerRecord = ReadRecord(reader, MaximumRecordCharacters);
            if (headerRecord.EndOfFile) return;
            if (headerRecord.Oversized)
            {
                state.InvalidRows++;
                state.AddWarning($"Header in {source.Metadata.Name} exceeds the bounded record length.");
                return;
            }
            if (headerRecord.Partial)
            {
                state.PartialRows++;
                state.AddWarning($"Header in {source.Metadata.Name} is truncated.");
                return;
            }
            if (!TryParseCsv(headerRecord.Text!, out var headerCells) ||
                !TryCreateHeader(headerCells, out var headers))
            {
                state.InvalidRows++;
                state.AddWarning($"Header in {source.Metadata.Name} is not a usable CSV header.");
                return;
            }

            while (true)
            {
                var record = ReadRecord(reader, MaximumRecordCharacters);
                if (record.EndOfFile) break;
                state.RowsRead++;
                if (record.Oversized)
                {
                    state.InvalidRows++;
                    state.AddWarning($"An oversized row in {source.Metadata.Name} was ignored.");
                    continue;
                }
                if (record.Partial)
                {
                    state.PartialRows++;
                    continue;
                }
                if (!TryParseCsv(record.Text!, out var cells) || cells.Count < headers.Count)
                {
                    state.InvalidRows++;
                    continue;
                }

                var parse = TryParseRaw(headers, cells, cutoffDate, source.Date,
                    state.Options, out var raw, out var unverified);
                if (parse == ParseResult.Invalid)
                {
                    state.InvalidRows++;
                    continue;
                }
                if (parse == ParseResult.Rejected)
                {
                    state.RejectedRows++;
                    continue;
                }
                if (unverified) state.HostPollWarning = true;
                state.EligibleRawRows++;

                var key = new MinuteCohortKey(FloorMinute(raw.TimestampUtc), raw.CohortKey);
                if (!accumulators.TryGetValue(key, out var accumulator))
                {
                    // Keep a small amount of room beyond the requested output
                    // cap so that the caller gets Truncated rather than a
                    // silently incomplete result when the next group qualifies.
                    var accumulatorLimit = Math.Min(
                        MaximumAccumulatorsPerFile, state.Options.MaximumMinutes + 1);
                    if (accumulators.Count >= accumulatorLimit)
                    {
                        state.Truncated = true;
                        continue;
                    }
                    accumulator = new MinuteAccumulator(raw.CohortKey);
                    accumulators.Add(key, accumulator);
                }

                accumulator.Add(raw, state);
            }
        }
        catch (InvalidDataException exception)
        {
            state.PartialRows++;
            state.Truncated = true;
            state.AddWarning($"Telemetry archive {source.Metadata.Name} could not be fully decompressed: {exception.Message}");
        }
        catch (DecoderFallbackException exception)
        {
            state.InvalidRows++;
            state.Truncated = true;
            state.AddWarning($"Telemetry file {source.Metadata.Name} contains invalid UTF-8: {exception.Message}");
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            state.PartialRows++;
            state.Truncated = true;
            state.AddWarning($"Telemetry file {source.Metadata.Name} could not be fully read: {exception.Message}");
        }

        if (state.HostPollWarning && !state.HostPollWarningReported)
        {
            state.AddWarning("Host-poll-time observations are allowed but their hardware freshness is unverified.");
            state.HostPollWarningReported = true;
        }

        foreach (var entry in accumulators.OrderBy(x => x.Key.MinuteUtc)
                     .ThenBy(x => x.Key.CohortKey, StringComparer.Ordinal))
        {
            foreach (var row in entry.Value.Build(entry.Key.MinuteUtc,
                         state.Options.MinimumObservationsPerMinute, state))
            {
                if (state.Rows.Count >= state.Options.MaximumMinutes)
                {
                    state.Truncated = true;
                    break;
                }
                state.Rows.Add(row);
            }
            if (state.Rows.Count >= state.Options.MaximumMinutes &&
                accumulators.Count > state.Rows.Count)
                state.Truncated = true;
        }

        if (state.Truncated && !state.TruncationWarningReported)
        {
            state.AddWarning($"Aggregate minute cap ({state.Options.MaximumMinutes}) reached; coverage is truncated.");
            state.TruncationWarningReported = true;
        }
    }

    static ParseResult TryParseRaw(HeaderMap headers, IReadOnlyList<string> cells,
        DateOnly cutoffDate, DateOnly sourceDate, ShadowReadOptions options,
        out RawObservation raw, out bool hostPollUnverified)
    {
        raw = default!;
        hostPollUnverified = false;

        string Get(params string[] names) => headers.Get(cells, names);
        var timestampText = Get("timestamp_utc", "timestamp", "time_utc", "time");
        if (!TryTimestamp(timestampText, out var timestamp)) return ParseResult.Invalid;
        timestamp = timestamp.ToUniversalTime();
        if (timestamp.UtcDateTime.Date >= cutoffDate.ToDateTime(TimeOnly.MinValue).Date)
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("input_voltage_v", "connector_voltage_v", "voltage_v"),
                out var voltage, out var voltageInvalid) || voltage is not double voltageValue)
            return voltageInvalid ? ParseResult.Invalid : ParseResult.Rejected;
        if (!double.IsFinite(voltageValue) || voltageValue < MinimumVoltageV || voltageValue > MaximumVoltageV)
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("connector_current_a", "current_a"),
                out var current, out var currentInvalid))
            return currentInvalid ? ParseResult.Invalid : ParseResult.Rejected;
        if (current is double currentValue &&
            (!double.IsFinite(currentValue) || currentValue < 0))
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("connector_power_w"), out var connectorPower,
                out var connectorPowerInvalid))
            return connectorPowerInvalid ? ParseResult.Invalid : ParseResult.Rejected;
        if (connectorPower is double connectorPowerValue &&
            (!double.IsFinite(connectorPowerValue) || connectorPowerValue < 0))
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("analysis_power_w", "load_w", "power_w"),
                out var analysisPower, out var analysisPowerInvalid))
            return analysisPowerInvalid ? ParseResult.Invalid : ParseResult.Rejected;
        if (analysisPower is double analysisPowerValue &&
            (!double.IsFinite(analysisPowerValue) || analysisPowerValue < 0))
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("board_power_w"), out var boardPower, out var boardPowerInvalid) ||
            !TryOptionalNumber(Get("gpu_temp_c", "temperature_c", "gpu_temperature_c"),
                out var temperature, out var temperatureInvalid) ||
            !TryOptionalNumber(Get("pcie_voltage_v"), out var pcieVoltage,
                out var pcieVoltageInvalid))
            return ParseResult.Invalid;
        if (boardPower is double boardValue && (!double.IsFinite(boardValue) || boardValue < 0) ||
            temperature is double temperatureValue && !double.IsFinite(temperatureValue) ||
            pcieVoltage is double pcieVoltageValue &&
                (!double.IsFinite(pcieVoltageValue) || pcieVoltageValue < MinimumVoltageV ||
                 pcieVoltageValue > MaximumVoltageV))
            return ParseResult.Rejected;

        if (!TryOptionalNumber(Get("sample_age_seconds", "age_seconds"), out var age,
                out var ageInvalid))
            return ageInvalid ? ParseResult.Invalid : ParseResult.Rejected;
        if (age is double ageValue &&
            (!double.IsFinite(ageValue) || ageValue < 0 || ageValue > options.MaximumAgeSeconds))
            return ParseResult.Rejected;

        if (!TryOptionalTimestamp(Get("voltage_timestamp_utc"), out var voltageTimestamp) ||
            !TryOptionalTimestamp(Get("electrical_source_timestamp_utc"), out var sourceTimestamp))
            return ParseResult.Invalid;
        if (!IsFreshAt(voltageTimestamp, timestamp, options.MaximumAgeSeconds) ||
            !IsFreshAt(sourceTimestamp, timestamp, options.MaximumAgeSeconds))
            return ParseResult.Rejected;

        // HybridStorage partitions by UTC day.  A row from another day is
        // treated as a source-file integrity failure instead of being allowed
        // to bridge a gap or duplicate a neighboring daily file.
        if (timestamp.UtcDateTime.Date != sourceDate.ToDateTime(TimeOnly.MinValue).Date)
            return ParseResult.Rejected;

        var gpuUuid = NormalizeIdentity(GetFirstNonEmpty(headers, cells, "gpu_uuid", "gpu_id"));
        var electricalSource = NormalizeIdentity(GetFirstNonEmpty(headers, cells,
            "electrical_source", "voltage_source"));
        var analysisSourceText = GetFirstNonEmpty(headers, cells,
            "analysis_power_source", "analysis_source", "analysis_load_source", "load_source");
        var analysisSource = NormalizeToken(analysisSourceText);
        if (!IsIdentity(gpuUuid) || !IsIdentity(electricalSource) || !IsIdentity(analysisSource))
            return ParseResult.Rejected;

        var provenanceText = GetFirstNonEmpty(headers, cells,
            "connector_power_provenance", "power_provenance");
        var provenance = NormalizeProvenance(provenanceText);
        var power = connectorPower;
        string powerProvenance;
        if (power is null)
        {
            if (analysisPower is double &&
                string.Equals(analysisSource, "CONNECTOR_POWER", StringComparison.Ordinal))
            {
                // analysis_power_w is ambiguous in general.  This explicit
                // source is the narrow exception, and the output label makes
                // that exception visible to downstream consumers.
                power = analysisPower;
                powerProvenance = "AnalysisPower:CONNECTOR_POWER";
            }
            else if (current is double currentForPower &&
                     provenance == "DerivedFromVoltageAndCurrent")
            {
                power = currentForPower * voltageValue;
                powerProvenance = provenance;
            }
            else return ParseResult.Rejected;
        }
        else
        {
            if (provenance is null) return ParseResult.Rejected;
            powerProvenance = provenance;
        }

        if (power is not double powerValue || !double.IsFinite(powerValue) ||
            powerValue < options.MinimumConnectorPowerW)
            return ParseResult.Rejected;

        var stateValues = new[]
        {
            Get("status"), Get("detail"), Get("acquisition_health"),
            Get("electrical_freshness_kind"), Get("value_change_flags"),
        };
        if (stateValues.Any(IsRejectedState)) return ParseResult.Rejected;

        var settledText = Get("is_settled", "settled", "load_settled", "settled_state");
        if (!string.IsNullOrWhiteSpace(settledText) &&
            bool.TryParse(settledText.Trim(), out var settled) && !settled)
            return ParseResult.Rejected;
        if (!string.IsNullOrWhiteSpace(settledText) &&
            !bool.TryParse(settledText.Trim(), out _))
            return ParseResult.Invalid;

        var freshness = NormalizeToken(Get("electrical_freshness_kind"));
        hostPollUnverified = freshness.Contains("HOSTPOLL", StringComparison.Ordinal) ||
            freshness.Contains("UNVERIFIED", StringComparison.Ordinal) ||
            stateValues.Any(x => x.Contains("host-poll-time", StringComparison.OrdinalIgnoreCase) ||
                x.Contains("freshness unverified", StringComparison.OrdinalIgnoreCase));

        var cohort = "gpu_uuid=" + gpuUuid +
            ";electrical_source=" + electricalSource +
            ";analysis_source=" + analysisSource +
            ";power_provenance=" + powerProvenance;
        raw = new RawObservation(timestamp, voltageValue, current, powerValue,
            boardPower, temperature, pcieVoltage, cohort, powerProvenance);
        return ParseResult.Accepted;
    }

    static bool IsRejectedState(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = NormalizeToken(value);
        return normalized.Contains("STALE", StringComparison.Ordinal) ||
            normalized.Contains("GAP", StringComparison.Ordinal) ||
            normalized.Contains("ERROR", StringComparison.Ordinal) ||
            normalized.Contains("INVALID", StringComparison.Ordinal) ||
            normalized.Contains("UNAVAILABLE", StringComparison.Ordinal) ||
            normalized.Contains("FAIL", StringComparison.Ordinal) ||
            normalized.Contains("REPEATED", StringComparison.Ordinal) ||
            normalized.Contains("SETTLING", StringComparison.Ordinal) ||
            normalized.Contains("UNSETTLED", StringComparison.Ordinal) ||
            normalized.Contains("NOT_SETTLED", StringComparison.Ordinal);
    }

    static string GetFirstNonEmpty(HeaderMap headers, IReadOnlyList<string> cells,
        params string[] names)
    {
        foreach (var name in names)
        {
            var value = headers.Get(cells, name);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return string.Empty;
    }

    static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, Invariant,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.RoundtripKind,
            out timestamp) && timestamp != default;

    static bool TryOptionalTimestamp(string value, out DateTimeOffset? timestamp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timestamp = null;
            return true;
        }
        if (!TryTimestamp(value, out var parsed))
        {
            timestamp = null;
            return false;
        }
        timestamp = parsed;
        return true;
    }

    static bool IsFreshAt(DateTimeOffset? sourceTimestamp,
        DateTimeOffset rowTimestamp, double maximumAgeSeconds)
    {
        if (sourceTimestamp is not DateTimeOffset source) return true;
        var age = (rowTimestamp.ToUniversalTime() - source.ToUniversalTime()).TotalSeconds;
        return double.IsFinite(age) && age >= 0 && age <= maximumAgeSeconds;
    }

    static bool TryOptionalNumber(string value, out double? number, out bool invalid)
    {
        invalid = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            number = null;
            return true;
        }
        if (!double.TryParse(value.Trim(), NumberStyles.Float, Invariant, out var parsed))
        {
            number = null;
            invalid = true;
            return false;
        }
        number = parsed;
        return true;
    }

    static string NormalizeIdentity(string value) =>
        string.Join(' ', value.Trim().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

    static string NormalizeToken(string value) =>
        NormalizeIdentity(value).Replace('-', '_').Replace(' ', '_').ToUpperInvariant();

    static bool IsIdentity(string value) =>
        value.Length > 0 && value.Length <= MaximumIdentityCharacters &&
        value.All(x => !char.IsControl(x));

    static string? NormalizeProvenance(string value)
    {
        var normalized = NormalizeToken(value);
        return normalized switch
        {
            "MEASURED" or "DIRECT" or "SENSOR" => "Measured",
            "DERIVED" or "DERIVED_FROM_VOLTAGE_AND_CURRENT" or
                "DERIVEDFROMVOLTAGEANDCURRENT" or "CURRENT_TIMES_VOLTAGE" or
                "CURRENTXVOLTAGE" or "CONNECTOR_CURRENT" => "DerivedFromVoltageAndCurrent",
            "" or "UNKNOWN" or "UNAVAILABLE" or "NONE" => null,
            _ => null,
        };
    }

    static DateTimeOffset FloorMinute(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour,
            utc.Minute, 0, TimeSpan.Zero);
    }

    static bool TryCreateHeader(IReadOnlyList<string> cells, out HeaderMap header)
    {
        var indices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < cells.Count; i++)
        {
            var name = NormalizeToken(i == 0 ? cells[i].TrimStart('\uFEFF') : cells[i]);
            if (name.Length == 0) continue;
            if (indices.ContainsKey(name))
            {
                header = default!;
                return false;
            }
            indices.Add(name, i);
        }
        header = new HeaderMap(indices, cells.Count);
        return indices.ContainsKey("TIMESTAMP_UTC") || indices.ContainsKey("TIMESTAMP") ||
            indices.ContainsKey("TIME_UTC") || indices.ContainsKey("TIME");
    }

    static bool TryParseCsv(string record, out List<string> fields)
    {
        fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var fieldHasContent = false;
        for (var i = 0; i < record.Length; i++)
        {
            var character = record[i];
            if (quoted)
            {
                if (character == '"')
                {
                    if (i + 1 < record.Length && record[i + 1] == '"')
                    {
                        value.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        closedQuote = true;
                    }
                }
                else value.Append(character);
                continue;
            }

            if (character == ',' )
            {
                fields.Add(value.ToString());
                value.Clear();
                fieldHasContent = false;
                closedQuote = false;
                continue;
            }
            if (character == '"')
            {
                if (fieldHasContent || closedQuote) return false;
                quoted = true;
                fieldHasContent = true;
                continue;
            }
            if (closedQuote && !char.IsWhiteSpace(character)) return false;
            value.Append(character);
            if (!char.IsWhiteSpace(character)) fieldHasContent = true;
        }
        if (quoted) return false;
        fields.Add(value.ToString());
        return true;
    }

    static BoundedRecord ReadRecord(TextReader reader, int maximumCharacters)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var any = false;
        var quoted = false;
        var oversized = false;
        while (true)
        {
            var value = reader.Read();
            if (value < 0)
            {
                if (!any) return new(null, EndOfFile: true, Oversized: false, Partial: false);
                return new(oversized ? null : builder.ToString(), false, oversized, quoted);
            }
            any = true;
            var character = (char)value;
            if (!quoted && (character == '\n' || character == '\r'))
            {
                if (character == '\r' && reader.Peek() == '\n') _ = reader.Read();
                return new(oversized ? null : builder.ToString(), false, oversized, false);
            }

            if (character == '"')
            {
                if (quoted && reader.Peek() == '"')
                {
                    AppendBounded(builder, '"', maximumCharacters, ref oversized);
                    var escaped = reader.Read();
                    AppendBounded(builder, (char)escaped, maximumCharacters, ref oversized);
                    continue;
                }
                quoted = !quoted;
            }
            AppendBounded(builder, character, maximumCharacters, ref oversized);
        }
    }

    static void AppendBounded(StringBuilder builder, char value, int maximumCharacters,
        ref bool oversized)
    {
        if (builder.Length < maximumCharacters) builder.Append(value);
        else oversized = true;
    }

    static bool IsRecoverable(Exception exception) => exception is IOException or
        UnauthorizedAccessException or SecurityException or PathTooLongException;

    static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < MaximumWarnings) warnings.Add(warning);
    }

    readonly record struct SelectedSource(string Path, DateOnly Date, bool IsCsv,
        ShadowSourceFile Metadata);

    readonly record struct MinuteCohortKey(DateTimeOffset MinuteUtc, string CohortKey);

    readonly record struct BoundedRecord(string? Text, bool EndOfFile,
        bool Oversized, bool Partial);

    enum ParseResult { Accepted, Invalid, Rejected }

    sealed class HeaderMap(IReadOnlyDictionary<string, int> indices, int count)
    {
        public int Count { get; } = count;

        public string Get(IReadOnlyList<string> cells, params string[] names)
        {
            foreach (var name in names)
            {
                if (indices.TryGetValue(NormalizeToken(name), out var index) && index < cells.Count)
                    return cells[index].Trim();
            }
            return string.Empty;
        }
    }

    sealed class MinuteAccumulator(string cohortKey)
    {
        readonly Dictionary<DateTimeOffset, RawObservation> observations = new();
        readonly HashSet<DateTimeOffset> quarantined = new();
        bool bounded;

        public void Add(RawObservation observation, ReadState state)
        {
            if (quarantined.Contains(observation.TimestampUtc))
            {
                state.ConflictingRows++;
                return;
            }
            if (observations.TryGetValue(observation.TimestampUtc, out var previous))
            {
                if (previous.Equals(observation)) state.DuplicateRows++;
                else
                {
                    // A conflicting duplicate cannot safely contribute either
                    // value.  Quarantine the timestamp, including the first
                    // value already held in the bounded minute list.
                    observations.Remove(observation.TimestampUtc);
                    quarantined.Add(observation.TimestampUtc);
                    state.ConflictingRows++;
                }
                return;
            }
            if (observations.Count >= MaximumSamplesPerMinute)
            {
                bounded = true;
                return;
            }
            observations.Add(observation.TimestampUtc, observation);
        }

        public IEnumerable<ShadowTelemetryRow> Build(DateTimeOffset minuteUtc,
            int minimumObservations, ReadState state)
        {
            if (bounded && !state.SampleBoundWarningReported)
            {
                state.AddWarning("At least one minute contained more than the bounded sample list; excess samples were ignored.");
                state.SampleBoundWarningReported = true;
            }
            if (observations.Count < minimumObservations) yield break;

            var ordered = observations.Values.OrderBy(x => x.ConnectorPowerW).ToArray();
            var clusters = new List<List<RawObservation>>();
            foreach (var observation in ordered)
            {
                if (clusters.Count == 0)
                {
                    clusters.Add(new List<RawObservation> { observation });
                    continue;
                }
                var current = clusters[^1];
                var anchor = current[0].ConnectorPowerW;
                var tolerance = Math.Max(10, Math.Abs(anchor) * .10);
                if (Math.Abs(observation.ConnectorPowerW - anchor) <= tolerance)
                    current.Add(observation);
                else
                    clusters.Add(new List<RawObservation> { observation });
            }

            // A materially changing load must not be mixed into one median,
            // but a minute still represents one stable cohort row to the
            // downstream trend model.  Select the dominant settled band; a
            // tie is resolved by the lower median power so the result is
            // repeatable regardless of input order.  Load is a feature, not
            // part of cohort identity.
            var dominant = clusters
                .Where(cluster => cluster.Count >= minimumObservations)
                .OrderByDescending(cluster => cluster.Count)
                .ThenBy(cluster => Median(cluster.Select(x => x.ConnectorPowerW)))
                .FirstOrDefault();
            if (dominant is null) yield break;

            var medianPower = Median(dominant.Select(x => x.ConnectorPowerW));
            yield return new ShadowTelemetryRow(
                minuteUtc,
                Median(dominant.Select(x => x.VoltageV)),
                MedianNullable(dominant.Select(x => x.ConnectorCurrentA)),
                medianPower,
                MedianNullable(dominant.Select(x => x.BoardPowerW)),
                MedianNullable(dominant.Select(x => x.TemperatureC)),
                MedianNullable(dominant.Select(x => x.PcieVoltageV)),
                cohortKey,
                dominant.Count,
                dominant[0].PowerProvenance);
        }

        static double Median(IEnumerable<double> values)
        {
            var ordered = values.OrderBy(x => x).ToArray();
            var middle = ordered.Length / 2;
            return ordered.Length % 2 == 0
                ? (ordered[middle - 1] + ordered[middle]) / 2
                : ordered[middle];
        }

        static double? MedianNullable(IEnumerable<double?> values)
        {
            var present = values.Where(x => x is double).Select(x => x!.Value);
            return present.Any() ? Median(present) : null;
        }
    }

    readonly record struct RawObservation(
        DateTimeOffset TimestampUtc,
        double VoltageV,
        double? ConnectorCurrentA,
        double ConnectorPowerW,
        double? BoardPowerW,
        double? TemperatureC,
        double? PcieVoltageV,
        string CohortKey,
        string PowerProvenance);

    sealed class ReadState(ShadowReadOptions options, DateTimeOffset cutoffUtc)
    {
        public ShadowReadOptions Options { get; } = options;
        public DateTimeOffset CutoffUtc { get; } = cutoffUtc;
        public readonly List<ShadowTelemetryRow> Rows = new();
        public readonly List<ShadowSourceFile> Files = new();
        public readonly List<string> Warnings = new();
        public long RowsRead;
        public long EligibleRawRows;
        public long InvalidRows;
        public long RejectedRows;
        public long DuplicateRows;
        public long ConflictingRows;
        public long PartialRows;
        public bool Truncated;
        public bool HostPollWarning;
        public bool HostPollWarningReported;
        public bool TruncationWarningReported;
        public bool SampleBoundWarningReported;

        public void AddWarning(string warning) => ShadowTelemetryReader.AddWarning(Warnings, warning);
    }
}
