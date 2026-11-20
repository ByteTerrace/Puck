using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;

namespace Puck.World.Protocol;

/// <summary>A reload's keyed delta between two revisions of a world's source: the members whose bytes moved from the
/// revision the authority last took to the one the sender read, each carried as its <see cref="WorldDocumentBasis"/>
/// delta, pinned to both revisions.</summary>
/// <param name="BaseHash">The revision pin (<see cref="WorldDefinitionMembers.Pin"/>) of the revision the delta was cut
/// from.</param>
/// <param name="TargetHash">The revision pin of the revision it reproduces.</param>
/// <param name="Patch">A compact JSON object naming each moved root member: its delta, or <c>null</c> for a member the
/// next revision removed.</param>
/// <param name="DocumentDirectory">The next revision's asset directory.</param>
public sealed record WorldReloadDelta(string BaseHash, string TargetHash, byte[] Patch, string? DocumentDirectory) {
    /// <summary>The named refusal for a delta cut from a revision other than the one the authority holds.</summary>
    public const string StaleBaseCode = "world.reload.stale-base";
    /// <summary>The named refusal for a delta that does not reproduce its claimed revision.</summary>
    public const string TargetMismatchCode = "world.reload.target-mismatch";
    /// <summary>The named refusal for a revision change the delta vocabulary cannot carry.</summary>
    public const string UnrepresentableCode = "world.reload.delta-unrepresentable";
    /// <summary>The named refusal for a delta whose bytes do not read.</summary>
    public const string InvalidCode = "world.reload.delta-invalid";

    // The next revision a delta reproduces over the revision it was cut from, kept beside that revision by the process
    // that cut it: reconstruction is a function of the two revisions alone, so an authority in that process holding
    // the same revision and pins takes this result in place of deriving it again.
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldDefinition, Reconstruction> Reconstructions = new();

    private sealed record Reconstruction(string BaseHash, string TargetHash, WorldDefinition Next);

    /// <summary>Cuts the delta between two revisions of a source, reading only the root members whose digests differ
    /// (<see cref="WorldDefinitionMembers"/>), and proves each member's delta merges into the next revision's member.
    /// A member whose delta cannot reproduce it is carried whole.</summary>
    /// <remarks>The next revision is kept beside <paramref name="previous"/>, so <see cref="TryReconstruct"/> over that
    /// same instance in this process answers it without reconstructing.</remarks>
    /// <param name="previous">The revision the authority holds.</param>
    /// <param name="next">The revision the sender read.</param>
    /// <returns>The delta and its pins; its patch is empty when the revisions hold the same members.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">A member's change cannot be carried even whole.</exception>
    public static WorldReloadDelta Between(WorldDefinition previous, WorldDefinition next) {
        ArgumentNullException.ThrowIfNull(argument: previous);
        ArgumentNullException.ThrowIfNull(argument: next);

        var patch = new JsonObject();
        // The next revision as the authority reconstructs it: every member and keyed row its change leaves equal is the
        // previous revision's own instance, so what the authority keeps by identity carries over.
        var reconstructed = WorldDefinitionMembers.ShareUnchanged(basis: previous, next: next, patch: patch, patchOf: MemberDelta);
        var basePin = WorldDefinitionMembers.Of(definition: previous).Pin;
        var targetPin = WorldDefinitionMembers.Of(definition: next).Pin;

        Reconstructions.AddOrUpdate(key: previous, value: new Reconstruction(BaseHash: basePin, Next: reconstructed, TargetHash: targetPin));
        return new(basePin, targetPin, CanonicalJsonDocument.SerializeCompact(patch), next.DocumentDirectory);
    }

    // One member's delta, proved to merge into the target under the root merge's rules (the shades member's included); a
    // delta that does not is replaced by the whole target.
    private static JsonNode? MemberDelta(string name, JsonNode? basis, JsonNode? target) {
        if (basis is null) { return target; }

        var delta = WorldDocumentBasis.DiffValue(basis: basis, target: target);

        if (Merges(basis: basis, delta: delta, name: name, target: target)) { return delta; }

        var whole = Whole(target: target);

        return (Merges(basis: basis, delta: whole, name: name, target: target)
            ? whole
            : throw new InvalidDataException(message: $"{UnrepresentableCode}: the member '{name}' cannot be carried as a delta"));
    }
    // Whether a member's delta merges, at the document root, into the target member.
    private static bool Merges(string name, JsonNode? basis, JsonNode? delta, JsonNode? target) =>
        WorldDocumentBasis.TryMerge(basis: new JsonObject { [name] = basis?.DeepClone() }, composed: out var merged, overlay: new JsonObject { [name] = delta?.DeepClone() }, reason: out _) &&
        OrderedJson.Equals(left: merged![name], right: target);
    // A value that replaces whatever it merges over: an object or list marked "$replace", or the value itself.
    private static JsonNode? Whole(JsonNode? target) {
        switch (target) {
            case JsonObject members: {
                    var whole = new JsonObject { [WorldDocumentBasis.ReplaceMemberName] = true };

                    foreach (var (name, value) in members) { whole[name] = value?.DeepClone(); }
                    return whole;
                }
            case JsonArray rows: {
                    var whole = new JsonArray { new JsonObject { [WorldDocumentBasis.ReplaceMemberName] = true } };

                    foreach (var row in rows) { whole.Add(value: row?.DeepClone()); }
                    return whole;
                }
            default:
                return target?.DeepClone();
        }
    }

    /// <summary>Gets the root members the delta names, in patch order.</summary>
    /// <returns>The members' JSON names.</returns>
    /// <exception cref="InvalidDataException">The patch is not a JSON object.</exception>
    public IReadOnlyList<string> Members() => [.. PatchTree().Select(selector: static member => member.Key)];

    /// <summary>Reproduces the next revision over the revision the delta was cut from: each named member merged from
    /// its delta, every other member <paramref name="previous"/>'s own instance, and within a merged member every row
    /// the delta leaves alone <paramref name="previous"/>'s own (<see cref="WorldDefinitionMembers.Replace"/>).</summary>
    /// <param name="previous">The revision the authority holds.</param>
    /// <param name="next">The next revision, or <see langword="null"/> on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the base pin agrees with <paramref name="previous"/> and the result reproduces the target
    /// pin.</returns>
    public bool TryReconstruct(WorldDefinition previous, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldDefinition? next, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: previous);

        next = null;
        if (Reconstructions.TryGetValue(key: previous, value: out var held) &&
            string.Equals(held.BaseHash, BaseHash, StringComparison.Ordinal) && string.Equals(held.TargetHash, TargetHash, StringComparison.Ordinal)) {
            next = held.Next;
            reason = string.Empty;
            return true;
        }

        var before = WorldDefinitionMembers.Of(definition: previous);

        if (!string.Equals(before.Pin, BaseHash, StringComparison.Ordinal)) {
            reason = $"{StaleBaseCode}: the delta was cut from source revision {BaseHash}, and the authority holds {before.Pin}";
            return false;
        }
        if (TryApplyPatch(previous, Patch, DocumentDirectory, out var reconstructed, out reason)) {
            if (!string.Equals(TargetHash, WorldDefinitionMembers.Of(definition: reconstructed).Pin, StringComparison.Ordinal)) {
                reason = $"{TargetMismatchCode}: the reconstructed revision does not match {TargetHash}";
                return false;
            }
            next = reconstructed;
            reason = string.Empty;
            return true;
        }
        return false;
    }

    /// <summary>Applies the already admitted form of a reload to a reconstructed journal prefix.</summary>
    public static bool TryApplyPatch(WorldDefinition previous, ReadOnlySpan<byte> patch, string? directory,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldDefinition? candidate, out string reason) {
        candidate = null;
        try {
            var replacements = new List<(string Name, ReadOnlyMemory<byte>? Value)>();
            var trees = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            foreach (var (name, delta) in PatchTree(patch)) {
                if (delta is null) { replacements.Add((name, null)); continue; }
                var tree = WorldDefinitionMembers.Write(name, WorldDefinitionMembers.Value(previous, name));
                trees.Add(name, tree);
                var basis = new JsonObject { [name] = tree };
                if (!WorldDocumentBasis.TryMerge(basis, new JsonObject { [name] = delta.DeepClone() }, out var merged, out reason)) {
                    reason = $"{InvalidCode}: the member '{name}' does not merge: {reason}";
                    return false;
                }
                replacements.Add((name, merged![name] is { } value ? CanonicalJsonDocument.SerializeCompact(value) : null));
            }
            candidate = WorldDefinitionMembers.Replace(previous, replacements, directory, trees);
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException) {
            reason = $"{InvalidCode}: {exception.Message}";
            return false;
        }
    }

    /// <summary>Merges a reconstructed source delta over the live document and returns the accepted undo patch.
    /// An unchanged keyed row keeps its live instance. A row both sides changed takes the source's value and is
    /// named in <paramref name="conflicts"/>.</summary>
    /// <param name="previous">The revision the delta was cut from.</param>
    /// <param name="next">The revision it reproduces (<see cref="TryReconstruct"/>).</param>
    /// <param name="live">The live document.</param>
    /// <param name="candidate">The merged document, or <see langword="null"/> on refusal.</param>
    /// <param name="conflicts">The paths of rows and values the source won from the live document.</param>
    /// <param name="accepted">The overlay that replays this edit, or empty on refusal.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the merge produced a document.</returns>
    public bool TryMergeOver(WorldDefinition previous, WorldDefinition next, WorldDefinition live,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldDefinition? candidate, out IReadOnlyList<string> conflicts, out byte[] accepted, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: previous);
        ArgumentNullException.ThrowIfNull(argument: next);
        ArgumentNullException.ThrowIfNull(argument: live);

        candidate = null;
        conflicts = [];
        accepted = [];
        try {
            var found = new List<string>();
            candidate = WorldDefinitionMembers.MergeReload(previous, next, live, PatchTree(), MemberDelta, found, out accepted);
            conflicts = found;
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException or NotSupportedException) {
            reason = $"{InvalidCode}: {exception.Message}";
            return false;
        }
    }

    private JsonObject PatchTree() => PatchTree(Patch);
    private static JsonObject PatchTree(ReadOnlySpan<byte> patch) {
        try {
            return CanonicalJsonDocument.ParseObject(patch);
        } catch (JsonException error) {
            throw new InvalidDataException($"{InvalidCode}: {error.Message}", error);
        }
    }
}
