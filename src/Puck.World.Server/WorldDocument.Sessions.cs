using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // A session acts only while it lives: a principal naming an ended session's epoch, or a session this world never
    // admitted, is refused by name before its payload is read.
    private bool TryRefuseStaleSession(in SubmissionEnvelope envelope, out WorldSubmissionResult refusal) {
        if (
            (envelope.Principal.Kind == PrincipalKind.Session) &&
            !Host.GrantTable.IsLiveSession(principal: envelope.Principal)
        ) {
            refusal = new WorldSubmissionResult.Refusal(
                Code: WorldServer.StaleSessionCode,
                Detail: $"{envelope.Principal.Describe()} is not a live session on this world"
            );

            return true;
        }

        refusal = WorldSubmissionResult.Ack.Instance;

        return false;
    }
}
