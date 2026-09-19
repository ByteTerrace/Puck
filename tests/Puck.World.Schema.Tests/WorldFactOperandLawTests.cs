using Puck.State.Rules;
using Xunit;
using CompiledCellRef = Puck.State.Rules.CompiledCellRef;

namespace Puck.World.Schema.Tests;

/// <summary>A world operand answers in the encoding its host answers in, and a live child key reaches the host as the
/// key it resolved to.</summary>
public sealed class WorldFactOperandLawTests {
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
}
/// <summary>A host that serves no world fact but the influence read, recording the operand it was handed.</summary>
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
    public Span<long> BoardScratch(int cells) => m_scratch.AsSpan(
        length: Math.Min(
            val1: cells,
            val2: m_scratch.Length
        ),
        start: 0
    );
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
    public RuleFact Read(LinkStalenessOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(ChannelOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(NearestOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(NavigationOperand operand) => Unserved();
    /// <inheritdoc/>
    public RuleFact Read(BoardCellOfOperand operand) => Unserved();
    /// <inheritdoc/>
    public int ResolveBody(in CompiledBodyRef bodyRef) => -1;
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
