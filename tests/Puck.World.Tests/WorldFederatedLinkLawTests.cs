using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a mutation submitted through a committed traveler's federated link returns at once: the destination's
/// verdict reaches the completion when the lane delivers it, and the routed deadline's named refusal reaches it when
/// the lane never does. The submitting thread never waits for the destination's tick. The transport here holds the
/// verdict behind a gate the law releases, so the law observes the order of events, never a duration.
/// </summary>
public sealed class WorldFederatedLinkLawTests {
    // A routed-request door whose answers wait behind gates the law opens, bounded by the real routed deadline on a
    // test clock.
    private sealed class GatedRequests(VirtualClock clock) : IWorldRoutedRequests {
        public List<TaskCompletionSource<WorldFederationAnswer>> Gates { get; } = [];
        public WorldOutputHub? NarrationHub => null;

        public Task<WorldFederationAnswer> AnswerAsync(string sourceAuthority, WorldFederationRequest kind, byte[] body) {
            var gate = new TaskCompletionSource<WorldFederationAnswer>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

            lock (Gates) {
                Gates.Add(item: gate);
            }

            return WorldRemoteAuthority.WithinDeadlineAsync(answer: gate.Task, clock: clock, endpoint: "peer", kind: kind);
        }
        public bool TryCredential(int bodyIndex, out string sourceAuthority, out WorldMobilityIdentity mobility) {
            sourceAuthority = "origin/world";
            mobility = new WorldMobilityIdentity(
                DepartedFrom: new WorldEntityAddress(Authority: "origin/world", Generation: 7, Index: 4),
                Epoch: 1UL,
                Incarnation: new WorldEntityAddress(Authority: "origin/world", Generation: 7, Index: 4)
            );

            return true;
        }
        public bool TryForwardIntent(int bodyIndex, in IntentSubmission submission, out string reason) {
            reason = string.Empty;

            return true;
        }
    }

    private static readonly WorldMutation Mutation = new WorldMutation.RemovePlacement(Id: "crate1", Principal: Principal.Peer(generation: 1, index: 4));

    // Submits the mutation on a thread of its own and returns whether the call came back. A link that waited for its
    // verdict would still be inside the call, since only the law opens the gate; the bound only turns such a wait into a
    // failure rather than a hang, and the thread is a background one so a stuck call never holds the run open.
    private static bool Submits(WorldFederatedServerLink link, TaskCompletionSource<WorldSubmissionResult> answered) {
        var submitting = new Thread(start: () => link.SubmitWorldMutation(completion: result => answered.TrySetResult(result: result), mutation: Mutation, operationId: Guid.NewGuid())) {
            IsBackground = true,
        };

        submitting.Start();

        return submitting.Join(millisecondsTimeout: 60_000);
    }
    private static async Task<WorldFederationAnswer> CompletionOf(WorldSubmissionResult result) {
        using var frame = new MemoryStream();

        await WorldPeerWireFormat.WriteResultAsync(ct: CancellationToken.None, result: result, stream: frame);

        return new WorldFederationAnswer(Body: frame.ToArray(), Failure: default, Kind: WorldFederationResponse.Completion);
    }

    [Fact]
    public async Task ATravelersMutationReturnsAtOnceAndItsVerdictArrivesWhenTheLaneDeliversIt() {
        var clock = new VirtualClock();
        var requests = new GatedRequests(clock: clock);
        var link = new WorldFederatedServerLink(authority: requests);
        var answered = new TaskCompletionSource<WorldSubmissionResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        // The submitting thread returns while the verdict is still held: the gate is closed, and nothing has answered.
        Assert.True(condition: Submits(answered: answered, link: link), userMessage: "the submitting thread waited for a verdict the lane was holding");
        Assert.False(condition: answered.Task.IsCompleted);

        var applied = new WorldSubmissionResult.Mutation(Outcome: WorldMutationOutcome.AppliedOutcome(
            binding: new WorldMutationBinding(Actor: Mutation.Principal, OperationId: Guid.NewGuid(), PayloadDigest: new string(c: 'a', count: 64)),
            code: "world.mutation.applied",
            version: new WorldDocumentVersion(Activation: Guid.NewGuid(), Sequence: 3L)
        ));

        _ = Assert.Single(collection: requests.Gates).TrySetResult(result: await CompletionOf(result: applied));
        Assert.Equal(actual: await answered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken), expected: applied);
    }
    [Fact]
    public async Task ATravelersMutationTheLaneNeverAnswersEndsInTheNamedRefusalAtTheDeadline() {
        var clock = new VirtualClock();
        var requests = new GatedRequests(clock: clock);
        var link = new WorldFederatedServerLink(authority: requests);
        var answered = new TaskCompletionSource<WorldSubmissionResult>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(condition: Submits(answered: answered, link: link), userMessage: "the submitting thread waited for a verdict the lane was holding");

        // The deadline runs on the clock and fires exactly at its due instant, not before.
        await clock.ExpireAsync(ct: TestContext.Current.CancellationToken, dueTime: WorldRemoteAuthority.RoutedRequestDeadline, pending: answered.Task);

        var refusal = Assert.IsType<WorldSubmissionResult.Refusal>(@object: await answered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(actual: refusal.Code, expected: WorldFederatedServerLink.CompletionUnavailableCode);
        Assert.Contains(expectedSubstring: "did not answer Submission within 10s", actualString: refusal.Detail);
    }
}
