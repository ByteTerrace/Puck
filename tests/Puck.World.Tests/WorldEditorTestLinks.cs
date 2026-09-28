using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>A link that records every envelope it is handed and each envelope's completion, as a destination world
/// would receive them, so a law answers each verdict itself, in the order it chooses.</summary>
/// <param name="definition">The destination's document, which answers its population queries.</param>
internal sealed class RecordingLink(WorldDefinition definition) : IServerLink {
    /// <summary>Gets a verdict that applied.</summary>
    public static WorldSubmissionResult Applied { get; } = new WorldSubmissionResult.Mutation(Outcome: new WorldMutationOutcome(
        Actor: Principal.Console,
        AffectedGroupRevision: null,
        Code: "world.mutation.applied",
        Decision: WorldMutationDecision.Applied,
        Detail: "applied",
        DurableWatermark: null,
        OperationId: Guid.NewGuid(),
        PayloadDigest: "digest",
        PersistenceStatus: WorldMutationPersistenceStatus.NotRequested
    ));
    /// <summary>Gets a verdict the destination's grant check refused.</summary>
    public static WorldSubmissionResult Refused { get; } = new WorldSubmissionResult.Refusal(Code: "world.grant.denied", Detail: "the seat may not mutate placements here");

    /// <summary>Gets each submitted envelope's completion, in submission order.</summary>
    public List<Action<WorldSubmissionResult>?> Completions { get; } = [];
    /// <summary>Gets each submitted envelope, in submission order.</summary>
    public List<WorldSubmissionPayload> Submitted { get; } = [];

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
        Submitted.Add(item: payload);
        Completions.Add(item: completion);

        return Submitted.Count;
    }
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
    /// <param name="link">The link its edits are submitted over.</param>
    /// <param name="pose">Where its body 0 stands, in its own coordinates.</param>
    /// <returns>The endpoint.</returns>
    public static WorldAuthorityEndpoint Of(string identity, WorldDefinition definition, IServerLink link, Vector3 pose) => new(
        adjacencies: static () => null,
        clockOwnedHere: false,
        definition: () => definition,
        identity: identity,
        nextInputTick: static () => 2UL,
        observe: sink => {
            sink.DeliverDefinition(definition: definition);
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
