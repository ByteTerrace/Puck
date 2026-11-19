using Puck.State.Rules;

namespace Puck.World;

/// <summary>What a world fact's answer depends on beyond the state rows it reads, as a recipient must hold it to be
/// told anything derived from the answer.</summary>
[Flags]
public enum WorldDisclosureReach : byte {
    /// <summary>The answer depends on nothing beyond the rows the fact reads.</summary>
    None = 0,
    /// <summary>The answer depends on bodies: which exist, where they are, how they move or what they carry. A
    /// recipient is told only when its delivery carries every body.</summary>
    Bodies = 1,
    /// <summary>The answer depends on the physical field regions: a sight line, a path or a paint reaches any of
    /// them. A recipient is told only when it reads every field row of every region whole.</summary>
    Fields = 2,
}

/// <summary>A world fact, effect or rule policy that declares what its answer depends on, so a disclosure decision
/// over anything derived from it reads the declaration from the fact's own type.</summary>
/// <remarks>The rows a fact reads are its <c>CollectReads</c>; this declaration carries the rest. A fact that
/// requires a host facet and does not implement this interface is undeclared, and
/// <see cref="WorldRuleDisclosure"/> withholds whatever depends on it from every recipient but the authority.</remarks>
public interface IWorldDisclosedFact {
    /// <summary>Appends what the fact's answer depends on beyond the rows it reads.</summary>
    /// <param name="into">The dependencies being collected.</param>
    void CollectDisclosure(WorldDisclosureNeeds into);
}

/// <summary>The disclosure dependencies of one rule's observable outcome: the rows a recipient must read whole, the
/// placements and machine screens it must hold, the reach it must see, and the facts nothing declared.</summary>
public sealed class WorldDisclosureNeeds {
    private readonly HashSet<int> m_rows = [];
    private readonly HashSet<string> m_placements = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<int> m_screens = [];
    private readonly SortedSet<string> m_undeclared = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets the catalog ordinals of the rows a recipient must read whole.</summary>
    public IReadOnlyCollection<int> Rows => m_rows;
    /// <summary>Gets the ids of the placements a recipient's placement projection must carry.</summary>
    public IReadOnlyCollection<string> Placements => m_placements;
    /// <summary>Gets the indices of the screens whose machine a recipient must hold.</summary>
    public IReadOnlyCollection<int> Screens => m_screens;
    /// <summary>Gets the reach a recipient must see.</summary>
    public WorldDisclosureReach Reach { get; private set; }
    /// <summary>Gets the type names of the host facts that declare no disclosure, in ordinal order; any entry
    /// withholds the outcome from every recipient but the authority.</summary>
    public IReadOnlyCollection<string> Undeclared => m_undeclared;

    /// <summary>Records a row a recipient must read whole.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal; a negative ordinal names no row and is ignored.</param>
    public void AddRow(int rowOrdinal) {
        if (rowOrdinal >= 0) { _ = m_rows.Add(item: rowOrdinal); }
    }
    /// <summary>Records every row a read set touches.</summary>
    /// <param name="reads">The read or write set.</param>
    public void AddRows(IEnumerable<CellAccess> reads) {
        ArgumentNullException.ThrowIfNull(argument: reads);

        foreach (var access in reads) { AddRow(rowOrdinal: access.RowOrdinal); }
    }
    /// <summary>Records a placement a recipient must hold.</summary>
    /// <param name="placementId">The placement's id.</param>
    public void AddPlacement(string placementId) {
        ArgumentException.ThrowIfNullOrEmpty(argument: placementId);

        _ = m_placements.Add(item: placementId);
    }
    /// <summary>Records a screen whose machine a recipient must hold.</summary>
    /// <param name="screen">The screen index.</param>
    public void AddScreen(int screen) => m_screens.Add(item: screen);
    /// <summary>Records reach a recipient must see.</summary>
    /// <param name="reach">The reach.</param>
    public void Add(WorldDisclosureReach reach) => Reach |= reach;
    /// <summary>Records a host fact that declares no disclosure.</summary>
    /// <param name="typeName">The fact's type name.</param>
    public void AddUndeclared(string typeName) {
        ArgumentException.ThrowIfNullOrEmpty(argument: typeName);

        _ = m_undeclared.Add(item: typeName);
    }
    /// <summary>Folds another rule's dependencies into these.</summary>
    /// <param name="other">The other dependencies.</param>
    public void UnionWith(WorldDisclosureNeeds other) {
        ArgumentNullException.ThrowIfNull(argument: other);

        m_rows.UnionWith(other: other.m_rows);
        m_placements.UnionWith(other: other.m_placements);
        m_screens.UnionWith(other: other.m_screens);
        m_undeclared.UnionWith(other: other.m_undeclared);
        Reach |= other.Reach;
    }
}

/// <summary>Collects what a compiled world rule's observable outcome depends on: everything its judgement reads, every
/// fact a firing reads before it lands, and every row it writes, classified by each fact's own declaration.</summary>
/// <remarks>The rows come from the rule's read and write sets and from each fact's own <c>CollectReads</c>; every
/// other dependency comes from <see cref="IWorldDisclosedFact"/> on the fact's type. A facet fact without that
/// declaration is recorded as undeclared rather than classified by a list here, so a fact type added later cannot be
/// delivered to a recipient by omission.</remarks>
public static class WorldRuleDisclosure {
    /// <summary>Collects one rule's dependencies.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="into">The dependencies being collected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> or <paramref name="into"/> is
    /// <see langword="null"/>.</exception>
    public static void Collect(CompiledRule rule, WorldDisclosureNeeds into) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: into);

        var access = RuleDataflow.Reads(rule: rule);
        var facts = new RuleNeedsBuilder();

        RuleDataflow.CollectJudgement(facts: facts, reads: access, rule: rule);
        RuleDataflow.CollectEffectFacts(effects: rule.Effects, includeSources: true, into: facts);
        if (rule is CompiledWorldFactsRule world) {
            if (world.Interaction is not null) {
                // A sweep pairs carriers across the whole population, so whether it fires depends on every body.
                into.Add(reach: WorldDisclosureReach.Bodies);
            }
            if (world.Decision is { } decision) {
                RuleDataflow.CollectGateSchedulingFacts(gate: (decision.Interrupt ?? []), into: facts);
                RuleDataflow.CollectEffectFacts(effects: decision.OnNoChoice, includeSources: true, into: facts);
                foreach (var option in decision.Options) {
                    RuleDataflow.CollectGateSchedulingFacts(gate: option.Gate, into: facts);
                    RuleDataflow.CollectExpressionSchedulingFacts(into: facts, tokens: option.Score);
                    RuleDataflow.CollectEffectFacts(effects: option.Effects, includeSources: true, into: facts);
                    option.Neighbors?.CollectDisclosure(into: into);
                }
            }
        }
        rule.CollectWrites(into: access);
        into.AddRows(reads: access);
        foreach (var row in rule.Needs.HostOwnedRows) { into.AddRow(rowOrdinal: row); }
        foreach (var fact in facts.FacetFacts) { Classify(fact: fact, into: into); }
    }

    /// <summary>Classifies one host fact: the rows it reads, then its own declaration, or undeclared when its type
    /// carries none.</summary>
    /// <param name="fact">The fact.</param>
    /// <param name="into">The dependencies being collected.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fact"/> or <paramref name="into"/> is
    /// <see langword="null"/>.</exception>
    public static void Classify(ICompiledFact fact, WorldDisclosureNeeds into) {
        ArgumentNullException.ThrowIfNull(argument: fact);
        ArgumentNullException.ThrowIfNull(argument: into);

        var reads = new List<CellAccess>();

        switch (fact) {
            case IRuleOperand operand:
                operand.CollectReads(into: reads);
                break;
            case IRuleKey key:
                key.CollectReads(into: reads);
                break;
            case IRuleEffect effect:
                effect.CollectReads(into: reads);
                effect.CollectWrites(into: reads);
                break;
        }
        into.AddRows(reads: reads);
        if (fact is IWorldDisclosedFact declared) {
            declared.CollectDisclosure(into: into);
        } else {
            into.AddUndeclared(typeName: fact.GetType().Name);
        }
    }
}
