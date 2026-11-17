using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // Live Load/Reload submissions still preparing off the tick, taken into m_pending at the next drain.
    private readonly WorldRebuildPreparations m_preparations = new();

    /// <summary>Gets a task that completes once every rebuild preparation started so far has finished; a finished one
    /// enters the buffered ops at the next drain.</summary>
    internal Task RebuildPreparations => m_preparations.Completion;

    /// <summary>Buffers a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// for the next <see cref="WorldServer.Step"/> (drained before intents), preparing its candidate inline. Retains the
    /// submitting envelope's connection/correlation identity — see <see cref="EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="request">The rebuild request.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="expectedContentHash">Replay only: the CAS content hash a recorded tape entry pins. When set,
    /// <see cref="ApplyRebuild"/> compares it against the hash it computes for this drive's own resolved candidate
    /// (its own base for Reset, a fresh re-read of a file <see cref="WorldRebuildRequest.Origin"/> for Load/Reload) and
    /// refuses by name on a mismatch, before any other guard runs. <see langword="null"/> (the default) is the live
    /// path — nothing to compare against, since the live drive is what establishes the hash a later recording pins.
    /// <see cref="WorldReplaySnapshot.Drive"/> is the one caller that ever passes a non-null value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    internal void EnqueueRebuild(WorldRebuildRequest request, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) {
        ArgumentNullException.ThrowIfNull(argument: request);

        m_pending.Enqueue(item: CaptureRebuild(
            connectionId: connectionId,
            correlationId: correlationId,
            expectedContentHash: expectedContentHash,
            principal: principal,
            request: request
        ).Prepare());
    }
    // A live submission. A Load or Reload carries a candidate whose reconstruction and validation read only the
    // capture, so a host with workers prepares it on the thread pool and the tick takes it when it is ready; the tick
    // never waits for it. A Reset's candidate is the base as it stands at apply, so it has nothing to prepare.
    private void SubmitRebuild(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId) {
        if ((request.Kind == WorldRebuildKind.Reset) || (Host.Jobs is not { Serial: false })) {
            EnqueueRebuild(
                connectionId: connectionId,
                correlationId: correlationId,
                principal: principal,
                request: request
            );
            return;
        }

        m_preparations.Start(preparation: CaptureRebuild(
            connectionId: connectionId,
            correlationId: correlationId,
            expectedContentHash: null,
            principal: principal,
            request: request
        ));
    }
    // Everything a preparation reads, taken from the authority now: the installed declarations a delta names, their
    // version, the operator check, and the proof transport for the candidate's path. A delta names the declarations
    // it was diffed against, which an install since may have replaced while its sender still holds them; any other
    // request, or a base nothing holds any longer, is prepared against the live definition (a delta then refuses as
    // stale).
    private WorldRebuildPreparation CaptureRebuild(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string? expectedContentHash) {
        var named = ((request.Delta is { } delta) ? Installed(version: delta.BaseVersion) : null);

        return CaptureRebuild(
            basis: (named ?? m_definition),
            connectionId: connectionId,
            correlationId: correlationId,
            expectedContentHash: expectedContentHash,
            principal: principal,
            request: request,
            version: ((named is null) ? Host.DocumentVersion : request.Delta!.BaseVersion)
        );
    }
    private WorldRebuildPreparation CaptureRebuild(WorldRebuildRequest request, Principal principal, int connectionId, long correlationId, string? expectedContentHash, WorldDefinition basis, WorldDocumentVersion version) => new(
        Basis: basis,
        ConnectionId: connectionId,
        CorrelationId: correlationId,
        ExpectedContentHash: expectedContentHash,
        Machines: Host.Machines.ValidationCatalog,
        Neighbours: ((request.Origin is WorldRebuildOrigin.File { Path: var candidatePath })
            ? ResolveRebuildNeighbours(path: candidatePath)
            : Host.Neighbours),
        Operator: Host.Grants.IsOperator(principal),
        Principal: principal,
        Request: request,
        Version: version
    );
    /// <summary>Moves every finished rebuild preparation, oldest first, into the buffered ops. Runs at the start of
    /// each drain, so a prepared rebuild installs at a tick boundary.</summary>
    internal void TakePreparedRebuilds() {
        while (m_preparations.TryTake(op: out var op)) {
            m_pending.Enqueue(item: op);
        }
    }
    /// <summary>Answers every rebuild still preparing with a refusal, as <see cref="RefusePending"/> answers a buffered
    /// one.</summary>
    /// <param name="reason">Why the rebuilds will not install.</param>
    internal void RefusePreparations(string reason) => m_preparations.Refuse(refuse: preparation => RejectRebuild(
        connectionId: preparation.ConnectionId,
        correlationId: preparation.CorrelationId,
        reason: reason,
        verb: RebuildVerb(kind: preparation.Request.Kind)
    ));

    // A replay drive's Load/Reload carries no document, so the pinned path is re-read through the door the live verb
    // read it through: drawn for this instance and admitted on its own facts, since the tick path reaches no transport.
    private WorldDefinition RereadForReplay(string verb, string path, out string contentHash) {
        if (!WorldDefinitionLoader.TryLoadFileForAdmission(
            admission: out var reread,
            catalog: Host.Machines.ValidationCatalog,
            contentHash: out contentHash,
            documents: Host.RebuildDocuments,
            instanceIdentity: Host.InstanceIdentity,
            path: path,
            proveNeighbours: false,
            reason: out var reason
        )) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: $"{verb}: cannot re-read '{path}' for replay — {reason}");
        }

        return reread!.Definition;
    }

    /// <summary>Resolves the proof transport appropriate to one replacement document path.</summary>
    internal IWorldNeighbourResolver? ResolveRebuildNeighbours(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return (Host.RebuildNeighbours?.Invoke(arg: path) ?? Host.Neighbours);
    }
}
