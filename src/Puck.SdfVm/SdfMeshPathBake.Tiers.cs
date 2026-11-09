using System.Numerics;
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

    /// <summary>Returns the tier a plan bakes each group at: the coarsest whose cell stays under half a display pixel at
    /// the group's planned box from the nearest demand point (<see cref="SdfMeshTier.Of"/>, <see cref="SdfMeshDemand.PixelAt(SdfMeshBakeGroup, IReadOnlyList{DynamicTransform})"/>),
    /// a dynamic group's box carried into world space by its slot's transform among <paramref name="transforms"/> and
    /// its pixel read in the slot's units. The plan holds it to the rungs that change the group's mesh. A plan made on a
    /// build's thread can read a frame's demand points mid-update; a tier so misjudged is corrected once the group lands
    /// (<see cref="Retier(SdfFrame)"/>).</summary>
    /// <param name="transforms">The dynamic transforms the plan is made at.</param>
    /// <returns>The function a plan asks each group's tier of.</returns>
    private Func<SdfMeshBakeGroup, int> Tiers(IReadOnlyList<DynamicTransform> transforms) =>
        (PlannedTier ?? (group => SdfMeshTier.Of(pixel: Demand.PixelAt(group: group, transforms: transforms))));

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
    private float MeasurePixel(Group group) {
        if (CopiesOf(group: group) is not { } copies) {
            var (center, half) = WorldBox(group: group);
            var units = ((group.Dynamic && (group.Slot < m_demandTransforms.Count)) ? SdfMeshGeometryBuilder.UnitsOf(transform: m_demandTransforms[group.Slot]) : 1f);

            return (Demand.PixelAt(center: center, half: half) / units);
        }

        var finest = float.PositiveInfinity;

        foreach (var index in copies) {
            var matrix = m_demandCopies[index].Matrix;
            var (center, half) = TransformDemandBox(group: group, matrix: matrix);
            var units = MathF.Max(x: 1.0e-6f, y: new Vector3(x: matrix.M11, y: matrix.M12, z: matrix.M13).Length());

            finest = MathF.Min(x: finest, y: (Demand.PixelAt(center: center, half: half) / units));
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
                m_retiers++;
                if (group.Stored) { m_retiersReused++; }
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
    // SdfMeshTier.MaxInFlight at once: the replacement bakes (or lands from the library) while the coarser mesh draws. A
    // coarser tier demanded changes nothing: the finer mesh stays until the library evicts it, and a restart then plans
    // the group at the tier the view asks. Under the gate, on the frame's thread.
    private void Retier(SdfFrame frame) {
        var inFlight = 0;

        foreach (var (group, _) in m_replacing) { inFlight += (group.Retiering ? 1 : 0); }

        var candidates = m_retierCandidates;
        var owed = 0;
        var error = 0f;
        var frames = 0;

        candidates.Clear();
        for (var index = 0; (index < m_groups.Count); index++) {
            var group = m_groups[index];

            if (group.Retired || !group.HasDemandBox) { continue; }

            var pixel = MeasurePixel(group: group);
            var demanded = SdfMeshTier.Clamp(coarsest: group.CoarsestTier, finest: group.FinestTier, tier: SdfMeshTier.Of(pixel: pixel));

            if (demanded != group.DemandedTier) {
                group.DemandedTier = demanded;
                group.RetierRefused = false;
            }
            // The tier a frame draws is the replaced group's while a replacement is still to land.
            var resident = (group.Replaces ?? group).Tier;

            if (demanded >= resident) {
                group.OwedFrames = 0;
                continue;
            }
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
        if ((candidates.Count == 0) || (inFlight >= SdfMeshTier.MaxInFlight)) { return; }
        candidates.Sort(comparison: static (left, right) => ((left.Pixel.CompareTo(value: right.Pixel) is var order and not 0) ? order : left.Index.CompareTo(value: right.Index)));

        var replaced = false;

        foreach (var (_, index, tier) in candidates) {
            if (inFlight >= SdfMeshTier.MaxInFlight) { break; }
            if (Retier(frame: frame, index: index, old: m_groups[index], tier: tier)) { inFlight++; }
            replaced = true;
        }
        if (replaced) { m_productRevision++; }
    }
    // Plans a group again at a finer tier, as Plan would plan it at this frame, and starts it in the group's place: the
    // library's mesh of that geometry lands at once, and otherwise the group bakes (or loads from the store) while the
    // coarser mesh it replaces keeps drawing; true when the replacement is still to land.
    private bool Retier(SdfFrame frame, int index, Group old, int tier) {
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
