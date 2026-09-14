using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

// Resolves the immutable session row while its ordinary command-pump scope is active.
internal sealed class SiloServerLink(WorldSiloHost host) : IPrincipalServerLink {
    private IPrincipalServerLink Link => (((WorldNarrationScope.Current is { } row) && host.Instances.TryGet(
        instance: out var instance,
        name: row
    ) && (instance?.Link is IPrincipalServerLink link))
        ? link
        : throw new InvalidOperationException(message: "No principal-aware World link is bound to this command.")
    );

    public void Query(WorldQuery query, WorldPrincipal principal, Action<QueryAnswer> completion) => Link.Query(
        completion: completion,
        principal: principal,
        query: query
    );
    public void Query(WorldQuery query, Action<QueryAnswer> completion) => throw new NotSupportedException(message: "Silo queries require an explicit acting principal.");
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => Link.SubmitEnvelope(
        payload: payload,
        principal: principal
    );
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId) =>
        Link.SubmitEnvelope(
            operationId: operationId,
            payload: payload,
            principal: principal
        );
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId, Action<WorldSubmissionResult>? completion) =>
        Link.SubmitEnvelope(
            completion: completion,
            operationId: operationId,
            payload: payload,
            principal: principal
        );
    public void SubmitIntent(in IntentSubmission submission) => Link.SubmitIntent(submission: submission);
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => throw new NotSupportedException(message: "Use the authenticated admission door.");
}
