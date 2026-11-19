using System.Globalization;
using System.Text;

namespace Puck.SdfVm;

// The floor: each group's mesh at its coarsest tier (SdfMeshTier.Coarsest), which a group arriving with nothing to draw
// plans first (Tiers, Arrives) and keeps with its own claim while a finer tier draws (Group.KeptFloor), named ahead of
// the draws so its words stay in the mesh region (ServeAhead). A finer tier the region refuses under its limit is
// evicted to that floor at once (Evict), which then takes its detail back only through the budget.
//
// The invariant: a group in reach either draws a mesh whose words stand, or is counted as a hole (CountHoles). The one
// door every drawn mesh passes is Group.Shown, read by the dresser through FrameAccess.MeshOf; the floor reaches it as
// the arrival's own product, as the replaced group's shown mesh while a finer tier lands ahead (ShowReplaced, the one
// place a replacement keeps its predecessor's floor), and as the product an eviction puts in a refused group's place.
// Nothing enforces that a group has a floor: one taken whole from the library, or replanned with nothing shown, keeps
// none until it arrives again, and the floor counts name such groups (DescribeFloors).
public sealed partial class SdfMeshPathBake {
    // The groups ObserveResidency found drawing a refused finer tier over a standing kept floor, which the next prepared
    // frame evicts; the evictions made and the bytes of the finer tiers they let go; the groups in reach that drew no
    // standing mesh at the latest frame, and the frames that had any.
    private readonly List<(Group Group, int Index)> m_evictions = [];
    private long m_evicted;
    private long m_evictedBytes;
    private int m_holes;
    private long m_holeFrames;

    // Finds each group whose drawn finer tier the mesh region refuses while the floor it keeps stands there: its draws
    // wait out of the frame, so the next prepared frame evicts it to that floor (Evict). Under the gate, on the frame's
    // thread, after the frame's mesh region is staged.
    private void CollectEvictions(SdfWorldTables tables) {
        m_evictions.Clear();
        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (group.Retired || !group.Done || (group.Replaces is not null) || group.Ahead || (group.KeptFloor is not { } floor) || (group.Mesh is not { } mesh)) { continue; }
            if (!AnyPiece(mesh: mesh, test: tables.RefusesMesh) || AnyPiece(mesh: floor, test: piece => !tables.MeshStands(mesh: piece))) { continue; }
            m_evictions.Add(item: (group, index));
        }
    }
    // Puts each collected group's floor in its place: a group planned at its coarsest tier, drawing the floor the group
    // kept where that floor was baked from the geometry the plan reads, and otherwise baking it as a coarser replacement
    // while the refused tier waits out of the frame. The floor takes its detail back only through the budget
    // (Group.Evicted). Under the gate, on the frame's thread.
    private void Evict(SdfFrame frame) {
        if (m_evictions.Count == 0) { return; }
        foreach (var (group, index) in m_evictions) {
            if ((index >= m_groups.Count) || !ReferenceEquals(objA: m_groups[index], objB: group) || group.Retired || (group.Replaces is not null) || group.Ahead ||
                (group.KeptFloor is not { } kept) || (group.Mesh is not { } detail)) { continue; }

            var plans = new List<Group> { group };

            _ = SdfMeshBakeGroup.Refresh(
                create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
                farDistance: frame.FarDistance,
                groups: plans,
                program: frame.Program,
                sampled: m_sampled,
                stale: static _ => true,
                tier: static _ => SdfMeshTier.Last,
                transforms: frame.DynamicTransforms
            );

            var floor = plans[0];

            if (ReferenceEquals(objA: floor, objB: group)) { continue; }

            var bytes = SdfMeshRegion.PlacedBytesOf(mesh: detail);

            floor.Evicted = true;
            floor.RetierRefused = true;
            floor.DemandedTier = group.DemandedTier;
            floor.DemandPixel = group.DemandPixel;
            m_evicted++;
            m_evictedBytes += bytes;
            if (group.FloorGeometry is { } baked && (baked == floor.Sealed)) {
                floor.TakeDemandBox();
                Replace(group: floor, index: index);
                floor.Identity = Identity.Value;
                floor.Mesh = kept.Acquire();
                floor.Done = true;
                floor.Stored = true;
                m_retiredGroups.Add(item: group);
                group.Retired = true;
                continue;
            }
            floor.Retiering = true;
            floor.RetierDeltaBytes = -bytes;
            _ = StartReplacement(group: floor, index: index, old: group);
        }
        m_evictions.Clear();
        m_productRevision++;
    }
    // Counts the groups in reach that draw no standing mesh: one still bringing its first mesh, or one whose drawn mesh's
    // words the region owes or refuses, demanded past a pixel at a demand point (SdfMeshDemand.Priority). A group with
    // nothing to draw once done (no surface) is no hole. Under the gate, on the frame's thread.
    private void CountHoles(SdfWorldTables tables) {
        var holes = 0;

        foreach (var group in m_groups) {
            if (group.Retired) { continue; }
            if (group.Shown is { } shown) {
                if (!AnyPiece(mesh: shown, test: tables.OwesMesh)) { continue; }
            } else if (group.Done) {
                continue;
            }
            if (!group.HasDemandBox || ((group.Done ? MeasureDemand(group, priority: true) : group.Priority) <= 1f)) { continue; }
            holes++;
        }
        m_holes = holes;
        if (holes > 0) { m_holeFrames++; }
    }
    private static bool AnyPiece(SdfMesh mesh, Func<SdfMesh, bool> test) {
        for (var piece = 0; (piece < mesh.PieceCount); piece++) { if (test(arg: mesh.Piece(index: piece))) { return true; } }
        return false;
    }

    // The classes a floor's bytes are reported by, in the order a group is classed.
    private enum FloorClass { Glass, Lettering, Skinned, Dynamic, Chunked, Static }

    private static FloorClass ClassOf(Group group, SdfMesh floor) =>
        (group.Glass ? FloorClass.Glass
            : (group.Geometry.ContainsGlyph ? FloorClass.Lettering
                : ((!floor.VertexMembers.IsEmpty) ? FloorClass.Skinned
                    : (group.Dynamic ? FloorClass.Dynamic
                        : ((floor.Chunks.Count > 0) ? FloorClass.Chunked : FloorClass.Static)))));

    /// <summary>Describes the residency's floors: the groups drawing their floor, still bringing it, whose floor meshed
    /// nothing or failed, drawing a finer tier with a floor kept and with none; the floors planned on arrival that still
    /// owe their demanded tier and the first details landed; the evictions to a floor and the bytes they let go; the
    /// groups in reach that drew no standing mesh at the latest frame and the frames that had any; and the floors' bytes
    /// in the mesh region by class (glass, lettering, skinned, dynamic, chunked, static), each floor once, logical words
    /// rather than the region's blocks.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeFloors() {
        lock (m_gate) {
            int shown = 0, pending = 0, empty = 0, failed = 0, kept = 0, none = 0;
            Span<long> bytes = stackalloc long[6];
            Span<int> floors = stackalloc int[6];
            var counted = new HashSet<SdfMesh>(comparer: ReferenceEqualityComparer.Instance);

            foreach (var group in m_groups) {
                if (group.Retired) { continue; }

                var atFloor = (group.Tier == group.CoarsestTier);

                if (group.Shown is not null) {
                    if ((group.Replaces ?? group).Tier == group.CoarsestTier) { shown++; } else if (group.KeptFloor is not null) { kept++; } else { none++; }
                } else if (atFloor) {
                    if (!group.Done) { pending++; } else if (group.Failed) { failed++; } else if (group.Mesh is null) { empty++; }
                }
                if ((group.Floor is not { } floor) || !counted.Add(item: floor)) { continue; }

                var kind = ((int)ClassOf(floor: floor, group: group));

                bytes[kind] += SdfMeshRegion.PlacedBytesOf(mesh: floor);
                floors[kind]++;
            }

            var line = new StringBuilder();

            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"floor-shown={shown} floor-pending={pending} floor-empty={empty} floor-failed={failed} floor-kept={kept} floorless={none} first-details-owed={m_firstDetailsOwed} floors-raised={m_floorsRaised} evicted={m_evicted} evicted-bytes={m_evictedBytes}B holes={m_holes} hole-frames={m_holeFrames}");
            for (var kind = 0; (kind < bytes.Length); kind++) {
                var name = ((FloorClass)kind).ToString().ToLowerInvariant();

                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" floor-bytes.{name}={bytes[kind]}B/{floors[kind]}");
            }

            return line.ToString();
        }
    }
}
