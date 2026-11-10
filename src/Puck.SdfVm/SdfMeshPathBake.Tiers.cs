using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // The replacements in flight, each with the index it was placed at: a settle rebake (Rebake) or a finer tier (Retier).
    // The replaced group draws until the replacement lands (FinishReplacements).
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
    // The residency's mesh region as the frame's thread last read it (ObserveResidency): its limit, zero before the
    // first reading, the bytes the frame's draws name, and the meshes it refuses.
    private long m_regionLimitBytes;
    private long m_regionNamedBytes;
    private int m_regionRefused;
    // The groups drawing a finer tier than their demand asks and the finer tiers the budget held back, as of the
    // latest frame; the coarser tiers landed; the half rungs every demand is coarsened by (SdfMeshTier.Biased), the
    // steps up and down, the frames the budget has pressed with nothing spare, the frames a step down has had room,
    // the frames a step down waits, and the frames since the latest step down.
    private int m_tiersSpare;
    private int m_tiersHeldBack;
    private int m_coarsened;
    private int m_bias;
    private int m_biasRaised;
    private int m_biasLowered;
    private int m_biasPressed;
    private int m_biasQuiet;
    private int m_biasWait = SdfMeshTier.BiasQuietFrames;
    private int m_biasSinceLowered = int.MaxValue;
    private readonly List<(long Saving, int Index, int Tier)> m_spareCandidates = [];

    // The bytes of mesh region the bake budgets its meshes to; no budget before the region's limit is read.
    private long RegionBudgetBytes => ((m_regionLimitBytes == 0L) ? long.MaxValue : ((m_regionLimitBytes / 8L) * SdfMeshTier.BudgetEighths));
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
    /// <returns>The function a plan asks each group's tier of.</returns>
    private Func<SdfMeshBakeGroup, int> Tiers(IReadOnlyList<DynamicTransform> transforms, bool copies = true) =>
        (PlannedTier ?? (copies
            ? (group => TierOf(pixel: PlannedPixel(group: group, transforms: transforms)))
            : (group => TierOf(pixel: Demand.PixelAt(group: group, transforms: transforms)))));

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
    private float MeasurePixel(Group group) => MeasurePixel(group: group, point: out _);
    // The metres one display pixel covers at a group's planned box while a plan is choosing its tier, in the group's own
    // units: a shared geometry's where the frame's copies of it stand, and any other group's at its planned box. On the
    // frame's thread, with the copy index at the planned program's frame (PrepareFrame indexes before it plans).
    private float PlannedPixel(SdfMeshBakeGroup group, IReadOnlyList<DynamicTransform> transforms) => ((CopiesOf(group: group) is { } copies)
        ? CopiesPixel(center: ((group.Low + group.High) * 0.5f), copies: copies, half: Vector3.Abs(value: ((group.High - group.Low) * 0.5f)), point: out _)
        : Demand.PixelAt(group: group, transforms: transforms));
    // MeasurePixel, and the demand point that sees the group finest; -1 with none.
    private float MeasurePixel(Group group, out int point) {
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);
            var units = ((group.Dynamic && (group.Slot < m_demandTransforms.Count)) ? SdfMeshGeometryBuilder.UnitsOf(transform: m_demandTransforms[group.Slot]) : 1f);

            return (Demand.PixelAt(center: center, half: half, point: out point) / units);
        }

        return CopiesPixel(center: group.DemandCenter, copies: copies, half: group.DemandHalf, point: out point);
    }
    // The metres one display pixel covers at a shared geometry's box, given in the geometry's own frame, from the demand
    // point that sees one of its copies finest, in the geometry's units; infinity, and no point, with no copy.
    private float CopiesPixel(IReadOnlyList<int> copies, Vector3 center, Vector3 half, out int point) {
        var finest = float.PositiveInfinity;

        point = -1;
        foreach (var index in copies) {
            var matrix = m_demandCopies[index].Matrix;
            var (boxCenter, boxHalf) = TransformBox(center: center, half: half, matrix: matrix);
            var units = MathF.Max(x: 1.0e-6f, y: new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13).Length());
            var pixel = (Demand.PixelAt(center: boxCenter, half: boxHalf, point: out var seen) / units);

            if (pixel < finest) {
                finest = pixel;
                point = seen;
            }
        }

        return finest;
    }
    // Retires each replaced group whose replacement has landed, and hands back one whose replacement meshed nothing: the
    // one step every replacement ends in, a settle rebake's (Rebake) and a finer tier's (Retier) alike. Under the gate.
    private void FinishReplacements() {
        var settles = Library.Settles;

        for (var at = (m_replacing.Count - 1); (at >= 0); at--) {
            var (group, index) = m_replacing[at];

            if ((index >= m_groups.Count) || !ReferenceEquals(objA: m_groups[index], objB: group) || (group.Replaces is not { } replaced)) {
                m_replacing.RemoveAt(index: at);
                continue;
            }
            if (!group.Done) { continue; }
            m_replacing.RemoveAt(index: at);
            if (group.Mesh is null) {
                // The replaced group keeps drawing: a settle rebake is not tried again at this pose, a finer tier not until
                // the demand asks another.
                if (group.Retiering) { replaced.RetierRefused = true; } else { replaced.Blocked = true; }
                Replace(group: replaced, index: index);
                m_productRevision++;
                if (!group.Retiering) { settles.Count(kind: SdfMeshSettle.Refused); }
                continue;
            }
            // The landed mesh replaces the drawn one: the replaced group retires here, its one place, and releases its mesh
            // once its last work ends.
            m_retiredGroups.Add(item: replaced);
            replaced.Retired = true;
            group.Replaces = null;
            if (group.Retiering) {
                if (group.Tier > replaced.Tier) { m_coarsened++; } else { m_retiers++; }
                if (group.Stored) { m_retiersReused++; }
                group.RetierDeltaBytes = 0L;
                m_worstOwedFrames = Math.Max(val1: m_worstOwedFrames, val2: group.OwedFrames);
                group.OwedFrames = 0;
                group.Retiering = false;
                continue;
            }
            settles.Count(kind: SdfMeshSettle.Done);
            if (group.Stored) { settles.Count(kind: SdfMeshSettle.Reused); }
        }
    }
    // Measures every group's demanded tier against the one it draws, counts the debt, and plans again at the demanded
    // tier each landed group that has owed a finer one for SdfMeshTier.HoldFrames frames, the finest demand first, at most
    // SdfMeshTier.MaxInFlight at once: the replacement bakes (or lands from the library) while the coarser mesh draws.
    // Detail is budgeted by demand inside the mesh region's budget (RegionBudgetBytes). A finer tier starts only while
    // the bytes the frame's draws name, with those the finer replacements in flight are expected to add, leave it room.
    // What a replacement adds is known once it lands, so the expectation is the surface's scaling
    // (SdfMeshTier.BytesAt) held to one part in SdfMeshTier.ReplacementShare of the budget, and the pressure below is
    // read from the bytes the draws name, never from an expectation. The budget presses while the draws name more than
    // it holds, or a finer tier waits and they name within one replacement of it: the groups drawing a finer tier than
    // their demand asks (spare) are then planned again at the demanded one, the largest saving first, until the draws
    // are expected two replacements under the budget. When it has pressed for SdfMeshTier.HoldFrames frames with no
    // spare group left, landing or waiting its hold, the demand itself is past the budget and every demand is coarsened
    // half a rung (m_bias), which makes the groups farthest past their rung spare. The bias is taken back half a rung
    // once the bytes the draws would name with every demand half a rung finer and every spare group coarsened have
    // stayed under that same mark, with nothing owed or in flight, for the quiet wait. With room to spare a coarser tier
    // demanded changes nothing: the finer mesh stays, so a viewer who comes back finds it. Under the gate, on the
    // frame's thread.
    private void Retier(SdfFrame frame) {
        var budget = RegionBudgetBytes;
        var replacement = (budget / SdfMeshTier.ReplacementShare);
        var target = (budget - (2L * replacement));
        var named = m_regionNamedBytes;
        var bias = m_bias;
        var inFlight = 0;
        var coarsening = 0;
        // The bytes the draws name once every finer replacement in flight has landed, which another must fit beside,
        // and the bytes the coarser ones in flight free.
        var filled = named;
        var freed = 0L;

        foreach (var (group, _) in m_replacing) {
            if (!group.Retiering) { continue; }
            inFlight++;
            if (group.RetierDeltaBytes > 0L) {
                filled = filled.AddSaturating(right: group.RetierDeltaBytes);
            } else {
                coarsening++;
                freed -= group.RetierDeltaBytes;
            }
        }

        var candidates = m_retierCandidates;
        var spare = m_spareCandidates;
        var owed = 0;
        var error = 0f;
        var frames = 0;
        var spares = 0;
        var sparesWaiting = 0;
        // The bytes the draws would name with every demand half a rung finer and every spare group coarsened.
        var finer = 0L;

        candidates.Clear();
        spare.Clear();
        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (group.Retired || !group.HasDemandBox) { continue; }

            var pixel = MeasurePixel(group: group);
            var demanded = SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: TierOf(pixel: pixel));

            if (demanded != group.DemandedTier) {
                group.DemandedTier = demanded;
                group.RetierRefused = false;
            }
            // The tier a frame draws is the replaced group's while a replacement is still to land.
            var shown = (group.Replaces ?? group);
            var resident = shown.Tier;

            if ((bias > 0) && (shown.Mesh is { } mesh)) {
                finer = finer.AddSaturating(right: SdfMeshTier.BytesAt(
                    bytes: SdfMeshRegion.PlacedBytesOf(mesh: mesh),
                    from: resident,
                    to: SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: SdfMeshTier.Of(pixel: SdfMeshTier.Biased(halfRungs: (bias - 1), pixel: pixel)))
                ));
            }
            if (demanded >= resident) {
                group.OwedFrames = 0;
                if ((demanded == resident) || (group.Mesh is not { } drawn)) {
                    group.SpareFrames = 0;
                    continue;
                }
                spares++;
                group.SpareFrames++;
                if (!group.Done || (group.Replaces is not null) || group.RetierRefused) { continue; }
                if (group.SpareFrames < SdfMeshTier.HoldFrames) {
                    sparesWaiting++;
                    continue;
                }

                var bytes = SdfMeshRegion.PlacedBytesOf(mesh: drawn);

                spare.Add(item: ((bytes - SdfMeshTier.BytesAt(bytes: bytes, from: resident, to: demanded)), index, demanded));
                continue;
            }
            group.SpareFrames = 0;
            group.OwedFrames++;
            owed++;
            error = MathF.Max(x: error, y: (SdfMeshTier.Cell(tier: resident) / pixel));
            frames = Math.Max(val1: frames, val2: group.OwedFrames);
            if (!group.Done || (group.Mesh is null) || (group.Replaces is not null) || group.RetierRefused || (group.OwedFrames < SdfMeshTier.HoldFrames)) { continue; }
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

        foreach (var (_, index, tier) in candidates) {
            var old = m_groups[index];
            var bytes = SdfMeshRegion.PlacedBytesOf(mesh: old.Mesh!);
            var delta = Math.Min(val1: replacement, val2: (SdfMeshTier.BytesAt(bytes: bytes, from: old.Tier, to: tier) - bytes));

            // A finer tier the budget has no room for waits, the finest demand first in line for what is freed.
            if ((heldBack > 0) || (delta > (budget - filled))) {
                heldBack++;
                continue;
            }
            if (inFlight >= SdfMeshTier.MaxInFlight) { break; }
            if (Retier(delta: delta, frame: frame, index: index, old: old, tier: tier)) { inFlight++; }
            // One the library held has landed, and the next frame's draws name it.
            filled = filled.AddSaturating(right: delta);
            replaced = true;
        }
        m_tiersHeldBack = heldBack;
        if ((named > budget) || ((heldBack > 0) && (named > (budget - replacement)))) {
            var expected = (named - freed);

            m_biasQuiet = 0;
            spare.Sort(comparison: static (left, right) => ((right.Saving.CompareTo(value: left.Saving) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index)));
            foreach (var (saving, index, tier) in spare) {
                if ((inFlight >= SdfMeshTier.MaxInFlight) || (expected <= target)) { break; }
                if (Retier(delta: -saving, frame: frame, index: index, old: m_groups[index], tier: tier)) { inFlight++; }
                // One the library held has landed, and its saving shows in the next frame's draws.
                coarsening++;
                expected -= saving;
                replaced = true;
            }
            if ((spare.Count > 0) || (sparesWaiting > 0) || (coarsening > 0)) {
                m_biasPressed = 0;
            } else if ((++m_biasPressed >= SdfMeshTier.HoldFrames) && (bias < SdfMeshTier.MaxBias)) {
                Volatile.Write(location: ref m_bias, value: (bias + 1));
                m_biasRaised++;
                m_biasPressed = 0;
                // A step up soon after a step down is a bounce: the next step down waits twice as long.
                if (m_biasSinceLowered < m_biasWait) { m_biasWait = ((int)Math.Min(val1: (2L * m_biasWait), val2: (1L << 20))); }
                m_biasSinceLowered = int.MaxValue;
            }
        } else {
            m_biasPressed = 0;
            if ((bias > 0) && (owed == 0) && (inFlight == 0) && (finer <= target)) {
                if (++m_biasQuiet >= m_biasWait) {
                    Volatile.Write(location: ref m_bias, value: (bias - 1));
                    m_biasLowered++;
                    m_biasQuiet = 0;
                    m_biasSinceLowered = 0;
                }
            } else {
                m_biasQuiet = 0;
            }
        }
        if (m_biasSinceLowered < int.MaxValue) { m_biasSinceLowered++; }
        if (replaced) { m_productRevision++; }
    }
    // Plans a group again at another tier, as Plan would plan it at this frame, and starts it in the group's place: the
    // library's mesh of that geometry lands at once, and otherwise the group bakes (or loads from the store) while the
    // mesh it replaces keeps drawing; true when the replacement is still to land. The delta is the bytes of mesh region
    // the replacement is expected to add.
    private bool Retier(SdfFrame frame, int index, Group old, int tier, long delta) {
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

        // A dynamic slot no active instance rides any more plans nothing; the next program drops the group.
        if (ReferenceEquals(objA: group, objB: old)) { return false; }
        group.Retiering = true;
        group.RetierDeltaBytes = delta;
        group.OwedFrames = old.OwedFrames;
        group.DemandedTier = old.DemandedTier;
        return StartReplacement(group: group, index: index, old: old);
    }
    // Starts a replacement planned for a landed group in that group's place: the library's mesh of its geometry lands at
    // once, and otherwise it bakes (or loads from the store) while the mesh it replaces keeps drawing; true when the
    // replacement is still to land. FinishReplacements retires the replaced group once it does. Under the gate.
    private bool StartReplacement(Group group, Group old, int index) {
        group.TakeDemandBox();
        group.Replaces = old;
        Replace(group: group, index: index);
        m_replacing.Add(item: (group, index));
        group.Identity = Identity.Value;
        if (Library.TryTake(geometry: group.Sealed, identity: group.Identity, materials: group.MaterialOrder, mesh: out var baked)) {
            group.Mesh = baked;
            group.Done = true;
            group.Stored = true;
            return false;
        }

        group.Mesh = old.Mesh?.Acquire();
        _ = Begin(group: group);

        return true;
    }
}
