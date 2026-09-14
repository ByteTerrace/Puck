using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="StateDomain"/> — the declared cell-domain union that replaced the five
/// hand-kept discriminators (<c>Board</c>/<c>Tokens</c>/<c>Zone</c>/<c>KeysFrom</c>/<c>History</c>). Each case is
/// proved against its own control (a sibling case the same probe must answer differently for), plus the
/// $type-discriminated JSON round trip and the unauthored-row inference every plain row still gets for free.
/// </summary>
public sealed class WorldStateDomainLawTests {
    private static WorldDefinition Document(params WorldStateRow[] rows) => new(
        Simulation: new WorldSimulationDefaults(RateHz: 240),
        StateRaw: new WorldStateSection(
            World: rows,
            Lattices: [
            new LatticeTopology.Grid(
                    Name: "board",
                    Origin: new Puck.Assets.Documents.DocumentVector3(
                        x: 0,
                        y: 0,
                        z: 0
                    ),
                    CellSize: 1,
                    Width: 2,
                    Depth: 2
                ),
        ]
        )
    );
    private static WorldStateRow RoundTrip(WorldDefinition definition, string name) {
        var parsed = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));

        return WorldDefinitionRows.FindStateRow(
            rows: parsed.State,
            name: name
        )!;
    }
    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void CellCeiling_HonoursAnAuthoredCapacityUpToTheOneBoundAndDefaultsTheRoomOtherwise() {
        var ordinary = new WorldStateRow(
            CellName.Parse(candidate: "plain"),
            CellKind.Int,
            Capacity: 200
        );
        var linked = new WorldStateRow(
            CellName.Parse(candidate: "linked"),
            CellKind.Int,
            Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "domain")),
            Capacity: 200
        );
        var unauthored = new WorldStateRow(
            CellName.Parse(candidate: "room"),
            CellKind.Int,
            Cells: [new StateCell(
                    CellName.Parse(candidate: "a"),
                    0L
                )]
        );
        var oversized = new WorldStateRow(
            CellName.Parse(candidate: "big"),
            CellKind.Int,
            Capacity: (StateCapacity.MaxCellsPerRow + 1)
        );

        Assert.Equal(
            expected: 200,
            actual: ordinary.CellCeiling
        );
        Assert.Equal(
            expected: 200,
            actual: linked.CellCeiling
        );
        Assert.Equal(
            expected: StateCapacity.DefaultCellRoom,
            actual: unauthored.CellCeiling
        );
        Assert.Equal(
            expected: StateCapacity.MaxCellsPerRow,
            actual: oversized.CellCeiling
        );
    }
    [Fact]
    public void CellsOf_AddressesATopologyAndAdmitsAnUnwrittenEmptyFill() {
        var board = new WorldStateRow(
            CellName.Parse(candidate: "occupancy"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf(
                Empty: -1,
                Topology: "board"
            ),
            Cells: [new(
                    CellName.Parse(candidate: "0"),
                    3
                )]
        );
        var domain = Assert.IsType<StateDomain.CellsOf>(@object: board.EffectiveDomain);

        Assert.Equal(
            expected: "board",
            actual: domain.Topology
        );
        Assert.Equal(
            expected: -1L,
            actual: domain.Empty
        );
        Assert.True(condition: board.IsKeyed);

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: Document(board))
        );

        // Control: naming a topology the document never declares is refused by name, not silently accepted.
        var dangling = board with { Domain = new StateDomain.CellsOf(Topology: "nowhere") };

        Assert.Contains(
            expectedSubstring: "no valid discrete topology",
            actualString: Validate(definition: Document(dangling))
        );
    }
    [Fact]
    public void EveryCase_RoundTripsThroughTheDollarTypeDiscriminatedWire() {
        var slot = new WorldStateRow(
            CellName.Parse(candidate: "slot"),
            CellKind.Int,
            Domain: new StateDomain.Slot(),
            Cells: [new(
                    WorldStateRow.SlotKey,
                    1
                )]
        );
        var keys = new WorldStateRow(
            CellName.Parse(candidate: "keys"),
            CellKind.Int,
            Domain: new StateDomain.Keys(),
            Capacity: 2,
            Cells: [new(
                    CellName.Parse(candidate: "a"),
                    1
                )]
        );
        var keysOf = new WorldStateRow(
            CellName.Parse(candidate: "keysOf"),
            CellKind.Int,
            Domain: new StateDomain.KeysOf(
                CellName.Parse(candidate: "keys"),
                Ordered: false
            ),
            Cells: [new(
                    CellName.Parse(candidate: "a"),
                    1
                )]
        );
        var cellsOf = new WorldStateRow(
            CellName.Parse(candidate: "cellsOf"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf(
                Empty: 2,
                Topology: "board"
            )
        );
        var ring = new WorldStateRow(
            CellName.Parse(candidate: "ring"),
            CellKind.Int,
            Domain: new StateDomain.Ring(
                Capacity: 4,
                Empty: 7
            )
        );

        var definition = Document(
            slot,
            keys,
            keysOf,
            cellsOf,
            ring
        );

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: definition)
        );

        Assert.IsType<StateDomain.Slot>(@object: RoundTrip(
            definition: definition,
            name: "slot"
        ).Domain);
        Assert.IsType<StateDomain.Keys>(@object: RoundTrip(
            definition: definition,
            name: "keys"
        ).Domain);
        Assert.Equal(
            expected: keysOf.Domain,
            actual: RoundTrip(
                definition: definition,
                name: "keysOf"
            ).Domain
        );
        Assert.Equal(
            expected: cellsOf.Domain,
            actual: RoundTrip(
                definition: definition,
                name: "cellsOf"
            ).Domain
        );
        Assert.Equal(
            expected: ring.Domain,
            actual: RoundTrip(
                definition: definition,
                name: "ring"
            ).Domain
        );
    }
    [Fact]
    public void KeysOf_OrderedGivesZoneSemanticsAndUnorderedGivesAttributeSemantics() {
        var zone = new WorldStateRow(
            CellName.Parse(candidate: "pile"),
            CellKind.Bool,
            Domain: new StateDomain.KeysOf(
                CellName.Parse(candidate: "cards"),
                Ordered: true
            ),
            Capacity: 1,
            Cells: [new(
                    CellName.Parse(candidate: "t1"),
                    1
                )]
        );
        var attribute = new WorldStateRow(
            CellName.Parse(candidate: "rank"),
            CellKind.Int,
            Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
            Cells: [new(
                    CellName.Parse(candidate: "t1"),
                    7
                )]
        );

        Assert.True(condition: zone.IsKeyed);
        Assert.True(condition: attribute.IsKeyed);
        var zoneDomain = Assert.IsType<StateDomain.KeysOf>(@object: zone.EffectiveDomain);
        var attributeDomain = Assert.IsType<StateDomain.KeysOf>(@object: attribute.EffectiveDomain);

        Assert.True(condition: zoneDomain.Ordered);
        Assert.False(condition: attributeDomain.Ordered);
        Assert.Equal(
            expected: "cards",
            actual: zoneDomain.Row.Value
        );

        // Control: the underlying domain row is a plain Keys row, not a KeysOf — a row can point AT a domain without
        // becoming indistinguishable from one.
        var domainRow = new WorldStateRow(
            CellName.Parse(candidate: "domain"),
            CellKind.Int,
            Capacity: 4
        );

        Assert.IsType<StateDomain.Keys>(@object: domainRow.EffectiveDomain);

        var cards = new WorldStateRow(
            CellName.Parse(candidate: "cards"),
            CellKind.Int,
            Capacity: 1,
            Cells: [new(
                    CellName.Parse(candidate: "t1"),
                    0
                )]
        );
        var deck = Document(
            cards,
            zone
        );

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: deck)
        );

        var badKind = Document(
            cards,
            zone with { Kind = CellKind.Int }
        );

        Assert.Contains(
            expectedSubstring: "boolean membership",
            actualString: Validate(definition: badKind)
        );
    }
    [Fact]
    public void KeysOf_RefusesAnEmptyDomainAndABoardDomain() {
        var emptyDomain = new WorldStateRow(
            CellName.Parse(candidate: "cards"),
            CellKind.Int,
            Capacity: 4
        );
        var attribute = new WorldStateRow(
            CellName.Parse(candidate: "rank"),
            CellKind.Int,
            Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "cards")),
            Cells: [new(
                    CellName.Parse(candidate: "t1"),
                    7
                )]
        );

        Assert.Contains(
            expectedSubstring: "outside token domain",
            actualString: Validate(definition: Document(
                emptyDomain,
                attribute
            ))
        );

        // Control: the same attribute row against a domain row that already carries its keys validates clean.
        var populatedDomain = emptyDomain with { Cells = [new(
                CellName.Parse(candidate: "t1"),
                0
            )] };

        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: Document(
                populatedDomain,
                attribute
            ))
        );

        var board = new WorldStateRow(
            CellName.Parse(candidate: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf(Topology: "board")
        );
        var attributeOverBoard = new WorldStateRow(
            CellName.Parse(candidate: "owner"),
            CellKind.Int,
            Domain: new StateDomain.KeysOf(CellName.Parse(candidate: "board"))
        );

        Assert.Contains(
            expectedSubstring: "names no token domain",
            actualString: Validate(definition: Document(
                board,
                attributeOverBoard
            ))
        );
    }
    [Fact]
    public void Ring_KeepsCapacitySlotsAndAnEmptyFillOffTheDomainNotTheRow() {
        var ring = new WorldStateRow(
            CellName.Parse(candidate: "taps"),
            CellKind.Int,
            Domain: new StateDomain.Ring(
                Capacity: 3,
                Empty: -1
            ),
            HistoryCursor: 1,
            Cells: [new(
                    CellName.Parse(candidate: "0"),
                    9
                )]
        );
        var domain = Assert.IsType<StateDomain.Ring>(@object: ring.EffectiveDomain);

        Assert.Equal(
            expected: 3,
            actual: domain.Capacity
        );
        Assert.Equal(
            expected: -1L,
            actual: domain.Empty
        );
        Assert.True(condition: ring.IsKeyed);
        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: Document(ring))
        );

        // Control: a ring authoring its own row-level capacity (rather than the domain's) is refused — the domain,
        // not the row, is where a ring's capacity lives.
        var doubled = ring with { Capacity = 3 };

        Assert.Contains(
            expectedSubstring: "lives on its domain",
            actualString: Validate(definition: Document(doubled))
        );
    }
    [Fact]
    public void UnauthoredPhaseRow_InfersKeys_NeverSlot() {
        var phase = new WorldStateRow(
            CellName.Parse(candidate: "turns"),
            CellKind.Int,
            Phase: new StatePhase(Sequence: 1)
        );

        Assert.True(condition: phase.IsKeyed);
        Assert.False(condition: phase.IsSlot);
        Assert.IsType<StateDomain.Keys>(@object: phase.EffectiveDomain);
        Assert.Equal(
            expected: string.Empty,
            actual: Validate(definition: Document(phase))
        );

        // Control: an authored Slot domain contradicts a phase trait and is refused rather than silently admitted.
        var slotPhase = phase with { Domain = new StateDomain.Slot() };

        Assert.Contains(
            expectedSubstring: "phase requires an integer row without cells/capacity",
            actualString: Validate(definition: Document(slotPhase))
        );
    }
    [Fact]
    public void UnauthoredRow_InfersSlotOrKeys_FromCellsAlone() {
        var emptySlot = new WorldStateRow(
            CellName.Parse(candidate: "empty"),
            CellKind.Int
        );

        Assert.True(condition: emptySlot.IsSlot);
        Assert.False(condition: emptySlot.IsKeyed);
        Assert.IsType<StateDomain.Slot>(@object: emptySlot.EffectiveDomain);

        var oneValue = new WorldStateRow(
            CellName.Parse(candidate: "v"),
            CellKind.Int,
            Cells: [new(
                    WorldStateRow.SlotKey,
                    5
                )]
        );

        Assert.True(condition: oneValue.IsSlot);
        Assert.IsType<StateDomain.Slot>(@object: oneValue.EffectiveDomain);

        // Control: a declared capacity, or more than one cell, or a single non-slot-keyed cell all infer Keys instead.
        var byCapacity = new WorldStateRow(
            CellName.Parse(candidate: "cap"),
            CellKind.Int,
            Capacity: 3
        );
        var byCellCount = new WorldStateRow(
            CellName.Parse(candidate: "many"),
            CellKind.Int,
            Cells: [new(
                    CellName.Parse(candidate: "a"),
                    1
                ), new(
                    CellName.Parse(candidate: "b"),
                    2
                )]
        );
        var byKeyedSingle = new WorldStateRow(
            CellName.Parse(candidate: "one"),
            CellKind.Int,
            Cells: [new(
                    CellName.Parse(candidate: "a"),
                    1
                )]
        );

        foreach (var row in new[] { byCapacity, byCellCount, byKeyedSingle }) {
            Assert.False(condition: row.IsSlot);
            Assert.True(condition: row.IsKeyed);
            Assert.IsType<StateDomain.Keys>(@object: row.EffectiveDomain);
        }

        Assert.Same(
            expected: StateDomain.Slot.Instance,
            actual: emptySlot.EffectiveDomain
        );
        Assert.Same(
            expected: StateDomain.Keys.Instance,
            actual: byCapacity.EffectiveDomain
        );
    }
}
