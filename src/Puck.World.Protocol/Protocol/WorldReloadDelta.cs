using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;

namespace Puck.World.Protocol;

/// <summary>A reload's keyed document delta, pinned to one authority document and one exact result.</summary>
/// <param name="BaseVersion">The authority document the sender compared.</param>
/// <param name="BaseHash">Its canonical document fingerprint.</param>
/// <param name="TargetHash">The reconstructed document's canonical fingerprint.</param>
/// <param name="Patch">The compact JSON delta in <see cref="WorldDocumentBasis"/> vocabulary.</param>
/// <param name="DocumentDirectory">The replacement document's asset directory.</param>
public sealed record WorldReloadDelta(WorldDocumentVersion BaseVersion, string BaseHash, string TargetHash, byte[] Patch, string? DocumentDirectory) {
    /// <summary>The named refusal for a base the authority cannot name: another activation's declarations, or ones an
    /// install replaced that nothing holds any longer.</summary>
    public const string StaleBaseCode = "world.reload.stale-base";
    /// <summary>The named refusal for a base whose bytes disagree with the sender's claimed base.</summary>
    public const string BaseMismatchCode = "world.reload.base-mismatch";
    /// <summary>The named refusal for a delta that does not reconstruct its claimed result.</summary>
    public const string TargetMismatchCode = "world.reload.target-mismatch";

    // The candidate a delta reconstructs over the declarations it was diffed against, kept beside those declarations by
    // the process that diffed them: reconstruction is a function of the basis and the target alone, so an authority in
    // that process that names the same basis instance and the same pins takes this result in place of deriving it
    // again. An authority anywhere else holds no entry and reconstructs.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorldDefinition, Reconstruction> Reconstructions = new();

    private sealed record Reconstruction(string BaseHash, string TargetHash, WorldDefinition Candidate);

    /// <summary>Computes a delta with the existing document diff and proves its merge reproduces the target.</summary>
    /// <remarks>The candidate the delta reconstructs (<paramref name="target"/> holding <paramref name="current"/>'s own
    /// instance of every member and keyed row the patch leaves unchanged) is kept beside <paramref name="current"/>, so
    /// <see cref="TryPrepare"/> over that same instance in this process answers it without reconstructing.</remarks>
    /// <param name="current">The installed declarations, rather than an export of live state.</param>
    /// <param name="version">Their authority version.</param>
    /// <param name="target">The newly compiled document.</param>
    /// <returns>The patch and its pins; never a whole-document fallback.</returns>
    /// <exception cref="InvalidDataException">The document diff cannot reproduce the target, or sharing changed its
    /// canonical fingerprint.</exception>
    public static WorldReloadDelta Create(WorldDefinition current, WorldDocumentVersion version, WorldDefinition target) {
        var (basis, next, patch) = Diff(current: current, target: target);
        var baseHash = WorldDefinitionFingerprint.Compute(current);
        var targetHash = WorldDefinitionFingerprint.Compute(target);
        var shared = WorldDocumentWalker.Share(basis: current, basisTree: basis, patch: patch, value: target, valueTree: next);

        if (!ReferenceEquals(objA: shared, objB: target) && !string.Equals(targetHash, WorldDefinitionFingerprint.Compute(shared), StringComparison.Ordinal)) {
            throw new InvalidDataException($"{TargetMismatchCode}: the shared document does not match {targetHash}");
        }
        Reconstructions.AddOrUpdate(key: current, value: new Reconstruction(BaseHash: baseHash, Candidate: shared, TargetHash: targetHash));
        return new(version, baseHash, targetHash, CanonicalJsonDocument.SerializeCompact(patch), target.DocumentDirectory);
    }

    // The canonical trees of two documents and the patch from the first to the second, proved to merge into the second.
    private static (JsonObject Basis, JsonObject Next, JsonObject Patch) Diff(WorldDefinition current, WorldDefinition target) {
        var basis = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(current));
        var next = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(target));
        var patch = WorldDocumentBasis.Diff(basis, next);

        if (!WorldDocumentBasis.TryMerge(basis, patch, out var merged, out var reason)) {
            throw new InvalidDataException($"world.reload.delta-unrepresentable: {reason}");
        }
        if (!OrderedJson.Equals(merged, next)) {
            throw new InvalidDataException($"world.reload.delta-unrepresentable: the patch merges into another document at {FirstDifference(left: merged, path: "$", right: next)}");
        }
        return (basis, next, patch);
    }
    // Where two trees first differ, in document order, as a path and the two values' kinds.
    private static string FirstDifference(JsonNode? left, JsonNode? right, string path) {
        if ((left is JsonObject leftObject) && (right is JsonObject rightObject)) {
            using var leftMembers = leftObject.GetEnumerator();
            using var rightMembers = rightObject.GetEnumerator();

            while (true) {
                var hasLeft = leftMembers.MoveNext();
                var hasRight = rightMembers.MoveNext();

                if (!hasLeft && !hasRight) { return path; }
                if (!hasLeft) { return $"{path} (the target holds member {rightMembers.Current.Key}, {(rightMembers.Current.Value?.ToJsonString() ?? "null")}, which the merge lacks)"; }
                if (!hasRight) { return $"{path} (the merge holds member {leftMembers.Current.Key}, {(leftMembers.Current.Value?.ToJsonString() ?? "null")}, which the target lacks)"; }
                if (!string.Equals(leftMembers.Current.Key, rightMembers.Current.Key, StringComparison.Ordinal)) {
                    return $"{path} (member {leftMembers.Current.Key} where the target holds {rightMembers.Current.Key})";
                }
                if (!OrderedJson.Equals(leftMembers.Current.Value, rightMembers.Current.Value)) {
                    return FirstDifference(left: leftMembers.Current.Value, path: $"{path}.{leftMembers.Current.Key}", right: rightMembers.Current.Value);
                }
            }
        }
        if ((left is JsonArray leftRows) && (right is JsonArray rightRows)) {
            if (leftRows.Count != rightRows.Count) { return $"{path} ({leftRows.Count} rows where the target holds {rightRows.Count})"; }
            for (var index = 0; (index < leftRows.Count); index++) {
                if (!OrderedJson.Equals(leftRows[index], rightRows[index])) {
                    return FirstDifference(left: leftRows[index], path: $"{path}[{index}]", right: rightRows[index]);
                }
            }
        }
        return $"{path} ({(left?.ToJsonString() ?? "null")} where the target holds {(right?.ToJsonString() ?? "null")})";
    }

    /// <summary>Returns <paramref name="target"/> holding <paramref name="current"/>'s own instance of every member and
    /// keyed row their document diff leaves unchanged: the candidate <see cref="TryPrepare"/> reconstructs from that
    /// diff's patch, for a holder of the whole target (a replay drive's re-read, an install that landed between a
    /// delta's preparation and its application).</summary>
    /// <remarks>Sharing is decided from the content diff alone, so a target shared here and one reconstructed from the
    /// same diff's patch share the same members and rows with <paramref name="current"/>: what an install keeps by
    /// instance is the same on a live run and on its replay.</remarks>
    /// <param name="current">The installed declarations.</param>
    /// <param name="target">The complete replacement document.</param>
    /// <returns>The shared target; <paramref name="target"/> itself when nothing could be shared.</returns>
    /// <exception cref="InvalidDataException">The document diff cannot reproduce the target.</exception>
    public static WorldDefinition Share(WorldDefinition current, WorldDefinition target) {
        var (basis, next, patch) = Diff(current: current, target: target);

        return WorldDocumentWalker.Share(basis: current, basisTree: basis, patch: patch, value: target, valueTree: next);
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

    /// <summary>Reconstructs a candidate without changing the authority's document. Every member and keyed row the
    /// patch leaves unchanged is <paramref name="current"/>'s own instance (<see cref="WorldDocumentWalker.Share"/>), so
    /// what the authority derived from them by reference carries over, and the target pin is checked on that shared
    /// candidate, so sharing never changes the canonical bytes installed.</summary>
    /// <param name="current">The authority's installed declarations.</param>
    /// <param name="version">Their version.</param>
    /// <param name="candidate">The locally validated result, or null on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the base, merge, document and result pin all agree.</returns>
    public bool TryPrepare(WorldDefinition current, WorldDocumentVersion version,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldDefinition? candidate, out string reason) {
        candidate = null;
        // The reconstruction this process already made over these very declarations: its base pin is their fingerprint
        // as the diff computed it, so the pins compare without hashing the document again.
        if (BaseVersion.IsDelivered && (BaseVersion == version) && Reconstructions.TryGetValue(key: current, value: out var held) &&
            string.Equals(held.BaseHash, BaseHash, StringComparison.Ordinal) && string.Equals(held.TargetHash, TargetHash, StringComparison.Ordinal)) {
            candidate = held.Candidate;
            reason = string.Empty;
            return true;
        }
        if (RefuseBase(current, version) is { } refusal) { reason = refusal; return false; }
        try {
            var basis = WorldProjectionDelta.Tree(WorldDefinitionSerialization.SerializeCompact(current));
            var patch = WorldProjectionDelta.Tree(Patch);
            if (!WorldDocumentBasis.TryMerge(basis, patch, out var merged, out reason)) { return false; }
            var prepared = WorldDocumentWalker.Share(
                basis: current,
                basisTree: basis,
                patch: patch,
                value: WorldDefinitionSerialization.Deserialize(CanonicalJsonDocument.SerializeCompact(merged!), DocumentDirectory),
                valueTree: merged!
            );
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
