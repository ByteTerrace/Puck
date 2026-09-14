using Puck.World.Server;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Locks the bounded downstream transport for typed reflow proposals while preserving plain query replies.</summary>
public sealed class ReflowProposalPeerWireLawTests {
    private static byte[] WriteResult(WorldSubmissionResult result) {
        using var stream = new MemoryStream();

        WorldPeerWireFormat.WriteResultAsync(
            stream,
            result,
            CancellationToken.None
        ).GetAwaiter().GetResult();
        return stream.ToArray();
    }

    [Fact]
    public void OversizedProposalIsAnExplicitRefusalFrame() {
        var batch = new WorldMutation.Batch(
            WorldPrincipal.Console,
            [new WorldMutation.UpsertStateCell(
                    WorldPrincipal.Console,
                    "selected",
                    "$value",
                    1,
                    WorldDocumentWriteKind.Set
                )]
        );
        var proposal = new WorldPlacementProposal(
            Candidates: 1,
            Cost: 1,
            Moved: 1,
            Mutation: batch
        ) {
            AffectedIds = Enumerable.Repeat(
            new string(
                c: 'x',
                count: 1024
            ),
            256
        ).ToArray(),
        };

        var frame = WriteResult(result: new WorldSubmissionResult.Query(Answer: new QueryAnswer(
            "proposal",
            Payload: proposal
        )));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out _,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.Refusal
        );
    }
    [Fact]
    public void PlainQueryRemainsTheExistingTextOnlyKind() {
        var frame = WriteResult(result: new WorldSubmissionResult.Query(Answer: new QueryAnswer("plain")));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.Query
        );
        Assert.True(condition: WorldPeerWireFormat.TryReadResult(
            kind,
            body.Span,
            out var decoded,
            out _
        ));
        var answer = Assert.IsType<WorldSubmissionResult.Query>(@object: decoded).Answer;

        Assert.Equal(
            "plain",
            answer.Text
        );
        Assert.Null(@object: answer.Payload);
    }
    [Fact]
    public void TruncatedAndOversizedTypedProposalBodiesRefuse() {
        var dependency = new WorldSpatialReadDependency(
            0,
            0,
            1,
            1,
            new string(
                c: 'b',
                count: 64
            )
        );
        var batch = new WorldMutation.Batch(
            WorldPrincipal.Console,
            [new WorldMutation.UpsertStateCell(
                    WorldPrincipal.Console,
                    "selected",
                    "$value",
                    1,
                    WorldDocumentWriteKind.Set
                )],
            ExpectedSpatialReads: [dependency]
        );
        var proposal = new WorldPlacementProposal(
            Candidates: 1,
            Cost: 1,
            Moved: 1,
            Mutation: batch
        );
        var frame = WriteResult(result: new WorldSubmissionResult.Query(Answer: new QueryAnswer(
            "proposal",
            Payload: proposal
        )));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.False(condition: WorldPeerWireFormat.TryReadResult(
            kind,
            body.Span[..^1],
            out _,
            out _
        ));

        var oversized = new byte[(((WorldPeerWireFormat.MaxDownstreamFrameBytes - sizeof(uint)) - sizeof(byte)) + 1)];

        Assert.False(condition: WorldPeerWireFormat.TryReadResult(
            body: oversized,
            kind: WorldPeerWireFormat.DownstreamKind.QueryPlacementProposal,
            reason: out _,
            result: out _
        ));
    }
    [Fact]
    public void TypedProposalRoundTripsWithCanonicalBatchAndMetadata() {
        var dependency = new WorldSpatialReadDependency(
            MinXRaw: -65536,
            MinZRaw: -65536,
            MaxXRaw: 65536,
            MaxZRaw: 65536,
            Fingerprint: new string(
                c: 'a',
                count: 64
            ),
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
            Candidates: 7,
            Cost: 9,
            Moved: 2,
            Mutation: batch
        ) {
            AffectedIds = ["store-a", "store-b"],
            Constraints = ["members<=64"],
        };

        var frame = WriteResult(result: new WorldSubmissionResult.Query(Answer: new QueryAnswer(
            Text: "[world.reflow: 2 moved]",
            Payload: proposal
        )));

        Assert.True(condition: WorldPeerWireFormat.TryDecodeDownstream(
            body: out var body,
            frame: frame,
            kind: out var kind
        ));
        Assert.Equal(
            actual: kind,
            expected: WorldPeerWireFormat.DownstreamKind.QueryPlacementProposal
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
        var answer = Assert.IsType<WorldSubmissionResult.Query>(@object: decoded).Answer;
        var round = Assert.IsType<WorldPlacementProposal>(@object: answer.Payload);

        Assert.Equal(
            proposal.Mutation.Principal,
            round.Mutation.Principal
        );
        Assert.Equal(
            proposal.Mutation.Mutations,
            round.Mutation.Mutations
        );
        Assert.Equal(
            (proposal.Candidates, proposal.Moved, proposal.Cost),
            (round.Candidates, round.Moved, round.Cost)
        );
        Assert.Equal(
            proposal.AffectedIds,
            round.AffectedIds
        );
        Assert.Equal(
            proposal.Constraints,
            round.Constraints
        );
        Assert.Equal(
            proposal.SpatialReads,
            round.SpatialReads
        );
    }
}
