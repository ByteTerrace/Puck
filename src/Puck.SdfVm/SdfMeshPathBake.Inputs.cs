using System.Diagnostics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfMeshPathBake {
    // Lists the groups whose geometry reads a frame input (SdfMeshInputs), after Restart planned the groups: a sampled
    // field's snapshot or an operand's snapped pose.
    private void ListReaders() {
        m_inputReaders.Clear();
        m_operandReaders = 0;
        for (var index = 0; (index < m_groups.Count); index++) {
            var geometry = m_groups[index].Geometry;

            if (geometry.ReadsOperands) { m_operandReaders++; }
            if (geometry.ReadsSampled || geometry.ReadsOperands) { m_inputReaders.Add(item: index); }
        }
    }
    // Plans again, at a frame's inputs, the groups of the same program whose geometry hashed an input the frame no longer
    // holds: a snapshot its baked sampled set replaced, or an operand standing a step from where the geometry snapped it
    // (SdfMeshGeometryBuilder.OperandsMatch, one snap and compare an operand). Only a reader can (m_inputReaders), so the
    // work is the readers', never the whole plan's, and a frame whose sampled set stands visits only the operands'
    // readers. A reader still baking older inputs keeps baking them, and the newest plan waits as its successor, which the
    // frame after it lands starts (Promote): one bake of a group in flight, the latest inputs winning. A reader whose
    // successor already holds the frame's inputs is not planned again.
    private void Revisit(SdfFrame frame) {
        var restarted = Stopwatch.GetTimestamp();
        var sampled = frame.Sampled.Baked;
        var transforms = frame.DynamicTransforms;
        var resampled = !ReferenceEquals(objA: sampled, objB: m_sampled);

        if (resampled) {
            m_sampled = sampled;
            m_resamples++;
            // A slice samples every baked handle.
            m_slice?.Reset();
        }

        List<Group>? stale = null;
        List<int>? indices = null;

        foreach (var index in m_inputReaders) {
            var group = m_groups[index];
            var geometry = group.Geometry;

            if (!resampled && !geometry.ReadsOperands) { continue; }
            if (resampled) { m_resampleGroups++; }
            if (Holds(geometry: geometry, sampled: sampled, transforms: transforms)) {
                // The inputs moved back to the ones the group bakes: a newer plan waiting on it is no longer newer.
                DropSuccessor(group: group);
                continue;
            }
            if ((group.Successor is { } successor) && Holds(geometry: successor.Geometry, sampled: sampled, transforms: transforms)) { continue; }
            (stale ??= []).Add(item: group);
            (indices ??= []).Add(item: index);
        }
        if (stale is null) { return; }

        var plans = new List<Group>(collection: stale);

        _ = SdfMeshBakeGroup.Refresh(
            create: static (selector, dynamic) => new Group(dynamic: dynamic, selector: selector),
            farDistance: frame.FarDistance,
            groups: plans,
            program: m_program!,
            sampled: sampled,
            stale: group => !group.Geometry.OperandsMatch(transforms: transforms),
            tier: Tiers(transforms: transforms),
            transforms: transforms
        );

        var started = new PlaceCounts();
        var replaced = false;
        var leaders = Leaders();
        var counts = Library.Inputs;

        for (var at = 0; (at < plans.Count); at++) {
            var old = stale[at];
            var plan = plans[at];

            if (ReferenceEquals(objA: old, objB: plan)) { continue; }
            if (!old.Geometry.OperandsMatch(transforms: transforms)) {
                m_operandReplans++;
                counts.Count(kind: SdfMeshInputs.Operand);
            } else {
                counts.Count(kind: SdfMeshInputs.Resampled);
            }
            if (!old.Done) {
                if (old.Successor is null) {
                    m_successors++;
                } else {
                    m_coalesced++;
                    counts.Count(kind: SdfMeshInputs.Coalesced);
                }
                old.Successor = plan;
                continue;
            }
            replaced |= Succeed(identity: Identity.Value, index: indices![at], leaders: leaders, old: old, plan: plan, started: ref started);
        }
        BeginOrphans(indices: m_inputReaders, started: ref started);
        BeginAccounts(cause: (resampled ? "resample" : "operand"), fresh: false, instances: frame.Program.Instances.Count, restarted: restarted, started: started);
        if (replaced) { m_productRevision++; }
    }
    // Whether a geometry holds a frame's inputs: the snapshots its sampled set carries and its operands' steps.
    private static bool Holds(SdfMeshGeometryBuilder geometry, SdfSampledSet sampled, IReadOnlyList<DynamicTransform> transforms) =>
        (geometry.SampledMatches(sampled: sampled) && geometry.OperandsMatch(transforms: transforms));
}
