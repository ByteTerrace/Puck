using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool TryAdmitCompleteMutation(WorldMutation mutation, bool preMetered, out WorldMutationAdmission admission) {
        // A batch is admitted member by member, each on its own subject; the first member meters the dispatch budget.
        if (mutation is WorldMutation.Batch batch) {
            admission = default;
            if (batch.Mutations.Count == 0) {
                return false;
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
}
