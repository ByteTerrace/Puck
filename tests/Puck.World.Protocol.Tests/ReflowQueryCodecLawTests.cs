using Xunit;

namespace Puck.World.Protocol.Tests;

/// <summary>Locks the reflow query ordinals and the guarded batch input envelope to their canonical wire shapes.</summary>
public sealed class ReflowQueryCodecLawTests {
    private static WorldQuery RoundTrip(WorldQuery query) {
        Assert.True(
            WorldFrameCodec.TryEncode(
                payload: new WorldSubmissionPayload.Query(Value: query),
                frame: out var frame,
                failure: out var encodeFailure
            ),
            encodeFailure.ToString()
        );
        Assert.True(
            WorldFrameCodec.TryDecode(frame: frame, payload: out var decoded, failure: out var decodeFailure),
            decodeFailure.ToString()
        );
        return Assert.IsType<WorldSubmissionPayload.Query>(decoded).Value;
    }

    [Fact]
    public void ReflowPreviewStatusAndCancelRoundTrip() {
        var preview = new WorldQuery.ReflowPreview(new WorldPlacementReflowRequest(
            TemplateId: "granaryStores",
            PlacementIds: ["store-a", "store-b"],
            Edits: [new WorldPlacementReflowEdit(PlacementId: "store-a", Scale: 1.25f)],
            PreserveInfluenceCoverage: true
        ));

        var decodedPreview = Assert.IsType<WorldQuery.ReflowPreview>(RoundTrip(preview));
        Assert.Equal(preview.Request.TemplateId, decodedPreview.Request.TemplateId);
        Assert.Equal(preview.Request.PlacementIds, decodedPreview.Request.PlacementIds);
        Assert.Equal(preview.Request.Edits, decodedPreview.Request.Edits);
        Assert.Equal(preview.Request.PreserveInfluenceCoverage, decodedPreview.Request.PreserveInfluenceCoverage);
        Assert.IsType<WorldQuery.ReflowStatus>(RoundTrip(new WorldQuery.ReflowStatus()));
        Assert.IsType<WorldQuery.ReflowCancel>(RoundTrip(new WorldQuery.ReflowCancel()));
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
            Fingerprint: new string('A', 64)
        );
        var spatial = new WorldSpatialReadDependency(
            MinXRaw: -65536,
            MinZRaw: -65536,
            MaxXRaw: 65536,
            MaxZRaw: 65536,
            Fingerprint: new string('B', 64),
            MinYRaw: -32768,
            MaxYRaw: 32768
        );
        var batch = new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [member],
            ExpectedSpatialReads: [spatial],
            ExpectedInputs: expectedInputs
        );

        Assert.True(WorldSubmissionCodec.TryEncodeMutation(batch, out var bytes, out var encodeFailure), encodeFailure.ToString());
        Assert.True(WorldSubmissionCodec.TryDecodeMutation(bytes, out var decoded, out var decodeFailure), decodeFailure.ToString());
        var round = Assert.IsType<WorldMutation.Batch>(decoded);
        Assert.NotNull(round.ExpectedInputs);
        Assert.Equal(expectedInputs.PlacementIds, round.ExpectedInputs!.PlacementIds);
        Assert.Equal(expectedInputs.StateRows, round.ExpectedInputs.StateRows);
        Assert.Equal(expectedInputs.Fingerprint, round.ExpectedInputs.Fingerprint);
        Assert.Equal(spatial, Assert.Single(round.ExpectedSpatialReads!));
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
                Fingerprint: new string('A', 64)
            )
        );

        Assert.False(malformed.TryValidateShape(out _));
        Assert.False(WorldSubmissionCodec.TryEncodeMutation(malformed, out _, out _));
    }
}
