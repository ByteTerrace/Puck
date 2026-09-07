using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool TryAdmitCompleteMutation(WorldMutation mutation, bool preMetered, out WorldMutationAdmission admission) {
        // A batch is admitted member by member, each on its own subject; the first member meters the dispatch budget.
        if (mutation is WorldMutation.Batch batch) {
            admission = default;
            if (!batch.TryValidateShape(out _)) {
                admission = new WorldMutationAdmission(WorldMutationAdmissionRule.MalformedBatch, default, default, default, default, 0);
                return false;
            }
            if (batch.ExpectedCells is { } cells) {
                for (var index = 0; index < cells.Count; index++) {
                    if (!TryAdmitGuardObservation(batch.Principal, cells[index].Row, out admission)) { return false; }
                }
            }
            if (batch.ExpectedDefinition is not null) {
                if (batch.ExpectedStateRows is { } rows) {
                    for (var index = 0; index < rows.Count; index++) {
                        if (!TryAdmitGuardObservation(batch.Principal, rows[index], out admission)) { return false; }
                    }
                } else {
                    for (var index = 0; index < m_definition.State.Count; index++) {
                        if (!TryAdmitGuardObservation(batch.Principal, m_definition.State[index].Name, out admission)) { return false; }
                    }
                }
            }
            for (var index = 0; index < batch.Mutations.Count; index++) {
                if (!TryAdmitCompleteMutation(batch.Mutations[index], preMetered || (index > 0), out admission)) {
                    return false;
                }
            }
            return true;
        }
        var ordinal = WorldMutationKindCatalog.OrdinalOf(mutation);
        var section = SectionOf(mutation);
        if (mutation is WorldMutation.TransformState transform) {
            foreach (var row in WorldStateTransforms.Subjects(transform.Transform)) {
                if (!TryAdmitMutation(mutation.Principal, section, ordinal, GrantSubject.State(row), null, false, out admission)) {
                    return false;
                }
            }
        }
        return TryAdmitMutation(mutation.Principal, section, ordinal, RowScopedEditSubjectOf(mutation), RowScopedMutateSubjectOf(mutation), !preMetered, out admission);
    }

    private bool TryAdmitGuardObservation(WorldPrincipal principal, string row, out WorldMutationAdmission admission) {
        admission = default;
        if (principal == WorldPrincipal.World) { return true; }
        var subject = GrantSubject.State(row);
        var verdict = m_grants.Allows(principal, WorldCapability.Observe, subject);
        if (verdict) { return true; }
        admission = new WorldMutationAdmission(WorldMutationAdmissionRule.ObservationDenied, verdict, subject, subject, default, 0);
        return false;
    }
}
