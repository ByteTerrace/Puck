using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Proves the typed mutation completion preserves its decision and persistence axes over the peer wire.</summary>
public sealed class MutationOutcomeWireLawTests {
    private static byte[] WriteResultSync(WorldSubmissionResult result) {
        using var stream = new MemoryStream();

        WorldPeerWireFormat.WriteResultAsync(
            stream,
            result,
            CancellationToken.None
        ).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    [Fact]
    public void AppliedOutcomeRoundTripsWithWatermarkAndGroupRevision() {
        var expected = new WorldMutationOutcome(
            OperationId: Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef"),
            Actor: WorldPrincipal.Console,
            PayloadDigest: new string(
                c: 'a',
                count: 64
            ),
            Decision: WorldMutationDecision.Applied,
            Code: "world.mutation.applied",
            Detail: "committed",
            AffectedGroupRevision: 12,
            PersistenceStatus: WorldMutationPersistenceStatus.Durable,
            DurableWatermark: new WorldDurableWatermark(
                CheckpointOrdinal: 44,
                JournalSequence: 45,
                RootSequence: 9,
                Tick: 99
            )
        );

        var frame = WriteResultSync(result: new WorldSubmissionResult.Mutation(Outcome: expected));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.MutationOutcome
        );
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                kind,
                body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );

        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(@object: decoded).Outcome;

        Assert.Equal(
            actual: actual,
            expected: expected
        );
    }
    [Fact]
    public void BindingUsesStampedActorAndCanonicalPayloadBytes() {
        var envelope = new SubmissionEnvelope(
            ConnectionId: SubmissionEnvelope.LocalConnectionId,
            SessionGeneration: 0,
            Sequence: 1,
            CorrelationId: 1,
            Principal: WorldPrincipal.Console,
            Payload: new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveKit(
                WorldPrincipal.Console,
                "kit"
            )),
            OperationId: Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef")
        );

        Assert.True(
            condition: WorldMutationBindingFactory.TryCreate(
                binding: out var binding,
                detail: out var detail,
                envelope: in envelope
            ),
            userMessage: detail
        );
        Assert.Equal(
            WorldPrincipal.Console,
            binding.Actor
        );
        Assert.Equal(
            64,
            binding.PayloadDigest.Length
        );
        Assert.Equal(
            binding.PayloadDigest,
            binding.PayloadDigest.ToLowerInvariant()
        );

        var changedActor = envelope with { Principal = WorldPrincipal.Seat(slot: 0) };

        Assert.False(condition: WorldMutationBindingFactory.TryCreate(
            binding: out _,
            detail: out var mismatch,
            envelope: in changedActor
        ));
        Assert.Contains(
            actualString: mismatch,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not match"
        );
    }
    [Fact]
    public void DefaultEnvelopeCannotProduceAnOperationBinding() {
        var envelope = default(SubmissionEnvelope);

        Assert.False(condition: WorldMutationBindingFactory.TryCreate(
            binding: out _,
            detail: out var detail,
            envelope: in envelope
        ));
        Assert.Contains(
            actualString: detail,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "operation id"
        );
    }
    [Fact]
    public void DurableOutcomeWithoutPublicationProofIsMalformed() {
        var malformed = new WorldMutationOutcome(
            Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string(
                c: 'e',
                count: 64
            ),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            string.Empty,
            null,
            WorldMutationPersistenceStatus.Durable,
            null
        );

        Assert.False(condition: malformed.IsValid);
        var frame = WriteResultSync(result: new WorldSubmissionResult.Mutation(Outcome: malformed));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.Refusal
        );
    }
    [Fact]
    public void DurableRefusalRoundTripsWithoutBecomingApplied() {
        var expected = new WorldMutationOutcome(
            Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string(
                c: 'b',
                count: 64
            ),
            WorldMutationDecision.Refused,
            "world.mutation.denied",
            "grant missing",
            null,
            WorldMutationPersistenceStatus.Durable,
            new WorldDurableWatermark(
                CheckpointOrdinal: 2,
                JournalSequence: null,
                RootSequence: 4,
                Tick: 7
            )
        );

        var frame = WriteResultSync(result: new WorldSubmissionResult.Mutation(Outcome: expected));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                kind,
                body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );

        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(@object: decoded).Outcome;

        Assert.True(condition: actual.Refused);
        Assert.False(condition: actual.Applied);
        Assert.Equal(
            WorldMutationPersistenceStatus.Durable,
            actual.PersistenceStatus
        );
    }
    [Fact]
    public void MutationFramePreservesCallerOperationId() {
        var operationId = Guid.Parse(input: "11223344-5566-7788-99aa-bbccddeeff00");
        var payload = new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveKit(
            WorldPrincipal.Console,
            "kit"
        ));

        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                failure: out var encodeFailure,
                frame: out var frame,
                operationId: operationId,
                payload: payload
            ),
            userMessage: encodeFailure.ToString()
        );
        Assert.True(
            condition: WorldFrameCodec.TryDecode(
                failure: out var decodeFailure,
                frame: frame,
                operationId: out var carriedId,
                payload: out var decoded
            ),
            userMessage: decodeFailure.ToString()
        );
        Assert.Equal(
            actual: carriedId,
            expected: operationId
        );
        Assert.Equal(
            actual: decoded,
            expected: payload
        );
    }
    [Fact]
    public void MutationFrameRejectsMissingOperationIdMetadata() {
        var payload = new WorldSubmissionPayload.Mutation(Value: new WorldMutation.RemoveKit(
            WorldPrincipal.Console,
            "kit"
        ));

        Assert.False(condition: WorldFrameCodec.TryEncode(
            failure: out var failure,
            frame: out _,
            payload: payload
        ));
        Assert.Equal(
            WorldCodecRefusal.PayloadMalformed,
            failure.Refusal
        );
        Assert.Contains(
            "operation id",
            failure.Detail,
            StringComparison.OrdinalIgnoreCase
        );
    }
    [Fact]
    public void PendingOutcomeHasNoDurabilityProofYet() {
        var expected = new WorldMutationOutcome(
            Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string(
                c: 'd',
                count: 64
            ),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            "awaiting receipt",
            null,
            WorldMutationPersistenceStatus.Pending,
            null
        );

        Assert.True(condition: expected.IsValid);
        var frame = WriteResultSync(result: new WorldSubmissionResult.Mutation(Outcome: expected));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                kind,
                body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            WorldMutationPersistenceStatus.Pending,
            Assert.IsType<WorldSubmissionResult.Mutation>(@object: decoded).Outcome.PersistenceStatus
        );
    }
    [Fact]
    public void RecoveryRequiredWithoutWatermarkRoundTripsAsUncertain() {
        var expected = new WorldMutationOutcome(
            Guid.Parse(input: "01234567-89ab-cdef-0123-456789abcdef"),
            WorldPrincipal.Console,
            new string(
                c: 'c',
                count: 64
            ),
            WorldMutationDecision.Applied,
            "world.mutation.applied",
            string.Empty,
            null,
            WorldMutationPersistenceStatus.RecoveryRequired,
            null
        );

        var frame = WriteResultSync(result: new WorldSubmissionResult.Mutation(Outcome: expected));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.MutationOutcome
        );
        Assert.True(
            condition: WorldPeerWireFormat.TryReadResult(
                kind,
                body.Span,
                out var decoded,
                out var reason
            ),
            userMessage: reason
        );
        var actual = Assert.IsType<WorldSubmissionResult.Mutation>(@object: decoded).Outcome;

        Assert.Equal(
            WorldMutationPersistenceStatus.RecoveryRequired,
            actual.PersistenceStatus
        );
        Assert.Null(value: actual.DurableWatermark);
    }
}
