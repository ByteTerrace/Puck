using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Silo;

// Resolves the immutable session row while its ordinary command-pump scope is active.
internal sealed class SiloServerLink(WorldSiloHost host) : IPrincipalServerLink {
    private IPrincipalServerLink Link => WorldNarrationScope.Current is { } row && host.Instances.TryGet(row, out var instance) && instance?.Link is IPrincipalServerLink link
        ? link : throw new InvalidOperationException("No principal-aware World link is bound to this command.");
    public void SubmitIntent(in IntentSubmission submission) => Link.SubmitIntent(submission);
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => Link.SubmitEnvelope(payload, principal);
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId) =>
        Link.SubmitEnvelope(payload, principal, operationId);
    public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal, Guid operationId, Action<WorldSubmissionResult>? completion) =>
        Link.SubmitEnvelope(payload, principal, operationId, completion);
    public void Query(WorldQuery query, WorldPrincipal principal, Action<QueryAnswer> completion) => Link.Query(query, principal, completion);
    public void Query(WorldQuery query, Action<QueryAnswer> completion) => throw new NotSupportedException("Silo queries require an explicit acting principal.");
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => throw new NotSupportedException("Use the authenticated admission door.");
}
