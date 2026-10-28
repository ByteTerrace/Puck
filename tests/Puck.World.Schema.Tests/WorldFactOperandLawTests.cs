using Puck.State.Rules;
using Puck.Commands;
using Xunit;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;

namespace Puck.World.Schema.Tests;

/// <summary>A world operand answers in the encoding its host answers in, and a live child key reaches the host as the
/// key it resolved to.</summary>
public sealed class WorldFactOperandLawTests {
    [InlineData("roster", "roster", false)]
    [InlineData("roster", "scope", false)]
    [InlineData("scope", "scope", true)]
    [Theory]
    public void QuiescenceAdmitsBothScopeDomainsAndRefusesAnAmbiguousName(string rowName, string selection, bool ambiguous) {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(Name: CellName.Parse(candidate: rowName), Kind: CellKind.Int, Capacity: 1, Cells: []),
                StateFixtures.IntSlot("witness"),
                new WorldStateRow(Name: CellName.Parse(candidate: "control"), Kind: CellKind.Int,
                    Cells: [StateFixtures.Cell("request", 0), StateFixtures.Cell("status", 0),
                        StateFixtures.Cell("cursor", 0), StateFixtures.Cell("completed", 0)]),
            ]),
            ReplaysRaw = [new WorldScopedReplay("scope", ["witness"], [0], [Principal.Console], "control", 1, [])],
            Rules = [new WorldRule(CellName.Parse(candidate: "quiet"), [new ActionEffect.SetState("witness", 1m)],
                Gate: new ActionPredicate.CompareState(((WorldRuleFacts.PhysicsQuiescent + ":") + selection), ExpressionOp.Equal, 1m),
                Mode: ActionTriggerMode.Level)],
        };

        if (ambiguous) {
            var refusal = Assert.Throws<RuleException>(testCode: () => WorldFactsCompiler.CompileAll(definition: definition));

            Assert.Contains(expectedSubstring: "ambiguous", actualString: refusal.Message);
        } else {
            Assert.Single(collection: WorldFactsCompiler.CompileAll(definition: definition));
        }
    }

    private const string Channel = "water";
    private const string ChildKey = "3";
    private const string PlacementId = "granaryStores";
    private const string SelectorRow = "selection";
    private const string TargetRow = "targets";

    private static StateSection Section() => new(Rows: [
        new StateRow(
            Name: CellName.Parse(candidate: SelectorRow),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: StateRow.SlotKey,
                    Value: CellValue.Int(value: 3L)
                )]
        ),
        new StateRow(
            Name: CellName.Parse(candidate: TargetRow),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [new StateCell(
                    Key: CellName.Parse(candidate: ChildKey),
                    Value: CellValue.Int(value: 0L)
                )]
        ),
    ]);
    private static WorldPlacementInfluenceOperand Operand(CompiledCellRef? keyFrom, string? childKey = null) => new(
        channel: Channel,
        childKey: childKey,
        keyFrom: keyFrom,
        placementId: PlacementId
    );

    [InlineData(TopologyKind.Grid)]
    [InlineData(TopologyKind.Box)]
    [InlineData(TopologyKind.Hex)]
    [InlineData(TopologyKind.Graph)]
    [InlineData(TopologyKind.Tiling)]
    [Theory]
    public void CellOfPricesDirectCoordinateResolutionIndependentlyOfBoardSizeAndScansGraphCentres(TopologyKind kind) {
        long Price(int size) {
            var origin = new Puck.Assets.Documents.DocumentVector3(x: 0, y: 0, z: 0);
            LatticeTopology topology = kind switch {
                TopologyKind.Grid => new LatticeTopology.Grid("grid", origin, 1, size, size),
                TopologyKind.Box => new LatticeTopology.Box("grid", origin, 1, size, size, size, 1),
                TopologyKind.Hex => new LatticeTopology.Hex("grid", origin, 1, size),
                TopologyKind.Tiling => new LatticeTopology.Tiling(CellSize: 1, Family: TilingFamily.Triangular, Name: "grid", Origin: origin, Radius: size),
                _ => new LatticeTopology.Graph("grid", origin, 1,
                    Enumerable.Range(count: size, start: 0).Select(selector: i => new GraphCell(i.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                        new Puck.Assets.Documents.DocumentVector3(x: i, y: 0, z: 0))).ToArray(),
                    [new GraphDirection(Name: "next", Opposite: "next")], []),
            };
            var definition = Fixtures.BuildDocument() with {
                StateRaw = new WorldStateSection(Lattices: [topology], World: [
                    new WorldStateRow(CellName.Parse(candidate: "board"), CellKind.Int, Domain: new StateDomain.CellsOf("grid")),
                    StateFixtures.IntSlot("point"),
                ]),
                Rules = [new WorldRule(CellName.Parse(candidate: "cell-of"), [
                    new ActionEffect.SetState(State: "point", FromState: "$board:cellOf:board:body:0"),
                ])],
            };
            var context = WorldFactsCompiler.Context(definition: definition);
            var rule = Assert.Single(collection: WorldFactsCompiler.CompileAll(definition: definition));

            return RuleWorkBudget.EffectsCost(rule.Effects, context).Units;
        }

        if (kind is TopologyKind.Graph or TopologyKind.Tiling) {
            Assert.True(condition: (Price(size: 3) > Price(size: 1)));
        } else {
            Assert.Equal(Price(size: 1), Price(size: ((kind == TopologyKind.Box) ? 7 : 19)));
        }
    }
    [Fact]
    public void ThePlacementInfluenceOperandReadsInTheIntegerEncodingItsHostAnswersIn() {
        Assert.Equal(
            actual: Operand(keyFrom: null).ValueKind,
            expected: new PlacementInfluenceOperand(
                channel: Channel,
                placementId: PlacementId
            ).ValueKind
        );
        Assert.Equal(
            actual: Operand(keyFrom: null).ValueKind,
            expected: CellKind.Int
        );
    }
    [Fact]
    public void ALiveChildKeyReachesTheHostAsTheKeyItResolvedTo() {
        var reader = new RecordingWorldFacts(section: Section());
        var catalog = reader.Arena.Catalog;

        Assert.True(condition: catalog.TryResolve(
            handle: out var selector,
            lane: StateLane.Document,
            name: SelectorRow
        ));

        var operand = Operand(keyFrom: new CompiledCellRef(
            Key: catalog.Keys.Intern(name: StateRow.SlotKey),
            Kind: CellKind.Int,
            RowOrdinal: selector.Ordinal
        ));

        _ = ((IRuleOperand)operand).Read(reader: reader);

        Assert.NotNull(@object: reader.Influence);
        Assert.Equal(
            actual: reader.Influence!.Key,
            expected: ChildKey
        );
    }
    [Fact]
    public void ALiteralChildKeyStillReachesTheHost() {
        var reader = new RecordingWorldFacts(section: Section());

        _ = ((IRuleOperand)Operand(
            childKey: ChildKey,
            keyFrom: null
        )).Read(reader: reader);

        Assert.NotNull(@object: reader.Influence);
        Assert.Equal(
            actual: reader.Influence!.Key,
            expected: ChildKey
        );
    }
    [Fact]
    public void ALiveChildKeyThatNamesNoCellReadsAbsentWithoutReachingTheHost() {
        var reader = new RecordingWorldFacts(section: Section());
        var operand = Operand(keyFrom: new CompiledCellRef(
            Key: default,
            Kind: CellKind.Int,
            RowOrdinal: -1
        ));
        var fact = ((IRuleOperand)operand).Read(reader: reader);

        Assert.Null(@object: reader.Influence);
        Assert.True(condition: fact.IsAbsent);
    }
    [Fact]
    public void AnIdentityFactReadsItsLaneCellLiveAtTheEvaluationsTime() {
        var reader = new RecordingWorldFacts(section: new StateSection(Rows: [new StateRow(
                Name: CellName.Parse(candidate: WorldIdentityFactLane.RowName),
                Kind: CellKind.Int,
                Capacity: 4,
                Advance: new StateAdvance(
                    PerSecondDenominator: 1L,
                    PerSecondNumerator: 1L
                ),
                Cells: [new StateCell(
                        Key: CellName.Parse(candidate: WorldIdentityFactLane.Key(
                            bodyIndex: 0,
                            fact: "hp"
                        )),
                        Value: CellValue.Int(value: 3L)
                    )]
            )])) {
            ResolvedBody = 0,
        };
        var operand = new WorldIdentityFactOperand(
            body: default,
            capacity: 1,
            fact: "hp",
            laneOrdinal: 0
        );

        Assert.Equal(
            actual: operand.Read(
                facet: reader,
                reader: reader
            ).Value,
            expected: 3L
        );

        reader.EngineTick = (2UL * ((ulong)Puck.Maths.FixedTickConversion.TicksPerSecond));

        Assert.Equal(
            actual: operand.Read(
                facet: reader,
                reader: reader
            ).Value,
            expected: 5L
        );
    }
}
/// <summary>A host that serves no world fact but the influence read and one configurable body resolution, recording
/// the influence operand it was handed.</summary>
public sealed class RecordingWorldFacts : IStateReader, IWorldFacts {
    private readonly long[] m_locals = new long[RuleCapacity.MaxLocalsPerRule];
    private readonly long[] m_scratch = new long[64];

    /// <summary>Initializes the host over a fresh arena of a section.</summary>
    /// <param name="section">The section.</param>
    public RecordingWorldFacts(StateSection section) {
        var catalog = StateCatalog.Compile(section: section);

        Arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
    }

    /// <inheritdoc/>
    public StateArena Arena { get; }
    /// <inheritdoc/>
    public Span<long> Locals => m_locals;
    /// <inheritdoc/>
    public CellKey BoundEachKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundPreviousKey { get; set; }
    /// <inheritdoc/>
    public CellKey BoundTokenKey { get; set; }
    /// <inheritdoc/>
    public ulong EngineTick { get; set; }
    /// <summary>Gets the influence operand the last read handed this host, or <see langword="null"/>.</summary>
    public PlacementInfluenceOperand? Influence { get; private set; }
    /// <inheritdoc/>
    public Span<long> PatternWord => m_scratch;
    /// <inheritdoc/>
    public ulong Tick { get; set; }

    private static RuleFact Unserved() => throw new NotSupportedException(message: "this host serves the influence read alone");

    /// <inheritdoc/>
    public int BoundIndex(BoundKey key) => -1;
    /// <inheritdoc/>
    public CellKey PairKey(PairKeyFact key) => default;
    /// <inheritdoc/>
    public RuleFact Read(PlacementInfluenceOperand operand) {
        Influence = operand;

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: 1L
        );
    }
    /// <inheritdoc/>
    public RuleFact Read(PopulationOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(PhysicsQuiescentOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(ClockOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(RegionOccupancyOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(MachineMemoryOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(ArgBodyOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(BodyDistanceOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(LineOfSightOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(ParkedOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(UprightOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(BodyFactOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(BodyPositionOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(EngagedOperand operand) => Unserved();
    public RuleFact Read(BodyStateOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(LinkStalenessOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(ChannelOperand operand) => Unserved();
    public RuleFact Read(WorldInputOperand operand) => Unserved();
    public RuleFact Read(PointerOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(NearestOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(NavigationOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(BoardCellOfOperand operand) => Unserved();
    public RuleFact Read(BoardSeatOperand operand) => Unserved();

    /// <summary>Gets or sets the body index every body reference resolves to; <c>-1</c> resolves none.</summary>
    public int ResolvedBody { get; set; } = -1;

    /// <inheritdoc/>
    public int ResolveBody(in CompiledBodyRef bodyRef) => ResolvedBody;
    /// <inheritdoc/>
    public bool TryReadHostOwnedCell(int rowOrdinal, int cell, out long value) {
        value = 0L;

        return false;
    }
    /// <inheritdoc/>
    public bool TryReadHostOwnedSlot(int rowOrdinal, out CellValue value) {
        value = default;

        return false;
    }
}
