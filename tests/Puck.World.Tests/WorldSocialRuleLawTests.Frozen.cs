using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class WorldSocialRuleLawTests {
    [Fact]
    public void FrozenSocialOwnershipSurvivesAuthorityWireAndBothRuleWriteDoors() {
        var result = new WorldValueExpression([new WorldValueToken.SocialResult()]);
        var document = Document(Rule("attempt",
            new ActionEffect.ObserveSocial(Evidence()), new ActionEffect.SetState("observeResult", Expression: result),
            new ActionEffect.ForgetSocial(Relationship()), new ActionEffect.SetState("forgetResult", Expression: result))) with {
            StateRaw = new(World: [Slot("observeResult"), Slot("forgetResult")], Social: Policy()),
        };
        using var fixture = Fixtures.FreshServer(document);
        var checkpoint = Capture(fixture);
        var bank = WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(Policy()), checkpoint.Server.Social!);
        var observer = new WorldEntityAddress("social-test", 0, 0);
        var key = new WorldSocialImpressionKey(observer, new("social-test", 1, 0), 0);
        bank.Observe(new(key, new(new("social-test", 2, 0), "help.outcome", 1), 0, FixedQ4816.One.Value, FixedQ4816.One.Value));
        Assert.True(bank.TryFreezeObserver(observer, new("upstream", 17), out var reason), reason);
        checkpoint = checkpoint with { Server = checkpoint.Server with { Social = bank.Capture() } };
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint), out var decoded, out reason), reason);
        fixture.Server.RestoreCheckpoint(decoded!);
        Assert.Contains("frozen 1 observers", fixture.Server.DescribeSocialBudget());
        Assert.Contains("frozen=True", fixture.Server.DescribeSocial(WorldPrincipal.Console, new(Relationship())));
        fixture.Step();
        Assert.Equal((long)WorldSocialEvidenceResult.ObserverFrozen, Read(fixture, "observeResult"));
        Assert.Equal((long)WorldSocialEvidenceResult.ObserverFrozen, Read(fixture, "forgetResult"));
        var live = Capture(fixture);
        Assert.Equal(bank.Capture().Impressions, live.Server.Social!.Impressions);
        Assert.Equal(bank.Capture().Receipts, live.Server.Social.Receipts);
        var hash = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(live), out decoded, out reason), reason);
        fixture.Server.RestoreCheckpoint(decoded!);
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
        var hold = Assert.Single(live.Server.Social.FrozenObservers!);
        Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(live with { Server = live.Server with {
            Social = live.Server.Social with { FrozenObservers = [hold with { FrozenAt = live.Server.Social.EngineTick + 1 }] },
        } }));
        Assert.Equal(hash, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 0));
    }

    [Fact]
    public void ImpossibleFrozenObserverWireCountRefusesBeforeAllocatingClaims() {
        using var fixture = Fixtures.FreshServer(Document());
        var checkpoint = Capture(fixture);
        var malformed = checkpoint with { Server = checkpoint.Server with { Social = checkpoint.Server.Social! with {
            FrozenObservers = new MissingReservationRows<WorldSocialFrozenObserverCheckpoint>(),
        } } };
        Assert.False(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(malformed), out _, out var reason));
        Assert.Contains("social frozen observers", reason);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)]
    [InlineData(false, 1)] [InlineData(true, 1)]
    [InlineData(false, 2)] [InlineData(true, 2)]
    [InlineData(false, 3)] [InlineData(true, 3)]
    public void PolicyReplacementCannotDiscardOutstandingOwnership(bool frozen, int operation) {
        using var fixture = Fixtures.FreshServer(Document());
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(WorldPrincipal.Console, Slot("kept")));
        fixture.Step();
        var checkpoint = Capture(fixture);
        // Undo and reset must also protect a recovered base that names a different policy.
        var replacement = fixture.Server.Definition with { StateRaw = operation % 2 == 0
            ? new(World: [Slot("replacement")])
            : new(World: [Slot("replacement")], Social: Policy() with { ReceiptCapacity = 513 }) };
        var replacementBytes = WorldDefinitionSerialization.Serialize(replacement);
        var bank = WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(Policy()), checkpoint.Server.Social!);
        var observer = new WorldEntityAddress("social-test", 0, 0);
        if (frozen) { Assert.True(bank.TryFreezeObserver(observer, new("upstream", 17), out _)); }
        else { Assert.True(bank.TryReserveImport(new("upstream", 17), [new(observer, 0, 0)], out _)); }
        checkpoint = checkpoint with { Server = checkpoint.Server with { Social = bank.Capture(), BaseDefinitionJson = replacementBytes } };
        fixture.Server.RestoreCheckpoint(checkpoint);
        var echoes = new List<WorldEditEcho>(); fixture.Server.EchoTap += echoes.Add;
        var originalBytes = WorldDefinitionSerialization.Serialize(fixture.Server.Definition);
        void Apply() {
            if (operation == 3) { fixture.Server.EnqueueUndo(1, WorldPrincipal.Console); }
            else {
                var kind = operation == 2 ? WorldRebuildKind.Reset : operation == 1 ? WorldRebuildKind.Reload : WorldRebuildKind.Load;
                fixture.Server.EnqueueRebuild(new(kind, replacement, "social-policy-probe.world.json", true,
                    WorldDefinitionFileSource.ComputeContentHash(replacementBytes)), WorldPrincipal.Console);
            }
            fixture.Step();
        }
        Apply();
        Assert.Equal(originalBytes, WorldDefinitionSerialization.Serialize(fixture.Server.Definition));
        Assert.Contains(echoes, echo => echo.Rejected && echo.Message.Contains("social policy", StringComparison.Ordinal));
        var live = Capture(fixture);
        Assert.Equal(checkpoint.Server.Journal.Count, live.Server.Journal.Count);
        Assert.Equal(frozen ? 1 : 0, live.Server.Social!.FrozenObservers!.Count);
        Assert.Equal(frozen ? 0 : 1, live.Server.Social.ImportReservations!.Count);
        // Explicitly resolving the holds opens exactly the same operation; this is not a blanket reload ban.
        fixture.Server.RestoreCheckpoint(live with { Server = live.Server with { Social = live.Server.Social with {
            FrozenObservers = [], ImportReservations = [],
        } } });
        echoes.Clear(); Apply();
        Assert.DoesNotContain(echoes, echo => echo.Rejected);
        Assert.Equal(replacementBytes, WorldDefinitionSerialization.Serialize(fixture.Server.Definition));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EqualDetachedPolicyAndOrdinaryMutationPreserveOwnershipHolds(bool frozen) {
        using var fixture = Fixtures.FreshServer(Document());
        var checkpoint = Capture(fixture);
        var bank = WorldSocialMemory.Restore(CompiledWorldSocialPolicy.Compile(Policy()), checkpoint.Server.Social!);
        var observer = new WorldEntityAddress("social-test", 0, 0);
        if (frozen) { Assert.True(bank.TryFreezeObserver(observer, new("upstream", 17), out _)); }
        else { Assert.True(bank.TryReserveImport(new("upstream", 17), [new(observer, 0, 0)], out _)); }
        fixture.Server.RestoreCheckpoint(checkpoint with { Server = checkpoint.Server with { Social = bank.Capture() } });
        var document = Document() with { StateRaw = new(World: [Slot("loaded")], Social: Policy()) };
        fixture.Server.EnqueueRebuild(new(WorldRebuildKind.Load, document, "equal-policy-probe.world.json", true,
            WorldDefinitionFileSource.ComputeContentHash(WorldDefinitionSerialization.Serialize(document))), WorldPrincipal.Console);
        fixture.Step();
        Assert.NotNull(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "loaded"));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateRow(WorldPrincipal.Console, Slot("ordinary")));
        fixture.Step();
        Assert.NotNull(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "ordinary"));
        fixture.Server.EnqueueUndo(1, WorldPrincipal.Console); fixture.Step();
        Assert.Null(WorldDefinitionRows.FindStateRow(fixture.Server.Definition.State, "ordinary"));
        Assert.Equal(bank.Capture().FrozenObservers, Memory(fixture).FrozenObservers);
        var expected = bank.Capture().ImportReservations!; var actual = Memory(fixture).ImportReservations!;
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++) {
            Assert.Equal(expected[index].Key, actual[index].Key);
            Assert.Equal(expected[index].Members, actual[index].Members);
        }
    }
}
