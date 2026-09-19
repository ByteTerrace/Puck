using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    internal bool TryAdmitCompleteMutation(WorldMutation mutation, bool preMetered, out WorldMutationAdmission admission) {
        // A batch is admitted member by member, each on its own subject; the first member meters the dispatch budget.
        if (mutation is WorldMutation.Batch batch) {
            admission = default;
            if (!batch.TryValidateShape(reason: out _)) {
                admission = new WorldMutationAdmission(
                    Budget: 0,
                    DecidingSubject: default,
                    Mask: default,
                    Rule: WorldMutationAdmissionRule.MalformedBatch,
                    Subject: default,
                    Verdict: default
                );
                return false;
            }
            if (batch.ExpectedCells is { } cells) {
                for (var index = 0; (index < cells.Count); index++) {
                    if (!TryAdmitGuardObservation(
                        batch.Principal,
                        cells[index].Row,
                        out admission
                    )) { return false; }
                }
            }
            if (batch.ExpectedDefinition is not null) {
                if (batch.ExpectedStateRows is { } rows) {
                    for (var index = 0; (index < rows.Count); index++) {
                        if (!TryAdmitGuardObservation(
                            batch.Principal,
                            rows[index],
                            out admission
                        )) { return false; }
                    }
                } else {
                    for (var index = 0; (index < m_definition.State.Count); index++) {
                        if (!TryAdmitGuardObservation(
                            batch.Principal,
                            m_definition.State[index].Name,
                            out admission
                        )) { return false; }
                    }
                }
            }
            if (batch.ExpectedInputs is { } inputs) {
                foreach (var row in inputs.StateRows) {
                    if (!TryAdmitGuardObservation(
                        batch.Principal,
                        row,
                        out admission
                    )) { return false; }
                }
            }
            for (var index = 0; (index < batch.Mutations.Count); index++) {
                if (!TryAdmitCompleteMutation(
                    batch.Mutations[index],
                    (preMetered || (index > 0)),
                    out admission
                )) {
                    return false;
                }
            }
            return true;
        }
        var ordinal = WorldMutationKindCatalog.OrdinalOf(mutation: mutation);
        var section = SectionOf(mutation: mutation);

        if (mutation is WorldMutation.TransformState transform) {
            foreach (var row in WorldArenaTransforms.Subjects(transform: transform.Transform)) {
                if (!Host.TryAdmitMutation(
                    mutation.Principal,
                    section,
                    ordinal,
                    GrantSubject.State(name: row),
                    null,
                    false,
                    out admission
                )) {
                    return false;
                }
            }
        }
        return Host.TryAdmitMutation(
            mutation.Principal,
            section,
            ordinal,
            RowScopedEditSubjectOf(mutation: mutation),
            RowScopedMutateSubjectOf(mutation: mutation),
            !preMetered,
            out admission
        );
    }
    private bool TryAdmitGuardObservation(WorldPrincipal principal, string row, out WorldMutationAdmission admission) {
        admission = default;
        if (principal == WorldPrincipal.World) { return true; }
        var subject = GrantSubject.State(name: row);
        var verdict = Host.GrantTable.Allows(
            capability: WorldCapability.Observe,
            principal: principal,
            subject: subject
        );

        if (verdict) { return true; }
        admission = new WorldMutationAdmission(
            Budget: 0,
            DecidingSubject: subject,
            Mask: default,
            Rule: WorldMutationAdmissionRule.ObservationDenied,
            Subject: subject,
            Verdict: verdict
        );
        return false;
    }
}
