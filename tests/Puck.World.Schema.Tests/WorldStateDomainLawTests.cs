using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldStateDomain"/> — the declared cell-domain union that replaced the five
/// hand-kept discriminators (<c>Board</c>/<c>Tokens</c>/<c>Zone</c>/<c>KeysFrom</c>/<c>History</c>). Each case is
/// proved against its own control (a sibling case the same probe must answer differently for), plus the
/// $type-discriminated JSON round trip and the unauthored-row inference every plain row still gets for free.
/// </summary>
public sealed class WorldStateDomainLawTests {
    private static WorldDefinition Document(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(World: rows, Lattices: [
            new WorldStateLatticeTopology.Grid(Name: "board", Origin: new Puck.Assets.Documents.DocumentVector3(0, 0, 0), CellSize: 1, Width: 2, Depth: 2),
        ])
    );
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason) ? string.Empty : reason);
    private static WorldStateRow RoundTrip(WorldDefinition definition, string name) {
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        return WorldDefinitionRows.FindStateRow(rows: parsed.State, name: name)!;
    }

    [Fact]
    public void UnauthoredRow_InfersSlotOrKeys_FromCellsAlone() {
        var emptySlot = new WorldStateRow(WorldCellName.Parse("empty"), CellKind.Int);
        Assert.True(emptySlot.IsSlot);
        Assert.False(emptySlot.IsKeyed);
        Assert.IsType<WorldStateDomain.Slot>(@object: emptySlot.EffectiveDomain);

        var oneValue = new WorldStateRow(WorldCellName.Parse("v"), CellKind.Int, Cells: [new(WorldStateRow.SlotKey, 5)]);
        Assert.True(oneValue.IsSlot);
        Assert.IsType<WorldStateDomain.Slot>(@object: oneValue.EffectiveDomain);

        // Control: a declared capacity, or more than one cell, or a single non-slot-keyed cell all infer Keys instead.
        var byCapacity = new WorldStateRow(WorldCellName.Parse("cap"), CellKind.Int, Capacity: 3);
        var byCellCount = new WorldStateRow(WorldCellName.Parse("many"), CellKind.Int, Cells: [new(WorldCellName.Parse("a"), 1), new(WorldCellName.Parse("b"), 2)]);
        var byKeyedSingle = new WorldStateRow(WorldCellName.Parse("one"), CellKind.Int, Cells: [new(WorldCellName.Parse("a"), 1)]);
        foreach (var row in new[] { byCapacity, byCellCount, byKeyedSingle }) {
            Assert.False(row.IsSlot);
            Assert.True(row.IsKeyed);
            Assert.IsType<WorldStateDomain.Keys>(@object: row.EffectiveDomain);
        }

        Assert.Same(expected: WorldStateDomain.Slot.Instance, actual: emptySlot.EffectiveDomain);
        Assert.Same(expected: WorldStateDomain.Keys.Instance, actual: byCapacity.EffectiveDomain);
    }

    [Fact]
    public void KeysOf_OrderedGivesZoneSemanticsAndUnorderedGivesAttributeSemantics() {
        var zone = new WorldStateRow(WorldCellName.Parse("pile"), CellKind.Bool, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards"), Ordered: true), Capacity: 1, Cells: [new(WorldCellName.Parse("t1"), 1)]);
        var attribute = new WorldStateRow(WorldCellName.Parse("rank"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards")), Cells: [new(WorldCellName.Parse("t1"), 7)]);

        Assert.True(zone.IsKeyed);
        Assert.True(attribute.IsKeyed);
        var zoneDomain = Assert.IsType<WorldStateDomain.KeysOf>(@object: zone.EffectiveDomain);
        var attributeDomain = Assert.IsType<WorldStateDomain.KeysOf>(@object: attribute.EffectiveDomain);
        Assert.True(zoneDomain.Ordered);
        Assert.False(attributeDomain.Ordered);
        Assert.Equal(expected: "cards", actual: zoneDomain.Row.Value);

        // Control: the underlying domain row is a plain Keys row, not a KeysOf — a row can point AT a domain without
        // becoming indistinguishable from one.
        var domainRow = new WorldStateRow(WorldCellName.Parse("domain"), CellKind.Int, Capacity: 4);
        Assert.IsType<WorldStateDomain.Keys>(@object: domainRow.EffectiveDomain);

        var cards = new WorldStateRow(WorldCellName.Parse("cards"), CellKind.Int, Capacity: 1, Cells: [new(WorldCellName.Parse("t1"), 0)]);
        var deck = Document(cards, zone);
        Assert.Equal(expected: string.Empty, actual: Validate(deck));

        var badKind = Document(cards, zone with { Kind = CellKind.Int });
        Assert.Contains(expectedSubstring: "boolean membership", actualString: Validate(badKind));
    }

    [Fact]
    public void KeysOf_RefusesAnEmptyDomainAndABoardDomain() {
        var emptyDomain = new WorldStateRow(WorldCellName.Parse("cards"), CellKind.Int, Capacity: 4);
        var attribute = new WorldStateRow(WorldCellName.Parse("rank"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("cards")), Cells: [new(WorldCellName.Parse("t1"), 7)]);
        Assert.Contains(expectedSubstring: "outside token domain", actualString: Validate(Document(emptyDomain, attribute)));

        // Control: the same attribute row against a domain row that already carries its keys validates clean.
        var populatedDomain = emptyDomain with { Cells = [new(WorldCellName.Parse("t1"), 0)] };
        Assert.Equal(expected: string.Empty, actual: Validate(Document(populatedDomain, attribute)));

        var board = new WorldStateRow(WorldCellName.Parse("board"), CellKind.Int, Domain: new WorldStateDomain.CellsOf(Topology: "board"));
        var attributeOverBoard = new WorldStateRow(WorldCellName.Parse("owner"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("board")));
        Assert.Contains(expectedSubstring: "names no token domain", actualString: Validate(Document(board, attributeOverBoard)));
    }

    [Fact]
    public void CellsOf_AddressesATopologyAndAdmitsAnUnwrittenEmptyFill() {
        var board = new WorldStateRow(WorldCellName.Parse("occupancy"), CellKind.Int, Domain: new WorldStateDomain.CellsOf(Topology: "board", Empty: -1), Cells: [new(WorldCellName.Parse("0"), 3)]);
        var domain = Assert.IsType<WorldStateDomain.CellsOf>(@object: board.EffectiveDomain);
        Assert.Equal(expected: "board", actual: domain.Topology);
        Assert.Equal(expected: -1L, actual: domain.Empty);
        Assert.True(board.IsKeyed);

        Assert.Equal(expected: string.Empty, actual: Validate(Document(board)));

        // Control: naming a topology the document never declares is refused by name, not silently accepted.
        var dangling = board with { Domain = new WorldStateDomain.CellsOf(Topology: "nowhere") };
        Assert.Contains(expectedSubstring: "no valid discrete topology", actualString: Validate(Document(dangling)));
    }

    [Fact]
    public void Ring_KeepsCapacitySlotsAndAnEmptyFillOffTheDomainNotTheRow() {
        var ring = new WorldStateRow(WorldCellName.Parse("taps"), CellKind.Int, Domain: new WorldStateDomain.Ring(Capacity: 3, Empty: -1), HistoryCursor: 1, Cells: [new(WorldCellName.Parse("0"), 9)]);
        var domain = Assert.IsType<WorldStateDomain.Ring>(@object: ring.EffectiveDomain);
        Assert.Equal(expected: 3, actual: domain.Capacity);
        Assert.Equal(expected: -1L, actual: domain.Empty);
        Assert.True(ring.IsKeyed);
        Assert.Equal(expected: string.Empty, actual: Validate(Document(ring)));

        // Control: a ring authoring its own row-level capacity (rather than the domain's) is refused — the domain,
        // not the row, is where a ring's capacity lives.
        var doubled = ring with { Capacity = 3 };
        Assert.Contains(expectedSubstring: "lives on its domain", actualString: Validate(Document(doubled)));
    }

    [Fact]
    public void EveryCase_RoundTripsThroughTheDollarTypeDiscriminatedWire() {
        var slot = new WorldStateRow(WorldCellName.Parse("slot"), CellKind.Int, Domain: new WorldStateDomain.Slot(), Cells: [new(WorldStateRow.SlotKey, 1)]);
        var keys = new WorldStateRow(WorldCellName.Parse("keys"), CellKind.Int, Domain: new WorldStateDomain.Keys(), Capacity: 2, Cells: [new(WorldCellName.Parse("a"), 1)]);
        var keysOf = new WorldStateRow(WorldCellName.Parse("keysOf"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("keys"), Ordered: false), Cells: [new(WorldCellName.Parse("a"), 1)]);
        var cellsOf = new WorldStateRow(WorldCellName.Parse("cellsOf"), CellKind.Int, Domain: new WorldStateDomain.CellsOf(Topology: "board", Empty: 2));
        var ring = new WorldStateRow(WorldCellName.Parse("ring"), CellKind.Int, Domain: new WorldStateDomain.Ring(Capacity: 4, Empty: 7));

        var definition = Document(slot, keys, keysOf, cellsOf, ring);
        Assert.Equal(expected: string.Empty, actual: Validate(definition));

        Assert.IsType<WorldStateDomain.Slot>(@object: RoundTrip(definition, "slot").Domain);
        Assert.IsType<WorldStateDomain.Keys>(@object: RoundTrip(definition, "keys").Domain);
        Assert.Equal(expected: keysOf.Domain, actual: RoundTrip(definition, "keysOf").Domain);
        Assert.Equal(expected: cellsOf.Domain, actual: RoundTrip(definition, "cellsOf").Domain);
        Assert.Equal(expected: ring.Domain, actual: RoundTrip(definition, "ring").Domain);
    }

    [Fact]
    public void UnauthoredPhaseRow_InfersKeys_NeverSlot() {
        var phase = new WorldStateRow(WorldCellName.Parse("turns"), CellKind.Int, Phase: new WorldStatePhase(Sequence: 1));
        Assert.True(phase.IsKeyed);
        Assert.False(phase.IsSlot);
        Assert.IsType<WorldStateDomain.Keys>(@object: phase.EffectiveDomain);
        Assert.Equal(expected: string.Empty, actual: Validate(Document(phase)));

        // Control: an authored Slot domain contradicts a phase trait and is refused rather than silently admitted.
        var slotPhase = phase with { Domain = new WorldStateDomain.Slot() };
        Assert.Contains(expectedSubstring: "phase requires an integer row without cells/capacity", actualString: Validate(Document(slotPhase)));
    }

    [Fact]
    public void CellCeiling_HonoursAnAuthoredCapacityUpToTheOneBoundAndDefaultsTheRoomOtherwise() {
        var ordinary = new WorldStateRow(WorldCellName.Parse("plain"), CellKind.Int, Capacity: 200);
        var linked = new WorldStateRow(WorldCellName.Parse("linked"), CellKind.Int, Domain: new WorldStateDomain.KeysOf(WorldCellName.Parse("domain")), Capacity: 200);
        var unauthored = new WorldStateRow(WorldCellName.Parse("room"), CellKind.Int, Cells: [new WorldStateCell(WorldCellName.Parse("a"), 0L)]);
        var oversized = new WorldStateRow(WorldCellName.Parse("big"), CellKind.Int, Capacity: WorldStateCapacity.MaxCellsPerRow + 1);

        Assert.Equal(expected: 200, actual: ordinary.CellCeiling);
        Assert.Equal(expected: 200, actual: linked.CellCeiling);
        Assert.Equal(expected: WorldStateCapacity.DefaultCellRoom, actual: unauthored.CellCeiling);
        Assert.Equal(expected: WorldStateCapacity.MaxCellsPerRow, actual: oversized.CellCeiling);
    }
}
