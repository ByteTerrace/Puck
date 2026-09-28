using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>A link that records every envelope it is handed and each envelope's completion, as a destination world
/// would receive them, so a law answers each verdict itself, in the order it chooses. It stands for the destination's
/// installs too: <see cref="Version"/> is the document the destination last installed, which an applied verdict moves
/// forward and a law delivers to the destination's mirror.</summary>
/// <param name="definition">The destination's document, which answers its population queries.</param>
internal sealed class RecordingLink(WorldDefinition definition) : IServerLink {
    /// <summary>Gets a verdict the destination's grant check refused.</summary>
    public static WorldSubmissionResult Refused { get; } = new WorldSubmissionResult.Refusal(Code: "world.grant.denied", Detail: "the seat may not mutate placements here");

    /// <summary>Gets or sets the destination's activation: a law moves it to stand for another world behind the same
    /// link.</summary>
    public Guid Activation { get; set; } = Guid.NewGuid();
    /// <summary>Gets each submitted envelope's completion, in submission order.</summary>
    public List<Action<WorldSubmissionResult>?> Completions { get; } = [];

    /// <summary>Gets or sets whether the link throws instead of accepting an envelope, as a closed link does.</summary>
    public bool Fails { get; set; }
    /// <summary>Gets or sets what the link answers each envelope with before it returns, or <see langword="null"/> to
    /// leave every verdict to <see cref="Complete"/>.</summary>
    public Func<WorldSubmissionResult>? Inline { get; set; }
    /// <summary>Gets or sets the destination's install ordinal.</summary>
    public long Sequence { get; set; }

    /// <summary>Gets each submitted envelope, in submission order.</summary>
    public List<WorldSubmissionPayload> Submitted { get; } = [];

    /// <summary>Gets the version of the document the destination last installed.</summary>
    public WorldDocumentVersion Version => new(Activation: Activation, Sequence: Sequence);

    /// <summary>Returns a verdict that applied: the destination installs the edit, one past its last install.</summary>
    /// <returns>The verdict.</returns>
    public WorldSubmissionResult Applied() => new WorldSubmissionResult.Mutation(Outcome: new WorldMutationOutcome(
        Actor: Principal.Console,
        AffectedGroupRevision: null,
        Code: "world.mutation.applied",
        Decision: WorldMutationDecision.Applied,
        Detail: "applied",
        DurableWatermark: null,
        OperationId: Guid.NewGuid(),
        PayloadDigest: "digest",
        PersistenceStatus: WorldMutationPersistenceStatus.NotRequested,
        Version: new WorldDocumentVersion(Activation: Activation, Sequence: ++Sequence)
    ));
    /// <summary>Answers one submitted envelope with a verdict.</summary>
    /// <param name="index">The envelope's submission index.</param>
    /// <param name="result">The verdict.</param>
    public void Complete(int index, WorldSubmissionResult result) => Completions[index]?.Invoke(obj: result);
    /// <summary>Returns the placement one submitted envelope upserts.</summary>
    /// <param name="index">The envelope's submission index.</param>
    /// <returns>The placement row.</returns>
    public WorldPlacement Placement(int index) => ((WorldMutation.UpsertPlacement)((WorldSubmissionPayload.Mutation)Submitted[index]).Value).Placement;
    public void Query(WorldQuery query, Action<QueryAnswer> completion) => new SilentLink(definition: definition).Query(completion: completion, query: query);
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
        completion: null,
        operationId: Guid.Empty,
        payload: payload,
        principal: principal
    );
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) {
        if (Fails) {
            throw new InvalidOperationException(message: "the link is closed");
        }

        Submitted.Add(item: payload);
        Completions.Add(item: completion);

        if (Inline is { } verdict) {
            completion?.Invoke(obj: verdict());
        }

        return Submitted.Count;
    }
    /// <summary>Returns the principal one submitted envelope's mutation was composed under.</summary>
    /// <param name="index">The envelope's submission index.</param>
    /// <returns>The principal.</returns>
    public Principal PrincipalOf(int index) => ((WorldSubmissionPayload.Mutation)Submitted[index]).Value.Principal;
    public void SubmitIntent(in IntentSubmission submission) {
    }
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) {
    }
}
/// <summary>Builds the endpoint of a world a seat crosses into: its document delivered, and one active body posed in
/// it under the endpoint's own authority.</summary>
internal static class EditorEndpoints {
    private sealed class Lease : IDisposable {
        public void Dispose() {
        }
    }

    /// <summary>Returns an endpoint of a world named <paramref name="identity"/> under the authority of the same name.</summary>
    /// <param name="identity">The instance and authority name.</param>
    /// <param name="definition">The world's document.</param>
    /// <param name="link">The link its edits are submitted over, whose version the document is delivered at.</param>
    /// <param name="pose">Where its body 0 stands, in its own coordinates.</param>
    /// <returns>The endpoint.</returns>
    public static WorldAuthorityEndpoint Of(string identity, WorldDefinition definition, RecordingLink link, Vector3 pose) => Of(
        definition: definition,
        identity: identity,
        link: ((IServerLink)link),
        pose: pose,
        version: link.Version
    );
    /// <summary>Returns an endpoint of a world named <paramref name="identity"/> whose document is delivered at a given
    /// version.</summary>
    /// <param name="identity">The instance and authority name.</param>
    /// <param name="definition">The world's document.</param>
    /// <param name="link">The link its edits are submitted over.</param>
    /// <param name="version">The version the document is delivered at.</param>
    /// <param name="pose">Where its body 0 stands, in its own coordinates.</param>
    /// <returns>The endpoint.</returns>
    public static WorldAuthorityEndpoint Of(string identity, WorldDefinition definition, IServerLink link, WorldDocumentVersion version, Vector3 pose) => new(
        adjacencies: static () => null,
        clockOwnedHere: false,
        definition: () => definition,
        identity: identity,
        nextInputTick: static () => 2UL,
        observe: sink => {
            sink.DeliverDefinition(
                definition: definition,
                version: version
            );
            sink.DeliverSnapshot(snapshot: new WorldSnapshot(
                Authority: identity,
                EngineTick: 1680UL,
                Entries: new[] { new EntitySnapshot(
                    Active: true,
                    BodyColor: Vector3.One,
                    CatalogRig: 0,
                    Continuity: EntityContinuity.Continuous,
                    Generation: 1,
                    Index: 0,
                    Kit: 0,
                    Look: 0,
                    Orientation: Quaternion.Identity,
                    Position: pose
                ) },
                Revision: 0,
                StepTicks: 1680UL,
                Tick: 1UL
            ));

            return new Lease();
        },
        submissions: link
    );
}
/// <summary>A link that forwards to whichever link it holds now, as a traveler route does when it follows its body onward
/// before the destination's documents reach the traveler's mirror.</summary>
/// <param name="target">The link it forwards to first.</param>
internal sealed class SwitchingLink(IServerLink target) : IServerLink {
    /// <summary>Gets or sets the link every submission goes to now.</summary>
    public IServerLink Target { get; set; } = target;

    public void Query(WorldQuery query, Action<QueryAnswer> completion) => Target.Query(completion: completion, query: query);
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => Target.SubmitEnvelope(payload: payload, principal: principal);
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId) => Target.SubmitEnvelope(operationId: operationId, payload: payload, principal: principal);
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) => Target.SubmitEnvelope(
        completion: completion,
        operationId: operationId,
        payload: payload,
        principal: principal
    );
    public void SubmitIntent(in IntentSubmission submission) => Target.SubmitIntent(submission: in submission);
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => Target.SubmitSession(completion: completion, request: request);
}
/// <summary>A link that forwards to a world's own link and makes two moments of a race observable: a submission entering
/// it, before it reaches the world, and a verdict the world hands back, which it holds on the thread the world answers it
/// on until the law releases it. A world's tick answers its verdicts inside its authority, so a held verdict holds that
/// authority too.</summary>
/// <param name="target">The world's own link.</param>
internal sealed class GatedLink(IServerLink target) : IServerLink {
    private readonly TaskCompletionSource m_released = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private TaskCompletionSource? m_entering;
    private TaskCompletionSource? m_holding;

    /// <summary>Arms the link to signal the next submission that enters it.</summary>
    /// <returns>A task that completes when that submission enters, before it reaches the world.</returns>
    public Task ArmEntering() => (m_entering = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    /// <summary>Arms the link to hold the next verdict the world hands back until <see cref="Release"/>.</summary>
    /// <returns>A task that completes when that verdict is held.</returns>
    public Task ArmHold() => (m_holding = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    /// <summary>Releases a held verdict to its completion, and every later one.</summary>
    public void Release() => m_released.TrySetResult();
    public void Query(WorldQuery query, Action<QueryAnswer> completion) => target.Query(completion: completion, query: query);
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal) => SubmitEnvelope(
        completion: null,
        operationId: Guid.Empty,
        payload: payload,
        principal: principal
    );
    public long SubmitEnvelope(WorldSubmissionPayload payload, Principal principal, Guid operationId, Action<WorldSubmissionResult>? completion) {
        _ = Interlocked.Exchange(location1: ref m_entering, value: null)?.TrySetResult();

        return target.SubmitEnvelope(
            completion: ((completion is null) ? null : result => {
                if (Interlocked.Exchange(location1: ref m_holding, value: null) is { } holding) {
                    holding.SetResult();
                    m_released.Task.Wait();
                }

                completion(obj: result);
            }),
            operationId: operationId,
            payload: payload,
            principal: principal
        );
    }
    public void SubmitIntent(in IntentSubmission submission) => target.SubmitIntent(submission: in submission);
    public void SubmitSession(SessionRequest request, Action<SessionReply> completion) => target.SubmitSession(completion: completion, request: request);
}
