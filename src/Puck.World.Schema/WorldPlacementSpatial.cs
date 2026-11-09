using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Puck.Assets.Documents;
using Puck.Abstractions.Documents;
using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>The contract a named placement-local spatial volume contributes to queries.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldPlacementSpatialRole>))]
public enum WorldPlacementSpatialRole : byte {
    /// <summary>Blocks occupation by another occupation volume.</summary>
    Occupation,
    /// <summary>Reserves passage around an occupation volume.</summary>
    Clearance,
    /// <summary>Reports presence to a named, author-defined channel without blocking.</summary>
    Influence,
}
/// <summary>The shared shape vocabulary used by occupation, clearance, and influence volumes.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSpatialShapeKind>))]
public enum WorldSpatialShapeKind : byte {
    /// <summary>A finite oriented box.</summary>
    Box,
    /// <summary>A finite sphere.</summary>
    Sphere,
}
/// <summary>A placement-local bounded shape. Box yaw is measured about +Y; Y extents are part of the contract.</summary>
/// <param name="Kind">The shape kind.</param>
/// <param name="Center">The local shape center.</param>
/// <param name="HalfExtents">Positive local half extents for a box; zero for a sphere.</param>
/// <param name="Radius">Positive radius for a sphere; zero for a box.</param>
/// <param name="YawDegrees">The local box yaw, in degrees. Sphere yaw is ignored.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSpatialShape(
    WorldSpatialShapeKind Kind,
    DocumentVector3 Center,
    DocumentVector3 HalfExtents,
    float Radius = 0f,
    float YawDegrees = 0f
);
/// <summary>A named spatial volume authored on one placement. The name is placement-local metadata; an influence
/// channel is opaque to the engine and is never interpreted as water, power, or another game noun.</summary>
/// <param name="Name">The stable name returned by spatial queries.</param>
/// <param name="Role">Whether the volume occupies, reserves clearance, or reports influence.</param>
/// <param name="Shape">The common bounded shape.</param>
/// <param name="Channel">An author-defined influence label. Required only for influence volumes.</param>
/// <param name="Pinned">Whether layout policy must preserve this volume's placement transform.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementSpatialVolume(
    string Name,
    WorldPlacementSpatialRole Role,
    WorldSpatialShape Shape,
    string? Channel = null,
    bool Pinned = false
);
/// <summary>The fixed-point shape kind used by compiled spatial queries.</summary>
public enum FixedSpatialShapeKind : byte {
    /// <summary>An oriented box.</summary>
    Box,
    /// <summary>A sphere.</summary>
    Sphere,
}
/// <summary>An immutable fixed-point shape compiled from a document volume: an oriented box, or a sphere whose
/// <see cref="Box"/> carries only its center.</summary>
/// <param name="Kind">The shape kind.</param>
/// <param name="Box">The oriented box, or for a sphere a world-axis box at the sphere's center with zero extents.</param>
/// <param name="Radius">The sphere's radius; zero for a box.</param>
public readonly record struct FixedSpatialShape(
    FixedSpatialShapeKind Kind,
    FixedOrientedBox Box,
    FixedQ4816 Radius
) {
    /// <summary>Gets the shape's world-space center.</summary>
    public FixedVector3 Center => Box.Center;
    /// <summary>Gets a box's half-extents along its own axes; zero for a sphere.</summary>
    public FixedVector3 HalfExtents => Box.HalfExtents;
}
/// <summary>An inclusive world-space AABB used only for exhaustive spatial index candidate discovery.</summary>
public readonly record struct FixedSpatialAabb(FixedVector3 Center, FixedVector3 Extent) {
    /// <summary>Returns whether two closed AABBs overlap.</summary>
    public bool Intersects(FixedSpatialAabb other) =>
        ((Int128.Abs(value: (((Int128)Center.X.Value) - other.Center.X.Value)) <= (((Int128)Extent.X.Value) + other.Extent.X.Value)) &&
        (Int128.Abs(value: (((Int128)Center.Y.Value) - other.Center.Y.Value)) <= (((Int128)Extent.Y.Value) + other.Extent.Y.Value)) &&
        (Int128.Abs(value: (((Int128)Center.Z.Value) - other.Center.Z.Value)) <= (((Int128)Extent.Z.Value) + other.Extent.Z.Value)));
}
/// <summary>One compiled named volume and its broadphase envelope.</summary>
public readonly record struct CompiledSpatialVolume(
    string PlacementId,
    string Name,
    WorldPlacementSpatialRole Role,
    string? Channel,
    bool Pinned,
    FixedSpatialShape Shape,
    FixedSpatialAabb Bounds
);
/// <summary>Why a row's authored spatial volumes are ineligible for this static index.</summary>
public readonly record struct SpatialQueryUnsupported(string PlacementId, string Reason);
/// <summary>The result of one bounded-volume query. <see cref="WorkUnits"/> makes broadphase traversal work observable.</summary>
public readonly record struct SpatialQueryResult(
    IReadOnlyList<CompiledSpatialVolume> Volumes,
    int WorkUnits
);
/// <summary>Role filter for <see cref="WorldSpatialQueryIndex.Query"/>.</summary>
[Flags]
public enum WorldSpatialQueryMask : byte {
    /// <summary>No roles.</summary>
    None = 0,
    /// <summary>Occupation volumes.</summary>
    Occupation = 1,
    /// <summary>Clearance volumes.</summary>
    Clearance = 2,
    /// <summary>Influence volumes.</summary>
    Influence = 4,
    /// <summary>All roles.</summary>
    All = Occupation | Clearance | Influence,
}
/// <summary>An immutable per-definition spatial index. Its deterministic AABB BVH is exhaustive over bounded volumes;
/// exact overlap remains available through the exact yaw-aware narrowphase overloads.</summary>
public sealed class WorldSpatialQueryIndex {
    private readonly Lazy<SpatialBvh> m_bvh;
    private readonly ImmutableArray<int> m_influenceOrder;
    private readonly ImmutableDictionary<string, FixedVector3> m_occupationTargets;
    private readonly ImmutableArray<SpatialQueryUnsupported> m_unsupported;
    private readonly ImmutableArray<CompiledSpatialVolume> m_volumes;

    /// <summary>Creates an immutable index from already compiled volumes.</summary>
    public WorldSpatialQueryIndex(IReadOnlyList<CompiledSpatialVolume> volumes, IReadOnlyList<SpatialQueryUnsupported>? unsupported = null) {
        m_volumes = volumes.ToImmutableArray();
        m_unsupported = (unsupported ?? []).ToImmutableArray();
        m_occupationTargets = OccupationTargets(volumes: m_volumes);
        // The influence order reads only each volume's role, channel and placement, never its geometry.
        m_influenceOrder = Enumerable.Range(
            0,
            m_volumes.Length
        )
            .Where(predicate: index => (m_volumes[index].Role == WorldPlacementSpatialRole.Influence))
            .OrderBy(
            index => m_volumes[index].Channel,
            StringComparer.Ordinal
        )
            .ThenBy(
            index => m_volumes[index].PlacementId,
            StringComparer.Ordinal
        )
            .ThenBy(keySelector: index => index)
            .ToImmutableArray();
        m_bvh = BuildLazily(volumes: m_volumes);
    }

    // The same volumes with some geometry replaced: nothing but the geometry changed, so the influence order and the
    // unsupported rows carry over, and the BVH builds only if a broadphase query asks for it.
    private WorldSpatialQueryIndex(WorldSpatialQueryIndex source, ImmutableArray<CompiledSpatialVolume> volumes, bool occupationMoved) {
        m_volumes = volumes;
        m_unsupported = source.m_unsupported;
        m_influenceOrder = source.m_influenceOrder;
        m_occupationTargets = (occupationMoved
            ? OccupationTargets(volumes: volumes)
            : source.m_occupationTargets
        );
        m_bvh = BuildLazily(volumes: volumes);
    }

    private static Lazy<SpatialBvh> BuildLazily(ImmutableArray<CompiledSpatialVolume> volumes) => new(valueFactory: () => {
        if (volumes.Length == 0) { return new SpatialBvh(Nodes: [], Root: -1); }
        var order = Enumerable.Range(
            0,
            volumes.Length
        ).ToArray();
        var nodes = new List<SpatialBvhNode>(capacity: ((volumes.Length * 2) - 1));

        _ = BuildBvh(
            nodes,
            order,
            start: 0,
            count: order.Length,
            volumes: volumes
        );
        return new SpatialBvh(
            Nodes: [.. nodes],
            Root: 0
        );
    });
    private static ImmutableDictionary<string, FixedVector3> OccupationTargets(ImmutableArray<CompiledSpatialVolume> volumes) {
        var occupationTargets = ImmutableDictionary.CreateBuilder<string, FixedVector3>(keyComparer: StringComparer.Ordinal);

        foreach (var volume in volumes) {
            if (
                (volume.Role == WorldPlacementSpatialRole.Occupation) &&
                !occupationTargets.ContainsKey(key: volume.PlacementId)
            ) {
                occupationTargets.Add(
                    key: volume.PlacementId,
                    value: volume.Shape.Center
                );
            }
        }
        return occupationTargets.ToImmutable();
    }

    // The compiled volumes with the given indices replaced, in the same order: an influence or occupation read sees the
    // new geometry without the whole document compiling again.
    internal WorldSpatialQueryIndex WithGeometry(IReadOnlyDictionary<int, CompiledSpatialVolume> replacements) {
        var volumes = m_volumes.ToBuilder();
        var occupationMoved = false;

        foreach (var (index, volume) in replacements) {
            occupationMoved |= (volumes[index].Role == WorldPlacementSpatialRole.Occupation);
            volumes[index] = volume;
        }
        return new WorldSpatialQueryIndex(
            this,
            volumes.ToImmutable(),
            occupationMoved
        );
    }

    /// <summary>Gets rows that carried spatial data but cannot be represented by a static index.</summary>
    public IReadOnlyList<SpatialQueryUnsupported> Unsupported => m_unsupported;
    /// <summary>Gets all statically queryable volumes in deterministic authored order.</summary>
    public IReadOnlyList<CompiledSpatialVolume> Volumes => m_volumes;

    private static FixedSpatialAabb BoundsFor(int[] order, int start, int count, IReadOnlyList<CompiledSpatialVolume> volumes) {
        var region = WorldSpatialReadRegion.Empty;

        for (var index = start; (index < (start + count)); index++) {
            region = region.Enclose(box: volumes[order[index]].Bounds);
        }
        return region.Bounds;
    }
    private static int BuildBvh(List<SpatialBvhNode> nodes, int[] order, int start, int count, IReadOnlyList<CompiledSpatialVolume> volumes) {
        var nodeIndex = nodes.Count;

        nodes.Add(item: default);
        var bounds = BoundsFor(
            count: count,
            order: order,
            start: start,
            volumes: volumes
        );

        if (count == 1) {
            nodes[nodeIndex] = new SpatialBvhNode(
                Bounds: bounds,
                Left: -1,
                Right: -1,
                VolumeIndex: order[start]
            );
            return nodeIndex;
        }

        Array.Sort(
            order,
            start,
            count,
            Comparer<int>.Create(comparison: (left, right) => {
                var leftCenter = volumes[left].Bounds.Center.X.Value;
                var rightCenter = volumes[right].Bounds.Center.X.Value;

                return LexicographicOrder.Compare(
                    leftPrimary: leftCenter,
                    leftSecondary: left,
                    rightPrimary: rightCenter,
                    rightSecondary: right
                );
            })
        );
        var split = (count / 2);
        var leftChild = BuildBvh(
            count: split,
            nodes: nodes,
            order: order,
            start: start,
            volumes: volumes
        );
        var rightChild = BuildBvh(
            count: (count - split),
            nodes: nodes,
            order: order,
            start: (start + split),
            volumes: volumes
        );

        nodes[nodeIndex] = new SpatialBvhNode(
            Bounds: bounds,
            Left: leftChild,
            Right: rightChild,
            VolumeIndex: -1
        );
        return nodeIndex;
    }
    // Whether a shape's closed volume holds a point; a point whose offset leaves the carrier is outside.
    private static bool Contains(in FixedSpatialShape shape, FixedVector3 point) {
        if (shape.Kind == FixedSpatialShapeKind.Sphere) {
            return (FixedVector3.TrySubtract(
                difference: out var delta,
                left: point,
                right: shape.Center
            ) && delta.IsWithin(radius: shape.Radius));
        }

        return shape.Box.Contains(point: point);
    }
    private static bool IsBlockingPair(WorldPlacementSpatialRole candidate, WorldPlacementSpatialRole existing) =>
        (((candidate == WorldPlacementSpatialRole.Occupation) && (existing is WorldPlacementSpatialRole.Occupation or WorldPlacementSpatialRole.Clearance)) ||
        ((candidate == WorldPlacementSpatialRole.Clearance) && (existing == WorldPlacementSpatialRole.Occupation)));
    private static bool Matches(WorldPlacementSpatialRole role, WorldSpatialQueryMask mask) => role switch {
        WorldPlacementSpatialRole.Occupation => ((mask & WorldSpatialQueryMask.Occupation) != 0),
        WorldPlacementSpatialRole.Clearance => ((mask & WorldSpatialQueryMask.Clearance) != 0),
        WorldPlacementSpatialRole.Influence => ((mask & WorldSpatialQueryMask.Influence) != 0),
        _ => false,
    };

    /// <summary>Returns whether two compiled placement volumes conflict under the authored blocking roles.</summary>
    public static bool Conflicts(in CompiledSpatialVolume left, in CompiledSpatialVolume right) {
        return (
            !string.Equals(
            a: left.PlacementId,
            b: right.PlacementId,
            comparisonType: StringComparison.Ordinal
        ) &&
            IsBlockingPair(
            candidate: left.Role,
            existing: right.Role
        ) &&
            TryOverlap(
            left: left.Shape,
            right: right.Shape
        )
        );
    }
    /// <summary>Counts distinct other placement providers whose influence volume covers the named placement's
    /// compiled center. The channel is compared ordinally and remains opaque to the engine.</summary>
    public int CountInfluences(string channel, string placementId) {
        if (!m_occupationTargets.TryGetValue(
            key: placementId,
            value: out var target
        )) { return 0; }

        var count = 0;

        for (var order = 0; (order < m_influenceOrder.Length);) {
            var volume = m_volumes[m_influenceOrder[order]];
            var channelComparison = string.Compare(
                volume.Channel,
                channel,
                StringComparison.Ordinal
            );

            if (channelComparison < 0) {
                order++;
                continue;
            }
            if (channelComparison > 0) {
                break;
            }
            var provider = volume.PlacementId;
            var providerCovers = false;

            do {
                providerCovers |= Contains(
                    volume.Shape,
                    target
                );
                order++;
                if (order >= m_influenceOrder.Length) {
                    break;
                }
                volume = m_volumes[m_influenceOrder[order]];
            } while (string.Equals(
                a: volume.Channel,
                b: channel,
                comparisonType: StringComparison.Ordinal
            ) &&
                     string.Equals(
                a: volume.PlacementId,
                b: provider,
                comparisonType: StringComparison.Ordinal
            ));
            if (
                string.Equals(
                a: provider,
                b: placementId,
                comparisonType: StringComparison.Ordinal
            ) ||
                !providerCovers
            ) {
                continue;
            }
            count++;
        }
        return count;
    }
    /// <summary>Returns whether a candidate volume conflicts with any indexed blocking volume. Occupation blocks
    /// occupation and clearance; clearance blocks occupation; influence never blocks.</summary>
    public bool HasBlockingOverlap(in CompiledSpatialVolume candidate) => HasBlockingOverlap(
        candidate: candidate,
        workUnits: out _
    );
    /// <summary>Finds blocking overlap and reports the visited BVH nodes for bounded planner accounting.</summary>
    public bool HasBlockingOverlap(in CompiledSpatialVolume candidate, out int workUnits) {
        workUnits = 0;
        var tree = m_bvh.Value;

        if (tree.Root < 0) {
            return false;
        }
        Span<int> stack = stackalloc int[64];
        var stackCount = 1;

        stack[0] = tree.Root;
        while (stackCount > 0) {
            var node = tree.Nodes[stack[--stackCount]];

            workUnits++;
            if (!node.Bounds.Intersects(other: candidate.Bounds)) {
                continue;
            }
            if (node.VolumeIndex >= 0) {
                var existing = m_volumes[node.VolumeIndex];

                if (Conflicts(
                    left: existing,
                    right: candidate
                )) {
                    return true;
                }
                continue;
            }
            stack[stackCount++] = node.Right;
            stack[stackCount++] = node.Left;
        }
        return false;
    }
    /// <summary>Returns whether the index has a statically queryable occupation volume for a placement.</summary>
    /// <param name="placementId">The placement identity to find.</param>
    /// <returns><see langword="true"/> when at least one occupation volume belongs to the placement.</returns>
    public bool HasOccupationTarget(string placementId) => m_occupationTargets.ContainsKey(key: placementId);
    /// <summary>Finds every indexed volume whose full AABB intersects <paramref name="bounds"/>.</summary>
    /// <remarks>This is a bounded-volume query, not nearest-neighbor sampling. The returned work count equals the
    /// number of BVH nodes inspected and therefore exposes the bounded broadphase cost.</remarks>
    public SpatialQueryResult Query(FixedSpatialAabb bounds, WorldSpatialQueryMask mask = WorldSpatialQueryMask.All) {
        var matches = new List<CompiledSpatialVolume>();
        var tree = m_bvh.Value;

        if (tree.Root < 0) {
            return new SpatialQueryResult(
                Volumes: matches,
                WorkUnits: 0
            );
        }

        Span<int> stack = stackalloc int[64];
        var stackCount = 1;

        stack[0] = tree.Root;
        var work = 0;

        while (stackCount > 0) {
            var node = tree.Nodes[stack[--stackCount]];

            work++;
            if (!node.Bounds.Intersects(other: bounds)) {
                continue;
            }
            if (node.VolumeIndex >= 0) {
                var volume = m_volumes[node.VolumeIndex];

                if (Matches(
                    role: volume.Role,
                    mask: mask
                )) {
                    matches.Add(item: volume);
                }
                continue;
            }
            stack[stackCount++] = node.Right;
            stack[stackCount++] = node.Left;
        }

        return new SpatialQueryResult(
            Volumes: matches,
            WorkUnits: work
        );
    }
    /// <summary>Runs the exact shape narrowphase for two compiled volumes. AABBs are never treated as contact proof.</summary>
    public static bool TryOverlap(in CompiledSpatialVolume left, in CompiledSpatialVolume right) {
        var leftShape = left.Shape;
        var rightShape = right.Shape;

        return TryOverlap(
            left: in leftShape,
            right: in rightShape
        );
    }
    /// <summary>Runs the exact fixed-point sphere/box or oriented-box separating-axis test.</summary>
    public static bool TryOverlap(in FixedSpatialShape left, in FixedSpatialShape right) {
        if (
            (left.Kind == FixedSpatialShapeKind.Sphere) &&
            (right.Kind == FixedSpatialShapeKind.Sphere)
        ) {
            return (FixedVector3.TrySubtract(
                difference: out var delta,
                left: left.Center,
                right: right.Center
            ) && delta.IsWithin(radius: checked((left.Radius + right.Radius))));
        }

        if (left.Kind == FixedSpatialShapeKind.Sphere) {
            return right.Box.IntersectsSphere(
                center: left.Center,
                radius: left.Radius
            );
        }
        if (right.Kind == FixedSpatialShapeKind.Sphere) {
            return left.Box.IntersectsSphere(
                center: right.Center,
                radius: right.Radius
            );
        }

        return FixedOrientedBox.Overlaps(
            left: left.Box,
            right: right.Box
        );
    }

    private readonly record struct SpatialBvhNode(FixedSpatialAabb Bounds, int Left, int Right, int VolumeIndex);
    private readonly record struct SpatialBvh(ImmutableArray<SpatialBvhNode> Nodes, int Root);
}
/// <summary>Compiles authored placement spatial volumes and their world-space fixed query shapes.</summary>
public static class WorldSpatialQueryCompilation {
    // Keep transformed coordinates and extents in a conservative Q48.16 envelope: 2^38 raw (2^22 world units).
    // Quaternion intermediates and three-term projections stay representable; raw distance squares fit Int128.
    // This is a numeric representability envelope, not a gameplay or world-size ceiling.
    private const long NarrowphaseRawLimit = (1L << 38);

    private static readonly ConditionalWeakTable<WorldPlacementsSection, CompiledIndex> Cache = new();
    private static readonly WorldSpatialQueryIndex Empty = new([]);

    private static FixedSpatialAabb BoundsFor(FixedSpatialShape shape) {
        if (shape.Kind == FixedSpatialShapeKind.Sphere) {
            return new FixedSpatialAabb(
                Center: shape.Center,
                Extent: new FixedVector3(
                    X: shape.Radius,
                    Y: shape.Radius,
                    Z: shape.Radius
                )
            );
        }
        return new FixedSpatialAabb(
            Center: shape.Center,
            Extent: shape.Box.WorldExtent
        );
    }
    private static FixedSpatialShape CompileShape(WorldSpatialShape authored, CompiledPlacementFrame frame) {
        var center = checked((frame.FixedPosition + Rotate(
            vector: checked((FixedVector3.FromVector3(value: authored.Center.Value) * frame.FixedScale)),
            yaw: frame.FixedYawRadians
        )));
        var yaw = checked((frame.FixedYawRadians + FixedQ4816.FromDegrees(degrees: authored.YawDegrees)));
        var rotation = Yaw(radians: yaw);

        if (authored.Kind == WorldSpatialShapeKind.Sphere) {
            return new FixedSpatialShape(
                Box: FixedOrientedBox.AxisAligned(
                    center: center,
                    halfExtents: FixedVector3.Zero
                ),
                Kind: FixedSpatialShapeKind.Sphere,
                Radius: checked((FixedQ4816.FromDouble(value: authored.Radius) * frame.FixedScale))
            );
        }

        var extents = checked((FixedVector3.FromVector3(value: authored.HalfExtents.Value) * frame.FixedScale));

        return new FixedSpatialShape(
            Box: FixedOrientedBox.FromRotation(
                center: center,
                halfExtents: extents,
                rotation: rotation
            ),
            Kind: FixedSpatialShapeKind.Box,
            Radius: FixedQ4816.Zero
        );
    }
    private static FixedVector3 Rotate(FixedVector3 vector, FixedQ4816 yaw) {
        // Check before the quaternion kernel, whose full-width vector arithmetic permits carrier wraparound.
        if (!WithinNarrowphaseRange(value: vector)) { throw new OverflowException(message: "Spatial rotation input exceeds the query range."); }
        return Yaw(radians: yaw).Rotate(vector: vector);
    }
    private static bool TryCompileGeometry(WorldSpatialShape authored, CompiledPlacementFrame frame,
        out FixedSpatialShape shape, out FixedSpatialAabb bounds, out string reason) {
        shape = default;
        bounds = default;
        if (frame.FixedScale <= FixedQ4816.Zero) {
            reason = "placement frame scale is zero or negative in Q48.16";
            return false;
        }

        try {
            shape = CompileShape(
                authored: authored,
                frame: frame
            );
            if (
                !WithinNarrowphaseRange(value: shape.Center) ||
                ((shape.Kind == FixedSpatialShapeKind.Sphere)
                ? !WithinNarrowphaseRange(value: shape.Radius)
                : !WithinNarrowphaseRange(value: shape.HalfExtents))
            ) {
                reason = "compiled shape exceeds the fixed-point narrowphase range";
                return false;
            }
            if (shape.Kind == FixedSpatialShapeKind.Sphere) {
                if (shape.Radius <= FixedQ4816.Zero) {
                    reason = "sphere radius quantizes to zero after placement scale";
                    return false;
                }
            } else if (
                (shape.HalfExtents.X <= FixedQ4816.Zero) ||
                (shape.HalfExtents.Y <= FixedQ4816.Zero) ||
                (shape.HalfExtents.Z <= FixedQ4816.Zero)
            ) {
                reason = "box half-extents quantize to zero after placement scale";
                return false;
            }

            bounds = BoundsFor(shape: shape);
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            reason = "spatial geometry exceeds fixed-point range after placement scale";
            return false;
        }
    }

    // Every frame resolves through the one parent-chain resolver, inside the narrowphase envelope.
    private static readonly Func<FixedQ4816, bool> NarrowphaseRange = WithinNarrowphaseRange;

    private static bool TryResolveFrame(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> byId,
        Dictionary<string, CompiledPlacementFrame> frames, HashSet<string> visiting, ulong worldSeed, out CompiledPlacementFrame frame, out string reason) =>
        WorldPlacementFrameCompilation.TryResolveFrame(byId: byId, frame: out frame, frames: frames, placement: placement, reason: out reason, visiting: visiting, within: NarrowphaseRange, worldSeed: worldSeed);
    private static bool WithinNarrowphaseRange(FixedQ4816 value) =>
        ((value.Value >= -NarrowphaseRawLimit) && (value.Value <= NarrowphaseRawLimit));
    private static bool WithinNarrowphaseRange(FixedVector3 value) =>
        (WithinNarrowphaseRange(value: value.X) && WithinNarrowphaseRange(value: value.Y) && WithinNarrowphaseRange(value: value.Z));
    private static FixedQuaternion Yaw(FixedQ4816 radians) => FixedQuaternion.FromAxisAngle(
        angle: radians,
        axis: FixedVector3.UnitY
    );

    /// <summary>Compiles all eligible rows in authored order.</summary>
    /// <param name="placements">The rows.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>), which a dealt child's slot offset
    /// reads under a noise or scatter region.</param>
    /// <returns>The compiled index.</returns>
    public static WorldSpatialQueryIndex Compile(IReadOnlyList<WorldPlacement>? placements, ulong worldSeed) => Compile(
        bound: null,
        placements: placements,
        worldSeed: worldSeed
    );

    // A volume whose center or extents read the live state (a document-value reference) is recorded with the values it
    // compiled, so Resolve can tell the cached index has gone stale and recompile that volume alone. A volume the index
    // does not hold (its placement was refused) records no slot, and a change to it compiles the whole document.
    private static void NoteBound(WorldPlacementSpatialVolume authored, CompiledPlacementFrame frame, int slot, List<BoundVolume>? bound) {
        if (
            (bound is null) ||
            ((authored.Shape.Center.Reference is null) && (authored.Shape.HalfExtents.Reference is null))
        ) { return; }
        bound.Add(item: new BoundVolume(
            authored,
            frame,
            slot,
            authored.Shape.Center.Value,
            authored.Shape.HalfExtents.Value
        ));
    }
    private static WorldSpatialQueryIndex Compile(IReadOnlyList<WorldPlacement>? placements, List<BoundVolume>? bound, ulong worldSeed) {
        if (placements is not { Count: > 0 }) {
            return new WorldSpatialQueryIndex([]);
        }

        var byId = placements.ToDictionary(
            p => p.Id,
            StringComparer.Ordinal
        );
        var frames = new Dictionary<string, CompiledPlacementFrame>(comparer: StringComparer.Ordinal);
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);
        var volumes = new List<CompiledSpatialVolume>();
        var unsupported = new List<SpatialQueryUnsupported>();

        foreach (var placement in placements) {
            if (placement.Spatial is not { Count: > 0 }) {
                continue;
            }

            if (placement.Deal is not null) {
                continue;
            }

            if (
                (placement.Attach is not null) ||
                (placement.Inhabit is not null) ||
                (placement.Distribution is not null) ||
                (placement.Mirror is not null)
            ) {
                unsupported.Add(item: new SpatialQueryUnsupported(
                    PlacementId: placement.Id,
                    Reason: "dynamic, inhabited, distributed, or mirrored placement has no single static spatial frame"
                ));
                continue;
            }

            if (!TryResolveFrame(
                byId: byId,
                frame: out var frame,
                frames: frames,
                placement: placement,
                reason: out var reason,
                visiting: visiting,
                worldSeed: worldSeed
            )) {
                unsupported.Add(item: new SpatialQueryUnsupported(
                    PlacementId: placement.Id,
                    Reason: reason
                ));
                continue;
            }

            var start = volumes.Count;
            var boundStart = (bound?.Count ?? 0);

            foreach (var volume in placement.Spatial) {
                if (!TryCompileGeometry(
                    volume.Shape,
                    frame,
                    out var shape,
                    out var bounds,
                    out reason
                )) {
                    unsupported.Add(item: new SpatialQueryUnsupported(
                        PlacementId: placement.Id,
                        Reason: $"spatial volume '{volume.Name}' is not representable after fixed-point frame composition: {reason}"
                    ));
                    volumes.RemoveRange(
                        start,
                        (volumes.Count - start)
                    );
                    if (bound is not null) {
                        for (var held = boundStart; (held < bound.Count); held++) { bound[held] = bound[held] with { Slot = -1 }; }
                    }
                    NoteBound(
                        authored: volume,
                        bound: bound,
                        frame: frame,
                        slot: -1
                    );
                    break;
                }
                NoteBound(
                    authored: volume,
                    bound: bound,
                    frame: frame,
                    slot: volumes.Count
                );
                volumes.Add(item: new CompiledSpatialVolume(
                    placement.Id,
                    volume.Name,
                    volume.Role,
                    volume.Channel,
                    volume.Pinned,
                    shape,
                    bounds
                ));
            }
        }

        return new WorldSpatialQueryIndex(
            unsupported: unsupported,
            volumes: volumes
        );
    }

    /// <summary>Compiles one candidate placement against its already-authored parent frames.</summary>
    /// <param name="candidate">The candidate row, including any detached transform or spatial edit.</param>
    /// <param name="byId">The immutable placement lookup used to resolve the candidate's parent chain.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    /// <returns>The candidate's world-space volumes. Parent volumes are not included.</returns>
    /// <remarks>This entry point keeps bounded planning from rebuilding the world's index for every option.
    /// The caller is responsible for rejecting dynamic or otherwise unsupported participant frames.</remarks>
    public static IReadOnlyList<CompiledSpatialVolume> CompilePlacement(
        WorldPlacement candidate, IReadOnlyDictionary<string, WorldPlacement> byId, ulong worldSeed) {
        if (candidate.Spatial is not { Count: > 0 }) {
            return [];
        }

        var frames = new Dictionary<string, CompiledPlacementFrame>(comparer: StringComparer.Ordinal);
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (!TryResolveFrame(
            byId: byId,
            frame: out var frame,
            frames: frames,
            placement: candidate,
            reason: out _,
            visiting: visiting,
            worldSeed: worldSeed
        )) {
            return [];
        }

        var volumes = new CompiledSpatialVolume[candidate.Spatial.Count];

        for (var index = 0; (index < candidate.Spatial.Count); index++) {
            var authored = candidate.Spatial[index];

            if (!TryCompileGeometry(
                authored.Shape,
                frame,
                out var shape,
                out var bounds,
                out _
            )) {
                return [];
            }
            volumes[index] = new CompiledSpatialVolume(
                candidate.Id,
                authored.Name,
                authored.Role,
                authored.Channel,
                authored.Pinned,
                shape,
                bounds
            );
        }
        return volumes;
    }
    /// <summary>Gets the cached index for one immutable placement-section instance, compiled again when a volume that
    /// reads a state-bound value (a document-value reference) now reads another, or under another seed.</summary>
    /// <param name="section">The section, or <see langword="null"/>.</param>
    /// <param name="worldSeed">The world's reroll seed (<c>generation.worldSeed</c>).</param>
    /// <returns>The compiled index.</returns>
    public static WorldSpatialQueryIndex Resolve(WorldPlacementsSection? section, ulong worldSeed) {
        if (section is null) {
            return Empty;
        }
        // Bound parent translations and lattice steps move frames, beyond the volume-local refit below.
        if (WorldPlacementFrameCompilation.HasLiveInputs(section: section)) {
            return Compile(placements: section.Rows, worldSeed: worldSeed);
        }
        if (!Cache.TryGetValue(key: section, value: out var entry) || (entry.WorldSeed != worldSeed)) {
            entry = Compiled(section: section, worldSeed: worldSeed);
            Cache.AddOrUpdate(key: section, value: entry);
        }

        if (entry.Bound.Length == 0) { return entry.Index; }
        var moved = new List<int>();

        for (var held = 0; (held < entry.Bound.Length); held++) {
            var volume = entry.Bound[held];

            if (
                (volume.Authored.Shape.Center.Value != volume.Center) ||
                (volume.Authored.Shape.HalfExtents.Value != volume.HalfExtents)
            ) { moved.Add(item: held); }
        }
        if (moved.Count == 0) { return entry.Index; }
        entry = (Recompiled(
            entry: entry,
            moved: moved
        ) ?? Compiled(section: section, worldSeed: worldSeed));
        Cache.AddOrUpdate(
            key: section,
            value: entry
        );
        return entry.Index;
    }

    // Compiles again only the volumes whose bound values moved, in the slots they already hold. A volume whose new
    // geometry no longer compiles, or that holds no slot, makes the whole document compile again.
    private static CompiledIndex? Recompiled(CompiledIndex entry, List<int> moved) {
        var replacements = new Dictionary<int, CompiledSpatialVolume>();
        var bound = ((BoundVolume[])entry.Bound.Clone());

        foreach (var held in moved) {
            var volume = bound[held];

            if (volume.Slot < 0) { return null; }
            var seenCenter = volume.Authored.Shape.Center.Value;
            var seenHalfExtents = volume.Authored.Shape.HalfExtents.Value;

            if (!TryCompileGeometry(
                volume.Authored.Shape,
                volume.Frame,
                out var shape,
                out var bounds,
                out _
            )) { return null; }
            replacements.Add(
                key: volume.Slot,
                value: entry.Index.Volumes[volume.Slot] with { Shape = shape, Bounds = bounds }
            );
            bound[held] = volume with { Center = seenCenter, HalfExtents = seenHalfExtents };
        }
        return new CompiledIndex(
            entry.Index.WithGeometry(replacements: replacements),
            bound,
            entry.WorldSeed
        );
    }
    private static CompiledIndex Compiled(WorldPlacementsSection section, ulong worldSeed) {
        var bound = new List<BoundVolume>();
        var index = Compile(
            bound: bound,
            placements: section.Rows,
            worldSeed: worldSeed
        );

        return new CompiledIndex(
            Bound: [.. bound],
            Index: index,
            WorldSeed: worldSeed
        );
    }

    /// <summary>Counts the volumes the placements declare that read a state-bound value, the ones a write can move.</summary>
    /// <param name="placements">The document's placements.</param>
    /// <returns>The bound volume count.</returns>
    public static long BoundVolumeCount(IEnumerable<WorldPlacement> placements) => placements.Sum(selector: static placement => (placement.Spatial?.Count(predicate: static volume => ((volume.Shape.Center.Reference is not null) || (volume.Shape.HalfExtents.Reference is not null))) ?? 0));

    private sealed record BoundVolume(WorldPlacementSpatialVolume Authored, CompiledPlacementFrame Frame, int Slot, System.Numerics.Vector3 Center, System.Numerics.Vector3 HalfExtents);
    private sealed record CompiledIndex(WorldSpatialQueryIndex Index, BoundVolume[] Bound, ulong WorldSeed);

    // Hands a section's compiled index to a section whose rows differ from it only in their holding masks
    // (WorldDefinition.WithPlacementHoldings): the authored volumes it compiles read no shown creation.
    internal static void CarryAcrossHolding(WorldPlacementsSection source, WorldPlacementsSection target) {
        if (Cache.TryGetValue(key: source, value: out var index)) {
            Cache.AddOrUpdate(key: target, value: index);
        }
    }
}
