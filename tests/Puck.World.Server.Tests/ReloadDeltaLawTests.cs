using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class ReloadDeltaLawTests {
    [Fact]
    public void APaletteDeltaCrossesTheWireAndMatchesWholeDocumentReloadState() {
        var world = Fixtures.BuildDocument();
        var original = world.Creations[0];
        var recoloured = CreationFixtures.Prototype(original.Document with {
            Palette = [new("#6B8299", null, null, null)],
        }, original.Id);
        var target = world with { CreationsRaw = [recoloured, .. world.Creations.Skip(1)] };
        using var deltaWorld = Fixtures.FreshServer(world);
        using var wholeWorld = Fixtures.FreshServer(world);
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
        var actual = WorldStateHashComposition.HashAuthoritative(deltaWorld.Server, 1);
        var expected = WorldStateHashComposition.HashAuthoritative(wholeWorld.Server, 1);
        Assert.Equal(expected, actual);
        Console.WriteLine($"reload-delta parity: whole={expected:x16} delta={actual:x16} payload={bytes.Length}");
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
            "stale" => delta with { BaseVersion = delta.BaseVersion with { Sequence = delta.BaseVersion.Sequence - 1 } },
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

    [Fact]
    public void AnInstallBetweenPreparationAndApplyRefusesTheQueuedDelta() {
        using var fixture = Fixtures.FreshServer();
        var server = fixture.Server;
        var delta = WorldReloadDelta.Create(server.Definition, server.DocumentVersion, server.Definition);
        var echoes = new List<WorldEditEcho>();
        server.EchoTap += echoes.Add;
        server.EnqueueRebuild(new(WorldRebuildKind.Reset, null, null, false), Principal.Console);
        server.EnqueueRebuild(new(WorldRebuildKind.Reload, null, new WorldRebuildOrigin.File("palette.world.json"), false, "source", delta), Principal.Console);
        fixture.Step();
        Assert.Contains(echoes, echo => echo.Rejected && echo.Message.Contains(WorldReloadDelta.StaleBaseCode, StringComparison.Ordinal));
    }
}
