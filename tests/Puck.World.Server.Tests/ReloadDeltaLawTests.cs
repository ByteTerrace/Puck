using Puck.Commands;
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
        using var deltaWorld = Fixtures.FreshServer(world);
        using var wholeWorld = Fixtures.FreshServer(world);
        deltaWorld.Step();
        wholeWorld.Step();
        var hash = WorldDefinitionFileSource.ComputeContentHash(WorldDefinitionSerialization.Serialize(target));
        var delta = WorldReloadDelta.Create(deltaWorld.Server.Definition, deltaWorld.Server.DocumentVersion, target);
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
    [InlineData("stale", WorldReloadDelta.StaleBaseCode)]
    [InlineData("activation", WorldReloadDelta.StaleBaseCode)]
    [InlineData("base", WorldReloadDelta.BaseMismatchCode)]
    [InlineData("target", WorldReloadDelta.TargetMismatchCode)]
    public void TheAuthorityRefusesADeltaWhosePinsDoNotAgree(string fault, string code) {
        using var fixture = Fixtures.FreshServer();
        var current = fixture.Server.Definition;
        var delta = WorldReloadDelta.Create(current, fixture.Server.DocumentVersion, current);
        delta = fault switch {
            // A version this activation never installed: nothing can hold its declarations.
            "stale" => delta with { BaseVersion = delta.BaseVersion with { Sequence = delta.BaseVersion.Sequence + 1 } },
            "activation" => delta with { BaseVersion = new(Guid.NewGuid(), delta.BaseVersion.Sequence) },
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

    // The delta was reconstructed against the exact declarations it names, so an install queued ahead of it changes
    // nothing about what it installs: the reload lands its target over whatever stands, as a reload always replaces it.
    [Fact]
    public void AnInstallBetweenPreparationAndApplyKeepsTheQueuedDeltaTarget() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var target = server.Definition;
        var delta = WorldReloadDelta.Create(server.Definition, server.DocumentVersion, target);
        var echoes = new List<WorldEditEcho>();
        server.EchoTap += echoes.Add;
        server.EnqueueRebuild(new(WorldRebuildKind.Reset, null, null, false), Principal.Console);
        server.EnqueueRebuild(new(WorldRebuildKind.Reload, null, new WorldRebuildOrigin.File("palette.world.json"), false, "source", delta), Principal.Console);
        fixture.Step();
        Assert.Contains(echoes, echo => !echo.Rejected && echo.Message.StartsWith("world.reload applied", StringComparison.Ordinal));
    }
}
