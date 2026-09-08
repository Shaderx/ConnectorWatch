using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ConnectorWatch.Gui;

public sealed record PointSample(DateTimeOffset Time, DateTimeOffset SensorTime, double Voltage, double? Pcie,
    double? Power, int? Bin, string Status, double? Reference, double? P05, double? Drop, string Detail)
{
    public bool Eligible => Bin.HasValue && Status is "LEARNING_REFERENCE" or "REFERENCE_UNVERIFIED" or "WINDOW_WARMUP" or "NO_SHIFT_DETECTED" or "SUDDEN_DROOP" or "BASELINE_SHIFT";
    public bool Alert => Status is "SUDDEN_DROOP" or "BASELINE_SHIFT";
}
public sealed record Incident(string Id, DateTimeOffset Time, string Status, string Detail, double? Voltage = null, double? Drop = null, int? Bin = null);
public sealed record SnapshotIncident(string Id, string Detector, string Status, string Severity, string State,
    DateTimeOffset TriggeredAtUtc, DateTimeOffset? AcknowledgedAtUtc = null,
    DateTimeOffset? ResolvedAtUtc = null, bool PostComplete = false);
public sealed record Baseline(double? Median, double? P05, int Learning);
public sealed class Snapshot
{
    public DateTimeOffset Time { get; set; }
    public bool Stopped { get; set; }
    public string Status { get; set; } = "WAITING_FOR_DATA";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
    public string GpuUuid { get; set; } = "";
    public double? Voltage { get; set; }
    public double? Pcie { get; set; }
    public double? Current { get; set; }
    public double? PcieCurrent { get; set; }
    public double? Power { get; set; }
    public double? BoardPower { get; set; }
    public double? Temperature { get; set; }
    public double? Utilization { get; set; }
    public double? Limit { get; set; }
    public double? Drop { get; set; }
    public int? Bin { get; set; }
    public double? Median { get; set; }
    public double? P05 { get; set; }
    public double? Reference { get; set; }
    public int Learning { get; set; }
    public int Window { get; set; }
    public int Stable { get; set; }
    public int Schema { get; set; }
    public string ReferenceState { get; set; } = "";
    public string ReferenceCompatibility { get; set; } = "";
    // Typed electrical data is intentionally kept separate from the legacy
    // Voltage record.  A null value means that the daemon did not report a
    // field; the GUI never substitutes zero for missing hardware data.
    public string ElectricalSource { get; set; } = "";
    public string ElectricalSourcePath => ElectricalSource;
    public string ElectricalStatus { get; set; } = "UNAVAILABLE";
    public string ElectricalFreshnessKind { get; set; } = "Unavailable";
    public string ElectricalFreshnessDetail { get; set; } = "";
    public bool ElectricalFresh { get; set; }
    public bool ElectricalFreshnessVerified { get; set; }
    public DateTimeOffset? ElectricalSourceTimestamp { get; set; }
    public double? ConnectorVoltage { get; set; }
    public double? ConnectorCurrent { get; set; }
    public double? ConnectorPower { get; set; }
    public string ConnectorVoltageAvailability { get; set; } = "Missing";
    public string ConnectorCurrentAvailability { get; set; } = "Missing";
    public string ConnectorPowerAvailability { get; set; } = "Missing";
    public double? PcieVoltage { get; set; }
    public double? PciePower { get; set; }
    public string PcieVoltageAvailability { get; set; } = "Missing";
    public string PcieCurrentAvailability { get; set; } = "Missing";
    public string PciePowerAvailability { get; set; } = "Missing";
    public string AnalysisLoadSource { get; set; } = "";
    public double? AnalysisLoadValue { get; set; }
    public double? AnalysisLoad => AnalysisLoadValue;
    public string AnalysisLoadUnit { get; set; } = "";
    public bool AnalysisLoadAvailable { get; set; }
    public bool AnalysisLoadFresh { get; set; }
    public bool AnalysisLoadFreshnessVerified { get; set; }
    public string AnalysisLoadStatus { get; set; } = "UNAVAILABLE";
    public string AnalysisLoadDetail { get; set; } = "";
    public string DifferentialModelState { get; set; } = "UNAVAILABLE";
    public bool DifferentialModelLoaded { get; set; }
    public bool DifferentialModelFitted { get; set; }
    public double? DifferentialModelSlope { get; set; }
    public int DifferentialModelLearningSamples { get; set; }
    public string DifferentialModelDetail { get; set; } = "";
    public bool DifferentialPredictionAvailable { get; set; }
    public double? ExpectedVoltage { get; set; }
    public double? ObservedVoltage { get; set; }
    public double? Residual { get; set; }
    public double? ExcessDroop { get; set; }
    public double? DifferentialSlope { get; set; }
    public string DifferentialPredictionQualification { get; set; } = "";
    public string DifferentialPredictionDetail { get; set; } = "";
    public string ResidualDetectorName { get; set; } = "";
    public string ResidualDetectorStatus { get; set; } = "UNAVAILABLE";
    public bool ResidualDetectorAvailable { get; set; }
    public bool ResidualDetectorAlert { get; set; }
    public bool ResidualDetectorFastAlert { get; set; }
    public bool ResidualDetectorEwmaAlert { get; set; }
    public double? FilteredResidual { get; set; }
    public int? ResidualConsecutiveSamples { get; set; }
    public string ResidualDetectorDetail { get; set; } = "";
    public IReadOnlyList<SnapshotIncident> Incidents { get; set; } = Array.Empty<SnapshotIncident>();
    public int IncidentCount { get; set; }
    public int ActiveIncidentCount { get; set; }
    public string LatestIncidentState { get; set; } = "";
    public string LatestIncidentStatus { get; set; } = "";
    public string LatestIncidentDetail { get; set; } = "";
    public string WatchdogStatus { get; set; } = "UNAVAILABLE";
    public string WatchdogBoardStatus { get; set; } = "";
    public bool WatchdogAvailable { get; set; }
    public bool WatchdogReadOnly { get; set; }
    public bool WatchdogNoSafetyCertification { get; set; }
    public bool WatchdogFresh { get; set; }
    public bool WatchdogFreshnessVerified { get; set; }
    public bool WatchdogIncidentLatched { get; set; }
    public double? WatchdogConfiguredLimit { get; set; }
    public double? WatchdogObservedLimit { get; set; }
    public double? WatchdogBoardPower { get; set; }
    public double? WatchdogBaselineLimit { get; set; }
    public double? WatchdogDifferenceFromConfigured { get; set; }
    public double? WatchdogDifferenceFromBaseline { get; set; }
    public string WatchdogDetail { get; set; } = "";
    public string AcquisitionStatus { get; set; } = "";
    public string AcquisitionDetail { get; set; } = "";
    public bool AcquisitionFreshnessKnown { get; set; }
    public bool AcquisitionAnalysisAvailable { get; set; }
    public bool AcquisitionSourceAvailable { get; set; }
    public bool AcquisitionPowerAvailable { get; set; }
    public bool Fresh(double age = 5) => !Stopped && Time <= DateTimeOffset.UtcNow.AddSeconds(1) && (DateTimeOffset.UtcNow - Time).TotalSeconds <= age;
    public static Snapshot Parse(string text)
    {
        using var doc = JsonDocument.Parse(text); var r = doc.RootElement;
        var s = new Snapshot { Time = Date(r, "timestamp_utc") ?? default,
            Stopped = Bool(r, "stopped"), Detail = Str(r, "detail"),
            Source = Str(r, "voltage_source"), GpuUuid = Str(r, "gpu_uuid"),
            Schema = (int)(Num(r, "schema_version") ?? 0) };
        s.ElectricalSource = s.Source;
        if (TryObject(r, "voltage", out var v))
        {
            s.Voltage = Num(v, "Volts"); s.Power = Num(v, "Power");
            string extras = Str(v, "Extras");
            try { using var ex = JsonDocument.Parse(extras); s.Pcie = Num(ex.RootElement, "pcie_12v_v"); s.Current = Num(ex.RootElement, "12vhpwr_a"); s.PcieCurrent = Num(ex.RootElement, "pcie_12v_a"); } catch (JsonException) { }
            s.ConnectorVoltage = s.Voltage; s.ConnectorPower = s.Power; s.ConnectorCurrent = s.Current;
            s.PcieVoltage = s.Pcie;
            s.ConnectorVoltageAvailability = Availability(s.ConnectorVoltage);
            s.ConnectorCurrentAvailability = Availability(s.ConnectorCurrent);
            s.ConnectorPowerAvailability = Availability(s.ConnectorPower);
            s.PcieVoltageAvailability = Availability(s.PcieVoltage);
            s.PcieCurrentAvailability = Availability(s.PcieCurrent);
        }
        if (TryObject(r, "gpu", out var g))
        { s.BoardPower = Num(g, "Power"); s.Temperature = Num(g, "Temperature"); s.Utilization = Num(g, "Utilization"); s.Limit = Num(g, "Limit"); }
        if (TryObject(r, "analysis", out var a))
        { s.Status = Str(a, "Status"); s.Bin = (int?)Num(a, "Bin"); s.Drop = Num(a, "Drop"); s.Reference = Num(a, "Reference"); s.Median = Num(a, "Median"); s.P05 = Num(a, "P05"); }
        if (TryObject(r, "reference_lifecycle", out var reference))
        { s.ReferenceState = Str(reference, "state"); s.ReferenceCompatibility = Str(reference, "compatibility"); }
        if (TryObject(r, "progress", out var p))
        { s.Learning = (int)(Num(p, "learning_samples") ?? 0); s.Window = (int)(Num(p, "window_samples") ?? 0); s.Stable = (int)(Num(p, "stable_samples") ?? 0); }
        ParseElectrical(s, r);
        ParseAnalysisLoad(s, r);
        ParseDifferential(s, r);
        ParseWatchdog(s, r);
        ParseIncidents(s, r);
        ParseAcquisition(s, r);
        return s;
    }
    public static string Str(JsonElement r, string k) => TryProperty(r, k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    public static double? Num(JsonElement r, string k) => TryProperty(r, k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    public static DateTimeOffset? Date(JsonElement r, string k) => TryProperty(r, k, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
    static bool Bool(JsonElement r, string k) => TryProperty(r, k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b);
    static string Key(string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    static bool TryProperty(JsonElement r, string key, out JsonElement value)
    {
        if (r.ValueKind != JsonValueKind.Object) { value = default; return false; }
        if (r.TryGetProperty(key, out value)) return true;
        string wanted = Key(key);
        foreach (var property in r.EnumerateObject()) if (Key(property.Name) == wanted) { value = property.Value; return true; }
        value = default; return false;
    }
    static bool TryObject(JsonElement r, string key, out JsonElement value) => TryProperty(r, key, out value) && value.ValueKind == JsonValueKind.Object;
    static string Availability(double? value) => value.HasValue ? "Present" : "Missing";
    static double? Measurement(JsonElement parent, string key)
    {
        if (!TryProperty(parent, key, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? Num(value, "Value") : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    }
    static string MeasurementAvailability(JsonElement parent, string key, double? value)
    {
        if (!TryProperty(parent, key, out var raw)) return Availability(value);
        var reported = raw.ValueKind == JsonValueKind.Object ? Str(raw, "Availability") : "";
        return reported.Length > 0 ? reported : Availability(value);
    }
    static (bool Fresh, bool Verified, string Kind, string Detail, DateTimeOffset? SourceTime) Freshness(JsonElement parent)
    {
        if (!TryObject(parent, "Freshness", out var f)) return (false, false, "Unavailable", "freshness unavailable", null);
        string kind = Str(f, "Kind"); bool verified = kind.IndexOf("verified", StringComparison.OrdinalIgnoreCase) >= 0 && kind.IndexOf("unverified", StringComparison.OrdinalIgnoreCase) < 0;
        return (Bool(f, "IsFresh"), verified, kind.Length == 0 ? "Unavailable" : kind, Str(f, "Detail"), Date(f, "SourceTimestampUtc"));
    }
    static string StateFor(bool available, bool fresh, bool verified) => !available ? fresh ? "UNAVAILABLE" : "STALE" : !fresh ? "STALE" : !verified ? "UNVERIFIED" : "AVAILABLE";
    static string SourceOr(string preferred, string fallback) => preferred.Length > 0 ? preferred : fallback;
    static void ParseElectrical(Snapshot s, JsonElement root)
    {
        if (!TryObject(root, "electrical", out var e))
        {
            var fallbackFresh = s.Time != default ? (true, false, "HostPollTimestampUnverified", "legacy voltage payload; source freshness unverified", (DateTimeOffset?)null) : (false, false, "Unavailable", "electrical payload unavailable", (DateTimeOffset?)null);
            s.ElectricalFresh = fallbackFresh.Item1; s.ElectricalFreshnessVerified = fallbackFresh.Item2; s.ElectricalFreshnessKind = fallbackFresh.Item3; s.ElectricalFreshnessDetail = fallbackFresh.Item4; s.ElectricalSourceTimestamp = fallbackFresh.Item5;
            s.ElectricalStatus = StateFor(s.ConnectorVoltage.HasValue, s.ElectricalFresh, s.ElectricalFreshnessVerified);
            return;
        }
        s.ElectricalSource = SourceOr(Str(e, "Source"), s.Source);
        if (TryObject(e, "Connector", out var connector))
        {
            s.ConnectorVoltage = Measurement(connector, "Voltage"); s.ConnectorCurrent = Measurement(connector, "Current"); s.ConnectorPower = Measurement(connector, "Power");
            s.ConnectorVoltageAvailability = MeasurementAvailability(connector, "Voltage", s.ConnectorVoltage); s.ConnectorCurrentAvailability = MeasurementAvailability(connector, "Current", s.ConnectorCurrent); s.ConnectorPowerAvailability = MeasurementAvailability(connector, "Power", s.ConnectorPower);
            s.Voltage = s.ConnectorVoltage ?? s.Voltage; s.Current = s.ConnectorCurrent ?? s.Current; s.Power = s.ConnectorPower ?? s.Power;
        }
        if (TryObject(e, "Pcie", out var pcie))
        {
            s.PcieVoltage = Measurement(pcie, "Voltage"); s.PcieCurrent = Measurement(pcie, "Current"); s.PciePower = Measurement(pcie, "Power");
            s.PcieVoltageAvailability = MeasurementAvailability(pcie, "Voltage", s.PcieVoltage); s.PcieCurrentAvailability = MeasurementAvailability(pcie, "Current", s.PcieCurrent); s.PciePowerAvailability = MeasurementAvailability(pcie, "Power", s.PciePower);
            s.Pcie = s.PcieVoltage ?? s.Pcie;
        }
        var freshness = Freshness(e);
        s.ElectricalFresh = freshness.Fresh; s.ElectricalFreshnessVerified = freshness.Verified; s.ElectricalFreshnessKind = freshness.Kind; s.ElectricalFreshnessDetail = freshness.Detail; s.ElectricalSourceTimestamp = freshness.SourceTime;
        s.ElectricalStatus = StateFor(s.ConnectorVoltage.HasValue, s.ElectricalFresh, s.ElectricalFreshnessVerified);
    }
    static void ParseAnalysisLoad(Snapshot s, JsonElement root)
    {
        if (!TryObject(root, "analysis_load", out var load)) { s.AnalysisLoadStatus = "UNAVAILABLE"; s.AnalysisLoadDetail = "Selected analysis load was not reported."; return; }
        s.AnalysisLoadSource = Str(load, "Source"); s.AnalysisLoadValue = Num(load, "Value"); s.AnalysisLoadUnit = Str(load, "Unit"); s.AnalysisLoadAvailable = Bool(load, "IsAvailable") || !TryProperty(load, "IsAvailable", out _) && s.AnalysisLoadValue.HasValue; s.AnalysisLoadDetail = Str(load, "Detail");
        var freshness = Freshness(load); s.AnalysisLoadFresh = freshness.Fresh; s.AnalysisLoadFreshnessVerified = freshness.Verified;
        s.AnalysisLoadStatus = StateFor(s.AnalysisLoadAvailable && s.AnalysisLoadValue.HasValue, s.AnalysisLoadFresh, s.AnalysisLoadFreshnessVerified);
        if (s.AnalysisLoadDetail.Length == 0 && s.AnalysisLoadStatus != "AVAILABLE") s.AnalysisLoadDetail = s.AnalysisLoadStatus == "STALE" ? "Selected load is stale." : s.AnalysisLoadStatus == "UNVERIFIED" ? "Selected load timestamp is unverified." : "Selected load is unavailable.";
    }
    static void ParseDifferential(Snapshot s, JsonElement root)
    {
        if (TryObject(root, "differential_model", out var model)) { s.DifferentialModelLoaded = Bool(model, "is_loaded"); s.DifferentialModelFitted = Bool(model, "is_fitted"); s.DifferentialModelState = Str(model, "state"); if (s.DifferentialModelState.Length == 0) s.DifferentialModelState = s.DifferentialModelFitted ? "FITTED" : s.DifferentialModelLoaded ? "LOADED" : "UNAVAILABLE"; s.DifferentialModelSlope = Num(model, "apparent_slope_v_per_unit"); s.DifferentialModelLearningSamples = (int)(Num(model, "learning_samples") ?? 0); s.DifferentialModelDetail = Str(model, "detail"); }
        if (TryObject(root, "differential_prediction", out var prediction)) { s.DifferentialPredictionAvailable = Bool(prediction, "is_available"); s.ExpectedVoltage = Num(prediction, "expected_voltage_v"); s.ObservedVoltage = Num(prediction, "observed_voltage_v"); s.Residual = Num(prediction, "residual_v"); s.ExcessDroop = Num(prediction, "excess_droop_v"); s.DifferentialSlope = Num(prediction, "apparent_slope_v_per_unit"); s.DifferentialPredictionQualification = Str(prediction, "qualification"); s.DifferentialPredictionDetail = Str(prediction, "detail"); }
        if (TryObject(root, "residual_detector", out var detector)) { s.ResidualDetectorName = Str(detector, "detector"); s.ResidualDetectorStatus = Str(detector, "status"); if (s.ResidualDetectorStatus.Length == 0) s.ResidualDetectorStatus = "UNAVAILABLE"; s.ResidualDetectorAvailable = Bool(detector, "is_available"); s.ResidualDetectorAlert = Bool(detector, "is_alert"); s.ResidualDetectorFastAlert = Bool(detector, "is_fast_alert"); s.ResidualDetectorEwmaAlert = Bool(detector, "is_ewma_alert"); s.FilteredResidual = Num(detector, "filtered_residual_v"); s.ResidualConsecutiveSamples = (int?)Num(detector, "consecutive_samples"); s.ResidualDetectorDetail = Str(detector, "detail"); }
    }
    static void ParseWatchdog(Snapshot s, JsonElement root)
    {
        if (!TryObject(root, "power_limit_watchdog", out var wrapper) || !TryObject(wrapper, "result", out var result)) { s.WatchdogDetail = "Read-only power-limit monitor was not reported."; return; }
        s.WatchdogAvailable = true; s.WatchdogStatus = Str(result, "Status"); s.WatchdogBoardStatus = Str(result, "BoardPowerStatus"); s.WatchdogReadOnly = Bool(result, "IsReadOnly"); s.WatchdogNoSafetyCertification = Bool(result, "NoSafetyCertification"); s.WatchdogFresh = Bool(result, "IsFresh"); s.WatchdogFreshnessVerified = Bool(result, "FreshnessVerified"); s.WatchdogIncidentLatched = Bool(result, "IncidentLatched"); s.WatchdogConfiguredLimit = Num(result, "ConfiguredLimitWatts"); s.WatchdogObservedLimit = Num(result, "ObservedLimitWatts"); s.WatchdogBoardPower = Num(result, "BoardPowerWatts"); s.WatchdogBaselineLimit = Num(result, "BaselineLimitWatts"); s.WatchdogDifferenceFromConfigured = Num(result, "DifferenceFromConfiguredWatts"); s.WatchdogDifferenceFromBaseline = Num(result, "DifferenceFromBaselineWatts"); s.WatchdogDetail = Str(result, "Detail"); if (s.WatchdogDetail.Length == 0) s.WatchdogDetail = Str(wrapper, "load_detail");
    }
    static void ParseIncidents(Snapshot s, JsonElement root)
    {
        if (!TryProperty(root, "incidents", out var values) || values.ValueKind != JsonValueKind.Array) return;
        var incidents = new List<SnapshotIncident>();
        foreach (var item in values.EnumerateArray()) if (item.ValueKind == JsonValueKind.Object)
        {
            var time = Date(item, "TriggeredAtUtc") ?? Date(item, "triggered_at_utc") ?? default;
            incidents.Add(new SnapshotIncident(Str(item, "incident_id"), Str(item, "detector"), Str(item, "status"), Str(item, "severity"), Str(item, "state"), time, Date(item, "acknowledged_at_utc"), Date(item, "resolved_at_utc"), Bool(item, "post_complete")));
        }
        s.Incidents = incidents.OrderBy(i => i.TriggeredAtUtc).ToArray(); s.IncidentCount = incidents.Count; s.ActiveIncidentCount = incidents.Count(i => !string.Equals(i.State, "RESOLVED", StringComparison.OrdinalIgnoreCase));
        if (s.Incidents.Count > 0) { var latest = s.Incidents[^1]; s.LatestIncidentState = latest.State; s.LatestIncidentStatus = latest.Status; s.LatestIncidentDetail = latest.Detector.Length > 0 ? latest.Detector : latest.Severity; }
    }
    static void ParseAcquisition(Snapshot s, JsonElement root)
    {
        if (!TryObject(root, "acquisition", out var acquisition)) return;
        s.AcquisitionStatus = Str(acquisition, "status"); s.AcquisitionDetail = Str(acquisition, "detail"); s.AcquisitionFreshnessKnown = Bool(acquisition, "freshness_known"); s.AcquisitionAnalysisAvailable = Bool(acquisition, "analysis_available"); s.AcquisitionSourceAvailable = Bool(acquisition, "source_available"); s.AcquisitionPowerAvailable = Bool(acquisition, "power_available");
    }
}

public sealed class TelemetryStore
{
    const int MaxSamples = 200_000;
    readonly string directory;
    readonly ControlClient liveClient;
    readonly BoundedIdSet sampleIds = new(MaxSamples + 4096);
    bool reorder;
    readonly Dictionary<string, Cursor> cursors = new(StringComparer.OrdinalIgnoreCase);
    public BoundedBuffer<PointSample> Samples { get; } = new(MaxSamples);
    public BoundedBuffer<Incident> Incidents { get; } = new(2000);
    public Dictionary<int, Baseline> Baselines { get; } = new();
    public Snapshot? Current { get; set; }
    public bool Limited { get; private set; }
    public string ReadError { get; private set; } = "";
    (long Length, long Ticks)? baselineStamp;
    public TelemetryStore(string directory) { this.directory = directory; liveClient = new(directory); }
    public Task RefreshAsync() => Task.Run(async () => {
        ConnectorWatch.LiveTelemetry? live = null;
        if (await liveClient.Send("hello") != null) live = (await liveClient.Send("live"))?.Live;
        Refresh(live);
    });
    void AddSample(PointSample p)
    {
        if (!sampleIds.Add(p.Time.ToString("O"))) return;
        if (Samples.Count > 0 && p.Time < Samples[Samples.Count - 1].Time) reorder = true;
        Samples.Add(p);
    }
    void Refresh(ConnectorWatch.LiveTelemetry? live)
    {
        ReadError = "";
        string[] paths = Array.Empty<string>();
        void ReadPart(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
            { if (ReadError.Length == 0) ReadError = ex.Message; }
        }
        try
        {
            if (!Directory.Exists(directory)) return;
            var status = Path.Combine(directory, "status.json");
            ReadPart(() => { if (live != null) Current = Snapshot.Parse(live.Status); else if (File.Exists(status)) Current = Snapshot.Parse(ReadShared(status, 256 * 1024)); });
            // Keep just the two newest names while enumerating; do not sort all historical files.
            ReadPart(() => {
                var newest = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(directory, "telemetry-????-??-??.csv")) { newest.Add(path); if (newest.Count > 2) newest.Remove(newest.Min!); }
                paths = newest.ToArray();
            });
            foreach (var path in paths) ReadPart(() => ReadTelemetry(path));
            ReadPart(() => {
                if (live == null) return;
                var headers = ParseCsv(live.Header.TrimEnd('\r', '\n'));
                foreach (var row in live.Rows)
                {
                    var point = ParseSample(headers, ParseCsv(row.TrimEnd('\r', '\n')));
                    if (point != null) AddSample(point);
                }
            });
            ReadPart(() => ReadEvents(Path.Combine(directory, "events.csv")));
            var baseline = Path.Combine(directory, "baseline.json");
            ReadPart(() => { if (File.Exists(baseline))
            {
                var info = new FileInfo(baseline); var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                if (baselineStamp == stamp) return;
                using var d = JsonDocument.Parse(ReadShared(baseline, 8 * 1024 * 1024));
                JsonElement bins = default;
                bool hasBins = d.RootElement.TryGetProperty("Bins", out bins) ||
                    d.RootElement.TryGetProperty("bins", out bins);
                if (!hasBins && d.RootElement.TryGetProperty("accepted", out var accepted) &&
                    accepted.ValueKind == JsonValueKind.Object)
                    hasBins = accepted.TryGetProperty("bins", out bins);
                if (hasBins && bins.ValueKind == JsonValueKind.Object)
                {
                    Baselines.Clear();
                    foreach (var b in bins.EnumerateObject())
                        if (int.TryParse(b.Name, out var key) && key is >= 0 and <= 1000)
                        {
                            var median = Snapshot.Num(b.Value, "Reference") ?? Snapshot.Num(b.Value, "reference_volts");
                            var p05 = Snapshot.Num(b.Value, "ReferenceP05") ?? Snapshot.Num(b.Value, "reference_p05_volts");
                            if (median.HasValue) Baselines[key] = new(median, p05,
                                b.Value.TryGetProperty("Learning", out var learning) ? learning.GetArrayLength() :
                                (int)(Snapshot.Num(b.Value, "learning_samples") ?? 0));
                        }
                }
                baselineStamp = stamp;
            } });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { ReadError = ex.Message; }
        finally
        {
            if (reorder) { var sorted = Samples.OrderBy(p => p.Time).ToArray(); Samples.Clear(); Samples.AddRange(sorted); reorder = false; }
            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
            Samples.RemoveAll(s => s.Time < cutoff);
            Limited |= Samples.Evictions > 0 || Incidents.Evictions > 0;
            foreach (var old in cursors.Keys.Where(k => Path.GetFileName(k).StartsWith("telemetry-", StringComparison.OrdinalIgnoreCase) && !paths.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray()) cursors.Remove(old);
        }
    }
    public static string ReadShared(string path, int maxBytes = 1024 * 1024)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {maxBytes / 1024} KiB dashboard read limit.");
        using var r = new StreamReader(f); var b = new StringBuilder(); var chunk = new char[4096]; int read;
        while ((read = r.Read(chunk, 0, Math.Min(chunk.Length, maxBytes + 1 - b.Length))) > 0)
        { b.Append(chunk, 0, read); if (b.Length > maxBytes) throw new InvalidDataException("Dashboard text read exceeded its bound."); }
        return b.ToString();
    }
    void ReadTelemetry(string path)
    {
        if (!cursors.TryGetValue(path, out var c))
        {
            c = new Cursor(); cursors[path] = c;
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f); var header = new StringBuilder(); int ch;
            while ((ch = r.Read()) >= 0 && ch != '\n') { if (header.Length >= 262144) throw new InvalidDataException("CSV header exceeds dashboard limit."); header.Append((char)ch); }
            c.Headers = ParseCsv(header.ToString().TrimEnd('\r'));
        }
        foreach (var row in ReadRows(path, c)) { var p = ParseSample(c.Headers, row); if (p != null) AddSample(p); }
    }
    void ReadEvents(string path)
    {
        if (!File.Exists(path)) return;
        if (!cursors.TryGetValue(path, out var c)) cursors[path] = c = new Cursor();
        foreach (var r in ReadRows(path, c))
            if (r.Length >= 4 && DateTimeOffset.TryParse(r[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) && r[1] is "SUDDEN_DROOP" or "BASELINE_SHIFT" or "VOLTAGE_UNAVAILABLE")
            {
                var p = Samples.LastOrDefault(x => x.Time == t);
                Incidents.Add(new(t.ToString("O") + "|" + r[1], t, r[1], r[3], p?.Voltage, Number(r[2]), p?.Bin));
            }
    }
    IEnumerable<string[]> ReadRows(string path, Cursor c)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (f.Length < c.Offset) { c.Offset = 0; c.Reset(); }
        if (c.Offset == 0 && f.Length > 32 * 1024 * 1024) { c.Offset = f.Length - 32 * 1024 * 1024; c.Skip = true; Limited = true; }
        f.Position = c.Offset;
        var bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(16 * 1024));
        try
        {
            long remaining = Math.Min(f.Length - c.Offset, 8 * 1024 * 1024);
            while (remaining > 0)
            {
                int read = f.Read(bytes, 0, (int)Math.Min(16384, remaining)); if (read == 0) break;
                c.Offset += read; remaining -= read;
                int count = c.Decoder.GetChars(bytes, 0, read, chars, 0, false);
                for (int i = 0; i < count; i++)
                {
                    char ch = chars[i];
                    if (c.Skip) { if (ch == '\n') { c.Skip = false; c.Quoted = false; } continue; }
                    // Each quote flips parity, including escaped pairs across chunk boundaries.
                    if (ch == '"') c.Quoted = !c.Quoted;
                    if (ch == '\n' && !c.Quoted)
                    {
                        string record = c.Pending.ToString().TrimEnd('\r'); c.Pending.Clear();
                        if (c.Pending.Capacity > 16384) c.Pending.Capacity = 4096;
                        yield return ParseCsv(record); continue;
                    }
                    if (c.Pending.Length >= 262144) { c.Pending.Clear(); c.Pending.Capacity = 4096; c.Skip = true; c.Quoted = false; Limited = true; continue; }
                    c.Pending.Append(ch);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); ArrayPool<char>.Shared.Return(chars); }
    }
    public static string[] ParseCsv(string line)
    {
        var cells = new List<string>(); var b = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { b.Append('"'); i++; } else quoted = !quoted; }
            else if (line[i] == ',' && !quoted) { cells.Add(b.ToString()); b.Clear(); }
            else b.Append(line[i]);
        }
        cells.Add(b.ToString()); return cells.ToArray();
    }
    public static PointSample? ParseSample(string[] headers, string[] cells)
    {
        string Get(string key) { int i = Array.IndexOf(headers, key); return i >= 0 && i < cells.Length ? cells[i] : ""; }
        if (!DateTimeOffset.TryParse(Get("timestamp_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ||
            !DateTimeOffset.TryParse(Get("voltage_timestamp_utc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sensor) ||
            Number(Get("input_voltage_v")) is not double v || v < 6 || v > 16) return null;
        double? pcie = null;
        try { using var ex = JsonDocument.Parse(Get("extra_voltages_json")); pcie = Snapshot.Num(ex.RootElement, "pcie_12v_v"); } catch (JsonException) { }
        return new(t, sensor, v, pcie, Number(Get("analysis_power_w")), (int?)Number(Get("bin_w")), Get("status"), Number(Get("reference_v")), Number(Get("rolling_p05_v")), Number(Get("median_drop_v")), Get("detail"));
    }
    public static double? Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
    sealed class Cursor
    {
        public long Offset; public StringBuilder Pending = new(); public bool Quoted, Skip;
        public string[] Headers = Array.Empty<string>(); public Decoder Decoder = Encoding.UTF8.GetDecoder();
        public void Reset() { Pending.Clear(); Quoted = Skip = false; Decoder.Reset(); }
    }
}

public sealed record HistogramResult(double Start, double Width, int[] Counts, int Total, double? Median, double? P05);
public static class PlotData
{
    public static List<PointSample> Select(IEnumerable<PointSample> samples, DateTimeOffset from, DateTimeOffset to, int? bin, bool eligibleOnly)
        => samples.Where(s => s.Time >= from && s.Time <= to && (!bin.HasValue || s.Bin == bin) && (!eligibleOnly || s.Eligible))
            .DistinctBy(s => s.SensorTime).OrderBy(s => s.Time).ToList();
    public static HistogramResult Histogram(IReadOnlyList<PointSample> points)
    {
        if (points.Count == 0) return new(12, .01, Array.Empty<int>(), 0, null, null);
        var values = points.Select(p => p.Voltage).Order().ToArray();
        double width = Math.Max(.01, Math.Ceiling((values[^1] - values[0]) / 50 / .01) * .01);
        double start = Math.Floor(values[0] / width) * width;
        var counts = new int[(int)Math.Floor((values[^1] - start) / width) + 1];
        foreach (var v in values) counts[Math.Min(counts.Length - 1, (int)Math.Floor((v - start) / width))]++;
        double Percentile(double q) { double i = (values.Length - 1) * q; return values[(int)i] + (values[(int)Math.Ceiling(i)] - values[(int)i]) * (i - (int)i); }
        return new(start, width, counts, values.Length, Percentile(.5), Percentile(.05));
    }
    public static List<PointSample> Downsample(IReadOnlyList<PointSample> samples, int pixels, DateTimeOffset from, DateTimeOffset to)
    {
        pixels = Math.Max(1, pixels);
        if (samples.Count <= pixels * 4) return samples.ToList();
        double duration = Math.Max(.001, (to - from).TotalSeconds);
        var result = new List<PointSample>(Math.Min(samples.Count, pixels * 6 + 6));
        int Bucket(int i) => (int)((samples[i].Time - from).TotalSeconds / duration * pixels);
        Span<int> extrema = stackalloc int[6];
        for (int start = 0; start < samples.Count;)
        {
            int bucket = Bucket(start), end = start + 1, low = start, high = start, pcieLow = start, pcieHigh = start;
            while (end < samples.Count && Bucket(end) == bucket)
            {
                if (samples[end].Voltage < samples[low].Voltage) low = end;
                if (samples[end].Voltage > samples[high].Voltage) high = end;
                if ((samples[end].Pcie ?? double.PositiveInfinity) < (samples[pcieLow].Pcie ?? double.PositiveInfinity)) pcieLow = end;
                if ((samples[end].Pcie ?? double.NegativeInfinity) > (samples[pcieHigh].Pcie ?? double.NegativeInfinity)) pcieHigh = end;
                end++;
            }
            extrema[0] = start; extrema[1] = low; extrema[2] = high; extrema[3] = pcieLow; extrema[4] = pcieHigh; extrema[5] = end - 1; extrema.Sort();
            for (int i = 0; i < extrema.Length; i++) if (i == 0 || extrema[i] != extrema[i - 1]) result.Add(samples[extrema[i]]);
            start = end;
        }
        return result;
    }
}
