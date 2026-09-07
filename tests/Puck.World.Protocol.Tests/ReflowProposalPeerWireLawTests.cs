using Puck.World.Server;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Locks the bounded downstream transport for typed reflow proposals while preserving plain query replies.</summary>
public sealed class ReflowProposalPeerWireLawTests {
    [Fact]
    public void TypedProposalRoundTripsWithCanonicalBatchAndMetadata() {
        var dependency = new WorldSpatialReadDependency(
            MinXRaw: -65536,
            MinZRaw: -65536,
            MaxXRaw: 65536,
            MaxZRaw: 65536,
            Fingerprint: new string('a', 64),
            MinYRaw: -32768,
            MaxYRaw: 32768
        );
        var batch = new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: "selected",
                Key: "$value",
                Value: 1,
                Kind: WorldDocumentWriteKind.Set
            )],
            ExpectedSpatialReads: [dependency]
        );
        var proposal = new WorldPlacementProposal(
            Mutation: batch,
            Candidates: 7,
            Moved: 2,
            Cost: 9
        ) {
            AffectedIds = ["store-a", "store-b"],
            Constraints = ["members<=64"]
        };

        var frame = WriteResult(new WorldSubmissionResult.Query(new QueryAnswer(
            Text: "[world.reflow: 2 moved]",
            Payload: proposal
        )));

        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.QueryPlacementProposal, kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out var reason), reason);
        var answer = Assert.IsType<WorldSubmissionResult.Query>(decoded).Answer;
        var round = Assert.IsType<WorldPlacementProposal>(answer.Payload);
        Assert.Equal(proposal.Mutation.Principal, round.Mutation.Principal);
        Assert.Equal(proposal.Mutation.Mutations, round.Mutation.Mutations);
        Assert.Equal((proposal.Candidates, proposal.Moved, proposal.Cost), (round.Candidates, round.Moved, round.Cost));
        Assert.Equal(proposal.AffectedIds, round.AffectedIds);
        Assert.Equal(proposal.Constraints, round.Constraints);
        Assert.Equal(proposal.SpatialReads, round.SpatialReads);
    }

    [Fact]
    public void PlainQueryRemainsTheExistingTextOnlyKind() {
        var frame = WriteResult(new WorldSubmissionResult.Query(new QueryAnswer("plain")));

        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.Query, kind);
        Assert.True(WorldPeerWireFormat.TryReadResult(kind, body.Span, out var decoded, out _));
        var answer = Assert.IsType<WorldSubmissionResult.Query>(decoded).Answer;
        Assert.Equal("plain", answer.Text);
        Assert.Null(answer.Payload);
    }

    [Fact]
    public void TruncatedAndOversizedTypedProposalBodiesRefuse() {
        var dependency = new WorldSpatialReadDependency(0, 0, 1, 1, new string('b', 64));
        var batch = new WorldMutation.Batch(
            WorldPrincipal.Console,
            [new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "selected", "$value", 1, WorldDocumentWriteKind.Set)],
            ExpectedSpatialReads: [dependency]
        );
        var proposal = new WorldPlacementProposal(batch, 1, 1, 1);
        var frame = WriteResult(new WorldSubmissionResult.Query(new QueryAnswer("proposal", Payload: proposal)));
        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out var body));
        Assert.False(WorldPeerWireFormat.TryReadResult(kind, body.Span[..^1], out _, out _));

        var oversized = new byte[(WorldPeerWireFormat.MaxDownstreamFrameBytes - sizeof(uint) - sizeof(byte)) + 1];
        Assert.False(WorldPeerWireFormat.TryReadResult(
            WorldPeerWireFormat.DownstreamKind.QueryPlacementProposal,
            oversized,
            out _,
            out _
        ));
    }

    [Fact]
    public void OversizedProposalIsAnExplicitRefusalFrame() {
        var batch = new WorldMutation.Batch(
            WorldPrincipal.Console,
            [new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "selected", "$value", 1, WorldDocumentWriteKind.Set)]
        );
        var proposal = new WorldPlacementProposal(batch, 1, 1, 1) {
            AffectedIds = Enumerable.Repeat(new string('x', 1024), 256).ToArray()
        };

        var frame = WriteResult(new WorldSubmissionResult.Query(new QueryAnswer("proposal", Payload: proposal)));

        Assert.True(WorldPeerWireFormat.TryDecodeDownstream(frame, out var kind, out _));
        Assert.Equal(WorldPeerWireFormat.DownstreamKind.Refusal, kind);
    }

    private static byte[] WriteResult(WorldSubmissionResult result) {
        using var stream = new MemoryStream();
        WorldPeerWireFormat.WriteResultAsync(stream, result, CancellationToken.None).GetAwaiter().GetResult();
        return stream.ToArray();
    }
}
