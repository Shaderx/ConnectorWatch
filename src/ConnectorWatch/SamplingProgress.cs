using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConnectorWatch;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SamplingProgressState
{
    NO_SAMPLE,
    RUNNING,
    STALLED,
    STOPPED,
}

/// <summary>
/// Externally readable progress for a daemon instance.  Monotonic values are
/// only meaningful together with InstanceId; a restart always gets a new id
/// and starts with no completed sample.  JsonPropertyName attributes keep the
/// wire contract stable while callers use normal C# names.
/// </summary>
public sealed record SamplingProgressContract(
    [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("restart_sequence")] long RestartSequence,
    [property: JsonPropertyName("started_at_utc")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("progress_timestamp_utc")] DateTimeOffset ProgressTimestampUtc,
    [property: JsonPropertyName("monitor_running")] bool MonitorRunning,
    [property: JsonPropertyName("last_completed_sample_monotonic")] long? LastCompletedSampleMonotonic,
    [property: JsonPropertyName("last_completed_sample_utc")] DateTimeOffset? LastCompletedSampleUtc,
    [property: JsonPropertyName("sample_age_seconds")] double? SampleAgeSeconds,
    [property: JsonPropertyName("stalled")] bool Stalled,
    [property: JsonPropertyName("state")] SamplingProgressState State,
    [property: JsonPropertyName("stale_after_seconds")] double StaleAfterSeconds,
    [property: JsonPropertyName("stop_reason")] string? StopReason)
{
    public long? LastCompletedSampleMonotonicTicks => LastCompletedSampleMonotonic;
    public DateTimeOffset? LastCompletedSampleTimestampUtc => LastCompletedSampleUtc;
    public bool IsStalled => Stalled;
    public bool IsRunning => MonitorRunning;
}

/// <summary>Process-local progress tracker.  The tracker does not perform
/// file/pipe writes; its immutable contract is suitable for status.json,
/// progress.json, or a control response.</summary>
public sealed class SamplingProgressTracker
{
    readonly double staleAfterSeconds;
    readonly double startupGraceSeconds;
    readonly string instanceId;
    readonly long restartSequence;
    readonly DateTimeOffset startedAtUtc;
    readonly long startedMonotonic;
    long? lastCompletedMonotonic;
    DateTimeOffset? lastCompletedUtc;
    bool running = true;
    string? stopReason;

    public SamplingProgressTracker(
        string instanceId,
        DateTimeOffset startedAtUtc,
        long startedMonotonic,
        double staleAfterSeconds,
        long restartSequence = 0,
        double startupGraceSeconds = 0)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));
        if (!double.IsFinite(staleAfterSeconds) || staleAfterSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(staleAfterSeconds));
        if (!double.IsFinite(startupGraceSeconds) || startupGraceSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(startupGraceSeconds));
        if (restartSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(restartSequence));

        this.instanceId = instanceId;
        this.startedAtUtc = startedAtUtc.ToUniversalTime();
        this.startedMonotonic = startedMonotonic;
        this.staleAfterSeconds = staleAfterSeconds;
        this.startupGraceSeconds = startupGraceSeconds;
        this.restartSequence = restartSequence;
    }

    public string InstanceId => instanceId;
    public long RestartSequence => restartSequence;
    public DateTimeOffset StartedAtUtc => startedAtUtc;
    public bool MonitorRunning => running;
    public long? LastCompletedSampleMonotonic => lastCompletedMonotonic;
    public DateTimeOffset? LastCompletedSampleUtc => lastCompletedUtc;

    /// <summary>Marks a poll complete.  The UTC value is descriptive; all
    /// age calculations use the supplied monotonic timestamp.</summary>
    public void CompleteSample(long completedMonotonic, DateTimeOffset sampleTimestampUtc)
    {
        if (!running)
            throw new InvalidOperationException("Cannot complete a sample after the monitor stopped.");
        if (completedMonotonic < startedMonotonic)
            throw new ArgumentOutOfRangeException(nameof(completedMonotonic),
                "A completed sample cannot precede the instance start on the monotonic clock.");
        if (lastCompletedMonotonic.HasValue && completedMonotonic < lastCompletedMonotonic.Value)
            throw new ArgumentOutOfRangeException(nameof(completedMonotonic),
                "Completed sample monotonic timestamps must be nondecreasing.");
        lastCompletedMonotonic = completedMonotonic;
        lastCompletedUtc = sampleTimestampUtc.ToUniversalTime();
    }

    public void CompleteSample(PollTimingObservation poll,
        DateTimeOffset? sampleTimestampUtc = null)
    {
        if (poll is null) throw new ArgumentNullException(nameof(poll));
        CompleteSample(poll.PollEndMonotonic,
            sampleTimestampUtc ?? poll.HostTimestampUtc);
    }

    public void MarkSampleComplete(long completedMonotonic,
        DateTimeOffset sampleTimestampUtc) =>
        CompleteSample(completedMonotonic, sampleTimestampUtc);

    public void MarkStopped(string? reason = null)
    {
        running = false;
        stopReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
    }

    public SamplingProgressContract Snapshot(long nowMonotonic, DateTimeOffset nowUtc)
    {
        var uptime = MonotonicTime.ElapsedSeconds(startedMonotonic, nowMonotonic);
        var age = lastCompletedMonotonic.HasValue
            ? MonotonicTime.ElapsedSeconds(lastCompletedMonotonic.Value, nowMonotonic)
            : null;

        // A live instance with no completed sample is allowed a startup grace
        // period.  This keeps a source that has no analyzable data separate
        // from a genuinely frozen daemon.
        var stalled = running &&
            ((age.HasValue && age.Value > staleAfterSeconds) ||
             (!age.HasValue && uptime.HasValue && uptime.Value > startupGraceSeconds));
        var state = !running ? SamplingProgressState.STOPPED :
            stalled ? SamplingProgressState.STALLED :
            !lastCompletedMonotonic.HasValue ? SamplingProgressState.NO_SAMPLE :
            SamplingProgressState.RUNNING;
        return new(
            instanceId,
            restartSequence,
            startedAtUtc,
            nowUtc.ToUniversalTime(),
            running,
            lastCompletedMonotonic,
            lastCompletedUtc,
            age,
            stalled,
            state,
            staleAfterSeconds,
            stopReason);
    }

    public string SnapshotJson(long nowMonotonic, DateTimeOffset nowUtc,
        JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(Snapshot(nowMonotonic, nowUtc), options ?? JsonOptions.Default);
}

public static class SamplingProgressJson
{
    public static string Serialize(SamplingProgressContract progress,
        JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(progress, options ?? JsonOptions.Default);

    public static SamplingProgressContract Deserialize(string json,
        JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<SamplingProgressContract>(json, options ?? JsonOptions.Default)
        ?? throw new FormatException("Sampling progress JSON was empty.");
}

static class JsonOptions
{
    internal static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
