using System.Text.Json;
using Puck.Abstractions.Documents;

namespace Puck.World.Protocol;

/// <summary>A reload's keyed document delta, pinned to one authority document and one exact result.</summary>
/// <param name="BaseVersion">The authority document the sender compared.</param>
/// <param name="BaseHash">Its canonical document fingerprint.</param>
/// <param name="TargetHash">The reconstructed document's canonical fingerprint.</param>
/// <param name="Patch">The compact JSON delta in <see cref="WorldDocumentBasis"/> vocabulary.</param>
/// <param name="DocumentDirectory">The replacement document's asset directory.</param>
public sealed record WorldReloadDelta(WorldDocumentVersion BaseVersion, string BaseHash, string TargetHash, byte[] Patch, string? DocumentDirectory) {
    /// <summary>The named refusal for an intervening install or a different authority activation.</summary>
    public const string StaleBaseCode = "world.reload.stale-base";
    /// <summary>The named refusal for a base whose bytes disagree with the sender's claimed base.</summary>
    public const string BaseMismatchCode = "world.reload.base-mismatch";
    /// <summary>The named refusal for a delta that does not reconstruct its claimed result.</summary>
    public const string TargetMismatchCode = "world.reload.target-mismatch";

    /// <summary>Computes a delta with the existing document diff and proves its merge reproduces the target.</summary>
    /// <param name="current">The installed declarations, rather than an export of live state.</param>
    /// <param name="version">Their authority version.</param>
    /// <param name="target">The newly compiled document.</param>
    /// <returns>The patch and its pins; never a whole-document fallback.</returns>
    /// <exception cref="InvalidDataException">The document diff cannot reproduce the target.</exception>
    public static WorldReloadDelta Create(WorldDefinition current, WorldDocumentVersion version, WorldDefinition target) {
        var basis = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(current));
        var next = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(target));
        var patch = WorldDocumentBasis.Diff(basis, next);
        if (!WorldDocumentBasis.TryMerge(basis, patch, out var merged, out var reason) ||
            !System.Text.Json.Nodes.JsonNode.DeepEquals(merged, next)) {
            throw new InvalidDataException($"world.reload.delta-unrepresentable: {reason}");
        }
        return new(version, WorldDefinitionFingerprint.Compute(current), WorldDefinitionFingerprint.Compute(target),
            CanonicalJsonDocument.SerializeCompact(patch), target.DocumentDirectory);
    }

    /// <summary>Checks the exact base at both preparation and ordered application, before any mutation.</summary>
    /// <param name="current">The authority's installed declarations.</param>
    /// <param name="version">Their current version.</param>
    /// <returns>The named refusal, or null when the base agrees.</returns>
    public string? RefuseBase(WorldDefinition current, WorldDocumentVersion version) {
        if (!BaseVersion.IsDelivered || BaseVersion != version) {
            return $"{StaleBaseCode}: expected {BaseVersion}, current {version}";
        }
        return string.Equals(BaseHash, WorldDefinitionFingerprint.Compute(current), StringComparison.Ordinal)
            ? null : $"{BaseMismatchCode}: the current document does not match {BaseHash}";
    }

    /// <summary>Reconstructs and validates a candidate without changing the authority's document.</summary>
    /// <param name="current">The authority's installed declarations.</param>
    /// <param name="version">Their version.</param>
    /// <param name="candidate">The locally validated result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the base, merge, document and result pin all agree.</returns>
    public bool TryPrepare(WorldDefinition current, WorldDocumentVersion version,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldDefinition? candidate, out string reason) {
        candidate = null;
        if (RefuseBase(current, version) is { } refusal) { reason = refusal; return false; }
        try {
            var basis = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(current));
            var patch = WorldProjectionDelta.Tree(Patch);
            if (!WorldDocumentBasis.TryMerge(basis, patch, out var merged, out reason)) { return false; }
            var prepared = WorldDefinitionSerialization.Deserialize(CanonicalJsonDocument.SerializeCompact(merged!), DocumentDirectory);
            if (!string.Equals(TargetHash, WorldDefinitionFingerprint.Compute(prepared), StringComparison.Ordinal)) {
                reason = $"{TargetMismatchCode}: the reconstructed document does not match {TargetHash}";
                return false;
            }
            candidate = prepared;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException) {
            reason = $"world.reload.delta-invalid: {exception.Message}";
            return false;
        }
    }
}
