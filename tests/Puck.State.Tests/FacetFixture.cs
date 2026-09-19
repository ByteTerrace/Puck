namespace Puck.State.Tests;

/// <summary>A host capability the facet law suite's facts name, standing in for a document project's own.</summary>
public interface ICardFacts : IFacet {
    /// <summary>Returns the top card the host is holding.</summary>
    long Top();
}
/// <summary>A second capability, so a rule can name two and a host can serve one.</summary>
public interface IWeatherFacts : IFacet {
    /// <summary>Returns the live temperature.</summary>
    long Temperature();
}
/// <summary>A reader over the arena that serves no facet.</summary>
public class StubReader : IStateReader {
    private readonly long[] m_locals = new long[RuleCapacity.MaxLocalsPerRule];
    private readonly long[] m_boardScratch = new long[TopologyCompilation.MaxCells];
    private readonly long[] m_patternWord = new long[8];

    /// <summary>Initializes the reader over an arena.</summary>
    /// <param name="arena">The store.</param>
    public StubReader(StateArena arena) => Arena = arena;

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
    /// <inheritdoc/>
    public Span<long> PatternWord => m_patternWord;
    /// <inheritdoc/>
    public ulong Tick { get; set; }

    /// <inheritdoc/>
    public Span<long> BoardScratch(int cells) => m_boardScratch.AsSpan(
        length: Math.Min(
            val1: cells,
            val2: m_boardScratch.Length
        ),
        start: 0
    );
    /// <inheritdoc/>
    public int BoundIndex(BoundKey key) => -1;
}
/// <summary>A reader whose host serves <see cref="ICardFacts"/>.</summary>
public sealed class CardReader : StubReader, ICardFacts {
    /// <summary>Initializes the reader over an arena.</summary>
    /// <param name="arena">The store.</param>
    public CardReader(StateArena arena) : base(arena: arena) { }

    /// <inheritdoc/>
    public long Top() => 7L;
}
/// <summary>An operand whose read needs <see cref="ICardFacts"/>.</summary>
public sealed class TopCardOperand : OperandFact<ICardFacts> {
    /// <summary>Initializes the operand.</summary>
    public TopCardOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader, ICardFacts facet) => RuleFact.Finite(
        kind: CellKind.Int,
        value: facet.Top()
    );
}
/// <summary>An operand whose read needs <see cref="IWeatherFacts"/>.</summary>
public sealed class TemperatureOperand : OperandFact<IWeatherFacts> {
    /// <summary>Initializes the operand.</summary>
    public TemperatureOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader, IWeatherFacts facet) => RuleFact.Finite(
        kind: CellKind.Int,
        value: facet.Temperature()
    );
}
/// <summary>A key whose resolution needs <see cref="ICardFacts"/>.</summary>
public sealed class TopCardKeyFact : KeyFact<ICardFacts> {
    /// <inheritdoc/>
    public override CellKey Resolve(IStateReader reader, ICardFacts facet) => reader.Catalog.Keys.Intern(name: CellName.Parse(candidate: "top"));
}
/// <summary>An effect whose firing needs <see cref="ICardFacts"/> and cannot be rewound.</summary>
public sealed class SaveCardEffect : EffectFact<ICardFacts> {
    /// <summary>Initializes the effect.</summary>
    public SaveCardEffect() : base(describe: "save") { }

    /// <inheritdoc/>
    public override EffectNeeds Needs => EffectNeeds.Irreversible;

    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override bool TryFire(IEffectHost host, ICardFacts facet, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        return true;
    }
}
/// <summary>An effect whose firing needs <see cref="ICardFacts"/> and reads the tick.</summary>
public sealed class StampCardEffect : EffectFact<ICardFacts> {
    /// <summary>Initializes the effect.</summary>
    public StampCardEffect() : base(describe: "stamp") { }

    /// <inheritdoc/>
    public override EffectNeeds Needs => EffectNeeds.ReadsTick;

    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override bool TryFire(IEffectHost host, ICardFacts facet, in EffectFiring firing, out EffectRefusal refusal) {
        refusal = EffectRefusal.None;

        return true;
    }
}
