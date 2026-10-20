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
    private readonly ImmutableArray<SpatialBvhNode> m_bvh;
    private readonly ImmutableArray<int> m_influenceOrder;
    private readonly ImmutableDictionary<string, FixedVector3> m_occupationTargets;
    private readonly int m_root;
    private readonly ImmutableArray<SpatialQueryUnsupported> m_unsupported;
    private readonly ImmutableArray<CompiledSpatialVolume> m_volumes;

    /// <summary>Creates an immutable index from already compiled volumes.</summary>
    public WorldSpatialQueryIndex(IReadOnlyList<CompiledSpatialVolume> volumes, IReadOnlyList<SpatialQueryUnsupported>? unsupported = null) {
        m_volumes = volumes.ToImmutableArray();
        m_unsupported = (unsupported ?? []).ToImmutableArray();
        var occupationTargets = ImmutableDictionary.CreateBuilder<string, FixedVector3>(keyComparer: StringComparer.Ordinal);

        foreach (var volume in m_volumes) {
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
        m_occupationTargets = occupationTargets.ToImmutable();
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
        if (m_volumes.Length == 0) {
            m_bvh = [];
            m_root = -1;
        } else {
            var order = Enumerable.Range(
                0,
                m_volumes.Length
            ).ToArray();
            var nodes = new List<SpatialBvhNode>(capacity: ((m_volumes.Length * 2) - 1));

            _ = BuildBvh(
                nodes,
                order,
                start: 0,
                count: order.Length,
                volumes: m_volumes
            );
            m_bvh = nodes.ToImmutableArray();
            m_root = 0;
        }
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
        if (m_root < 0) {
            return false;
        }
        Span<int> stack = stackalloc int[64];
        var stackCount = 1;

        stack[0] = m_root;
        while (stackCount > 0) {
            var node = m_bvh[stack[--stackCount]];

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

        if (m_root < 0) {
            return new SpatialQueryResult(
                Volumes: matches,
                WorkUnits: 0
            );
        }

        Span<int> stack = stackalloc int[64];
        var stackCount = 1;

        stack[0] = m_root;
        var work = 0;

        while (stackCount > 0) {
            var node = m_bvh[stack[--stackCount]];

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
}
/// <summary>Compiles authored placement spatial volumes and their world-space fixed query shapes.</summary>
public static class WorldSpatialQueryCompilation {
    // Keep transformed coordinates and extents in a conservative Q48.16 envelope: 2^38 raw (2^22 world units).
    // Quaternion intermediates and three-term projections stay representable; raw distance squares fit Int128.
    // This is a numeric representability envelope, not a gameplay or world-size ceiling.
    private const long NarrowphaseRawLimit = (1L << 38);

    private static readonly ConditionalWeakTable<WorldPlacementsSection, StrongBox<WorldSpatialQueryIndex>> Cache = new();
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
    private static FixedSpatialShape CompileShape(WorldSpatialShape authored, SpatialFrame frame) {
        var center = checked((frame.Position + Rotate(
            vector: checked((FixedVector3.FromVector3(value: authored.Center.Value) * frame.Scale)),
            yaw: frame.YawRadians
        )));
        var yaw = checked((frame.YawRadians + FixedQ4816.FromDouble(value: (authored.YawDegrees * (Math.PI / 180d)))));
        var rotation = Yaw(radians: yaw);

        if (authored.Kind == WorldSpatialShapeKind.Sphere) {
            return new FixedSpatialShape(
                Box: FixedOrientedBox.AxisAligned(
                    center: center,
                    halfExtents: FixedVector3.Zero
                ),
                Kind: FixedSpatialShapeKind.Sphere,
                Radius: checked((FixedQ4816.FromDouble(value: authored.Radius) * frame.Scale))
            );
        }

        var extents = checked((FixedVector3.FromVector3(value: authored.HalfExtents.Value) * frame.Scale));

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
    private static bool TryCompileGeometry(WorldSpatialShape authored, SpatialFrame frame,
        out FixedSpatialShape shape, out FixedSpatialAabb bounds, out string reason) {
        shape = default;
        bounds = default;
        if (frame.Scale <= FixedQ4816.Zero) {
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
    private static bool TryResolveFrame(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> byId,
        Dictionary<string, SpatialFrame> frames, HashSet<string> visiting, out SpatialFrame frame, out string reason) {
        if (frames.TryGetValue(
            key: placement.Id,
            value: out frame
        )) { reason = string.Empty; return true; }
        if (!visiting.Add(item: placement.Id)) { frame = default; reason = $"placement '{placement.Id}' participates in a parent cycle"; return false; }

        try {
            var position = FixedVector3.FromVector3(value: placement.Position);
            var scale = FixedQ4816.FromDouble(value: placement.Scale);
            var yaw = FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180d)));

            if (placement.Parent is { Length: > 0 } parentId) {
                byId.TryGetValue(
                    key: parentId,
                    value: out var parent
                );
                if (
                    (parent is null) ||
                    ((parent.Distribution is not null) && (parent.Deal is null)) ||
                    (parent.Mirror is not null) ||
                    (parent.Attach is not null) ||
                    (parent.Inhabit is not null)
                ) {
                    frame = default;
                    reason = $"placement '{placement.Id}' requires a declared static single parent frame '{parentId}'";
                    return false;
                }
                if (!TryResolveFrame(
                    byId: byId,
                    frame: out var parentFrame,
                    frames: frames,
                    placement: parent,
                    reason: out reason,
                    visiting: visiting
                )) {
                    frame = default;
                    return false;
                }
                position = checked((parentFrame.Position + Rotate(
                    vector: checked((position * parentFrame.Scale)),
                    yaw: parentFrame.YawRadians
                )));
                scale = checked((scale * parentFrame.Scale));
                yaw = checked((yaw + parentFrame.YawRadians));
            }
            if (
                !WithinNarrowphaseRange(value: position) ||
                !WithinNarrowphaseRange(value: scale)
            ) {
                frame = default;
                reason = $"placement '{placement.Id}' transform exceeds the fixed-point spatial query range";
                return false;
            }
            frame = new SpatialFrame(
                Position: position,
                Scale: scale,
                YawRadians: yaw
            );
            frames[placement.Id] = frame;
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            frame = default;
            reason = $"placement '{placement.Id}' transform exceeds fixed-point range";
            return false;
        } finally {
            visiting.Remove(item: placement.Id);
        }
    }
    private static bool WithinNarrowphaseRange(FixedQ4816 value) =>
        ((value.Value >= -NarrowphaseRawLimit) && (value.Value <= NarrowphaseRawLimit));
    private static bool WithinNarrowphaseRange(FixedVector3 value) =>
        (WithinNarrowphaseRange(value: value.X) && WithinNarrowphaseRange(value: value.Y) && WithinNarrowphaseRange(value: value.Z));
    private static FixedQuaternion Yaw(FixedQ4816 radians) => FixedQuaternion.FromAxisAngle(
        angle: radians,
        axis: FixedVector3.UnitY
    );

    /// <summary>Compiles all eligible rows in authored order.</summary>
    public static WorldSpatialQueryIndex Compile(IReadOnlyList<WorldPlacement>? placements) {
        if (placements is not { Count: > 0 }) {
            return new WorldSpatialQueryIndex([]);
        }

        var byId = placements.ToDictionary(
            p => p.Id,
            StringComparer.Ordinal
        );
        var frames = new Dictionary<string, SpatialFrame>(comparer: StringComparer.Ordinal);
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
                visiting: visiting
            )) {
                unsupported.Add(item: new SpatialQueryUnsupported(
                    PlacementId: placement.Id,
                    Reason: reason
                ));
                continue;
            }

            var start = volumes.Count;

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
                    break;
                }
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
    /// <returns>The candidate's world-space volumes. Parent volumes are not included.</returns>
    /// <remarks>This entry point keeps bounded planning from rebuilding the world's index for every option.
    /// The caller is responsible for rejecting dynamic or otherwise unsupported participant frames.</remarks>
    public static IReadOnlyList<CompiledSpatialVolume> CompilePlacement(
        WorldPlacement candidate, IReadOnlyDictionary<string, WorldPlacement> byId) {
        if (candidate.Spatial is not { Count: > 0 }) {
            return [];
        }

        var frames = new Dictionary<string, SpatialFrame>(comparer: StringComparer.Ordinal);
        var visiting = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (!TryResolveFrame(
            byId: byId,
            frame: out var frame,
            frames: frames,
            placement: candidate,
            reason: out _,
            visiting: visiting
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
    /// <summary>Gets the cached index for one immutable placement-section instance.</summary>
    public static WorldSpatialQueryIndex Resolve(WorldPlacementsSection? section) {
        if (section is null) {
            return Empty;
        }
        return Cache.GetValue(
            section,
            static value => new StrongBox<WorldSpatialQueryIndex>(value: Compile(placements: value.Rows))
        ).Value!;
    }

    private readonly record struct SpatialFrame(FixedVector3 Position, FixedQ4816 YawRadians, FixedQ4816 Scale);
}
