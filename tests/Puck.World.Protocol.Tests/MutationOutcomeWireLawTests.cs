using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Proves the typed mutation completion preserves its decision and persistence axes over the peer wire.</summary>
public sealed class MutationOutcomeWireLawTests {
    [Fact]
    public void AppliedOutcomeRoundTripsWithWatermarkAndGroupRevision() {
        var expected = new WorldMutationOutcome(
            OperationId: Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            Actor: WorldPrincipal.Console,
            PayloadDigest: new string('a', 64),
            Decision: WorldMutationDecision.Applied,
            Code: "world.mutation.applied",
            Detail: "committed",
            AffectedGroupRevision: 12,
            PersistenceStatus: WorldMutationPersistenceStatus.Durable,
            DurableWatermark: new WorldDurableWatermark(RootSequence: 9, CheckpointOrdinal: 44, JournalSequence: 45, Tick: 99)
        );

        var frame = WriteResultSync(new WorldSubmissionResult.Mutation(Outcome: expected));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.MutationOutcome, kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out var reason), reason);

        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(decoded).Outcome;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DurableRefusalRoundTripsWithoutBecomingApplied() {
        var expected = new WorldMutationOutcome(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string('b', 64),
            WorldMutationDecision.Refused,
            "world.mutation.denied",
            "grant missing",
            null,
            WorldMutationPersistenceStatus.Durable,
            new WorldDurableWatermark(RootSequence: 4, CheckpointOrdinal: 2, JournalSequence: null, Tick: 7)
        );

        var frame = WriteResultSync(new WorldSubmissionResult.Mutation(expected));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out var reason), reason);

        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(decoded).Outcome;
        Assert.True(actual.Refused);
        Assert.False(actual.Applied);
        Assert.Equal(WorldMutationPersistenceStatus.Durable, actual.PersistenceStatus);
    }

    [Fact]
    public void RecoveryRequiredWithoutWatermarkRoundTripsAsUncertain() {
        var expected = new WorldMutationOutcome(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string('c', 64),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            string.Empty,
            null,
            WorldMutationPersistenceStatus.RecoveryRequired,
            null
        );

        var frame = WriteResultSync(new WorldSubmissionResult.Mutation(expected));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.MutationOutcome, kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out var reason), reason);
        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(decoded).Outcome;
        Assert.Equal(WorldMutationPersistenceStatus.RecoveryRequired, actual.PersistenceStatus);
        Assert.Null(actual.DurableWatermark);
    }

    [Fact]
    public void PendingOutcomeHasNoDurabilityProofYet() {
        var expected = new WorldMutationOutcome(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string('d', 64),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            "awaiting receipt",
            null,
            WorldMutationPersistenceStatus.Pending,
            null
        );

        Assert.True(expected.IsValid);
        var frame = WriteResultSync(new WorldSubmissionResult.Mutation(expected));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out var reason), reason);
        Assert.Equal(WorldMutationPersistenceStatus.Pending, Assert.IsType<WorldSubmissionResult.Mutation>(decoded).Outcome.PersistenceStatus);
    }

    [Fact]
    public void DurableOutcomeWithoutPublicationProofIsMalformed() {
        var malformed = new WorldMutationOutcome(
            Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string('e', 64),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            string.Empty,
            null,
            WorldMutationPersistenceStatus.Durable,
            null
        );

        Assert.False(malformed.IsValid);
        var frame = WriteResultSync(new WorldSubmissionResult.Mutation(malformed));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.Refusal, kind);
    }

    [Fact]
    public void BindingUsesStampedActorAndCanonicalPayloadBytes() {
        var envelope = new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0,
            Sequence: 1,
            CorrelationId: 1,
            Principal: WorldPrincipal.Console,
            Payload: new WorldSubmissionPayload.Mutation(
                new WorldMutation.RemoveKit(WorldPrincipal.Console, "kit")
            ),
            OperationId: Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")
        );

        Assert.True(WorldMutationBindingFactory.TryCreate(in envelope, out var binding, out var detail), detail);
        Assert.Equal(WorldPrincipal.Console, binding.Actor);
        Assert.Equal(64, binding.PayloadDigest.Length);
        Assert.Equal(binding.PayloadDigest, binding.PayloadDigest.ToLowerInvariant());

        var changedActor = envelope with { Principal = WorldPrincipal.Seat(0) };
        Assert.False(WorldMutationBindingFactory.TryCreate(in changedActor, out _, out var mismatch));
        Assert.Contains("does not match", mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultEnvelopeCannotProduceAnOperationBinding() {
        var envelope = default(SubmissionEnvelope);

        Assert.False(WorldMutationBindingFactory.TryCreate(in envelope, out _, out var detail));
        Assert.Contains("operation id", detail, StringComparison.Ordinal);
    }

    private static byte[] WriteResultSync(WorldSubmissionResult result) {
        using var stream = new MemoryStream();
        WorldPeerWireFormat.WriteResultAsync(stream, result, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray();
    }
}
