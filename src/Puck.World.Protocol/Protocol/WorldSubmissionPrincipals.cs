using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>Judges the principals a submission payload names — its acting identity, a command's target, a grant's
/// holder, a group member or ownership recipient, a request's or query's subject, a handle's table, and every row of a
/// mutation batch — for a door that must see all of them.</summary>
public static class WorldSubmissionPrincipals {
    private static bool IsSession(Principal principal) => (principal.Kind == PrincipalKind.Session);
    private static bool IsSession(Grantee grantee) => (
        (grantee.Kind == GranteeKind.Principal) &&
        IsSession(principal: grantee.Principal)
    );
    private static bool NamesSession(WorldCommand command) => (IsSession(principal: command.Principal) || command switch {
        WorldCommand.ComposeControl compose => IsSession(principal: compose.TargetPrincipal),
        WorldCommand.DissolveControl dissolve => IsSession(principal: dissolve.TargetPrincipal),
        _ => false,
    });
    private static bool NamesSession(WorldMutation mutation) => (IsSession(principal: mutation.Principal) || mutation switch {
        WorldMutation.Batch batch => batch.Mutations.Any(predicate: NamesSession),
        WorldMutation.UpsertGrant upsert => IsSession(grantee: upsert.Row.Grantee),
        WorldMutation.RemoveGrant remove => IsSession(grantee: remove.Target.Grantee),
        WorldMutation.JoinGroup join => IsSession(principal: join.Member),
        WorldMutation.LeaveGroup leave => IsSession(principal: leave.Member),
        WorldMutation.KickMember kick => IsSession(principal: kick.Member),
        WorldMutation.OfferOwnership offer => IsSession(principal: offer.Recipient),
        _ => false,
    });
    private static bool NamesSession(WorldQuery query) => query switch {
        WorldQuery.GrantAllows allows => IsSession(principal: allows.Principal),
        WorldQuery.GrantHandleMint mint => IsSession(principal: mint.Principal),
        WorldQuery.GrantHandleResolve resolve => IsSession(principal: resolve.Handle.TablePrincipal),
        _ => false,
    };

    /// <summary>Determines whether a payload names an unembodied session principal anywhere. A session belongs to the
    /// world that admitted it and acts only in-process, so a submission arriving from a remote peer or a federated
    /// authority that names one is refused by name at that ingress.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns><see langword="true"/> when a session principal appears in the payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public static bool NamesSession(WorldSubmissionPayload payload) {
        ArgumentNullException.ThrowIfNull(argument: payload);

        return payload switch {
            WorldSubmissionPayload.Command command => NamesSession(command: command.Value),
            WorldSubmissionPayload.Grant grant => IsSession(grantee: grant.Value.Grantee),
            WorldSubmissionPayload.Revoke revoke => IsSession(grantee: revoke.Value.Grantee),
            WorldSubmissionPayload.Session session => IsSession(principal: session.Value.Principal),
            WorldSubmissionPayload.Mutation mutation => NamesSession(mutation: mutation.Value),
            WorldSubmissionPayload.Query query => NamesSession(query: query.Value),
            _ => false,
        };
    }
}
