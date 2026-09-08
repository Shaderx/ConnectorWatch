using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

/// <summary>
/// The lifecycle of an operator-visible detector incident.  Acknowledgement
/// only records that somebody has seen an incident; it never resolves or
/// weakens the incident.  Resolution is a separate explicit operation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentLifecycleState
{
    LATCHED,
    ACKNOWLEDGED,
    RESOLVED,

    Latched = LATCHED,
    Acknowledged = ACKNOWLEDGED,
    Resolved = RESOLVED,
}

/// <summary>Severity is descriptive metadata, not a safety certification.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentSeverity
{
    WARNING,
    CRITICAL,

    Warning = WARNING,
    Critical = CRITICAL,
}

/// <summary>
/// Bounded capture settings for the observations surrounding a detector
/// event.  Both time and count bounds are applied.  A count of zero disables
/// that bound; at least one positive bound is required for each side.
/// </summary>
public sealed record IncidentCaptureOptions
{
    /// <summary>Maximum number of observations retained before a trigger.</summary>
    public int PreSamples { get; init; } = 60;

    /// <summary>Maximum age of observations retained before a trigger.</summary>
    public TimeSpan PreWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum number of observations retained after a trigger.</summary>
    public int PostSamples { get; init; } = 60;

    /// <summary>Duration for which observations after a trigger are captured.</summary>
    public TimeSpan PostWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum number of incident records retained in the ledger.</summary>
    public int MaxIncidents { get; init; } = 128;

    /// <summary>
    /// Maximum number of recent observation identities retained for restart
    /// de-duplication.  This is independent of the forensic window bounds.
    /// </summary>
    public int MaxRecentObservationIds { get; init; } = 4096;

    // Friendly aliases for callers that describe the setting in seconds.
    [JsonIgnore]
    public double PreWindowSeconds
    {
        get => PreWindow.TotalSeconds;
        init => PreWindow = TimeSpan.FromSeconds(value);
    }

    [JsonIgnore]
    public double PostWindowSeconds
    {
        get => PostWindow.TotalSeconds;
        init => PostWindow = TimeSpan.FromSeconds(value);
    }

    internal void Validate()
    {
        if (PreSamples < 0 || PostSamples < 0 || MaxIncidents < 1 ||
            MaxRecentObservationIds < 1)
            throw new ArgumentOutOfRangeException(nameof(PreSamples),
                "Incident capture counts must be nonnegative and ledger bounds must be positive.");
        if (PreWindow < TimeSpan.Zero || PostWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreWindow),
                "Incident capture windows cannot be negative.");
        if (PreSamples == 0 && PreWindow == TimeSpan.Zero)
            throw new ArgumentException("At least one pre-window bound must be positive.", nameof(PreSamples));
        if (PostSamples == 0 && PostWindow == TimeSpan.Zero)
            throw new ArgumentException("At least one post-window bound must be positive.", nameof(PostSamples));
    }
}

/// <summary>One sanitized telemetry row retained for incident forensics.</summary>
public sealed record IncidentObservation
{
    [JsonPropertyName("timestamp_utc")] public DateTimeOffset TimestampUtc { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; }
    [JsonPropertyName("voltage_v")] public double? VoltageV { get; init; }
    [JsonPropertyName("load_w")] public double? LoadWatts { get; init; }
    [JsonPropertyName("detail")] public string Detail { get; init; }
    [JsonPropertyName("observation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObservationId { get; init; }

    [JsonConstructor]
    public IncidentObservation(DateTimeOffset timestampUtc, string status,
        double? voltageV = null, double? loadWatts = null, string detail = "",
        string? observationId = null)
    {
        if (timestampUtc == default)
            throw new ArgumentException("An incident observation needs a timestamp.", nameof(timestampUtc));
        if (string.IsNullOrWhiteSpace(status))
            throw new ArgumentException("An incident observation needs a status.", nameof(status));
        ValidateFinite(voltageV, nameof(voltageV));
        ValidateFinite(loadWatts, nameof(loadWatts));
        TimestampUtc = timestampUtc.ToUniversalTime();
        Status = Normalize(status, nameof(status));
        VoltageV = voltageV;
        LoadWatts = loadWatts;
        Detail = (detail ?? string.Empty).Trim();
        ObservationId = string.IsNullOrWhiteSpace(observationId)
            ? null : Normalize(observationId, nameof(observationId));
    }

    /// <summary>
    /// Returns a stable identity for this sample.  A producer may supply an
    /// id when it has a stronger source sequence; otherwise the complete
    /// observation is hashed so replaying a persisted row after restart is a
    /// no-op rather than a duplicated forensic sample.
    /// </summary>
    public string StableId => ObservationId ?? ComputeStableId(this);

    static string ComputeStableId(IncidentObservation sample)
    {
        string canonical = string.Join("|",
            sample.TimestampUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            sample.Status,
            sample.VoltageV?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            sample.LoadWatts?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            sample.Detail);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    static void ValidateFinite(double? value, string name)
    {
        if (value is double number && !double.IsFinite(number))
            throw new ArgumentOutOfRangeException(name, "Incident telemetry must be finite when present.");
    }

    internal static string Normalize(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", name)
            : value.Trim();
}

/// <summary>Metadata that turns one observation into a detector incident.</summary>
public sealed record IncidentTrigger
{
    [JsonPropertyName("detector")] public string Detector { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; }
    [JsonPropertyName("severity")] public IncidentSeverity Severity { get; init; }
    [JsonPropertyName("dedupe_key")] public string DedupeKey { get; init; }
    [JsonPropertyName("detail")] public string Detail { get; init; }

    [JsonConstructor]
    public IncidentTrigger(string detector, string status,
        IncidentSeverity severity = IncidentSeverity.WARNING,
        string? dedupeKey = null, string detail = "")
    {
        Detector = IncidentObservation.Normalize(detector, nameof(detector));
        Status = IncidentObservation.Normalize(status, nameof(status));
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        Severity = severity;
        DedupeKey = IncidentObservation.Normalize(
            string.IsNullOrWhiteSpace(dedupeKey) ? Detector + ":" + Status : dedupeKey,
            nameof(dedupeKey));
        Detail = (detail ?? string.Empty).Trim();
    }
}

/// <summary>Explicit resolution record; it is never synthesized by a clear sample.</summary>
public sealed record IncidentResolution
{
    [JsonPropertyName("incident_id")] public string IncidentId { get; init; }
    [JsonPropertyName("resolved_at_utc")] public DateTimeOffset ResolvedAtUtc { get; init; }
    [JsonPropertyName("resolved_by")] public string ResolvedBy { get; init; }
    [JsonPropertyName("note")] public string Note { get; init; }

    [JsonConstructor]
    public IncidentResolution(string incidentId, DateTimeOffset resolvedAtUtc,
        string resolvedBy, string note = "")
    {
        IncidentId = IncidentObservation.Normalize(incidentId, nameof(incidentId));
        if (resolvedAtUtc == default)
            throw new ArgumentException("A resolution needs a timestamp.", nameof(resolvedAtUtc));
        ResolvedAtUtc = resolvedAtUtc.ToUniversalTime();
        ResolvedBy = IncidentObservation.Normalize(resolvedBy, nameof(resolvedBy));
        Note = (note ?? string.Empty).Trim();
    }
}

/// <summary>
/// Immutable forensic capture around an incident trigger.  The trigger is
/// kept separate from the post-window, so consumers cannot mistake the event
/// row for a post-trigger recovery sample.
/// </summary>
public sealed record ForensicWindow
{
    [JsonPropertyName("pre")]
    public IReadOnlyList<IncidentObservation> Pre { get; init; }

    [JsonPropertyName("trigger")]
    public IncidentObservation Trigger { get; init; }

    [JsonPropertyName("post")]
    public IReadOnlyList<IncidentObservation> Post { get; init; }

    [JsonPropertyName("post_complete")]
    public bool PostComplete { get; init; }

    [JsonPropertyName("post_completed_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PostCompletedAtUtc { get; init; }

    [JsonConstructor]
    public ForensicWindow(IReadOnlyList<IncidentObservation> pre,
        IncidentObservation trigger, IReadOnlyList<IncidentObservation> post,
        bool postComplete = false, DateTimeOffset? postCompletedAtUtc = null)
    {
        if (pre is null) throw new ArgumentNullException(nameof(pre));
        if (post is null) throw new ArgumentNullException(nameof(post));
        Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
        Pre = Copy(pre);
        Post = Copy(post);
        if (Pre.Any(x => x.TimestampUtc > Trigger.TimestampUtc) ||
            Post.Any(x => x.TimestampUtc < Trigger.TimestampUtc))
            throw new ArgumentException("Forensic observations are not ordered around the trigger.");
        PostComplete = postComplete;
        PostCompletedAtUtc = postCompletedAtUtc?.ToUniversalTime();
        if (PostComplete && !PostCompletedAtUtc.HasValue)
            PostCompletedAtUtc = Trigger.TimestampUtc;
    }

    // Names used by consumers that prefer before/after terminology.
    [JsonIgnore] public IReadOnlyList<IncidentObservation> Before => Pre;
    [JsonIgnore] public IReadOnlyList<IncidentObservation> After => Post;
    [JsonIgnore] public bool IsComplete => PostComplete;

    static IReadOnlyList<IncidentObservation> Copy(IReadOnlyList<IncidentObservation> source) =>
        new ReadOnlyCollection<IncidentObservation>(source.ToList());
}

/// <summary>An incident plus its immutable forensic capture and operator records.</summary>
public sealed record LatchedIncident
{
    [JsonPropertyName("incident_id")] public string IncidentId { get; init; }
    [JsonPropertyName("detector")] public string Detector { get; init; }
    [JsonPropertyName("status")] public string Status { get; init; }
    [JsonPropertyName("severity")] public IncidentSeverity Severity { get; init; }
    [JsonPropertyName("dedupe_key")] public string DedupeKey { get; init; }
    [JsonPropertyName("triggered_at_utc")] public DateTimeOffset TriggeredAtUtc { get; init; }
    [JsonPropertyName("trigger_detail")] public string TriggerDetail { get; init; }
    [JsonPropertyName("forensic")] public ForensicWindow Forensic { get; init; }
    [JsonPropertyName("acknowledgement")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentAcknowledgement? Acknowledgement { get; init; }
    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IncidentResolution? Resolution { get; init; }

    [JsonConstructor]
    public LatchedIncident(string incidentId, string detector, string status,
        IncidentSeverity severity, string dedupeKey, DateTimeOffset triggeredAtUtc,
        string triggerDetail, ForensicWindow forensic,
        IncidentAcknowledgement? acknowledgement = null,
        IncidentResolution? resolution = null)
    {
        IncidentId = IncidentObservation.Normalize(incidentId, nameof(incidentId));
        Detector = IncidentObservation.Normalize(detector, nameof(detector));
        Status = IncidentObservation.Normalize(status, nameof(status));
        if (!Enum.IsDefined(severity)) throw new ArgumentOutOfRangeException(nameof(severity));
        Severity = severity;
        DedupeKey = IncidentObservation.Normalize(dedupeKey, nameof(dedupeKey));
        if (triggeredAtUtc == default)
            throw new ArgumentException("An incident needs a trigger timestamp.", nameof(triggeredAtUtc));
        TriggeredAtUtc = triggeredAtUtc.ToUniversalTime();
        TriggerDetail = (triggerDetail ?? string.Empty).Trim();
        Forensic = forensic ?? throw new ArgumentNullException(nameof(forensic));
        if (Forensic.Trigger.TimestampUtc != TriggeredAtUtc)
            throw new ArgumentException("Forensic trigger timestamp does not match the incident.", nameof(forensic));
        if (acknowledgement is not null &&
            !string.Equals(acknowledgement.IncidentId, IncidentId, StringComparison.Ordinal))
            throw new ArgumentException("Incident acknowledgement belongs to another incident.", nameof(acknowledgement));
        if (resolution is not null &&
            !string.Equals(resolution.IncidentId, IncidentId, StringComparison.Ordinal))
            throw new ArgumentException("Incident resolution belongs to another incident.", nameof(resolution));
        Acknowledgement = acknowledgement;
        Resolution = resolution;
    }

    [JsonIgnore]
    public IncidentLifecycleState State => Resolution is not null
        ? IncidentLifecycleState.RESOLVED
        : Acknowledgement is not null
            ? IncidentLifecycleState.ACKNOWLEDGED
            : IncidentLifecycleState.LATCHED;

    [JsonIgnore] public bool IsLatched => Resolution is null;
    [JsonIgnore] public bool IsAcknowledged => Acknowledgement is not null;
    [JsonIgnore] public bool IsResolved => Resolution is not null;
    [JsonIgnore] public bool IsForensicCaptureComplete => Forensic.PostComplete;
}

/// <summary>Versioned persisted ledger document.</summary>
public sealed record IncidentLedgerDocument
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("identity")] public string Identity { get; init; }
    [JsonPropertyName("pre_buffer")] public IReadOnlyList<IncidentObservation> PreBuffer { get; init; }
    [JsonPropertyName("incidents")] public IReadOnlyList<LatchedIncident> Incidents { get; init; }
    [JsonPropertyName("recent_observation_ids")] public IReadOnlyList<string> RecentObservationIds { get; init; }
    [JsonPropertyName("last_observation_at_utc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastObservationAtUtc { get; init; }
    [JsonPropertyName("updated_at_utc")] public DateTimeOffset UpdatedAtUtc { get; init; }

    [JsonConstructor]
    public IncidentLedgerDocument(int schemaVersion, string identity,
        IReadOnlyList<IncidentObservation>? preBuffer,
        IReadOnlyList<LatchedIncident>? incidents,
        IReadOnlyList<string>? recentObservationIds,
        DateTimeOffset? lastObservationAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (schemaVersion < 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        SchemaVersion = schemaVersion;
        Identity = IncidentObservation.Normalize(identity, nameof(identity));
        PreBuffer = new ReadOnlyCollection<IncidentObservation>(
            (preBuffer ?? Array.Empty<IncidentObservation>()).ToList());
        Incidents = new ReadOnlyCollection<LatchedIncident>(
            (incidents ?? Array.Empty<LatchedIncident>()).ToList());
        RecentObservationIds = new ReadOnlyCollection<string>(
            (recentObservationIds ?? Array.Empty<string>()).Select(x =>
                IncidentObservation.Normalize(x, nameof(recentObservationIds))).ToList());
        LastObservationAtUtc = lastObservationAtUtc?.ToUniversalTime();
        if (updatedAtUtc == default)
            throw new ArgumentException("An incident ledger needs an update timestamp.", nameof(updatedAtUtc));
        UpdatedAtUtc = updatedAtUtc.ToUniversalTime();
    }
}

public sealed record IncidentObservationResult(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("duplicate")] bool Duplicate,
    [property: JsonPropertyName("triggered")] bool Triggered,
    [property: JsonPropertyName("incident_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IncidentId,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("incident")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LatchedIncident? Incident)
{
    public bool IsDuplicate => Duplicate;
    public bool CreatedIncident => Triggered;
}

/// <summary>Stable wire names for incident lifecycle state.</summary>
public static class IncidentLifecycleStateExtensions
{
    public static string WireName(this IncidentLifecycleState state) => state switch
    {
        IncidentLifecycleState.LATCHED => "LATCHED",
        IncidentLifecycleState.ACKNOWLEDGED => "ACKNOWLEDGED",
        IncidentLifecycleState.RESOLVED => "RESOLVED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown incident lifecycle state."),
    };
}

/// <summary>
/// A bounded, restart-safe incident ledger.  It owns no detector policy: the
/// caller decides which result is an incident and supplies an
/// <see cref="IncidentTrigger"/>.  Clear/healthy observations are deliberately
/// insufficient to resolve an incident.
/// </summary>
public sealed class IncidentLedger
{
    public const int CurrentPersistenceSchemaVersion = 1;

    readonly object gate = new();
    readonly string identity;
    readonly IncidentCaptureOptions options;
    readonly List<IncidentObservation> preBuffer = [];
    readonly List<LatchedIncident> incidents = [];
    readonly Queue<string> recentObservationIds = [];
    readonly HashSet<string> recentObservationSet = new(StringComparer.Ordinal);
    DateTimeOffset? lastObservationAtUtc;
    DateTimeOffset updatedAtUtc;

    public IncidentLedger(string identity,
        IncidentCaptureOptions? options = null,
        DateTimeOffset? nowUtc = null)
    {
        this.identity = IncidentObservation.Normalize(identity, nameof(identity));
        this.options = options ?? new IncidentCaptureOptions();
        this.options.Validate();
        updatedAtUtc = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
    }

    IncidentLedger(string identity, IncidentCaptureOptions options,
        IncidentLedgerDocument document)
        : this(identity, options, document.UpdatedAtUtc)
    {
        if (document.SchemaVersion > CurrentPersistenceSchemaVersion)
            throw new InvalidDataException("Incident ledger schema is newer than this build supports.");
        if (!string.Equals(document.Identity, identity, StringComparison.Ordinal))
            throw new InvalidDataException("Incident ledger identity does not match the current monitor.");

        preBuffer.AddRange(document.PreBuffer);
        incidents.AddRange(document.Incidents);
        foreach (string id in document.RecentObservationIds)
            RememberObservation(id);
        lastObservationAtUtc = document.LastObservationAtUtc;
        updatedAtUtc = document.UpdatedAtUtc;
        TrimPreBuffer(lastObservationAtUtc ?? updatedAtUtc);
        TrimIncidents();
    }

    public string Identity => identity;
    public IncidentCaptureOptions Options => options;
    public DateTimeOffset? LastObservationAtUtc
    {
        get { lock (gate) return lastObservationAtUtc; }
    }

    public IReadOnlyList<LatchedIncident> Incidents
    {
        get { lock (gate) return SnapshotIncidents(); }
    }

    public IReadOnlyList<LatchedIncident> ActiveIncidents
    {
        get
        {
            lock (gate)
                return new ReadOnlyCollection<LatchedIncident>(
                    incidents.Where(x => x.IsLatched).ToList());
        }
    }

    public LatchedIncident? Get(string incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId)) return null;
        lock (gate)
            return incidents.FirstOrDefault(x => string.Equals(x.IncidentId,
                incidentId.Trim(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Records one row and optionally latches an incident at that row.  The
    /// trigger row is excluded from the pre-window and kept separately in the
    /// forensic capture. Existing active incidents receive the row as post
    /// context before a new incident is created.
    /// </summary>
    public IncidentObservationResult Observe(IncidentObservation observation,
        IncidentTrigger? trigger = null)
    {
        if (observation is null) throw new ArgumentNullException(nameof(observation));
        lock (gate)
        {
            string observationId = observation.StableId;
            if (recentObservationSet.Contains(observationId))
                return new(false, true, false, null,
                    "Observation was already recorded; restart replay is idempotent.", null);

            if (lastObservationAtUtc.HasValue && observation.TimestampUtc < lastObservationAtUtc.Value)
                throw new ArgumentOutOfRangeException(nameof(observation),
                    "Incident observations must not move backwards in time.");

            RememberObservation(observationId);
            foreach (int index in Enumerable.Range(0, incidents.Count).ToArray())
            {
                var existing = incidents[index];
                if (existing.IsLatched)
                    incidents[index] = AppendPost(existing, observation);
            }

            LatchedIncident? resultingIncident = null;
            bool triggered = false;
            if (trigger is not null)
            {
                var existing = incidents.FirstOrDefault(x => x.IsLatched &&
                    string.Equals(x.DedupeKey, trigger.DedupeKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    resultingIncident = existing;
                }
                else
                {
                    string incidentId = ComputeIncidentId(identity, trigger, observation.TimestampUtc);
                    // A resolved incident can have the same deterministic key
                    // only if the producer repeats the exact trigger timestamp.
                    // Reuse is safe and makes a restart/replay deterministic.
                    var prior = incidents.FirstOrDefault(x => x.IncidentId == incidentId);
                    if (prior is not null)
                    {
                        resultingIncident = prior;
                    }
                    else
                    {
                        var window = new ForensicWindow(
                            preBuffer, observation, Array.Empty<IncidentObservation>(),
                            postComplete: options.PostWindow == TimeSpan.Zero,
                            postCompletedAtUtc: options.PostWindow == TimeSpan.Zero
                                ? observation.TimestampUtc : null);
                        resultingIncident = new LatchedIncident(
                            incidentId, trigger.Detector, trigger.Status, trigger.Severity,
                            trigger.DedupeKey, observation.TimestampUtc, trigger.Detail,
                            window);
                        incidents.Add(resultingIncident);
                        triggered = true;
                        TrimIncidents();
                    }
                }
            }

            preBuffer.Add(observation);
            TrimPreBuffer(observation.TimestampUtc);
            lastObservationAtUtc = observation.TimestampUtc;
            updatedAtUtc = observation.TimestampUtc;
            return new(true, false, triggered, resultingIncident?.IncidentId,
                triggered ? "Incident latched with a bounded forensic pre-window."
                    : "Observation recorded.", resultingIncident);
        }
    }

    /// <summary>Convenience method for latching directly at one row.</summary>
    public LatchedIncident Latch(IncidentObservation observation, IncidentTrigger trigger)
    {
        var result = Observe(observation, trigger);
        if (!result.Accepted && !result.Duplicate)
            throw new InvalidOperationException(result.Detail);
        if (result.Incident is null)
            throw new InvalidOperationException("Incident was not created or found.");
        return result.Incident;
    }

    /// <summary>
    /// Acknowledgement is idempotent and deliberately leaves the incident
    /// latched.  Only Resolve can end the incident lifecycle.
    /// </summary>
    public IncidentAcknowledgement Acknowledge(string incidentId,
        DateTimeOffset acknowledgedAtUtc, string acknowledgedBy, string note = "")
    {
        lock (gate)
        {
            int index = FindIndex(incidentId);
            var incident = incidents[index];
            if (incident.Acknowledgement is not null)
                return incident.Acknowledgement;
            var acknowledgement = new IncidentAcknowledgement(incident.IncidentId,
                incident.Status, acknowledgedAtUtc, acknowledgedBy, note);
            incidents[index] = incident with { Acknowledgement = acknowledgement };
            updatedAtUtc = acknowledgement.AcknowledgedAtUtc;
            return acknowledgement;
        }
    }

    public bool ClearAcknowledgement(string incidentId)
    {
        lock (gate)
        {
            int index = FindIndex(incidentId);
            if (incidents[index].Acknowledgement is null) return false;
            incidents[index] = incidents[index] with { Acknowledgement = null };
            updatedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>
    /// Resolves only when explicitly called.  Repeating the same operation is
    /// idempotent and returns the original resolution timestamp/operator.
    /// </summary>
    public IncidentResolution Resolve(string incidentId,
        DateTimeOffset resolvedAtUtc, string resolvedBy, string note = "")
    {
        lock (gate)
        {
            int index = FindIndex(incidentId);
            var incident = incidents[index];
            if (incident.Resolution is not null)
                return incident.Resolution;
            var resolution = new IncidentResolution(incident.IncidentId,
                resolvedAtUtc, resolvedBy, note);
            incidents[index] = incident with { Resolution = resolution };
            updatedAtUtc = resolution.ResolvedAtUtc;
            TrimIncidents();
            return resolution;
        }
    }

    public IncidentLedgerDocument ToDocument()
    {
        lock (gate)
        {
            return new IncidentLedgerDocument(CurrentPersistenceSchemaVersion,
                identity, preBuffer, incidents, recentObservationIds.ToArray(),
                lastObservationAtUtc, updatedAtUtc);
        }
    }

    public string ToJson(bool indented = true) =>
        IncidentLedgerPersistence.Serialize(ToDocument(), indented);

    public static IncidentLedger Load(string json, string identity,
        IncidentCaptureOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Incident ledger JSON is empty.");
        return new IncidentLedger(identity, options ?? new IncidentCaptureOptions(),
            IncidentLedgerPersistence.Deserialize(json));
    }

    public static IncidentLedger LoadOrNew(string? json, string identity,
        IncidentCaptureOptions? options = null, DateTimeOffset? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new IncidentLedger(identity, options, nowUtc);
        return Load(json, identity, options);
    }

    static string ComputeIncidentId(string identity, IncidentTrigger trigger,
        DateTimeOffset timestampUtc)
    {
        string canonical = string.Join("|", "incident-v1", identity,
            trigger.DedupeKey, trigger.Status,
            timestampUtc.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        return "INC-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..24];
    }

    int FindIndex(string incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId))
            throw new ArgumentException("An incident id is required.", nameof(incidentId));
        int index = incidents.FindIndex(x => string.Equals(x.IncidentId,
            incidentId.Trim(), StringComparison.Ordinal));
        if (index < 0) throw new KeyNotFoundException("Incident was not found: " + incidentId);
        return index;
    }

    LatchedIncident AppendPost(LatchedIncident incident, IncidentObservation observation)
    {
        if (incident.Forensic.PostComplete || observation.TimestampUtc <= incident.TriggeredAtUtc)
            return incident;

        var post = incident.Forensic.Post.ToList();
        TimeSpan elapsed = observation.TimestampUtc - incident.TriggeredAtUtc;
        bool outsideTime = options.PostWindow > TimeSpan.Zero && elapsed > options.PostWindow;
        if (!outsideTime && (options.PostSamples == 0 || post.Count < options.PostSamples))
            post.Add(observation);

        bool complete = options.PostWindow == TimeSpan.Zero || outsideTime ||
            options.PostSamples > 0 && post.Count >= options.PostSamples;
        DateTimeOffset? completedAt = complete
            ? outsideTime ? incident.TriggeredAtUtc.Add(options.PostWindow) : observation.TimestampUtc
            : null;
        var window = incident.Forensic with
        {
            Post = new ReadOnlyCollection<IncidentObservation>(post),
            PostComplete = complete,
            PostCompletedAtUtc = completedAt,
        };
        return incident with { Forensic = window };
    }

    void RememberObservation(string id)
    {
        if (!recentObservationSet.Add(id)) return;
        recentObservationIds.Enqueue(id);
        while (recentObservationIds.Count > options.MaxRecentObservationIds)
        {
            string old = recentObservationIds.Dequeue();
            recentObservationSet.Remove(old);
        }
    }

    void TrimPreBuffer(DateTimeOffset referenceUtc)
    {
        if (options.PreWindow > TimeSpan.Zero)
            preBuffer.RemoveAll(x => referenceUtc - x.TimestampUtc > options.PreWindow);
        if (options.PreSamples > 0 && preBuffer.Count > options.PreSamples)
            preBuffer.RemoveRange(0, preBuffer.Count - options.PreSamples);
    }

    void TrimIncidents()
    {
        while (incidents.Count > options.MaxIncidents)
        {
            int index = incidents.FindIndex(x => x.IsResolved);
            if (index < 0) return; // Never discard an unresolved incident.
            incidents.RemoveAt(index);
        }
    }

    IReadOnlyList<LatchedIncident> SnapshotIncidents() =>
        new ReadOnlyCollection<LatchedIncident>(incidents.ToList());
}

/// <summary>Persistence helpers kept separate from the daemon's file policy.</summary>
public static class IncidentLedgerPersistence
{
    static JsonSerializerOptions Options(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(IncidentLedger ledger, bool indented = true) =>
        Serialize(ledger?.ToDocument() ?? throw new ArgumentNullException(nameof(ledger)), indented);

    public static string Serialize(IncidentLedgerDocument document, bool indented = true)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        return JsonSerializer.Serialize(document, Options(indented));
    }

    public static IncidentLedgerDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("Incident ledger JSON is empty.");
        try
        {
            return JsonSerializer.Deserialize<IncidentLedgerDocument>(json, Options(false))
                ?? throw new FormatException("Incident ledger JSON did not contain a document.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Incident ledger JSON is invalid or unsupported.", ex);
        }
    }
}
