using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Locks the reflow query ordinals and the guarded batch input envelope to their canonical wire shapes.</summary>
public sealed class ReflowQueryCodecLawTests {
    private static WorldQuery RoundTrip(WorldQuery query) {
        Assert.True(
            condition: WorldFrameCodec.TryEncode(
                payload: new WorldSubmissionPayload.Query(Value: query),
                frame: out var frame,
                failure: out var encodeFailure
            ),
            userMessage: encodeFailure.ToString()
        );
        Assert.True(
            condition: WorldFrameCodec.TryDecode(
                failure: out var decodeFailure,
                frame: frame,
                payload: out var decoded
            ),
            userMessage: decodeFailure.ToString()
        );
        return Assert.IsType<WorldSubmissionPayload.Query>(@object: decoded).Value;
    }

    [Fact]
    public void ExpectedInputsAndSpatialRegionRoundTripInsideBatchGuard() {
        var member = new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "selected",
            Key: "$value",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        );
        var expectedInputs = new WorldDefinitionReadDependency(
            PlacementIds: ["store-a"],
            StateRows: ["selected"],
            Fingerprint: new string(
                c: 'A',
                count: 64
            )
        );
        var spatial = new WorldSpatialReadDependency(
            MinXRaw: -65536,
            MinZRaw: -65536,
            MaxXRaw: 65536,
            MaxZRaw: 65536,
            Fingerprint: new string(
                c: 'B',
                count: 64
            ),
            MinYRaw: -32768,
            MaxYRaw: 32768
        );
        var batch = new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [member],
            ExpectedSpatialReads: [spatial],
            ExpectedInputs: expectedInputs
        );

        Assert.True(
            condition: WorldSubmissionCodec.TryEncodeMutation(
                bytes: out var bytes,
                failure: out var encodeFailure,
                mutation: batch
            ),
            userMessage: encodeFailure.ToString()
        );
        Assert.True(
            condition: WorldSubmissionCodec.TryDecodeMutation(
                bytes: bytes,
                failure: out var decodeFailure,
                mutation: out var decoded
            ),
            userMessage: decodeFailure.ToString()
        );
        var round = Assert.IsType<WorldMutation.Batch>(@object: decoded);

        Assert.NotNull(@object: round.ExpectedInputs);
        Assert.Equal(
            expectedInputs.PlacementIds,
            round.ExpectedInputs!.PlacementIds
        );
        Assert.Equal(
            expectedInputs.StateRows,
            round.ExpectedInputs.StateRows
        );
        Assert.Equal(
            expectedInputs.Fingerprint,
            round.ExpectedInputs.Fingerprint
        );
        Assert.Equal(
            spatial,
            Assert.Single(collection: round.ExpectedSpatialReads!)
        );
    }
    [Fact]
    public void GuardRejectsNullInputNamesBeforeWireEncoding() {
        var member = new WorldMutation.UpsertStateCell(
            Principal: WorldPrincipal.Console,
            Row: "selected",
            Key: "$value",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        );
        var malformed = new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [member],
            ExpectedInputs: new WorldDefinitionReadDependency(
                PlacementIds: [null!],
                StateRows: ["selected"],
                Fingerprint: new string(
                    c: 'A',
                    count: 64
                )
            )
        );

        Assert.False(condition: malformed.TryValidateShape(reason: out _));
        Assert.False(condition: WorldSubmissionCodec.TryEncodeMutation(
            bytes: out _,
            failure: out _,
            mutation: malformed
        ));
    }
    [Fact]
    public void ReflowPreviewStatusAndCancelRoundTrip() {
        var preview = new WorldQuery.ReflowPreview(Request: new WorldPlacementReflowRequest(
            TemplateId: "granaryStores",
            PlacementIds: ["store-a", "store-b"],
            Edits: [new WorldPlacementReflowEdit(
                    PlacementId: "store-a",
                    Scale: 1.25f
                )],
            PreserveInfluenceCoverage: true
        ));

        var decodedPreview = Assert.IsType<WorldQuery.ReflowPreview>(@object: RoundTrip(query: preview));

        Assert.Equal(
            preview.Request.TemplateId,
            decodedPreview.Request.TemplateId
        );
        Assert.Equal(
            preview.Request.PlacementIds,
            decodedPreview.Request.PlacementIds
        );
        Assert.Equal(
            preview.Request.Edits,
            decodedPreview.Request.Edits
        );
        Assert.Equal(
            preview.Request.PreserveInfluenceCoverage,
            decodedPreview.Request.PreserveInfluenceCoverage
        );
        Assert.IsType<WorldQuery.ReflowStatus>(@object: RoundTrip(query: new WorldQuery.ReflowStatus()));
        Assert.IsType<WorldQuery.ReflowCancel>(@object: RoundTrip(query: new WorldQuery.ReflowCancel()));
    }
}
