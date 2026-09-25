using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>Buffers a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// for the next <see cref="WorldServer.Step"/> (drained before intents). Retains the submitting envelope's
    /// connection/correlation identity — see <see cref="EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="request">The rebuild request.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="expectedContentHash">Replay only: the CAS content hash a recorded tape entry pins. When set,
    /// <see cref="ApplyRebuild"/> compares it against the hash it computes for this drive's own resolved candidate
    /// (its own base for Reset, a fresh re-read of <see cref="WorldRebuildRequest.PathHint"/> for Load/Reload) and
    /// refuses by name on a mismatch, before any other guard runs. <see langword="null"/> (the default) is the live
    /// path — nothing to compare against, since the live drive is what establishes the hash a later recording pins.
    /// <see cref="WorldReplaySnapshot.Drive"/> is the one caller that ever passes a non-null value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    internal void EnqueueRebuild(WorldRebuildRequest request, Principal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) {
        ArgumentNullException.ThrowIfNull(argument: request);

        string? preparationFailure = null;

        // A carried document is available at submission time, outside Step. Prove its neighbour claims here and carry
        // any refusal into the ordered tick-boundary decision; ApplyRebuild repeats only document-local checks.
        var rebuildNeighbours = ((request.PathHint is { } candidatePath)
            ? ResolveRebuildNeighbours(path: candidatePath)
            : Host.Neighbours
        );

        if (
            (request.Definition is { } supplied) &&
            !WorldDefinitionValidator.TryValidate(
            definition: supplied,
            neighbours: rebuildNeighbours,
            reason: out var proofReason
        )
        ) {
            preparationFailure = $"cross-document load proof failed before enqueue — {proofReason}";
        }

        m_pending.Enqueue(item: new WorldPendingOp.Rebuild(
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            ExpectedContentHash: expectedContentHash,
            PreparationFailure: preparationFailure,
            Principal: principal,
            Request: request
        ));
    }

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
