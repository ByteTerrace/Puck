using Puck.Commands;
using Puck.State;
using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>A batch crosses the checkpoint journal as one committed leaf whose members keep their concrete kinds.</summary>
public sealed class BatchMutationCodecLawTests {
    [Fact]
    public void ABatchRoundTripsWithEveryMemberKind() {
        var batch = new WorldMutation.Batch(
            Principal: Principal.World,
            Mutations: [
                new WorldMutation.UpsertStateCell(
                    Principal: Principal.World,
                    Row: "gold",
                    Key: "$value",
                    Value: 5L,
                    Kind: WorldDocumentWriteKind.Add
                ),
                new WorldMutation.RemoveStateCell(
                    Principal: Principal.World,
                    Row: "hand",
                    Key: "7"
                ),
                new WorldMutation.TransformState(
                    Principal.World,
                    new StateTransform.WriteSet(
                        Row: "history",
                        Set: "mask",
                        Value: 3L
                    )
                ),
            ]
        );

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
                bytes: out var bytes,
                failure: out var encodeFailure,
                mutation: batch
            ),
            userMessage: encodeFailure.Detail
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeCommittedMutation(
                bytes: bytes,
                failure: out var decodeFailure,
                mutation: out var decoded
            ),
            userMessage: decodeFailure.Detail
        );

        var round = Assert.IsType<WorldMutation.Batch>(@object: decoded);

        Assert.Equal(
            3,
            round.Mutations.Count
        );
        var cell = Assert.IsType<WorldMutation.UpsertStateCell>(@object: round.Mutations[0]);

        Assert.Equal(
            ("gold", "$value", 5L, WorldDocumentWriteKind.Add),
            (cell.Row, cell.Key, cell.Value, cell.Kind)
        );
        Assert.Equal(
            "hand",
            Assert.IsType<WorldMutation.RemoveStateCell>(@object: round.Mutations[1]).Row
        );
        var write = Assert.IsType<StateTransform.WriteSet>(@object: Assert.IsType<WorldMutation.TransformState>(@object: round.Mutations[2]).Transform);

        Assert.Equal(
            ("history", "mask", 3L),
            (write.Row.Spelling, write.Set.Spelling, write.Value)
        );
    }
    [Fact]
    public void GuardsRoundTripAndNestedActorsCannotEscalate() {
        var member = new WorldMutation.UpsertStateCell(
            Principal.Console,
            "gold",
            "$value",
            1,
            WorldDocumentWriteKind.Add
        );
        var valid = new WorldMutation.Batch(
            Principal.Console,
            [member],
            new string(
                c: 'A',
                count: 64
            ),
            [new WorldStateExpectation(
                    Change: 1,
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Key: null,
                    Kind: CellKind.Int,
                    Row: "gold",
                    Value: 5
                )],
            ["gold"],
            [new WorldSpatialReadDependency(
                    0,
                    0,
                    65536,
                    65536,
                    new string(
                        c: 'B',
                        count: 64
                    )
                )]
        );

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeMutation(
                bytes: out var bytes,
                failure: out var error,
                mutation: valid
            ),
            userMessage: error.Detail
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeMutation(
                bytes: bytes,
                failure: out error,
                mutation: out var decoded
            ),
            userMessage: error.Detail
        );
        var round = Assert.IsType<WorldMutation.Batch>(@object: decoded);

        Assert.Equal(
            valid.ExpectedDefinition,
            round.ExpectedDefinition
        );
        Assert.Equal(
            valid.ExpectedCells,
            round.ExpectedCells
        );
        Assert.Equal(
            valid.ExpectedStateRows,
            round.ExpectedStateRows
        );
        Assert.Equal(
            valid.ExpectedSpatialReads,
            round.ExpectedSpatialReads
        );
        var wrongActor = valid with { Principal = Principal.Seat(slot: 0) };

        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            bytes: out _,
            failure: out _,
            mutation: wrongActor
        ));
        Assert.False(condition: WorldSubmissionCodec.TryEncodeCommittedMutation(
            bytes: out _,
            failure: out _,
            mutation: wrongActor
        ));
        var nested = valid with { Mutations = [wrongActor] };

        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            bytes: out _,
            failure: out _,
            mutation: nested
        ));
        var malformed = valid with { ExpectedCells = [null!] };

        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            bytes: out _,
            failure: out _,
            mutation: malformed
        ));
        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            valid with { Mutations = [] },
            out _,
            out _
        ));
    }
}
