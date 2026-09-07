using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Puck.Assets.Documents;
using Puck.Abstractions.Documents;
using Puck.Maths;

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

/// <summary>An immutable fixed-point shape compiled from a document volume.</summary>
public readonly record struct FixedSpatialShape(
    FixedSpatialShapeKind Kind,
    FixedVector3 Center,
    FixedVector3 AxisX,
    FixedVector3 AxisY,
    FixedVector3 AxisZ,
    FixedVector3 HalfExtents,
    FixedQ4816 Radius
);

/// <summary>An inclusive world-space AABB used only for exhaustive spatial index candidate discovery.</summary>
public readonly record struct FixedSpatialAabb(FixedVector3 Center, FixedVector3 Extent) {
    /// <summary>Returns whether two closed AABBs overlap.</summary>
    public bool Intersects(FixedSpatialAabb other) =>
        Int128.Abs((Int128)Center.X.Value - other.Center.X.Value) <= (Int128)Extent.X.Value + other.Extent.X.Value &&
        Int128.Abs((Int128)Center.Y.Value - other.Center.Y.Value) <= (Int128)Extent.Y.Value + other.Extent.Y.Value &&
        Int128.Abs((Int128)Center.Z.Value - other.Center.Z.Value) <= (Int128)Extent.Z.Value + other.Extent.Z.Value;
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
    private readonly ImmutableArray<CompiledSpatialVolume> m_volumes;
    private readonly ImmutableArray<SpatialQueryUnsupported> m_unsupported;
    private readonly ImmutableArray<int> m_influenceOrder;
    private readonly ImmutableDictionary<string, FixedVector3> m_occupationTargets;
    private readonly ImmutableArray<SpatialBvhNode> m_bvh;
    private readonly int m_root;

    /// <summary>Creates an immutable index from already compiled volumes.</summary>
    public WorldSpatialQueryIndex(IReadOnlyList<CompiledSpatialVolume> volumes, IReadOnlyList<SpatialQueryUnsupported>? unsupported = null) {
        m_volumes = volumes.ToImmutableArray();
        m_unsupported = (unsupported ?? []).ToImmutableArray();
        var occupationTargets = ImmutableDictionary.CreateBuilder<string, FixedVector3>(StringComparer.Ordinal);
        foreach (var volume in m_volumes) {
            if (volume.Role == WorldPlacementSpatialRole.Occupation && !occupationTargets.ContainsKey(volume.PlacementId)) {
                occupationTargets.Add(volume.PlacementId, volume.Shape.Center);
            }
        }
        m_occupationTargets = occupationTargets.ToImmutable();
        m_influenceOrder = Enumerable.Range(0, m_volumes.Length)
            .Where(index => m_volumes[index].Role == WorldPlacementSpatialRole.Influence)
            .OrderBy(index => m_volumes[index].Channel, StringComparer.Ordinal)
            .ThenBy(index => m_volumes[index].PlacementId, StringComparer.Ordinal)
            .ThenBy(index => index)
            .ToImmutableArray();
        if (m_volumes.Length == 0) {
            m_bvh = [];
            m_root = -1;
        } else {
            var order = Enumerable.Range(0, m_volumes.Length).ToArray();
            var nodes = new List<SpatialBvhNode>(capacity: (m_volumes.Length * 2) - 1);
            _ = BuildBvh(nodes, order, start: 0, count: order.Length, volumes: m_volumes);
            m_bvh = nodes.ToImmutableArray();
            m_root = 0;
        }
    }

    /// <summary>Gets all statically queryable volumes in deterministic authored order.</summary>
    public IReadOnlyList<CompiledSpatialVolume> Volumes => m_volumes;
    /// <summary>Gets rows that carried spatial data but cannot be represented by a static index.</summary>
    public IReadOnlyList<SpatialQueryUnsupported> Unsupported => m_unsupported;

    /// <summary>Returns whether the index has a statically queryable occupation volume for a placement.</summary>
    /// <param name="placementId">The placement identity to find.</param>
    /// <returns><see langword="true"/> when at least one occupation volume belongs to the placement.</returns>
    public bool HasOccupationTarget(string placementId) => m_occupationTargets.ContainsKey(placementId);

    /// <summary>Finds every indexed volume whose full AABB intersects <paramref name="bounds"/>.</summary>
    /// <remarks>This is a bounded-volume query, not nearest-neighbor sampling. The returned work count equals the
    /// number of BVH nodes inspected and therefore exposes the bounded broadphase cost.</remarks>
    public SpatialQueryResult Query(FixedSpatialAabb bounds, WorldSpatialQueryMask mask = WorldSpatialQueryMask.All) {
        var matches = new List<CompiledSpatialVolume>();
        if (m_root < 0) {
            return new SpatialQueryResult(Volumes: matches, WorkUnits: 0);
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
                if (Matches(role: volume.Role, mask: mask)) {
                    matches.Add(volume);
                }
                continue;
            }
            stack[stackCount++] = node.Right;
            stack[stackCount++] = node.Left;
        }

        return new SpatialQueryResult(Volumes: matches, WorkUnits: work);
    }

    /// <summary>Counts distinct other placement providers whose influence volume covers the named placement's
    /// compiled center. The channel is compared ordinally and remains opaque to the engine.</summary>
    public int CountInfluences(string channel, string placementId) {
        if (!m_occupationTargets.TryGetValue(placementId, out var target)) { return 0; }

        var count = 0;
        for (var order = 0; order < m_influenceOrder.Length;) {
            var volume = m_volumes[m_influenceOrder[order]];
            var channelComparison = string.Compare(volume.Channel, channel, StringComparison.Ordinal);
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
                providerCovers |= Contains(volume.Shape, target);
                order++;
                if (order >= m_influenceOrder.Length) {
                    break;
                }
                volume = m_volumes[m_influenceOrder[order]];
            } while (string.Equals(volume.Channel, channel, StringComparison.Ordinal) &&
                     string.Equals(volume.PlacementId, provider, StringComparison.Ordinal));
            if (string.Equals(provider, placementId, StringComparison.Ordinal) || !providerCovers) {
                continue;
            }
            count++;
        }
        return count;
    }

    /// <summary>Runs the exact shape narrowphase for two compiled volumes. AABBs are never treated as contact proof.</summary>
    public static bool TryOverlap(in CompiledSpatialVolume left, in CompiledSpatialVolume right) {
        var leftShape = left.Shape;
        var rightShape = right.Shape;
        return TryOverlap(left: in leftShape, right: in rightShape);
    }

    /// <summary>Runs the exact fixed-point sphere/box or oriented-box separating-axis test.</summary>
    public static bool TryOverlap(in FixedSpatialShape left, in FixedSpatialShape right) {
        if ((left.Kind == FixedSpatialShapeKind.Sphere) && (right.Kind == FixedSpatialShapeKind.Sphere)) {
            if (!TrySubtract(left.Center, right.Center, out var delta)) {
                return false;
            }
            var radius = checked(left.Radius + right.Radius);
            return WithinRadius(delta, radius);
        }

        if (left.Kind == FixedSpatialShapeKind.Sphere) {
            return SphereBoxOverlap(sphere: in left, box: in right);
        }
        if (right.Kind == FixedSpatialShapeKind.Sphere) {
            return SphereBoxOverlap(sphere: in right, box: in left);
        }

        return BoxBoxOverlap(left: in left, right: in right);
    }

    /// <summary>Returns whether two compiled placement volumes conflict under the authored blocking roles.</summary>
    public static bool Conflicts(in CompiledSpatialVolume left, in CompiledSpatialVolume right) {
        return !string.Equals(left.PlacementId, right.PlacementId, StringComparison.Ordinal) &&
            IsBlockingPair(left.Role, right.Role) &&
            TryOverlap(left.Shape, right.Shape);
    }

    /// <summary>Returns whether a candidate volume conflicts with any indexed blocking volume. Occupation blocks
    /// occupation and clearance; clearance blocks occupation; influence never blocks.</summary>
    public bool HasBlockingOverlap(in CompiledSpatialVolume candidate) => HasBlockingOverlap(candidate, out _);

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
            if (!node.Bounds.Intersects(candidate.Bounds)) {
                continue;
            }
            if (node.VolumeIndex >= 0) {
                var existing = m_volumes[node.VolumeIndex];
                if (Conflicts(existing, candidate)) {
                    return true;
                }
                continue;
            }
            stack[stackCount++] = node.Right;
            stack[stackCount++] = node.Left;
        }
        return false;
    }

    private static bool IsBlockingPair(WorldPlacementSpatialRole candidate, WorldPlacementSpatialRole existing) =>
        (candidate == WorldPlacementSpatialRole.Occupation && existing is WorldPlacementSpatialRole.Occupation or WorldPlacementSpatialRole.Clearance) ||
        (candidate == WorldPlacementSpatialRole.Clearance && existing == WorldPlacementSpatialRole.Occupation);

    private static bool Matches(WorldPlacementSpatialRole role, WorldSpatialQueryMask mask) => role switch {
        WorldPlacementSpatialRole.Occupation => (mask & WorldSpatialQueryMask.Occupation) != 0,
        WorldPlacementSpatialRole.Clearance => (mask & WorldSpatialQueryMask.Clearance) != 0,
        WorldPlacementSpatialRole.Influence => (mask & WorldSpatialQueryMask.Influence) != 0,
        _ => false,
    };

    private static int BuildBvh(List<SpatialBvhNode> nodes, int[] order, int start, int count, IReadOnlyList<CompiledSpatialVolume> volumes) {
        var nodeIndex = nodes.Count;
        nodes.Add(default);
        var bounds = BoundsFor(order, start, count, volumes);
        if (count == 1) {
            nodes[nodeIndex] = new SpatialBvhNode(bounds, -1, -1, order[start]);
            return nodeIndex;
        }

        Array.Sort(order, start, count, Comparer<int>.Create((left, right) => {
            var leftCenter = volumes[left].Bounds.Center.X.Value;
            var rightCenter = volumes[right].Bounds.Center.X.Value;
            var comparison = leftCenter.CompareTo(rightCenter);
            return (comparison != 0) ? comparison : left.CompareTo(right);
        }));
        var split = count / 2;
        var leftChild = BuildBvh(nodes, order, start, split, volumes);
        var rightChild = BuildBvh(nodes, order, start + split, count - split, volumes);
        nodes[nodeIndex] = new SpatialBvhNode(bounds, leftChild, rightChild, -1);
        return nodeIndex;
    }

    private static FixedSpatialAabb BoundsFor(int[] order, int start, int count, IReadOnlyList<CompiledSpatialVolume> volumes) {
        var region = WorldSpatialReadRegion.Empty;
        for (var index = start; index < start + count; index++) {
            region = region.Enclose(box: volumes[order[index]].Bounds);
        }
        return region.Bounds;
    }

    private readonly record struct SpatialBvhNode(FixedSpatialAabb Bounds, int Left, int Right, int VolumeIndex);

    private static bool SphereBoxOverlap(in FixedSpatialShape sphere, in FixedSpatialShape box) {
        if (!TrySubtract(sphere.Center, box.Center, out var separation)) {
            return false;
        }
        var local = new FixedVector3(
            X: FixedVector3.Dot(separation, box.AxisX),
            Y: FixedVector3.Dot(separation, box.AxisY),
            Z: FixedVector3.Dot(separation, box.AxisZ)
        );
        var closest = new FixedVector3(
            X: FixedQ4816.Clamp(local.X, -box.HalfExtents.X, box.HalfExtents.X),
            Y: FixedQ4816.Clamp(local.Y, -box.HalfExtents.Y, box.HalfExtents.Y),
            Z: FixedQ4816.Clamp(local.Z, -box.HalfExtents.Z, box.HalfExtents.Z)
        );
        var delta = local - closest;
        return WithinRadius(delta, sphere.Radius);
    }

    private static bool Contains(in FixedSpatialShape shape, FixedVector3 point) {
        if (!TrySubtract(point, shape.Center, out var delta)) {
            return false;
        }
        if (shape.Kind == FixedSpatialShapeKind.Sphere) {
            return WithinRadius(delta, shape.Radius);
        }
        var local = new FixedVector3(
            FixedVector3.Dot(delta, shape.AxisX),
            FixedVector3.Dot(delta, shape.AxisY),
            FixedVector3.Dot(delta, shape.AxisZ));
        return FixedQ4816.Abs(local.X) <= shape.HalfExtents.X &&
               FixedQ4816.Abs(local.Y) <= shape.HalfExtents.Y &&
               FixedQ4816.Abs(local.Z) <= shape.HalfExtents.Z;
    }

    private static bool BoxBoxOverlap(in FixedSpatialShape left, in FixedSpatialShape right) {
        Span<FixedVector3> axes = stackalloc FixedVector3[15];
        axes[0] = left.AxisX;
        axes[1] = left.AxisY;
        axes[2] = left.AxisZ;
        axes[3] = right.AxisX;
        axes[4] = right.AxisY;
        axes[5] = right.AxisZ;
        axes[6] = FixedVector3.Cross(left.AxisX, right.AxisX);
        axes[7] = FixedVector3.Cross(left.AxisX, right.AxisY);
        axes[8] = FixedVector3.Cross(left.AxisX, right.AxisZ);
        axes[9] = FixedVector3.Cross(left.AxisY, right.AxisX);
        axes[10] = FixedVector3.Cross(left.AxisY, right.AxisY);
        axes[11] = FixedVector3.Cross(left.AxisY, right.AxisZ);
        axes[12] = FixedVector3.Cross(left.AxisZ, right.AxisX);
        axes[13] = FixedVector3.Cross(left.AxisZ, right.AxisY);
        axes[14] = FixedVector3.Cross(left.AxisZ, right.AxisZ);

        if (!TrySubtract(right.Center, left.Center, out var delta)) {
            return false;
        }
        foreach (var axis in axes) {
            if (axis.LengthSquared <= FixedQ4816.FromRawBits(1L)) {
                continue;
            }

            var leftRadius = ProjectionRadius(in left, axis);
            var rightRadius = ProjectionRadius(in right, axis);
            if (FixedQ4816.Abs(FixedVector3.Dot(delta, axis)) > checked(leftRadius + rightRadius)) {
                return false;
            }
        }

        return true;
    }

    private static FixedQ4816 ProjectionRadius(in FixedSpatialShape box, FixedVector3 axis) => checked(
        checked(FixedQ4816.Abs(FixedVector3.Dot(box.AxisX, axis)) * box.HalfExtents.X) +
        checked(FixedQ4816.Abs(FixedVector3.Dot(box.AxisY, axis)) * box.HalfExtents.Y) +
        checked(FixedQ4816.Abs(FixedVector3.Dot(box.AxisZ, axis)) * box.HalfExtents.Z));

    // Compare raw Q32 squares without rounding them back to Q16: rounding erases separation between very small
    // spheres. The compiled coordinate envelope keeps this three-term sum inside Int128.
    private static bool WithinRadius(FixedVector3 delta, FixedQ4816 radius) =>
        (Int128)delta.X.Value * delta.X.Value + (Int128)delta.Y.Value * delta.Y.Value +
        (Int128)delta.Z.Value * delta.Z.Value <= (Int128)radius.Value * radius.Value;

    private static bool TrySubtract(FixedVector3 left, FixedVector3 right, out FixedVector3 difference) {
        var x = (Int128)left.X.Value - right.X.Value;
        var y = (Int128)left.Y.Value - right.Y.Value;
        var z = (Int128)left.Z.Value - right.Z.Value;
        if (x < long.MinValue || x > long.MaxValue ||
            y < long.MinValue || y > long.MaxValue ||
            z < long.MinValue || z > long.MaxValue) {
            difference = default;
            return false;
        }
        difference = new FixedVector3(
            FixedQ4816.FromRawBits((long)x),
            FixedQ4816.FromRawBits((long)y),
            FixedQ4816.FromRawBits((long)z));
        return true;
    }
}

/// <summary>Compiles authored placement spatial volumes and their world-space fixed query shapes.</summary>
public static class WorldSpatialQueryCompilation {
    // Keep transformed coordinates and extents in a conservative Q48.16 envelope: 2^38 raw (2^22 world units).
    // Quaternion intermediates and three-term projections stay representable; raw distance squares fit Int128.
    // This is a numeric representability envelope, not a gameplay or world-size ceiling.
    private const long NarrowphaseRawLimit = (1L << 38);
    private static readonly FixedVector3 UnitX = new(FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero);
    private static readonly FixedVector3 UnitY = new(FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.Zero);
    private static readonly FixedVector3 UnitZ = new(FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One);
    private static readonly ConditionalWeakTable<WorldPlacementsSection, StrongBox<WorldSpatialQueryIndex>> s_cache = new();
    private static readonly WorldSpatialQueryIndex s_empty = new([]);

    /// <summary>Gets the cached index for one immutable placement-section instance.</summary>
    public static WorldSpatialQueryIndex Resolve(WorldPlacementsSection? section) {
        if (section is null) {
            return s_empty;
        }
        return s_cache.GetValue(section, static value => new StrongBox<WorldSpatialQueryIndex>(Compile(value.Rows))).Value!;
    }

    /// <summary>Compiles all eligible rows in authored order.</summary>
    public static WorldSpatialQueryIndex Compile(IReadOnlyList<WorldPlacement>? placements) {
        if (placements is not { Count: > 0 }) {
            return new WorldSpatialQueryIndex([]);
        }

        var byId = placements.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var frames = new Dictionary<string, SpatialFrame>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var volumes = new List<CompiledSpatialVolume>();
        var unsupported = new List<SpatialQueryUnsupported>();

        foreach (var placement in placements) {
            if (placement.Spatial is not { Count: > 0 }) {
                continue;
            }

            if (placement.Deal is not null) {
                continue;
            }

            if (placement.Attach is not null || placement.Inhabit is not null || placement.Distribution is not null || placement.Mirror is not null) {
                unsupported.Add(new SpatialQueryUnsupported(placement.Id, "dynamic, inhabited, distributed, or mirrored placement has no single static spatial frame"));
                continue;
            }

            if (!TryResolveFrame(placement, byId, frames, visiting, out var frame, out var reason)) {
                unsupported.Add(new SpatialQueryUnsupported(placement.Id, reason));
                continue;
            }

            var start = volumes.Count;
            foreach (var volume in placement.Spatial) {
                if (!TryCompileGeometry(volume.Shape, frame, out var shape, out var bounds, out reason)) {
                    unsupported.Add(new SpatialQueryUnsupported(placement.Id,
                        $"spatial volume '{volume.Name}' is not representable after fixed-point frame composition: {reason}"));
                    volumes.RemoveRange(start, volumes.Count - start);
                    break;
                }
                volumes.Add(new CompiledSpatialVolume(placement.Id, volume.Name, volume.Role, volume.Channel, volume.Pinned, shape, bounds));
            }
        }

        return new WorldSpatialQueryIndex(volumes, unsupported);
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

        var frames = new Dictionary<string, SpatialFrame>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        if (!TryResolveFrame(candidate, byId, frames, visiting, out var frame, out _)) {
            return [];
        }

        var volumes = new CompiledSpatialVolume[candidate.Spatial.Count];
        for (var index = 0; index < candidate.Spatial.Count; index++) {
            var authored = candidate.Spatial[index];
            if (!TryCompileGeometry(authored.Shape, frame, out var shape, out var bounds, out _)) {
                return [];
            }
            volumes[index] = new CompiledSpatialVolume(
                candidate.Id, authored.Name, authored.Role, authored.Channel, authored.Pinned,
                shape, bounds);
        }
        return volumes;
    }

    private static bool TryResolveFrame(WorldPlacement placement, IReadOnlyDictionary<string, WorldPlacement> byId,
        Dictionary<string, SpatialFrame> frames, HashSet<string> visiting, out SpatialFrame frame, out string reason) {
        if (frames.TryGetValue(placement.Id, out frame)) { reason = string.Empty; return true; }
        if (!visiting.Add(placement.Id)) { frame = default; reason = $"placement '{placement.Id}' participates in a parent cycle"; return false; }

        try {
            var position = FixedVector3.FromVector3(placement.Position);
            var scale = FixedQ4816.FromDouble(placement.Scale);
            var yaw = FixedQ4816.FromDouble(placement.YawDegrees * (Math.PI / 180d));
            if (placement.Parent is { Length: > 0 } parentId) {
                byId.TryGetValue(parentId, out var parent);
                if (parent is null || (parent.Distribution is not null && parent.Deal is null) ||
                    parent.Mirror is not null || parent.Attach is not null || parent.Inhabit is not null) {
                    frame = default;
                    reason = $"placement '{placement.Id}' requires a declared static single parent frame '{parentId}'";
                    return false;
                }
                if (!TryResolveFrame(parent, byId, frames, visiting, out var parentFrame, out reason)) {
                    frame = default;
                    return false;
                }
                position = AddChecked(parentFrame.Position, Rotate(ScaleChecked(position, parentFrame.Scale), parentFrame.YawRadians));
                scale = checked(scale * parentFrame.Scale);
                yaw = checked(yaw + parentFrame.YawRadians);
            }
            if (!WithinNarrowphaseRange(position) || !WithinNarrowphaseRange(scale)) {
                frame = default;
                reason = $"placement '{placement.Id}' transform exceeds the fixed-point spatial query range";
                return false;
            }
            frame = new SpatialFrame(position, yaw, scale);
            frames[placement.Id] = frame;
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            frame = default;
            reason = $"placement '{placement.Id}' transform exceeds fixed-point range";
            return false;
        } finally {
            visiting.Remove(placement.Id);
        }
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
            shape = CompileShape(authored, frame);
            if (!WithinNarrowphaseRange(shape.Center) ||
                (shape.Kind == FixedSpatialShapeKind.Sphere
                    ? !WithinNarrowphaseRange(shape.Radius)
                    : !WithinNarrowphaseRange(shape.HalfExtents))) {
                reason = "compiled shape exceeds the fixed-point narrowphase range";
                return false;
            }
            if (shape.Kind == FixedSpatialShapeKind.Sphere) {
                if (shape.Radius <= FixedQ4816.Zero) {
                    reason = "sphere radius quantizes to zero after placement scale";
                    return false;
                }
            } else if (shape.HalfExtents.X <= FixedQ4816.Zero ||
                       shape.HalfExtents.Y <= FixedQ4816.Zero ||
                       shape.HalfExtents.Z <= FixedQ4816.Zero) {
                reason = "box half-extents quantize to zero after placement scale";
                return false;
            }

            bounds = BoundsFor(shape);
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            reason = "spatial geometry exceeds fixed-point range after placement scale";
            return false;
        }
    }

    private static FixedSpatialShape CompileShape(WorldSpatialShape authored, SpatialFrame frame) {
        var center = AddChecked(frame.Position, Rotate(ScaleChecked(FixedVector3.FromVector3(authored.Center.Value), frame.Scale), frame.YawRadians));
        var yaw = checked(frame.YawRadians + FixedQ4816.FromDouble(authored.YawDegrees * (Math.PI / 180d)));
        var rotation = Yaw(yaw);
        if (authored.Kind == WorldSpatialShapeKind.Sphere) {
            return new FixedSpatialShape(FixedSpatialShapeKind.Sphere, center, UnitX, UnitY, UnitZ, default, checked(FixedQ4816.FromDouble(authored.Radius) * frame.Scale));
        }

        var extents = ScaleChecked(FixedVector3.FromVector3(authored.HalfExtents.Value), frame.Scale);
        return new FixedSpatialShape(FixedSpatialShapeKind.Box, center, rotation.Rotate(UnitX), rotation.Rotate(UnitY), rotation.Rotate(UnitZ), extents, FixedQ4816.Zero);
    }

    private static FixedVector3 ScaleChecked(FixedVector3 value, FixedQ4816 scale) => new(
        checked(value.X * scale), checked(value.Y * scale), checked(value.Z * scale));

    private static FixedVector3 AddChecked(FixedVector3 left, FixedVector3 right) => new(
        checked(left.X + right.X), checked(left.Y + right.Y), checked(left.Z + right.Z));

    private static bool WithinNarrowphaseRange(FixedQ4816 value) =>
        value.Value >= -NarrowphaseRawLimit && value.Value <= NarrowphaseRawLimit;

    private static bool WithinNarrowphaseRange(FixedVector3 value) =>
        WithinNarrowphaseRange(value.X) && WithinNarrowphaseRange(value.Y) && WithinNarrowphaseRange(value.Z);

    private static FixedSpatialAabb BoundsFor(FixedSpatialShape shape) {
        if (shape.Kind == FixedSpatialShapeKind.Sphere) {
            return new FixedSpatialAabb(shape.Center, new FixedVector3(shape.Radius, shape.Radius, shape.Radius));
        }
        return new FixedSpatialAabb(shape.Center, new FixedVector3(
            checked(
                checked(FixedQ4816.Abs(shape.AxisX.X) * shape.HalfExtents.X) +
                checked(FixedQ4816.Abs(shape.AxisY.X) * shape.HalfExtents.Y) +
                checked(FixedQ4816.Abs(shape.AxisZ.X) * shape.HalfExtents.Z)),
            checked(
                checked(FixedQ4816.Abs(shape.AxisX.Y) * shape.HalfExtents.X) +
                checked(FixedQ4816.Abs(shape.AxisY.Y) * shape.HalfExtents.Y) +
                checked(FixedQ4816.Abs(shape.AxisZ.Y) * shape.HalfExtents.Z)),
            checked(
                checked(FixedQ4816.Abs(shape.AxisX.Z) * shape.HalfExtents.X) +
                checked(FixedQ4816.Abs(shape.AxisY.Z) * shape.HalfExtents.Y) +
                checked(FixedQ4816.Abs(shape.AxisZ.Z) * shape.HalfExtents.Z))));
    }

    private static FixedVector3 Rotate(FixedVector3 vector, FixedQ4816 yaw) {
        // Check before the quaternion kernel, whose full-width vector arithmetic permits carrier wraparound.
        if (!WithinNarrowphaseRange(vector)) { throw new OverflowException("Spatial rotation input exceeds the query range."); }
        return Yaw(yaw).Rotate(vector);
    }
    private static FixedQuaternion Yaw(FixedQ4816 radians) => FixedQuaternion.FromAxisAngle(UnitY, radians);
    private readonly record struct SpatialFrame(FixedVector3 Position, FixedQ4816 YawRadians, FixedQ4816 Scale);
}
