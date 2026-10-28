using Puck.Commands;
using Puck.Maths;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class InputPatternTests {
    private static WorldInputOwner Owner(params WorldInputPattern[] patterns) {
        var catalog = WorldInputCatalog.Compile(declarations: [new("text", Protocol.ChannelShape.Text, Composition: true)], modes: null);

        return new(catalog, catalog.DefaultMapping, new(), [], patterns: patterns);
    }
    private static void Text(WorldInputOwner owner, string text, ulong tick = 1) {
        var events = text.Select(selector: (value, index) => new WorldInputEvent(0, WorldInputEventKind.Text, value, tick,
            FixedQ4816.Zero, (((uint)index) + 1), 0)).ToArray();

        Assert.True(condition: owner.TryAdmit(new(owner.Revision, owner.Epoch, owner.Mapping.Id, tick, default, default, events),
            new(tick, ((tick - 1) * 1680), 1680), [], out var reason), userMessage: reason);
    }
    private static WorldInputPattern Pair(string name = "pair", string? group = null, bool simultaneous = false) => new(name,
        [new("text", WorldInputEventKind.Text, 'h', SimultaneousGroup: (simultaneous ? "pair" : null)),
         new("text", WorldInputEventKind.Text, 'i', SimultaneousGroup: (simultaneous ? "pair" : null))], ConsumptionGroup: group);

    [Fact]
    public void OrderedRepeatedPrefixChoosesLexicographicIdsAtEqualTimesAndConsumptionRestores() {
        var owner = Owner(Pair());

        Text(owner, "hhi");
        Assert.True(condition: owner.TryMatch(0, new(1, 0, 1680), out var match));
        Assert.Equal(1UL, match.First);
        Assert.Equal(3UL, match.Last);
        Assert.True(condition: owner.Consume(match: match, pattern: 0));
        var restored = Owner(Pair());

        restored.Restore(checkpoint: owner.Capture());
        Assert.False(condition: restored.TryMatch(0, new(1, 0, 1680), out _));
    }
    [Fact]
    public void SimultaneousPredicatesIgnoreSourceOrderAndRespectDocumentConsumptionPriority() {
        var owner = Owner(Pair(group: "shared", name: "first", simultaneous: true), Pair(group: "shared", name: "second", simultaneous: true));

        Text(owner, "ih");
        Assert.False(condition: owner.TryMatch(1, new(1, 0, 1680), out _));
        Assert.True(condition: owner.TryMatch(0, new(1, 0, 1680), out var match));
        Assert.True(condition: owner.Consume(match: match, pattern: 0));
        Assert.False(condition: owner.TryMatch(1, new(1, 0, 1680), out _));
    }
    [Fact]
    public void BufferedEventExpiresAtTheExactAdmittedTickAndMatchingAllocatesNothing() {
        var owner = Owner(new WorldInputPattern("key", [new("text", WorldInputEventKind.Text, 'h')], BufferTicks: 2));

        Text(owner, "h");
        Assert.True(condition: owner.TryMatch(0, new(3, 3360, 1680), out _));
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 1000); index++) {
            if (!owner.TryMatch(0, new(3, 3360, 1680), out _)) { throw new InvalidOperationException(); }
        }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
        Assert.False(condition: owner.TryMatch(0, new(4, 5040, 1680), out _));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ConsumptionSharesTheRuleCommit(bool refuse) {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            ChannelsRaw = [.. basis.Channels, new("text", Protocol.ChannelShape.Text, Composition: true)],
            InputPatterns = [Pair()],
            InputHistory = new(EventCapacity: 16, MaximumEventsPerStep: 16),
            StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: CellName.Parse(candidate: "score"), Kind: CellKind.Int,
                Min: 0, Max: 1, Cells: [new(WorldStateRow.SlotKey, CellValue.Int(value: 0))])]),
            Rules = [new WorldRule(Name: CellName.Parse(candidate: "judge"),
                Gate: new ActionPredicate.CompareState("$inputMatch:1:pair", ExpressionOp.Equal, 1m),
                Effects: [new WorldEffect.ConsumeInput(Owner: "1", Pattern: "pair"), new ActionEffect.SetState("score", Value: (refuse ? 2m : 1m))])],
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat(); fixture.Step();
        var input = fixture.Server.Input.Admit(Principal.Seat(slot: 0), 0, []);

        Assert.True(condition: fixture.Server.Population.InputCatalog.TryGetLogicalId(id: out var channel, name: "text"));
        var interval = fixture.Server.Input.Interval;
        var events = new WorldInputEvent[] { new(channel, WorldInputEventKind.Text, 'h', interval.Tick, default, 1, 0),
            new(channel, WorldInputEventKind.Text, 'i', interval.Tick, default, 2, 0) };

        Assert.True(condition: fixture.Server.Input.TryAdmit(Principal.Seat(slot: 0), new(input.Revision, input.Epoch, input.Mapping.Id,
            interval.Tick, default, default, events, input.Mapping.NumericCount), out var reason), userMessage: reason);
        fixture.Step();
        Assert.Equal(refuse, input.TryMatch(0, fixture.Server.Input.Interval, out _));
        Assert.Equal((refuse ? 0 : 1), fixture.Row(name: "score").Cells!.Single().Value.Raw);
    }
}
