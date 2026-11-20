using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What a reload's preparation decided off the tick: the source revision it was cut from and the live
/// document it was merged over, the next revision, the merged candidate and the rows the source won from the live
/// document, and how the candidate installs.</summary>
/// <remarks>The install takes it only while the authority still holds that revision and that live document; a reload
/// whose live document moved before its turn (a mutation that landed between) is merged again on the tick.</remarks>
/// <param name="Revision">The revision the delta was cut from.</param>
/// <param name="Live">The live document the candidate was merged over.</param>
/// <param name="Next">The next revision.</param>
/// <param name="Candidate">The merged candidate.</param>
/// <param name="Conflicts">The path of every row and value both the source and the live document changed.</param>
/// <param name="Kind">How the candidate installs (<see cref="WorldDocument.ReloadInstallKindOf"/>).</param>
/// <param name="Rows">The state rows a value install loads.</param>
/// <param name="Validated">Whether the candidate was validated, as its install kind needs, without an admission to
/// carry: a value or presentation install's validation compiles nothing.</param>
public sealed record WorldReloadPreparation(WorldDefinition Revision, WorldDefinition Live, WorldDefinition Next, WorldDefinition Candidate, IReadOnlyList<string> Conflicts,
    WorldInstallKind Kind, IReadOnlyList<string> Rows, bool Validated) {
    internal byte[]? JournalPatch { get; init; }
}

/// <summary>What a whole-document rebuild's candidate alone decides, captured from the authority when the rebuild is
/// submitted and computed from that capture: a reload delta's next revision, reconstructed over the source revision the
/// authority holds, merged over the live document and classified by what it changes; the cross-document neighbour proof;
/// and the validation its install kind needs, whose programs a full install adopts (a
/// <see cref="WorldDefinitionAdmission"/>).</summary>
/// <remarks>Every input is an immutable value or a resolver that reads files and storage, so <see cref="Prepare"/>
/// reads no live authority state and may run on any thread. A live submission runs it off the authority tick and
/// gate (<see cref="WorldRebuildPreparations"/>); the replay drive and a history seek run it inline. Either way the
/// result is a buffered <see cref="WorldPendingOp.Rebuild"/> that installs only through
/// <see cref="WorldDocument.ApplyRebuild"/> at a tick boundary, which merges a reload again when the revision or the
/// live document it was prepared against has moved.</remarks>
/// <param name="Request">The submitted request.</param>
/// <param name="Principal">The acting principal.</param>
/// <param name="ConnectionId">The submitting envelope's connection id.</param>
/// <param name="CorrelationId">The submitting envelope's correlation id.</param>
/// <param name="ExpectedContentHash">The replay pin, when the submitter is a replay drive.</param>
/// <param name="Operator">Whether the principal was the operator when the rebuild was submitted.</param>
/// <param name="Revision">The source revision the authority held when the rebuild was submitted.</param>
/// <param name="Live">The live document when the rebuild was submitted.</param>
/// <param name="Neighbours">The proof transport for the candidate's cross-document claims.</param>
/// <param name="Machines">The machine catalog the authority's machine host validates against.</param>
internal sealed record WorldRebuildPreparation(WorldRebuildRequest Request, Principal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash, bool Operator, WorldDefinition Revision, WorldDefinition Live, IWorldNeighbourResolver? Neighbours, IMachineValidationCatalog Machines) {
    /// <summary>Reconstructs, merges and validates the candidate.</summary>
    /// <returns>The buffered op: the request carrying its preparation and admission, or the refusal preparation decided.</returns>
    internal WorldPendingOp.Rebuild Prepare() {
        var request = Request;
        string? failure = null;
        WorldDefinitionAdmission? admission = null;
        WorldReloadPreparation? reload = null;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Only this authority reconstructs the wire delta. ApplyRebuild merges it again when the revision or the live
        // document moved before its turn.
        if (request.Delta is { } delta) {
            if (!Operator) {
                failure = "world.reload requires the operator";
            } else if (!delta.TryReconstruct(Revision, out var next, out var deltaReason) ||
                !delta.TryMergeOver(Revision, next, Live, out var candidate, out var conflicts, out deltaReason)) {
                failure = deltaReason;
            } else {
                var kind = WorldDocument.ReloadInstallKindOf(candidate: candidate, live: Live, rows: out var rows);

                reload = new WorldReloadPreparation(Candidate: candidate, Conflicts: conflicts, Kind: kind, Live: Live, Next: next, Revision: Revision, Rows: rows, Validated: false) {
                    JournalPatch = kind == WorldInstallKind.Nothing ? null : delta.AcceptedPatch(Live, candidate)
                };
            }
        }

        var reconstructed = System.Diagnostics.Stopwatch.GetTimestamp();

        if (failure is null) {
            if (reload is { } prepared) {
                // A value or presentation install validates what it may change and compiles nothing; a full install
                // admits the candidate once, keeping its programs.
                switch (prepared.Kind) {
                    case WorldInstallKind.Nothing:
                        break;
                    case WorldInstallKind.Value:
                        if (!WorldDefinitionValidator.TryValidateTouchedStateRows(prepared.Candidate, prepared.Rows, out var rowReason)) {
                            failure = rowReason;
                        } else {
                            reload = prepared with { Validated = true };
                        }
                        break;
                    case WorldInstallKind.Presentation:
                        if (!WorldDefinitionValidator.TryValidateChange(after: prepared.Candidate, before: Live, compilation: out _, machines: Machines, neighbours: Neighbours, proveNeighbours: true, reason: out var presentationReason)) {
                            failure = $"cross-document load proof failed before enqueue — {presentationReason}";
                        } else {
                            reload = prepared with { Validated = true };
                        }
                        break;
                    default:
                        if (!WorldDefinitionValidator.TryAdmit(prepared.Candidate, Machines, Neighbours, out admission, out var fullReason)) {
                            failure = $"cross-document load proof failed before enqueue — {fullReason}";
                        }
                        break;
                }
            } else if ((request.Definition is { } supplied) &&
                !WorldDefinitionValidator.TryAdmit(supplied, Machines, Neighbours, out admission, out var proofReason)) {
                failure = $"cross-document load proof failed before enqueue — {proofReason}";
            }
        }

        return Op(request: request, failure: failure, admission: admission, reload: reload) with {
            AdmitMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: reconstructed).TotalMilliseconds,
            ReconstructMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: started, endingTimestamp: reconstructed).TotalMilliseconds,
        };
    }
    /// <summary>The buffered op for a preparation that could not complete.</summary>
    /// <param name="failure">Why it did not.</param>
    /// <returns>The op ApplyRebuild refuses by that reason.</returns>
    internal WorldPendingOp.Rebuild Refused(string failure) => Op(request: Request, failure: failure, admission: null, reload: null);

    private WorldPendingOp.Rebuild Op(WorldRebuildRequest request, string? failure, WorldDefinitionAdmission? admission, WorldReloadPreparation? reload) => new(
        Admission: admission,
        ConnectionId: ConnectionId,
        CorrelationId: CorrelationId,
        ExpectedContentHash: ExpectedContentHash,
        PreparationFailure: failure,
        Principal: Principal,
        Reload: reload,
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
