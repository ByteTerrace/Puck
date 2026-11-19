using System.Globalization;
using System.Text;
using BakeGate = Puck.SdfVm.SdfMeshBakeReport.Gate;

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
//
// The floor is also the mesh the irradiance volume traces in the group's place (Group.Trace, read by the dresser through
// FrameAccess.TraceOf into SdfMeshDraw.Trace): the plan alone chooses it, so a view's retier or the budget's bias leaves
// the trace scene standing. A kept floor is traced until the trace judge (SdfMeshTraceJudge, run by JudgeFloors on one
// pool thread) holds it to the group's first finer product and declines it; a declined floor's group traces what it
// draws, which moves with the view again.
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
            floor.RetierDeltaBytes = -bytes;
            _ = StartReplacement(group: floor, index: index, old: group);
        }
        m_evictions.Clear();
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
    // What the trace judge said of a kept floor (Group.FloorVerdict). Written by the judge's task, read on the frame's
    // thread.
    private sealed class TraceVerdict {
        public const int Unjudged = 0;
        public const int Judging = 1;
        public const int Admitted = 2;
        public const int Refused = 3;

        private int m_state;

        public int State { get => Volatile.Read(location: ref m_state); set => Volatile.Write(location: ref m_state, value: value); }
        public bool Declined => (State == Refused);
    }

    // The floors queued for the trace judge, each with the finer product it is held to (both claimed until judged) and
    // its verdict; whether the judge's task runs; the verdicts given; and whether a declined floor waits for the dresser
    // to read it (the next prepared frame moves the product revision). The product revision the last scan read.
    private readonly Queue<(SdfMesh Floor, SdfMesh Finer, TraceVerdict Verdict)> m_judging = new();
    private bool m_judgeRunning;
    private long m_admittedFloors;
    private long m_declinedFloors;
    private int m_declinedLanded;
    private long m_judgedProduct = -1;

    // Queues each kept floor that owes the trace judge a verdict (Group.OwesVerdict), when a group's product moved since
    // the last scan, and moves the product revision for every floor the judge has declined since the last frame, so the
    // dresser hands its draws the drawn mesh to trace in its place (FrameAccess.TraceOf). Under the gate, on the frame's
    // thread.
    private void JudgeFloors() {
        if (Interlocked.Exchange(location1: ref m_declinedLanded, value: 0) != 0) { m_productRevision++; }
        if (m_judgedProduct == m_productRevision) { return; }
        m_judgedProduct = m_productRevision;

        var queued = false;

        foreach (var group in m_groups) {
            if (group.Retired || group.Glass || group.Dynamic || !group.OwesVerdict) { continue; }
            group.FloorVerdict!.State = TraceVerdict.Judging;
            lock (m_judging) { m_judging.Enqueue(item: (group.CurrentFloor!.Acquire(), group.JudgedAgainst!.Acquire(), group.FloorVerdict)); }
            queued = true;
        }
        if (!queued) { return; }
        lock (m_judging) {
            if (m_judgeRunning) { return; }
            m_judgeRunning = true;
        }
        _ = Task.Run(action: Judge);
    }
    // The judge's task: one floor after another on one pool thread, so its work never takes more than a core.
    private void Judge() {
        while (true) {
            (SdfMesh Floor, SdfMesh Finer, TraceVerdict Verdict) next;

            lock (m_judging) {
                if (!m_judging.TryDequeue(result: out next)) {
                    m_judgeRunning = false;
                    return;
                }
            }
            try {
                var admitted = SdfMeshTraceJudge.Admits(finer: next.Finer, floor: next.Floor);

                next.Verdict.State = (admitted ? TraceVerdict.Admitted : TraceVerdict.Refused);
                if (admitted) {
                    Interlocked.Increment(location: ref m_admittedFloors);
                } else {
                    Interlocked.Increment(location: ref m_declinedFloors);
                    Volatile.Write(location: ref m_declinedLanded, value: 1);
                }
            } catch (Exception exception) {
                // A floor the judge cannot read stays unjudged and traced; the failure is reported, never thrown at a frame.
                next.Verdict.State = TraceVerdict.Unjudged;
                Console.Error.WriteLine(value: $"[sdf.mesh-bake] trace judge failed: {exception}");
            } finally {
                next.Floor.Release();
                next.Finer.Release();
            }
        }
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
    /// rather than the region's blocks; and the static groups whose placements the irradiance volume traces at their floor
    /// and at their drawn mesh, the floors the trace judge admitted and declined, and those it still owes.</summary>
    /// <returns>One line of <c>key=value</c> pairs.</returns>
    public string DescribeFloors() {
        using (EnterGate(site: BakeGate.Read)) {
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

            int traced = 0, drawn = 0, judging;

            foreach (var group in m_groups) {
                if (group.Retired || group.Dynamic || group.Glass || (group.Shown is null)) { continue; }
                if (group.Trace is null) { drawn++; } else { traced++; }
            }
            lock (m_judging) { judging = m_judging.Count; }
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"floor-shown={shown} floor-pending={pending} floor-empty={empty} floor-failed={failed} floor-kept={kept} floorless={none} first-details-owed={m_firstDetailsOwed} floors-raised={m_floorsRaised} evicted={m_evicted} evicted-bytes={m_evictedBytes}B holes={m_holes} hole-frames={m_holeFrames}");
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" trace-floor={traced} trace-drawn={drawn} trace-admitted={Interlocked.Read(location: ref m_admittedFloors)} trace-declined={Interlocked.Read(location: ref m_declinedFloors)} trace-judging={judging}");
            for (var kind = 0; (kind < bytes.Length); kind++) {
                var name = ((FloorClass)kind).ToString().ToLowerInvariant();

                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" floor-bytes.{name}={bytes[kind]}B/{floors[kind]}");
            }

            return line.ToString();
        }
    }
}
