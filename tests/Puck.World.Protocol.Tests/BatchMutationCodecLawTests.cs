using Puck.State;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>A batch crosses the checkpoint journal as one committed leaf whose members keep their concrete kinds.</summary>
public sealed class BatchMutationCodecLawTests {
    [Fact]
    public void GuardsRoundTripAndNestedActorsCannotEscalate() {
        var member = new WorldMutation.UpsertStateCell(WorldPrincipal.Console, "gold", "$value", 1, WorldDocumentWriteKind.Add);
        var valid = new WorldMutation.Batch(WorldPrincipal.Console, [member], new string('A', 64),
            [new WorldStateExpectation("gold", null, 5, ActionStateComparison.GreaterOrEqual, 1, CellKind.Int)], ["gold"], "court");
        Assert.True(WorldSubmissionCodec.TryEncodeMutation(valid, out var bytes, out var error), error.Detail);
        Assert.True(WorldSubmissionCodec.TryDecodeMutation(bytes, out var decoded, out error), error.Detail);
        var round = Assert.IsType<WorldMutation.Batch>(decoded);
        Assert.Equal(valid.ExpectedDefinition, round.ExpectedDefinition);
        Assert.Equal(valid.ExpectedCells, round.ExpectedCells);
        Assert.Equal(valid.ExpectedStateRows, round.ExpectedStateRows);
        Assert.Equal(valid.ExpectedLayoutTemplate, round.ExpectedLayoutTemplate);
        var wrongActor = valid with { Principal = WorldPrincipal.Seat(0) };
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(wrongActor, out _, out _));
        Assert.False(WorldSubmissionCodec.TryEncodeCommittedMutation(wrongActor, out _, out _));
        var nested = valid with { Mutations = [wrongActor] };
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(nested, out _, out _));
        var malformed = valid with { ExpectedCells = [null!] };
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(malformed, out _, out _));
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(valid with { Mutations = [] }, out _, out _));
    }
    [Fact]
    public void ABatchRoundTripsWithEveryMemberKind() {
        var batch = new WorldMutation.Batch(
            Principal: WorldPrincipal.World,
            Mutations: [
                new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: "gold", Key: "$value", Value: 5L, Kind: WorldDocumentWriteKind.Add),
                new WorldMutation.RemoveStateCell(Principal: WorldPrincipal.World, Row: "hand", Key: "7"),
                new WorldMutation.TransformState(WorldPrincipal.World, new StateTransform.Push(Row: "history", Value: 3L)),
            ]
        );

        Assert.True(WorldSubmissionCodec.TryEncodeCommittedMutation(mutation: batch, bytes: out var bytes, failure: out var encodeFailure), encodeFailure.Detail);
        Assert.True(WorldSubmissionCodec.TryDecodeCommittedMutation(bytes: bytes, mutation: out var decoded, failure: out var decodeFailure), decodeFailure.Detail);

        var round = Assert.IsType<WorldMutation.Batch>(decoded);
        Assert.Equal(3, round.Mutations.Count);
        var cell = Assert.IsType<WorldMutation.UpsertStateCell>(round.Mutations[0]);
        Assert.Equal(("gold", "$value", 5L, WorldDocumentWriteKind.Add), (cell.Row, cell.Key, cell.Value, cell.Kind));
        Assert.Equal("hand", Assert.IsType<WorldMutation.RemoveStateCell>(round.Mutations[1]).Row);
        var push = Assert.IsType<StateTransform.Push>(Assert.IsType<WorldMutation.TransformState>(round.Mutations[2]).Transform);
        Assert.Equal(("history", 3L), (push.Row, push.Value));
    }
}
