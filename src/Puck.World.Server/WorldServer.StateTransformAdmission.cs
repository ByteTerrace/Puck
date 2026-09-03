using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    private bool TryAdmitCompleteMutation(WorldMutation mutation, bool preMetered, out WorldMutationAdmission admission) {
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
