using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.World;

/// <summary>A placement's resolved WORLD-space transform — its own authored <see cref="WorldPlacement.Position"/>/
/// <see cref="WorldPlacement.YawDegrees"/> when it names no <see cref="WorldPlacement.Parent"/>, or those same fields
/// composed over the parent's own resolved frame otherwise. See <see cref="WorldPlacementFrameCompilation"/>.</summary>
/// <param name="Position">The resolved world position.</param>
/// <param name="YawDegrees">The resolved world yaw about +Y, degrees, wrapped to [0, 360).</param>
public readonly record struct CompiledPlacementFrame(Vector3 Position, float YawDegrees);

/// <summary>Compiles every placement's <see cref="WorldPlacement.Parent"/> chain into one <see cref="CompiledPlacementFrame"/>
/// per placement, resolved ONCE, statically, from the document's own placement rows — never per tick.
/// <see cref="WorldDefinition.PlacementFrames"/> is the cached, document-wide result every consumer of a placement's
/// WORLD transform reads instead of the row's own <see cref="WorldPlacement.Position"/>/<see cref="WorldPlacement.YawDegrees"/>
/// directly.</summary>
public static class WorldPlacementFrameCompilation {
    private static readonly IReadOnlyDictionary<string, CompiledPlacementFrame> s_empty = new Dictionary<string, CompiledPlacementFrame>(comparer: StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<WorldPlacementsSection, StrongBox<IReadOnlyDictionary<string, CompiledPlacementFrame>>> s_cache = new();
    // The SAME degrees-to-fixed-radians/UnitY idiom WorldColliderSet/WorldPlacementAttachment already use for a
    // placement's yaw — kept once here so a chain of composed rotations reads identically to a single one.
    private static readonly FixedVector3 UnitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    /// <summary>Gets the cached, document-wide compiled frame table for <paramref name="section"/>'s rows — the SAME
    /// derivation <see cref="TryValidate"/> checks the document against, so a validated document always compiles here
    /// too. A row whose <see cref="WorldPlacement.Parent"/> chain is invalid (reachable only for an UNVALIDATED
    /// section — a hand-built candidate that skipped <see cref="TryValidate"/>) falls back to its own authored
    /// Position/YawDegrees unchanged, since the one gate that must refuse a bad chain is validation, not this
    /// read path.</summary>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Resolve(WorldPlacementsSection? section) {
        if (section is null) {
            return s_empty;
        }

        return s_cache.GetValue(
            key: section,
            createValueCallback: static s => new StrongBox<IReadOnlyDictionary<string, CompiledPlacementFrame>>(value: Compile(placements: s.Rows))
        ).Value!;
    }

    /// <summary>Compiles every row's frame directly from <paramref name="placements"/>, uncached — the same
    /// derivation <see cref="Resolve"/> caches per section instance.</summary>
    public static IReadOnlyDictionary<string, CompiledPlacementFrame> Compile(IReadOnlyList<WorldPlacement>? placements) {
        if (placements is not { Count: > 0 }) {
            return s_empty;
        }

        var byId = new Dictionary<string, WorldPlacement>(capacity: placements.Count, comparer: StringComparer.Ordinal);

        foreach (var placement in placements) {
            byId[placement.Id] = placement;
        }

        var resolved = new Dictionary<string, CompiledPlacementFrame>(capacity: placements.Count, comparer: StringComparer.Ordinal);
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var placement in placements) {
            ResolveBestEffort(
                byId: byId,
                placement: placement,
                resolved: resolved,
                visiting: visiting
            );
        }

        return resolved;
    }

    /// <summary>Validates every placement's <see cref="WorldPlacement.Parent"/> chain, refusing BY NAME the first bad
    /// edge found in authored order: an unknown parent, a self-parent, a parent cycle, a distributed/mirrored parent
    /// (an expanded row has no single frame), or a non-unit-scale parent (composition rotates and translates only).</summary>
    public static bool TryValidate(IReadOnlyList<WorldPlacement>? placements, out string reason) {
        reason = string.Empty;

        if (placements is not { Count: > 0 }) {
            return true;
        }

        var byId = new Dictionary<string, WorldPlacement>(capacity: placements.Count, comparer: StringComparer.Ordinal);

        foreach (var placement in placements) {
            byId[placement.Id] = placement;
        }

        foreach (var placement in placements) {
            if (placement.Parent is not { Length: > 0 } parentId) {
                continue;
            }

            if (string.Equals(a: parentId, b: placement.Id, comparisonType: StringComparison.Ordinal)) {
                reason = $"placement '{placement.Id}' names itself as its own parent.";

                return false;
            }

            if (!byId.TryGetValue(key: parentId, value: out var parent)) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', which is not a declared placement.";

                return false;
            }

            if ((parent.Distribution is not null) || (parent.Mirror is not null)) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', which is distributed/mirrored — a child of an expanded row has no single frame.";

                return false;
            }

            if (parent.Scale != 1f) {
                reason = $"placement '{placement.Id}' names parent '{parentId}', whose scale ({parent.Scale}) is not 1 — composition rotates and translates only, never a parent's scale into a child's local offset.";

                return false;
            }

            var chain = new HashSet<string>(comparer: StringComparer.Ordinal) { placement.Id, parent.Id };
            var current = parent;

            while (current.Parent is { Length: > 0 } nextParentId) {
                if (!byId.TryGetValue(key: nextParentId, value: out var next)) {
                    // A dangling further ancestor is reported when THAT row is visited directly by the outer loop.
                    break;
                }

                if (!chain.Add(item: next.Id)) {
                    reason = $"placement '{placement.Id}' participates in a parent cycle through '{next.Id}'.";

                    return false;
                }

                current = next;
            }
        }

        return true;
    }

    private static CompiledPlacementFrame ResolveBestEffort(IReadOnlyDictionary<string, WorldPlacement> byId, WorldPlacement placement, Dictionary<string, CompiledPlacementFrame> resolved, HashSet<string> visiting) {
        if (resolved.TryGetValue(key: placement.Id, value: out var cached)) {
            return cached;
        }

        if (
            (placement.Parent is not { Length: > 0 } parentId) ||
            string.Equals(a: parentId, b: placement.Id, comparisonType: StringComparison.Ordinal) ||
            !byId.TryGetValue(key: parentId, value: out var parent) ||
            (parent.Distribution is not null) ||
            (parent.Mirror is not null) ||
            (parent.Scale != 1f) ||
            !visiting.Add(item: placement.Id)
        ) {
            var identity = new CompiledPlacementFrame(Position: placement.Position, YawDegrees: placement.YawDegrees);

            resolved[placement.Id] = identity;

            return identity;
        }

        var parentFrame = ResolveBestEffort(
            byId: byId,
            placement: parent,
            resolved: resolved,
            visiting: visiting
        );

        visiting.Remove(item: placement.Id);

        var composed = new CompiledPlacementFrame(
            Position: (parentFrame.Position + RotateY(vector: placement.Position, degrees: parentFrame.YawDegrees)),
            YawDegrees: NormalizeDegrees(degrees: (parentFrame.YawDegrees + placement.YawDegrees))
        );

        resolved[placement.Id] = composed;

        return composed;
    }
    private static float NormalizeDegrees(float degrees) {
        var wrapped = (degrees % 360f);

        return ((wrapped < 0f) ? (wrapped + 360f) : wrapped);
    }
    private static Vector3 RotateY(Vector3 vector, float degrees) {
        var angle = FixedQ4816.FromDouble(value: (degrees * (Math.PI / 180.0)));
        var rotation = FixedQuaternion.FromAxisAngle(axis: UnitY, angle: angle);

        return rotation.Rotate(vector: FixedVector3.FromVector3(value: vector)).ToVector3();
    }
}
