using System.Text;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins the in-place shuffle's determinism and cursor accounting, and what a hidden cell leaves behind
/// under each disclosure policy.</summary>
public sealed class WorldDeckAndDisclosureLawTests {
    [Fact]
    public void ShuffleIsAPermutationThatSpendsOneSamplePerPositionAndReplaysExactly() {
        var definition = Deck(52);

        var shuffled = Apply(definition, new WorldStateTransform.Shuffle("deck", "dice"));
        var order = Find(shuffled, "deck").Cells!.Select(c => c.Key.Value).ToArray();

        Assert.Equal(52, order.Length);
        Assert.Equal(52, order.Distinct().Count());
        Assert.NotEqual(Find(definition, "deck").Cells!.Select(c => c.Key.Value), order);
        Assert.Equal(51L, Find(shuffled, "dice").DrawCursor);

        var again = Apply(definition, new WorldStateTransform.Shuffle("deck", "dice"));
        Assert.Equal(order, Find(again, "deck").Cells!.Select(c => c.Key.Value));

        var later = Apply(shuffled, new WorldStateTransform.Shuffle("deck", "dice"));
        Assert.NotEqual(order, Find(later, "deck").Cells!.Select(c => c.Key.Value));
        Assert.Equal(102L, Find(later, "dice").DrawCursor);
    }

    [Fact]
    public void ShuffleRefusesUnorderedZonesAndBootOnlySites() {
        var unordered = Deck(4) with { };
        unordered = unordered with { StateRaw = unordered.StateRaw! with { World = unordered.StateRaw.World!.Select(r => r.Name.Value == "deck" ? r with { Zone = new("cards", Ordered: false) } : r).ToArray() } };
        Assert.False(WorldStateTransforms.TryApply(unordered, new WorldStateTransform.Shuffle("deck", "dice"), WorldPrincipal.World, 0, "test", out _, out var reason));
        Assert.Contains("ordered zone", reason);

        var bootSite = Deck(4);
        bootSite = bootSite with { StateRaw = bootSite.StateRaw! with { World = bootSite.StateRaw.World!.Select(r => r.Name.Value == "dice" ? r with { Draw = r.Draw! with { Timing = WorldDrawTiming.Boot } } : r).ToArray() } };
        Assert.False(WorldStateTransforms.TryApply(bootSite, new WorldStateTransform.Shuffle("deck", "dice"), WorldPrincipal.World, 0, "test", out _, out var siteReason));
        Assert.Contains("streamDraw site", siteReason);

        var ruled = Deck(4) with { Rules = [new WorldRule(WorldCellName.Parse("shuffle"), [new ActionEffect.TransformState(new WorldStateTransform.Shuffle("deck", "dice"))])] };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(ruled, out var ok), ok);
        var badRule = Deck(4) with { Rules = [new WorldRule(WorldCellName.Parse("shuffle"), [new ActionEffect.TransformState(new WorldStateTransform.Shuffle("deck", "deck"))])] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(badRule, out _));
    }

    [Fact]
    public void HiddenCellsLeaveExactlyWhatThePolicyAllows() {
        foreach (var (policy, cells, count) in new[] { (WorldHiddenCells.Omit, 0, 0), (WorldHiddenCells.Count, 0, 2), (WorldHiddenCells.Placeholder, 2, 2) }) {
            var definition = Hand(policy);
            var opponent = WorldStateDisclosure.Compose(definition, WorldPrincipal.Seat(1))!.Single(r => r.Name == "hand");
            var owner = WorldStateDisclosure.Compose(definition, WorldPrincipal.Seat(0))!.Single(r => r.Name == "hand");

            Assert.Equal(count, opponent.HiddenCount);
            Assert.Equal(cells, opponent.Cells.Count);
            Assert.All(opponent.Cells, c => { Assert.True(c.Hidden); Assert.Equal(string.Empty, c.Key); Assert.Null(c.Text); });
            Assert.Equal(0, owner.HiddenCount);
            Assert.Equal(new[] { "ace", "king" }, owner.Cells.Select(c => c.Key));

            var json = Encoding.UTF8.GetString(WorldProjection.Serialize(WorldProjection.Compose(definition, WorldDisclosureTier.Presentation, "test", 1, WorldPrincipal.Seat(1))!));
            Assert.DoesNotContain("\"ace\"", json);
            Assert.DoesNotContain("\"king\"", json);
            Assert.Equal(policy != WorldHiddenCells.Omit, json.Contains("hiddenCount"));
        }
    }

    [Fact]
    public void HiddenPolicyRoundTripsThroughTheStrictWireShape() {
        var definition = Hand(WorldHiddenCells.Placeholder);
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        Assert.Equal(WorldHiddenCells.Placeholder, Find(parsed, "hand").Visibility!.Hidden);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(parsed, out var reason), reason);
    }

    private static WorldDefinition Deck(int count) {
        var keys = Enumerable.Range(0, count).Select(i => $"c{i}").ToArray();
        return Fixtures.BuildDocument() with {
            StateRaw = new(World: [
                new(Name("cards"), CellKind.Int, Cells: keys.Select(k => Cell(k)).ToArray(), Tokens: new(), Capacity: count),
                new(Name("deck"), CellKind.Bool, Cells: keys.Select(k => Cell(k)).ToArray(), Zone: new("cards", Ordered: true), Capacity: count),
                new(Name("dice"), CellKind.Int, Draw: new WorldDraw(Generator: new WorldGenerator(Source: WorldGeneratorSource.StreamDraw), Timing: WorldDrawTiming.Event)),
            ]),
            Rules = [],
        };
    }
    private static WorldDefinition Hand(WorldHiddenCells hidden) => Fixtures.BuildDocument() with {
        StateRaw = new(World: [
            new(Name("cards"), CellKind.Int, Cells: [Cell("ace", 101), Cell("king", 202)], Tokens: new(), Visibility: new()),
            new(Name("hand"), CellKind.Bool, Cells: [Cell("ace") with { Visibility = new(["seat1"]) }, Cell("king") with { Visibility = new(["seat1"]) }], Zone: new("cards", Ordered: true), Visibility: new(Hidden: hidden)),
        ]),
        Rules = [],
    };
    private static WorldDefinition Apply(WorldDefinition definition, WorldStateTransform transform) {
        Assert.True(WorldStateTransforms.TryApply(definition, transform, WorldPrincipal.World, 1, "test", out var candidate, out var reason), reason);
        return candidate!;
    }
    private static WorldCellName Name(string value) => WorldCellName.Parse(value);
    private static WorldStateCell Cell(string key, long value = 1) => new(Name(key), value);
    private static WorldStateRow Find(WorldDefinition document, string row) => WorldDefinitionRows.FindStateRow(document.State, row)!;
}
