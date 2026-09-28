using Puck.Commands;

namespace Puck.World.Protocol;

/// <summary>A link a console's handlers submit through, which registers each line it mints in its row's table.</summary>
public interface IConsoleServerLink : IPrincipalServerLink {
    /// <summary>Gets the handle of the row this submission reaches, or <see langword="null"/> when that row keeps no
    /// console table and nothing registers.</summary>
    WorldDeferredVerbRow? Row { get; }
}
/// <summary>
/// The link a console's handlers submit one row's lines through: the row's <see cref="LoopbackTransport"/>, with every
/// submission it mints registered in the console's <see cref="WorldDeferredVerbEchoes"/> under the row and its
/// correlation before the authority applies it, so a verdict that arrives inside the submit (a grant, a screen op)
/// finds its line. A submission through the bare transport (a host's own reload, a replay, a rule) registers
/// nothing, so its verdict neither answers a console line nor counts in <c>wire.errors</c>. Created once per row by
/// <see cref="LoopbackTransport.ForConsole"/>.
/// </summary>
public sealed class WorldConsoleServerLink : IConsoleServerLink {
    private readonly LoopbackTransport m_transport;

    internal WorldConsoleServerLink(LoopbackTransport transport, WorldDeferredVerbRow row) {
        m_transport = transport;
        Row = row;
    }

    /// <inheritdoc cref="IConsoleServerLink.Row"/>
    public WorldDeferredVerbRow Row { get; }

    WorldDeferredVerbRow? IConsoleServerLink.Row => Row;

    /// <inheritdoc/>
    public void Query(WorldQuery query, Action<QueryAnswer> completion) => m_transport.Query(
        completion: completion,
        query: query
    );
    /// <inheritdoc/>
    public void Query(WorldQuery query, Principal principal, Action<QueryAnswer> completion) => m_transport.Query(
        completion: completion,
        principal: principal,
        query: query
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
        completion: null,
        operationId: Guid.Empty,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId) => SubmitEnvelope(
        completion: null,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) => m_transport.SubmitEnvelope(
        completion: completion,
        console: Row,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    /// <inheritdoc/>
    public void SubmitIntent(in IntentSubmission submission) => m_transport.SubmitIntent(submission: in submission);
    /// <inheritdoc/>
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => m_transport.SubmitSession(
        completion: completion,
        request: request
    );
}
