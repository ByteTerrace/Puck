using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class ReloadDeltaLawTests {
    [Fact]
    public void APaletteDeltaCrossesTheWireAndMatchesWholeDocumentReloadState() {
        var world = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var original = world.Creations[0];
        original = CreationFixtures.Prototype(original.Document with { Palette = CreationFixtures.Grey }, original.Id);
        world = world with { CreationsRaw = [original] };
        var recoloured = CreationFixtures.Prototype(original.Document with {
            Palette = [new("#6B8299", null, null, null)],
        }, original.Id);
        Assert.True(recoloured.DiffersInShadingAlone(original));
        var target = world with { CreationsRaw = [recoloured, .. world.Creations.Skip(1)] };
        var rows = WorldPresentationRows.Between(world, target);
        Assert.Single(rows.Creations);
        Assert.True(WorldPresentationRows.TryDeserialize(rows.Payload.Span, out var receivedRows));
        Assert.True(receivedRows!.TryApply(world, out var applied));
        Assert.Equal("#6B8299", applied!.Creations[0].Document.Palette![0].Color);
        using var deltaWorld = Fixtures.FreshServer(world);
        using var wholeWorld = Fixtures.FreshServer(world);
        var mirror = new WorldSessionMirror(placeholder: WorldProjection.Undisclosed);
        using var subscription = deltaWorld.Server.AttachSink(mirror);
        var work = new WorldBootWork();
        using var count = WorldBootWork.Attribute(work);
        deltaWorld.Step();
        wholeWorld.Step();
        var hash = WorldDefinitionFileSource.ComputeContentHash(WorldDefinitionSerialization.Serialize(target));
        var delta = WorldReloadDelta.Between(deltaWorld.Server.SourceRevision.Definition, target);
        var request = new WorldRebuildRequest(WorldRebuildKind.Reload, null, new WorldRebuildOrigin.File("palette.world.json"), false, hash, delta);
        Assert.True(WorldSubmissionCodec.TryEncodeRebuild(request, out var bytes, out var failure), failure.ToString());
        Assert.True(WorldSubmissionCodec.TryDecodeRebuild(bytes, out var decoded, out failure), failure.ToString());
        new LoopbackTransport(deltaWorld.Server).SubmitRebuild(decoded!, Principal.Console);
        // The old in-process rebuild is the oracle, independent of the delta reconstruction and its wire cap.
        wholeWorld.Server.EnqueueRebuild(request with { Definition = target, Delta = null }, Principal.Console);
        deltaWorld.Step();
        wholeWorld.Step();
        Assert.Equal("#6B8299", deltaWorld.Server.Definition.Creations[0].Document.Palette![0].Color);
        var actual = WorldStateHashComposition.HashAuthoritative(deltaWorld.Server, 2);
        var expected = WorldStateHashComposition.HashAuthoritative(wholeWorld.Server, 2);
        Assert.Equal(expected, actual);
        Assert.Equal("#6B8299", mirror.Document.Definition.Creations[0].Document.Palette![0].Color);
        Assert.Equal(1L, work.Read(WorldBootWork.PresentationRowsDelivered));
        var delivered = work.Read(WorldBootWork.PresentationBytesDelivered);
        Assert.Equal(rows.Payload.Length, delivered);
        Console.WriteLine($"presentation delivery: 1 row, {delivered} bytes");
        Console.WriteLine($"reload-delta parity: whole={expected:x16} delta={actual:x16} payload={bytes.Length}");
    }

    // A null in a delta removes the member on merge, so a member that becomes an explicit null cannot be reached
    // member by member: the diff replaces the object that holds it. A creation a mutation upserted is installed with
    // members its source holds as null written out, and the reload back to that source is such a change.
    [Theory]
    [InlineData("""{"shapes":[{"name":"a","blend":"Union","smooth":0}]}""", """{"shapes":[{"name":"a","blend":null,"smooth":null}]}""")]
    [InlineData("""{"look":{"wrap":0.5}}""", """{"look":{"wrap":null}}""")]
    [InlineData("""{"look":{}}""", """{"look":{"wrap":null}}""")]
    public void ADeltaReachesATargetThatHoldsAMemberAsNull(string basisJson, string targetJson) {
        var basis = System.Text.Json.Nodes.JsonNode.Parse(basisJson)!.AsObject();
        var target = System.Text.Json.Nodes.JsonNode.Parse(targetJson)!.AsObject();
        var delta = WorldDocumentBasis.Diff(basis, target);

        Assert.True(WorldDocumentBasis.TryMerge(basis, delta, out var merged, out var reason), reason);
        Assert.Equal(target.ToJsonString(), merged!.ToJsonString());
    }

    [Theory]
    [InlineData("base", WorldReloadDelta.StaleBaseCode)]
    [InlineData("target", WorldReloadDelta.TargetMismatchCode)]
    public void TheAuthorityRefusesADeltaWhosePinsDoNotAgree(string fault, string code) {
        using var fixture = Fixtures.FreshServer();
        var current = fixture.Server.Definition;
        var delta = WorldReloadDelta.Between(fixture.Server.SourceRevision.Definition, current);
        delta = fault switch {
            "base" => delta with { BaseHash = "wrong" },
            _ => delta with { TargetHash = "wrong" },
        };
        var echoes = new List<WorldEditEcho>();
        fixture.Server.EchoTap += echoes.Add;
        var link = new LoopbackTransport(fixture.Server);
        link.SubmitRebuild(new(WorldRebuildKind.Reload, null, new WorldRebuildOrigin.File("palette.world.json"), false, "source", delta), Principal.Console);
        fixture.Step();
        var refusal = Assert.Single(echoes, echo => echo.Kind == WorldEditEchoKind.Rebuild);
        Assert.True(refusal.Rejected);
        Assert.Contains(code, refusal.Message);
        Assert.Same(current, fixture.Server.Definition);
        Console.WriteLine(refusal.Message);
    }

    // The delta is cut between two source revisions and merged over the live document, so an install queued ahead of
    // it does not make it stale: it merges over whatever stands.
    [Fact]
    public void AnInstallBetweenPreparationAndApplyMergesTheQueuedDelta() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var delta = WorldReloadDelta.Between(server.SourceRevision.Definition, server.SourceRevision.Definition);
        var echoes = new List<WorldEditEcho>();
        server.EchoTap += echoes.Add;
        server.EnqueueRebuild(new(WorldRebuildKind.Reset, null, null, false), Principal.Console);
        server.EnqueueRebuild(new(WorldRebuildKind.Reload, null, new WorldRebuildOrigin.File("palette.world.json"), false, "source", delta), Principal.Console);
        fixture.Step();
        Assert.Contains(echoes, echo => !echo.Rejected && echo.Message.StartsWith("world.reload applied", StringComparison.Ordinal));
    }

    // A file edit and a live edit to different rows both stand; a row both changed takes the file's value and is
    // named, so the author sees which live edit the save replaced.
    [Fact]
    public void ASourceChangeMergesOverLiveEditsAndNamesTheRowsBothChanged() {
        var previous = System.Text.Json.Nodes.JsonNode.Parse("""{"rows":[{"id":"a","v":1},{"id":"b","v":1},{"id":"c","v":1}],"look":{"wrap":1,"tint":1}}""");
        var live = System.Text.Json.Nodes.JsonNode.Parse("""{"rows":[{"id":"a","v":2},{"id":"b","v":1},{"id":"c","v":5},{"id":"d","v":1}],"look":{"wrap":2,"tint":1}}""");
        var next = System.Text.Json.Nodes.JsonNode.Parse("""{"rows":[{"id":"a","v":1},{"id":"b","v":3},{"id":"c","v":4}],"look":{"wrap":1,"tint":3}}""");
        var conflicts = new List<string>();

        var merged = WorldDocumentBasis.MergeThree(previous, live, next, "$", conflicts);

        Assert.Equal("""{"rows":[{"id":"a","v":2},{"id":"b","v":3},{"id":"c","v":4},{"id":"d","v":1}],"look":{"wrap":2,"tint":3}}""", merged!.ToJsonString());
        Assert.Equal(["$.rows[id=\"c\"]"], conflicts);
    }

    // A shade program is keyed by its content, so a changed program removes one key and adds another; the delta carries
    // that as a removal and an addition the root merge applies.
    [Fact]
    public void AShadingChangeMergesAsARemovedAndAnAddedProgram() {
        var basis = System.Text.Json.Nodes.JsonNode.Parse("""{"shades":{"k1":{"body":"a"},"k2":{"body":"b"}}}""")!.AsObject();
        var target = System.Text.Json.Nodes.JsonNode.Parse("""{"shades":{"k1":{"body":"a"},"k3":{"body":"c"}}}""")!.AsObject();
        var delta = WorldDocumentBasis.Diff(basis, target);

        Assert.True(WorldDocumentBasis.TryMerge(basis, delta, out var merged, out var reason), reason);
        Assert.Equal(target.ToJsonString(), merged!.ToJsonString());
    }
}
