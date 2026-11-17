using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What a whole-document rebuild's candidate alone decides, captured from the authority when the rebuild is
/// submitted and computed from that capture: the reload delta's reconstruction against the installed declarations it
/// names, the cross-document neighbour proof, and the document-local validation whose programs the install adopts
/// (a <see cref="WorldDefinitionAdmission"/>).</summary>
/// <remarks>Every input is an immutable value or a resolver that reads files and storage, so <see cref="Prepare"/>
/// reads no live authority state and may run on any thread. A live submission runs it off the authority tick and
/// gate (<see cref="WorldRebuildPreparations"/>); the replay drive and a history seek run it inline. Either way the
/// result is a buffered <see cref="WorldPendingOp.Rebuild"/> that installs only through
/// <see cref="WorldDocument.ApplyRebuild"/> at a tick boundary, which rechecks the delta's base against the document
/// installed then.</remarks>
/// <param name="Request">The submitted request.</param>
/// <param name="Principal">The acting principal.</param>
/// <param name="ConnectionId">The submitting envelope's connection id.</param>
/// <param name="CorrelationId">The submitting envelope's correlation id.</param>
/// <param name="ExpectedContentHash">The replay pin, when the submitter is a replay drive.</param>
/// <param name="Operator">Whether the principal was the operator when the rebuild was submitted.</param>
/// <param name="Basis">The installed declarations when the rebuild was submitted: what a delta names.</param>
/// <param name="Version">Their authority version.</param>
/// <param name="Neighbours">The proof transport for the candidate's cross-document claims.</param>
/// <param name="Machines">The machine catalog the authority's machine host validates against.</param>
internal sealed record WorldRebuildPreparation(WorldRebuildRequest Request, Principal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash, bool Operator, WorldDefinition Basis, WorldDocumentVersion Version, IWorldNeighbourResolver? Neighbours, IMachineValidationCatalog Machines) {
    /// <summary>Reconstructs and admits the candidate.</summary>
    /// <returns>The buffered op: the request carrying its candidate and admission, or the refusal preparation decided.</returns>
    internal WorldPendingOp.Rebuild Prepare() {
        var request = Request;
        string? failure = null;
        WorldDefinitionAdmission? admission = null;

        // Only this authority reconstructs the wire delta. Its pins stay on the request, so ApplyRebuild rechecks the
        // base against whatever is installed when the op drains.
        if (request.Delta is { } delta) {
            if (!Operator) {
                failure = "world.reload requires the operator";
            } else if (delta.TryPrepare(Basis, Version, out var candidate, out var deltaReason)) {
                request = request with { Definition = candidate };
            } else {
                failure = deltaReason;
            }
        }

        if ((failure is null) && (request.Definition is { } supplied) &&
            !WorldDefinitionValidator.TryAdmit(supplied, Machines, Neighbours, out admission, out var proofReason)) {
            failure = $"cross-document load proof failed before enqueue — {proofReason}";
        }

        return Op(request: request, failure: failure, admission: admission);
    }
    /// <summary>The buffered op for a preparation that could not complete.</summary>
    /// <param name="failure">Why it did not.</param>
    /// <returns>The op ApplyRebuild refuses by that reason.</returns>
    internal WorldPendingOp.Rebuild Refused(string failure) => Op(request: Request, failure: failure, admission: null);

    private WorldPendingOp.Rebuild Op(WorldRebuildRequest request, string? failure, WorldDefinitionAdmission? admission) => new(
        Admission: admission,
        Basis: Basis,
        ConnectionId: ConnectionId,
        CorrelationId: CorrelationId,
        ExpectedContentHash: ExpectedContentHash,
        PreparationFailure: failure,
        Principal: Principal,
        Request: request
    );
}

/// <summary>The live rebuilds an authority is preparing off its tick, in submission order. Each is a
/// <see cref="BackgroundBuild{T}"/> the tick takes at its next drain (<see cref="TryTake"/>), so a finished preparation
/// enters the buffered ops at a tick boundary exactly as a submission arriving then would.</summary>
/// <remarks>Every member runs under the authority gate, which makes the authority the builds' one owner. A rebuild is
/// taken only once every rebuild submitted before it has been, so rebuilds keep their submission order among
/// themselves.</remarks>
internal sealed class WorldRebuildPreparations {
    private readonly Queue<(WorldRebuildPreparation Preparation, BackgroundBuild<WorldPendingOp.Rebuild> Build)> m_builds = new();

    /// <summary>Gets the number of rebuilds still preparing or prepared and not yet taken.</summary>
    internal int Count => m_builds.Count;
    /// <summary>Gets a task that completes once every preparation started so far has finished.</summary>
    internal Task Completion => Task.WhenAll(m_builds.Select(selector: static entry => (entry.Build.Completion ?? Task.CompletedTask)));

    /// <summary>Starts a preparation on the thread pool.</summary>
    /// <param name="preparation">The captured inputs.</param>
    internal void Start(WorldRebuildPreparation preparation) {
        var build = new BackgroundBuild<WorldPendingOp.Rebuild>();

        build.Start(build: _ => preparation.Prepare());
        m_builds.Enqueue(item: (preparation, build));
    }
    /// <summary>Takes the oldest preparation when it has finished.</summary>
    /// <param name="op">The prepared op, or one carrying the failure the preparation threw.</param>
    /// <returns>Whether an op was taken.</returns>
    internal bool TryTake([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldPendingOp.Rebuild? op) {
        if (!m_builds.TryPeek(result: out var head) || !head.Build.TryTake(result: out op, error: out var error)) {
            op = null;
            return false;
        }

        m_builds.Dequeue();
        op ??= head.Preparation.Refused(failure: $"{RebuildPreparationFailedCode}: {error?.Message}");
        return true;
    }
    /// <summary>Cancels every preparation and hands each to <paramref name="refuse"/>, oldest first.</summary>
    /// <param name="refuse">Answers one cancelled preparation's submitter.</param>
    internal void Refuse(Action<WorldRebuildPreparation> refuse) {
        while (m_builds.TryDequeue(result: out var entry)) {
            entry.Build.Cancel();
            refuse(obj: entry.Preparation);
        }
    }

    /// <summary>The named refusal for a preparation that threw.</summary>
    internal const string RebuildPreparationFailedCode = "world.rebuild.preparation-failed";
}
