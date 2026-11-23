using System.Numerics;
using Puck.Abstractions.Memory;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The replacements in flight, each with the index it was placed at: a settle rebake (Rebake) or a finer tier (Retier).
    // The replaced group draws until the replacement lands and its words stand in the mesh region (FinishReplacements).
    private readonly List<(Group Group, int Index)> m_replacing = [];

    // The groups whose demanded tier is finer than the one they draw, as of the latest frame, the worst owed error among
    // them in display pixels and the longest owed interval in frames; the worst of each since the bake started; and the
    // finer tiers that landed, those the library already held among them.
    private int m_tiersOwed;
    private float m_owedError;
    private int m_owedFrames;
    private float m_worstOwedError;
    private int m_worstOwedFrames;
    private int m_retiers;
    private int m_retiersReused;

    private readonly List<(float Pixel, int Index, int Tier)> m_retierCandidates = [];
    // The floors planned on arrival that have landed, as the latest decision found them, finest demand first, which start
    // their demanded tier from that decision on (StartFirsts), and the next of them to start; how many such floors still
    // owe it, which the bake owes the frame (Owes); and the first details that landed.
    private readonly List<(float Pixel, int Index, int Tier, Group Floor)> m_firstCandidates = [];
    private int m_firstNext;
    private int m_firstDetailsOwed;
    private int m_floorsRaised;

    // The frames since Retier last decided the tiers, which it does once a measure (SdfMeshTier.MeasureFrames).
    private int m_decideFrames;
    // The frames in a row Retier has left the detail alone, counted up to SdfMeshTier.SettleFrames; read off the
    // frame's thread (DetailSettled).
    private int m_detailQuiet;
    // The residency's mesh region as the frame's thread last read it (ObserveResidency): the bytes the frame's draws
    // name, and the meshes it refuses. Its limit bounds the budget through the grant (SdfResidencyGrant.Budget).
    private long m_regionNamedBytes;
    private int m_regionRefused;
    // The groups drawing a finer tier than their demand asks and the finer tiers the budget held back, as of the
    // latest frame; the coarser tiers landed; and the half rungs every demand is coarsened by (SdfMeshTier.Biased):
    // the presentation's one bias as the bake last read it from its grant, zero for a bake no residency observes.
    private int m_tiersSpare;
    private int m_tiersHeldBack;
    private int m_coarsened;
    private int m_bias;
    // The residency's part of its presentation's selector, from the tables that observe the bake (ObserveResidency):
    // the budget its meshes fill and the bias its demand is coarsened by. Null for a bake no residency observes, which
    // has no budget.
    private SdfResidencyGrant? m_detail;

    private readonly List<(long Saving, int Index, int Tier)> m_spareCandidates = [];

    /// <summary>Gets whether the bake's meshes stand at the detail its demand asks and its mesh region's budget
    /// allows: for <see cref="SdfMeshTier.SettleFrames"/> prepared frames in a row no group was planned again at
    /// another tier, none was landing, none that owes a finer tier was waiting its hold
    /// (<see cref="SdfMeshTier.HoldFrames"/>), and the budget pressed on nothing it can still coarsen. A finer tier
    /// the budget holds back at the most bias, and the slow step that takes a half rung of bias back
    /// (<see cref="SdfMeshTier.BiasQuietFrames"/>), are where the detail rests, not a move still owed.
    /// <see cref="Pending"/> does not cover this: a bake with every group landed still owes the tiers its demand
    /// moved to. Read from any thread.</summary>
    public bool DetailSettled => (Volatile.Read(location: ref m_detailQuiet) >= SdfMeshTier.SettleFrames);

    // The bytes the bake's meshes may fill, as its grant last answered (Retier); no budget for a bake no residency
    // observes.
    private long m_regionBudgetBytes = long.MaxValue;

    // The tier a pixel demands under the bias the budget holds.
    private int TierOf(float pixel) => SdfMeshTier.Of(pixel: SdfMeshTier.Biased(halfRungs: Volatile.Read(location: ref m_bias), pixel: pixel));
    /// <summary>Returns the tier a plan bakes each group at: the coarsest whose cell stays under half a display pixel at
    /// the group's planned box from the nearest demand point (<see cref="SdfMeshTier.Of"/>, <see cref="SdfMeshDemand.PixelAt(SdfMeshBakeGroup, IReadOnlyList{DynamicTransform})"/>),
    /// a dynamic group's box carried into world space by its slot's transform among <paramref name="transforms"/> and
    /// its pixel read in the slot's units, and a shared geometry's box measured where its copies stand
    /// (<see cref="PlannedPixel"/>), as the frames after measure it (<see cref="Retier(SdfFrame)"/>), under the bias
    /// the mesh region's budget holds (<see cref="SdfMeshTier.Biased"/>). The plan holds the tier to the rungs that
    /// change the group's mesh.</summary>
    /// <param name="transforms">The dynamic transforms the plan is made at.</param>
    /// <param name="copies">Whether the copy index is the planned program's and this thread may read it: the frame's
    /// thread under the gate, once the frame's copies are indexed. A plan made on a build's thread passes
    /// <see langword="false"/> and measures every group by its planned box, and the restart that adopts it plans again
    /// each group whose tier the frame's demand moves; such a plan can also read a frame's demand points mid-update.</param>
    /// <param name="arrives">Whether a planned group arrives with nothing of its own to draw (<see cref="PlansFloor"/>),
    /// which plans its floor first; <see langword="null"/> plans every group at its demanded tier.</param>
    /// <returns>The function a plan asks each group's tier of.</returns>
    private Func<SdfMeshBakeGroup, int> Tiers(IReadOnlyList<DynamicTransform> transforms, bool copies = true, Func<SdfMeshBakeGroup, bool>? arrives = null) {
        if (PlannedTier is { } planned) { return planned; }

        Func<SdfMeshBakeGroup, int> demanded = (copies
            ? (group => TierOf(pixel: PlannedPixel(group: group, transforms: transforms)))
            : (group => TierOf(pixel: Demand.PixelAt(group: group, transforms: transforms))));
        if (arrives is null) { return demanded; }
        // A group arriving with nothing of its own to draw plans its floor: the last tier, which the plan holds to the
        // coarsest rung that changes its mesh. It owes its demanded tier, which Retier starts once the floor stands, and
        // keeps the floor beside it (Group.KeepFloorOf), which every view far smaller than the one the tier was demanded
        // for draws in its place (SdfMeshBand). A group arriving out of view does the same, ordered behind those in view
        // (RankOf), since a view of it that turns up later small (a screen, a portal, a distant shadow) draws the floor.
        return group => {
            if (!arrives(arg: group)) { return demanded(arg: group); }
            ((Group)group).FirstDetailOwed = true;
            return SdfMeshTier.Last;
        };
    }
    // Whether a selector arrives with nothing to draw, given the group the bake held at it: none, or one still bringing its
    // floor with nothing shown yet. Such a group plans its floor first (Tiers).
    private static bool Arrives(Group? old) => ((old is null) || ((old.Shown is null) && (old.Tier == old.CoarsestTier)));
    // Whether a planned group arrives with nothing of its own to draw: its selector does (above), or no group the bake
    // held has its shape, so an edit made it (a shape moved, resized or added) and whatever its selector draws is the
    // shape before the edit. Its floor lands first and shows the edit at once; the detail follows. A group whose
    // selector alone moved (another placement came or went before it) has a shape the bake held, and takes its mesh
    // from the library at its demanded tier. A static group whose geometry reads a slot's pose takes another shape
    // whenever that slot moves, which is no edit, so it is judged by its selector alone.
    private static bool PlansFloor(Group? old, SdfMeshBakeGroup planned, HashSet<UInt128> shapes) => (Arrives(old: old) || NewShape(planned: planned, shapes: shapes));
    private static bool NewShape(SdfMeshBakeGroup planned, HashSet<UInt128> shapes) => ((planned.Dynamic || !planned.Geometry.ReadsSlots) && !shapes.Contains(item: planned.Shape));

    /// <summary>Gets the tier every plan of this bake asks for each group in place of the one its demand points ask
    /// (<see cref="SdfMeshTier"/>), held to the rungs that change the group's mesh; <see langword="null"/>, a residency's
    /// bake, plans by demand. An offline bake with no view (the mesh audit) sets it, since no demand point would plan
    /// every group at its coarsest tier.</summary>
    public Func<SdfMeshBakeGroup, int>? PlannedTier { get; init; }

    // Whether a planned group's tier differs from the one the demand now asks of it.
    private static bool Retiered(SdfMeshBakeGroup group, Func<SdfMeshBakeGroup, int> tiers) =>
        (SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: tiers(arg: group)) != group.Tier);
    // The metres one display pixel covers at a group's demand box, in the group's own units, from the demand point that
    // sees it finest: a shared geometry where its copies stand, as its priority is measured (MeasureDemand).
    private float MeasurePixel(Group group, out int point) => MeasurePixel(boxes: out _, group: group, point: out point, units: out _, box: out _);
    // The metres one display pixel covers at a group's planned box while a plan is choosing its tier, in the group's own
    // units: a shared geometry's where the frame's copies of it stand, and any other group's at its planned box. On the
    // frame's thread, with the copy index at the planned program's frame (PrepareFrame indexes before it plans).
    private float PlannedPixel(SdfMeshBakeGroup group, IReadOnlyList<DynamicTransform> transforms) => ((CopiesOf(group: group) is { } copies)
        ? CopiesPixel(center: ((group.Low + group.High) * 0.5f), copies: copies, half: Vector3.Abs(value: ((group.High - group.Low) * 0.5f)), point: out _, units: out _)
        : Demand.PixelAt(group: group, transforms: transforms));
    // Whether a view may see one of a shared geometry's copies, its box given in the geometry's own frame.
    private bool CopiesSeen(IReadOnlyList<int> copies, Vector3 center, Vector3 half) {
        foreach (var index in copies) {
            var (boxCenter, boxHalf) = TransformBox(center: center, half: half, matrix: m_copyPoses[index].Matrix);

            if (Demand.Sees(center: boxCenter, half: boxHalf)) { return true; }
        }
        return false;
    }
    // MeasurePixel, the demand point that sees the group finest, -1 with none, the boxes the measure read (one, or a
    // shared geometry's copies), the fewest units any of them is measured in, which bounds how far the pixel moves as
    // the demand points travel (SdfMeshDemand.Travel), and the one world box it read; null for a shared geometry's.
    private float MeasurePixel(Group group, out int point, out int boxes, out float units, out (Vector3 Center, Vector3 Half)? box) {
        boxes = 1;
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);

            units = UnitsOf(group: group);
            box = (center, half);

            return (Demand.PixelAt(center: center, half: half, point: out point) / units);
        }

        boxes = Math.Max(val1: 1, val2: copies.Count);
        box = null;
        return CopiesPixel(center: group.DemandCenter, copies: copies, half: group.DemandHalf, point: out point, units: out units);
    }
    // The units a group's lattice is in: its slot's for a dynamic group (SdfMeshGeometryBuilder.UnitsOf), metres for any
    // other.
    private float UnitsOf(Group group) => ((group.Dynamic && (group.Slot < m_slotPoses.Length)) ? SdfMeshGeometryBuilder.UnitsOf(transform: m_slotPoses[group.Slot]) : 1f);
    // The metres one display pixel covers at a shared geometry's box, given in the geometry's own frame, from the demand
    // point that sees one of its copies finest, in the geometry's units; infinity, and no point, with no copy. Also the
    // fewest units a copy is measured in.
    // Each copy's box is read once and held on this thread (SdfMeshDemand.ReadBoxes), with the copies' scales, for the
    // bounds a measure then reads at each (SdfMeshDemand.HeldBoundsOf).
    private float CopiesPixel(IReadOnlyList<int> copies, Vector3 center, Vector3 half, out int point, out float units) {
        var count = copies.Count;

        if (m_copyCenters.Length < count) {
            var capacity = ArrayCapacity.Grown(m_copyCenters.Length, count);

            m_copyCenters = new Vector3[capacity];
            m_copyHalves = new Vector3[capacity];
            m_copyScales = new float[capacity];
        }
        for (var copy = 0; (copy < count); copy++) { (m_copyCenters[copy], m_copyHalves[copy], m_copyScales[copy]) = CopyBox(center: center, copy: copies[copy], half: half); }
        Demand.ReadBoxes(centers: m_copyCenters.AsSpan(start: 0, length: count), halves: m_copyHalves.AsSpan(start: 0, length: count));

        var finest = float.PositiveInfinity;

        point = -1;
        units = float.PositiveInfinity;
        for (var copy = 0; (copy < count); copy++) {
            var pixel = (Demand.HeldPixelAt(box: copy, point: out var seen) / m_copyScales[copy]);

            units = MathF.Min(x: units, y: m_copyScales[copy]);
            if (pixel < finest) {
                finest = pixel;
                point = seen;
            }
        }
        if (!float.IsFinite(f: units)) { units = 1f; }

        return finest;
    }
    // The copies' boxes and scales the latest CopiesPixel read.
    private Vector3[] m_copyCenters = [];
    private Vector3[] m_copyHalves = [];
    private float[] m_copyScales = [];
    // Retires each replaced group whose replacement has landed and taken the draws (Group.Ahead), and hands back one
    // whose replacement meshed nothing: the one step every tier's replacement (Retier) ends in. Under the gate.
    private void FinishReplacements() {
        for (var at = (m_replacing.Count - 1); (at >= 0); at--) {
            var (group, index) = m_replacing[at];

            if ((index >= m_groups.Count) || !ReferenceEquals(objA: m_groups[index], objB: group) || (group.Replaces is not { } replaced)) {
                m_replacing.RemoveAt(index: at);
                continue;
            }
            if (group.Fill) {
                if (!group.Done) { continue; }
                m_replacing.RemoveAt(index: at);
                FinishFill(fill: group, index: index, replaced: replaced);
                continue;
            }
            if (group.Declined) {
                m_replacing.RemoveAt(index: at);
                replaced.RetierRefused = true;
                GiveBack(index: index, replaced: replaced, replacement: group);
                continue;
            }
            if (!group.Done || group.Ahead) { continue; }
            m_replacing.RemoveAt(index: at);
            if (group.Mesh is null) {
                // The replaced group keeps drawing, and a finer tier is not tried until the demand asks another; the
                // replacement retires, letting go of the floor it kept.
                replaced.RetierRefused = true;
                GiveBack(index: index, replaced: replaced, replacement: group);
                continue;
            }
            // The landed mesh replaces the drawn one: the replaced group retires here, its one place, and releases its mesh
            // once its last work ends. The tier the group's draws carry is its own from now on (FrameAccess.TierOf), a
            // change of what the dresser reads, so the product revision moves.
            m_retiredGroups.Add(item: replaced);
            replaced.Retired = true;
            group.Replaces = null;
            Touch(group: group);
            if (group.FirstDetail) { m_floorsRaised++; } else if (group.Tier > replaced.Tier) { m_coarsened++; } else { m_retiers++; }
            group.FirstDetail = false;
            if (group.Stored) { m_retiersReused++; }
            group.RetierDeltaBytes = 0L;
            m_worstOwedFrames = Math.Max(val1: m_worstOwedFrames, val2: group.OwedFrames);
            group.OwedFrames = 0;
        }
    }
    // Once every SdfMeshTier.MeasureFrames frames decides the tiers from the demand the frame before measured
    // (MeasureDemands, joined before this frame was prepared): every count of frames below then advances by the frames
    // since the last decision. A decision
    // reads the demanded tier of each group the demand watch attends (Attend), from the pixel its demand was last
    // measured at, against the one it draws, counts the debt, and plans again at the demanded tier each
    // landed group that has owed a finer one for SdfMeshTier.HoldFrames frames, the finest demand first, at most
    // SdfMeshTier.MaxInFlight at once: the replacement bakes (or lands from the library) while the coarser mesh draws.
    // Detail is budgeted by demand inside the budget the presentation's selector grants this residency
    // (SdfResidencyGrant.Budget): what its draws name, what its finer tiers in flight are expected to add, and its part
    // of the room every residency shares. A finer tier starts only while
    // the bytes the frame's draws name, with those the finer replacements in flight are expected to add, leave it room.
    // What a replacement adds is known once it lands, so the expectation is the surface's scaling
    // (SdfMeshTier.BytesAt) held to one part in SdfMeshTier.ReplacementShare of the budget, and the pressure below is
    // read from the bytes the draws name, never from an expectation. The budget presses while the draws name more than
    // it holds, or a finer tier waits and they name within one replacement of it: the groups drawing a finer tier than
    // their demand asks (spare) are then planned again at the demanded one, the largest saving first, until the draws
    // are expected two replacements under the budget. When it has pressed for SdfMeshTier.HoldFrames frames with no
    // spare group left, landing or waiting its hold, in this residency or any other, the demand itself is past the
    // budget and the selector coarsens every demand half a rung (SdfResidencySelector, read into m_bias), which makes
    // the groups farthest past their rung spare. It takes the bias back half a rung once the bytes every residency's
    // draws would name with every demand half a rung finer and every spare group coarsened have stayed under that same
    // mark, with nothing owed or in flight, for the quiet wait. With room to spare a coarser tier
    // demanded changes nothing: the finer mesh stays, so a viewer who comes back finds it. Under the gate, on the
    // frame's thread.
    private void Retier(SdfFrame frame) {
        if (++m_decideFrames < SdfMeshTier.MeasureFrames) {
            _ = StartFirsts(frame: frame, inFlight: out _);
            return;
        }

        var elapsed = m_decideFrames;

        m_decideFrames = 0;

        var named = m_regionNamedBytes;
        var bias = m_bias;
        var inFlight = 0;
        // The replacements baked and landing ahead of the draws: they add no bake work, so they do not count
        // against the most in flight, and a wave the library holds starts whole and takes the draws together.
        var landing = 0;
        var coarsening = 0;
        // The bytes the draws name once every finer replacement in flight has landed, which another must fit beside,
        // and the bytes the coarser ones in flight free.
        var filled = named;
        var freed = 0L;

        // A floor's first detail is the arrival's own bake, as a first plan's is: its bytes are what its demand asks,
        // read from the draws once it lands, and it holds no budgeted replacement back.
        var firstsInFlight = 0;

        // The rungs a ladder bake fills are outside the budget and the draws: they land in the store alone.
        var fills = 0;

        foreach (var (group, _) in m_replacing) {
            if (group.Fill) {
                fills++;
                continue;
            }
            if (group.FirstDetail) {
                firstsInFlight++;
                continue;
            }
            if (group.Done) { landing++; } else { inFlight++; }
            if (group.RetierDeltaBytes > 0L) {
                filled = filled.AddSaturating(right: group.RetierDeltaBytes);
            } else {
                coarsening++;
                freed -= group.RetierDeltaBytes;
            }
        }

        var budget = (m_detail?.Budget(filled: filled, named: named) ?? long.MaxValue);
        var replacement = (budget / SdfMeshTier.ReplacementShare);
        var target = (budget - (2L * replacement));

        m_regionBudgetBytes = budget;

        var candidates = m_retierCandidates;
        var spare = m_spareCandidates;
        var owed = 0;
        var error = 0f;
        var frames = 0;
        var spares = 0;
        var sparesWaiting = 0;
        // The groups that owe a finer tier and wait their hold, and whether the budget presses on something it can
        // still coarsen: with a replacement started or in flight, what keeps the detail from settled.
        var owedWaiting = 0;
        var pressing = false;

        var firsts = m_firstCandidates;
        var firstsOwed = 0;

        candidates.Clear();
        spare.Clear();
        firsts.Clear();

        // Only the groups the demand watch measured or the list set since they last stood (Attend): every other group
        // demands the tier it draws, with nothing owed and nothing in flight, so it adds nothing below.
        var attention = m_attention;
        var kept = 0;

        SdfResidencyWork.ProcessWork.Add(kind: SdfResidencyWork.TierGroups, amount: attention.Count);
        for (var at = 0; (at < attention.Count); at++) {
            var group = attention[at];

            if (group.Retired || !group.Listed || !group.HasDemandBox) {
                attention.Drop(group: group);
                continue;
            }
            if (float.IsNaN(f: group.DemandPixel)) { group.DemandPixel = MeasurePixel(group: group, point: out _); }

            var index = group.Index;
            var pixel = group.DemandPixel;
            var demanded = SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: TierOf(pixel: pixel));
            // The tier a frame draws is the replaced group's while a replacement is still to land.
            var resident = (group.Replaces ?? group).Tier;

            Decide(group: group, index: index, pixel: pixel, demanded: demanded, resident: resident);
            if ((demanded == resident) && !group.FirstDetailOwed && (group.Replaces is null)) {
                attention.Drop(group: group);
                continue;
            }
            attention.Keep(at: kept++, group: group);
        }
        attention.Truncate(count: kept);
        SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.TierDecisions);

        // Reads one group's demanded tier against the one it draws: a first detail it owes, a finer tier it owes or a
        // coarser one it could give back, each counted and, past its hold, a candidate.
        void Decide(Group group, int index, float pixel, int demanded, int resident) {
            if (demanded != group.DemandedTier) {
                group.DemandedTier = demanded;
                group.RetierRefused = false;
            }
            // A floor planned on arrival starts its demanded tier once it has landed, with no hold, whatever the budget:
            // the arrival's detail is what a first plan at the demanded tier would have baked.
            if (group.FirstDetailOwed) {
                if (demanded >= group.Tier) {
                    group.FirstDetailOwed = false;
                } else {
                    firstsOwed++;
                    if (group.Done && (group.Replaces is null) && !group.Ahead) { firsts.Add(item: (pixel, index, demanded, group)); }
                    return;
                }
            }
            if (demanded >= resident) {
                group.OwedFrames = 0;
                if ((demanded == resident) || (group.Mesh is null)) {
                    group.SpareFrames = 0;
                    return;
                }
                spares++;
                group.SpareFrames += elapsed;
                if (!group.Done || (group.Replaces is not null) || group.RetierRefused || group.Ahead) { return; }
                if (group.SpareFrames < SdfMeshTier.HoldFrames) {
                    sparesWaiting++;
                    return;
                }

                var bytes = group.PlacedBytes;

                spare.Add(item: ((bytes - SdfMeshTier.BytesAt(bytes: bytes, from: resident, to: demanded)), index, demanded));
                return;
            }
            group.SpareFrames = 0;
            group.OwedFrames += elapsed;
            owed++;
            error = MathF.Max(x: error, y: (SdfMeshTier.Cell(tier: resident) / pixel));
            frames = Math.Max(val1: frames, val2: group.OwedFrames);
            if (!group.Done || (group.Mesh is null) || (group.Replaces is not null) || group.RetierRefused || group.Ahead) { return; }
            if (group.OwedFrames < SdfMeshTier.HoldFrames) {
                owedWaiting++;
                return;
            }
            candidates.Add(item: (pixel, index, demanded));
        }
        m_tiersOwed = owed;
        m_owedError = error;
        m_owedFrames = frames;
        m_worstOwedError = MathF.Max(x: m_worstOwedError, y: error);
        m_worstOwedFrames = Math.Max(val1: m_worstOwedFrames, val2: frames);
        m_tiersSpare = spares;
        candidates.Sort(comparison: static (left, right) => ((left.Pixel.CompareTo(value: right.Pixel) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index)));

        var replaced = false;
        var heldBack = 0;

        firsts.Sort(comparison: static (left, right) => ((left.Pixel.CompareTo(value: right.Pixel) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index)));
        m_firstNext = 0;
        m_firstDetailsOwed = firstsOwed;
        if (StartFirsts(frame: frame, inFlight: out var firstsStarted) > 0) { replaced = true; }
        firstsInFlight += firstsStarted;
        firstsOwed = m_firstDetailsOwed;

        foreach (var (_, index, tier) in candidates) {
            var old = m_groups[index];
            var bytes = old.PlacedBytes;
            var delta = Math.Min(val1: replacement, val2: (SdfMeshTier.BytesAt(bytes: bytes, from: old.Tier, to: tier) - bytes));

            // A finer tier the budget has no room for waits, the finest demand first in line for what is freed.
            if ((heldBack > 0) || (delta > (budget - filled))) {
                heldBack++;
                continue;
            }
            if (inFlight >= SdfMeshTier.MaxInFlight) { break; }

            var started = Retier(delta: delta, frame: frame, index: index, old: old, tier: tier);

            if (started == TierStart.None) { continue; }
            if (started == TierStart.Baking) { inFlight++; }
            // One the library held has landed, and the next frame's draws name it.
            filled = filled.AddSaturating(right: delta);
            replaced = true;
        }
        m_tiersHeldBack = heldBack;

        // A mesh the region refuses presses whatever the bytes say: the arena's free runs, not the sum, decide a place.
        var over = ((named > budget) || ((heldBack > 0) && (named > (budget - replacement))) || (m_regionRefused > 0));
        var anySpare = false;

        if (over) {
            var expected = (named - freed);

            spare.Sort(comparison: static (left, right) => ((right.Saving.CompareTo(value: left.Saving) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index)));
            foreach (var (saving, index, tier) in spare) {
                if ((inFlight >= SdfMeshTier.MaxInFlight) || (expected <= target)) { break; }

                var started = Retier(delta: -saving, frame: frame, index: index, old: m_groups[index], tier: tier);

                if (started == TierStart.None) { continue; }
                if (started == TierStart.Baking) { inFlight++; }
                // One the library held has landed, and its saving shows in the next frame's draws.
                coarsening++;
                expected -= saving;
                replaced = true;
            }
            anySpare = ((spare.Count > 0) || (sparesWaiting > 0) || (coarsening > 0));
            pressing = (anySpare || (bias < SdfMeshTier.MaxBias));
        }
        // The selector moves the one bias on every residency's report; this bake coarsens its demand by what it answers.
        if (m_detail is { } detail) {
            Volatile.Write(location: ref m_bias, value: detail.Decide(report: new SdfDetailReport(
                AnySpare: anySpare,
                CanLower: (!over && (owed == 0) && (inFlight == 0) && (landing == 0) && (firstsInFlight == 0) && (firstsOwed == 0) && ((bias == 0) || (FinerBytes(bias: bias) <= target))),
                Elapsed: elapsed,
                Over: over
            )));
        }
        // A ladder bake's detail is settled once every drawn geometry's other rungs stand in the store too.
        var filling = ((Library.Policy == SdfMeshCachePolicy.Ladder) && FillLadders(fills: fills));

        Volatile.Write(location: ref m_filling, value: (filling ? 1 : 0));
        Volatile.Write(location: ref m_decidedAt, value: System.Diagnostics.Stopwatch.GetTimestamp());

        var hold = new DetailHold(
            Replaced: replaced,
            InFlight: inFlight,
            Landing: landing,
            OwedWaiting: owedWaiting,
            FirstsInFlight: firstsInFlight,
            FirstsOwed: firstsOwed,
            Pressing: pressing,
            Over: over,
            Spares: (spare.Count + sparesWaiting + coarsening),
            Bias: bias,
            Filling: filling
        );

        m_detailHold = hold;
        Volatile.Write(location: ref m_detailQuiet, value: (hold.Holds ? 0 : Math.Min(val1: (m_detailQuiet + elapsed), val2: SdfMeshTier.SettleFrames)));
    }
    // What the latest tier decision found keeping the detail from settled (DetailSettled), one count or flag a reason: a
    // tier started this decision, replacements still baking or landing ahead of the draws, owed finer tiers waiting their
    // hold, first details in flight or owed, a budget that presses on something it can still coarsen (over its budget,
    // with spare groups or the bias short of its most), and a ladder still being filled.
    private readonly record struct DetailHold(bool Replaced, int InFlight, int Landing, int OwedWaiting, int FirstsInFlight, int FirstsOwed, bool Pressing, bool Over, int Spares, int Bias, bool Filling) {
        public bool Holds => (Replaced || (InFlight > 0) || (Landing > 0) || (OwedWaiting > 0) || (FirstsInFlight > 0) || (FirstsOwed > 0) || Pressing || Filling);

        public override string ToString() {
            var parts = new List<string>();

            if (Replaced) { parts.Add(item: "a tier started"); }
            if (InFlight > 0) { parts.Add(item: $"{InFlight} tier(s) baking"); }
            if (Landing > 0) { parts.Add(item: $"{Landing} tier(s) landing ahead of the draws"); }
            if (OwedWaiting > 0) { parts.Add(item: $"{OwedWaiting} owed tier(s) waiting their hold"); }
            if (FirstsInFlight > 0) { parts.Add(item: $"{FirstsInFlight} first detail(s) in flight"); }
            if (FirstsOwed > 0) { parts.Add(item: $"{FirstsOwed} first detail(s) owed"); }
            if (Pressing) { parts.Add(item: $"the budget presses (over={(Over ? 1 : 0)} spares={Spares} bias={Bias}/{SdfMeshTier.MaxBias})"); }
            if (Filling) { parts.Add(item: "a ladder is still filling"); }
            return string.Join(separator: ", ", values: parts);
        }
    }

    // The latest tier decision's hold. Under the gate.
    private DetailHold m_detailHold;

    // The bytes the draws would name with every demand half a rung finer than the bias asks and every spare group
    // coarsened, and what it was summed at: the product revision (every change of a drawn mesh or its tier, Touch), the
    // demand revision of the finer tiers (MeasureDemands) and the bias.
    private (long Product, long Demand, int Bias, long Bytes) m_finer = (-1L, -1L, -1, 0L);

    // The bytes the draws would name half a rung finer (above), summed over every group with a drawn mesh only when an
    // input moved since the last sum. A floor that still owes its first detail adds nothing, as its decision reads none
    // of it. Under the gate.
    private long FinerBytes(int bias) {
        if ((m_finer.Product == m_productRevision) && (m_finer.Demand == m_finerDemandRevision) && (m_finer.Bias == bias)) { return m_finer.Bytes; }

        var finer = 0L;

        foreach (var group in m_groups) {
            if (group.Retired || !group.HasDemandBox || float.IsNaN(f: group.DemandPixel)) { continue; }

            var pixel = group.DemandPixel;

            if (group.FirstDetailOwed && (SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: TierOf(pixel: pixel)) < group.Tier)) { continue; }

            var shown = (group.Replaces ?? group);

            if (shown.Mesh is null) { continue; }
            finer = finer.AddSaturating(right: SdfMeshTier.BytesAt(bytes: shown.PlacedBytes, from: shown.Tier, to: FinerTierOf(bias: bias, group: group, pixel: pixel)));
        }
        m_finer = (m_productRevision, m_finerDemandRevision, bias, finer);

        return finer;
    }
    // Starts the first details the latest decision found owed (m_firstCandidates), finest demand first, at most
    // SdfMeshTier.FirstsPerFrame a frame, so an arrival's wave of landed floors is replanned over a few frames rather than
    // in one frame holding the gate; a floor replaced, retired or no longer owing since the decision is passed over. Returns
    // how many started, and how many of those are still to land. Under the gate, on the frame's thread.
    private int StartFirsts(SdfFrame frame, out int inFlight) {
        var firsts = m_firstCandidates;
        var started = 0;

        inFlight = 0;
        while ((m_firstNext < firsts.Count) && (started < SdfMeshTier.FirstsPerFrame)) {
            var (_, index, tier, floor) = firsts[m_firstNext++];

            if ((index >= m_groups.Count) || !ReferenceEquals(objA: m_groups[index], objB: floor) || !floor.FirstDetailOwed || floor.Retired || !floor.Done || (floor.Replaces is not null) || floor.Ahead) { continue; }
            floor.FirstDetailOwed = false;
            m_firstDetailsOwed--;

            var start = Retier(delta: 0L, first: true, frame: frame, index: index, old: floor, tier: tier);

            if (start == TierStart.None) { continue; }
            started++;
            if (start == TierStart.Baking) { inFlight++; }
        }
        // The list holds its floors only while some may still start.
        if (m_firstNext >= firsts.Count) {
            firsts.Clear();
            m_firstNext = 0;
        }
        return started;
    }
    // How a tier's replacement started (StartReplacement): none started, so the group is not asked that tier again until
    // its demand names another (Group.RetierRefused); the library's mesh landed at once; or it bakes or loads while the
    // mesh it replaces keeps drawing. Only a replacement that started moves the detail, so only one keeps it unsettled.
    private enum TierStart : byte { None, Landed, Baking }

    // Plans a group again at another tier, as Plan would plan it at this frame, and starts it in the group's place: the
    // library's mesh of that geometry lands at once, and otherwise the group bakes (or loads from the store) while the
    // mesh it replaces keeps drawing. The delta is the bytes of mesh region the replacement is expected to add; a first
    // detail replaces a floor planned on arrival, outside the budget.
    private TierStart Retier(SdfFrame frame, int index, Group old, int tier, long delta, bool first = false) {
        var plans = new List<Group> { old };

        _ = SdfMeshBakeGroup.Refresh(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: frame.FarDistance,
            groups: plans,
            program: frame.Program,
            sampled: m_sampled,
            stale: static _ => true,
            tier: _ => tier,
            transforms: frame.DynamicTransforms
        );

        var group = plans[0];

        // A dynamic slot no active instance rides any more plans nothing, and the next program drops the group; until then
        // it is not asked this tier again, or every decision would ask it and the detail would never settle.
        if (ReferenceEquals(objA: group, objB: old)) {
            old.RetierRefused = true;
            return TierStart.None;
        }
        group.FirstDetail = first;
        group.RetierDeltaBytes = delta;
        group.OwedFrames = old.OwedFrames;
        group.DemandedTier = old.DemandedTier;
        group.DemandPixel = old.DemandPixel;
        return StartReplacement(group: group, index: index, old: old);
    }
    // Starts a replacement planned for a landed group in that group's place: the library's mesh of its geometry is its
    // product at once, and otherwise it bakes (or loads from the store); the mesh it replaces keeps drawing until the
    // product stands in the mesh region. FinishReplacements retires the replaced group once the product has the draws.
    // Under the gate.
    private TierStart StartReplacement(Group group, Group old, int index) {
        // A bake that draws stored rungs alone keeps drawing what it draws when the store lacks the rung asked: the
        // group is asked again only once its demand names another tier (Group.RetierRefused). Live geometry has no
        // stored rung to lack: its tiers bake as its steps do, and a tier of a live group is live.
        group.InheritLive(from: old);
        if ((Library.Policy == SdfMeshCachePolicy.Stored) && !group.Live && !Stores(group: group, identity: Identity.Value)) {
            Library.Cache.Count(kind: SdfMeshCache.RungsMissed);
            SdfMeshBakeReport.RungMissed(asked: group.Tier, drawn: old.Tier, dynamic: group.Dynamic, residency: Residency, selector: group.Selector, shape: group.Shape);
            old.RetierRefused = true;
            group.Retired = true;
            return TierStart.None;
        }
        SdfResidencyWork.ProcessWork.Count(kind: SdfResidencyWork.TierStarts);
        group.TakeDemandBox();
        group.Replaces = old;
        Replace(group: group, index: index);
        m_replacing.Add(item: (group, index));
        group.Identity = Identity.Value;
        ShowReplaced(group: group, old: old);
        // The mesh of the geometry, or for a dynamic group the latest of its form, baked at another pose, as an
        // arriving group takes one (Place).
        if (Library.TryTake(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var baked) ||
            (group.Dynamic && Library.TryTakeForm(form: group.Form, identity: group.Identity, materials: group.MaterialOrder, mesh: out baked))) {
            Draw(group: group, mesh: baked);
            group.Done = true;
            group.Stored = true;
            return TierStart.Landed;
        }

        CountStarted(how: Start(group: group));

        return TierStart.Baking;
    }
}
