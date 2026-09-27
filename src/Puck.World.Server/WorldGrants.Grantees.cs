using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldGrants {
    // The grantees no live row may name, whatever the row grants. Each is refused by name so an author learns why.
    private bool RefusesGrantee(WorldGrant grant, out string reason) {
        // The world's own authority is structural — TryAdmitMutation admits it before this table is consulted — so
        // a row naming it would be an inert phantom grant.
        if (grant.Grantee.Principal.Kind == PrincipalKind.World) {
            reason = "the world's own authored program holds no grants — its authority is structural (a rule's effects and a kit's generate effect are the document acting on itself, never an actor submitting); a row here would be accepted and inert";

            return true;
        }

        // A session holds rows only while it lives. A row naming an ended or not-yet-admitted epoch would wait for
        // whichever later session reached that epoch and hand it authority its own admission never granted.
        if (
            (grant.Grantee.Principal.Kind == PrincipalKind.Session) &&
            !IsLiveSession(principal: grant.Grantee.Principal)
        ) {
            reason = $"{grant.Grantee.Describe()} is not a live session on this world — a session's rows are minted by its admission and embodiment and end with it";

            return true;
        }

        // A document holds no LIVE rows either — its grants are read off the owner's document
        // (Server.WorldOwnedWorlds.Decide/TryReadDurableState consult definition.Grants directly), never off this
        // table; a live row here would be budget-less, mask-less, and consulted by nothing.
        // `world.grants document:<id>` echoes the document-authored rows instead.
        if (grant.Grantee.Kind == GranteeKind.Document) {
            reason = "a document holds no LIVE grants — the cross-document durable-state write-back channel reads its rows off the OWNER'S DOCUMENT (world.grant.set authors them, world.grants document:<id> echoes them), so a row here would be accepted and inert";

            return true;
        }

        reason = string.Empty;

        return false;
    }
}
