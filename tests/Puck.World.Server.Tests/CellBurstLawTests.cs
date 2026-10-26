using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class CellBurstLawTests {
    private static readonly Principal Actor = Principal.Addon(name: "writer");

    private static WorldStateRow Row(string name) => new(CellName.Parse(candidate: name), CellKind.Int, Min: 0, Max: 100,
        Capacity: 32, Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0))]);
    private static WorldMutation.UpsertStateCell Write(string row, long value, WorldDocumentWriteKind kind = WorldDocumentWriteKind.Set) =>
        new(Actor, row, "a", value, kind);
    private static void Grant(WorldFixture fixture, string row) {
        fixture.Server.Grant(new WorldGrant(Grantee: Actor, Capability: WorldCapability.Mutate,
            Subject: GrantSubject.Section(section: WorldSection.State), Exclusive: false, Budget: 32,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State)), Principal.Console);
        fixture.Server.Grant(new WorldGrant(Grantee: Actor, Capability: WorldCapability.Edit,
            Subject: GrantSubject.State(name: row), Exclusive: false,
            KindMask: WorldMutationKindCatalog.KindsOf(section: WorldSection.State)), Principal.Console);
    }

    [Fact]
    public void AGuestBurstKeepsEveryJournalEntryAndReplaysAsIndividualMutations() {
        var definition = Fixtures.BuildDocument().WithWorldState([Row(name: "counter")]);
        using var fixture = Fixtures.FreshServer(definition);

        Grant(fixture: fixture, row: "counter");
        var journal = new List<(ulong Tick, ulong EngineTick, WorldMutation Mutation)>();
        var echoes = new List<WorldEditEcho>();

        fixture.Server.MutationJournalTap = (tick, engineTick, mutation) => journal.Add(item: (tick, engineTick, mutation));
        fixture.Server.EchoTap = echoes.Add;
        for (var index = 0; (index < 13); index++) {
            fixture.Server.EnqueueMutation(Write(kind: WorldDocumentWriteKind.Add, row: "counter", value: 1), sourceAddonInstanceId: 7, actOrdinal: ((ushort)index));
        }
        fixture.Step();
        Assert.Equal(13, journal.Count);
        Assert.Equal(13, echoes.Count);
        Assert.All(echoes, echo => Assert.False(condition: echo.Rejected));
        Assert.All(journal, entry => Assert.IsType<WorldMutation.UpsertStateCell>(@object: entry.Mutation));
        Assert.Equal(13, fixture.Server.ExportDefinition().State[0].Cells![0].Value.AsInt);
        using var replay = Fixtures.FreshServer(definition);

        Grant(fixture: replay, row: "counter");
        foreach (var entry in journal) {
            Assert.True(condition: replay.Server.TryApplyJournalTailMutation(engineTick: entry.EngineTick, mutation: entry.Mutation, tick: entry.Tick));
        }
        Assert.Equal(fixture.DefinitionBytes(), replay.DefinitionBytes());
        // Reuse must start from today's row, with neither yesterday's values nor a rejected load left in scratch.
        fixture.Server.EnqueueMutation(Write(kind: WorldDocumentWriteKind.Add, row: "counter", value: 2), sourceAddonInstanceId: 7);
        fixture.Step();
        Assert.Equal(15, fixture.Server.ExportDefinition().State[0].Cells![0].Value.AsInt);
    }
    [Fact]
    public void AnOverwrittenInvalidTextStillReceivesItsOwnRefusal() {
        var row = new WorldStateRow(CellName.Parse(candidate: "text"), CellKind.Text,
            Cells: [new StateCell(StateRow.SlotKey, CellValue.Text(value: "initial"))]);
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument().WithWorldState([row]));

        Grant(fixture: fixture, row: "text");
        var echoes = new List<WorldEditEcho>();

        fixture.Server.EchoTap = echoes.Add;
        foreach (var value in new[] { new string(c: 'x', count: (StateCapacity.MaxTextValueLength + 1)), "valid" }) {
            fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Actor, "text", StateRow.SlotKey.Value, 0,
                WorldDocumentWriteKind.Set, Text: value), sourceAddonInstanceId: 7);
        }
        fixture.Step();
        Assert.Equal(2, echoes.Count);
        Assert.True(condition: echoes[0].Rejected);
        Assert.False(condition: echoes[1].Rejected);
        Assert.Equal("valid", fixture.Server.ExportDefinition().State[0].Cells![0].Value.AsText);
    }
    [Fact]
    public void ARejectedMemberKeepsOrderedPartialSuccessAndItsOwnAuthorityVerdict() {
        using var fixture = Fixtures.FreshServer(Fixtures.BuildDocument().WithWorldState([Row(name: "counter"), Row(name: "secret")]));

        Grant(fixture: fixture, row: "counter");
        var echoes = new List<WorldEditEcho>();

        fixture.Server.EchoTap = echoes.Add;
        foreach (var write in new[] { Write("counter", 1), Write("counter", 101), Write("secret", 9), Write(kind: WorldDocumentWriteKind.Add, row: "counter", value: 2) }) {
            fixture.Server.EnqueueMutation(write, sourceAddonInstanceId: 7);
        }
        fixture.Step();
        Assert.Equal(4, echoes.Count);
        Assert.False(condition: echoes[0].Rejected);
        Assert.True(condition: echoes[1].Rejected);
        Assert.True(condition: echoes[2].Denied);
        Assert.False(condition: echoes[3].Rejected);
        Assert.Equal(3, fixture.Server.ExportDefinition().State[0].Cells![0].Value.AsInt);
        Assert.Equal(0, fixture.Server.ExportDefinition().State[1].Cells![0].Value.AsInt);
    }
}
