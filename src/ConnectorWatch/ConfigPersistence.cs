using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectorWatch;

/// <summary>Updates daemon-owned settings without reserializing the complete
/// configuration contract. Unknown fields belong to the package or operator
/// and must survive a live policy change.</summary>
internal static class ConfigPersistence
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void SetAutoAcceptReference(string path, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A configuration path is required.", nameof(path));

        JsonNode? parsed;
        try { parsed = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException ex)
        {
            throw new FormatException("Configuration JSON is invalid.", ex);
        }

        if (parsed is not JsonObject root)
            throw new FormatException("Configuration JSON must contain an object.");

        // Normalize any previous spelling (including snake_case) so startup's
        // strict Config deserializer sees exactly one canonical property. This
        // avoids creating two competing settings while retaining every
        // unrelated JSON property and value.
        string normalized = Normalize(nameof(Config.AutoAcceptReference));
        foreach (var propertyName in root.Select(pair => pair.Key).ToArray())
            if (Normalize(propertyName) == normalized)
                root.Remove(propertyName);
        root[nameof(Config.AutoAcceptReference)] = enabled;
        HybridStorage.Atomic(path, root.ToJsonString(Json));
    }

    static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());
}

/// <summary>Pure lifecycle gates for the automatic acceptance policy. The
/// differential fit readiness check is deliberately kept in the daemon runtime
/// because it depends on in-memory evidence collected during this process.</summary>
internal static class ReferenceAutoAcceptance
{
    public static bool CanAccept(ReferenceLifecycle lifecycle, bool enabled,
        bool sourceHealthy, out string detail)
    {
        if (lifecycle is null) throw new ArgumentNullException(nameof(lifecycle));
        var snapshot = lifecycle.Snapshot();
        if (!enabled)
        {
            detail = "Automatic acceptance is disabled.";
            return false;
        }
        if (!sourceHealthy)
        {
            detail = "Automatic acceptance requires a healthy voltage source.";
            return false;
        }
        if (snapshot.Accepted is not null)
        {
            detail = "An accepted reference already exists; archive it before automatic acceptance.";
            return false;
        }
        if (snapshot.State != ReferenceLifecycleState.REFERENCE_UNVERIFIED ||
            lifecycle.RequiresExplicitMigration ||
            snapshot.Compatibility is not (ReferenceCompatibility.COMPATIBLE or
                ReferenceCompatibility.RESTART or ReferenceCompatibility.LEGACY))
        {
            detail = "Automatic acceptance requires a compatible, unverified reference.";
            return false;
        }
        var candidate = snapshot.Candidate;
        if (candidate is null)
        {
            detail = "Waiting for a learned reference candidate.";
            return false;
        }
        if (candidate.Origin != ReferenceCandidateOrigin.LEARNED)
        {
            detail = "Legacy or imported candidates require explicit operator migration.";
            return false;
        }
        if (!candidate.IsQualified)
        {
            detail = "Waiting for the learned reference candidate to qualify.";
            return false;
        }
        detail = "A qualified learned reference is ready for automatic acceptance.";
        return true;
    }

    /// <summary>Runs the policy decision and fit-readiness gate. The caller
    /// supplies the daemon's preview and shared acceptance pipeline so this
    /// seam can be exercised without starting native sensor readers.</summary>
    public static bool TryAccept(ReferenceLifecycle lifecycle, bool enabled,
        bool sourceHealthy, long evidenceGeneration, ref long lastPreviewGeneration,
        int minimumRetrySamples, Func<DifferentialModelFitResult> preview,
        Func<ReferenceOperationResult> accept, out string detail)
    {
        if (preview is null) throw new ArgumentNullException(nameof(preview));
        if (accept is null) throw new ArgumentNullException(nameof(accept));
        if (!CanAccept(lifecycle, enabled, sourceHealthy, out detail)) return false;

        if (lastPreviewGeneration >= 0 &&
            evidenceGeneration - lastPreviewGeneration < Math.Max(1, minimumRetrySamples))
        {
            detail = string.Empty;
            return false;
        }

        lastPreviewGeneration = evidenceGeneration;
        var fit = preview();
        if (!fit.IsUsable)
        {
            detail = "Waiting for usable differential model evidence: " + fit.Detail;
            return false;
        }

        var operation = accept();
        detail = operation.Succeeded
            ? "Automatically accepted qualified learned reference."
            : operation.Detail;
        return operation.Succeeded;
    }
}
