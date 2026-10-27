using Puck.Commands;

namespace Puck.World;

/// <summary>An admitted identity or the current owner of an existing typed body reference.</summary>
/// <param name="Principal">A literal generation-bearing identity, when no body reference is present.</param>
/// <param name="Body">A compiled literal, bound, cell, placement or extremum body reference.</param>
public readonly record struct WorldInputOwnerReference(Principal Principal, CompiledBodyRef? Body = null) {
    /// <summary>Registers the state row that determines a dynamic owner.</summary>
    public void CollectReads(List<CellAccess> into) {
        if (Body is { Handle.IsValid: true } body) { into.Add(new(body.Handle.Ordinal, default)); }
    }
}

/// <summary>Generic history facts; no game or genre enters the expression IR.</summary>
public enum WorldInputFacet : byte {
    /// <summary>Whether a matching retained edge exists.</summary>
    Edge,
    /// <summary>Its calibrated capture or authored admission time.</summary>
    Time,
    /// <summary>Current simulation time minus judgement time.</summary>
    Age,
    /// <summary>The current hold's original time, even across a kept epoch or ring eviction.</summary>
    HeldSince,
    /// <summary>The current hold's original event identity; never a fresh edge.</summary>
    HeldPress,
    /// <summary>The event's raw semantic value, including a text Unicode scalar.</summary>
    Value,
    /// <summary>The admitted tick, for exact frame windows.</summary>
    Tick,
    /// <summary>Whether the named pattern has an unconsumed match.</summary>
    Match,
    /// <summary>The match's first event identity.</summary>
    MatchFirst,
    /// <summary>The match's last event identity.</summary>
    MatchLast,
    /// <summary>The first event's judged world time.</summary>
    MatchFirstTime,
    /// <summary>The last event's judged world time.</summary>
    MatchLastTime,
    /// <summary>The first event's processing tick.</summary>
    MatchFirstTick,
    /// <summary>The last event's processing tick.</summary>
    MatchLastTick,
}

/// <summary>A compiled owner/action history operand, resolved before simulation.</summary>
public sealed class WorldInputOperand : WorldOperandFact {
    /// <summary>Creates a typed input-history read.</summary>
    public WorldInputOperand(WorldInputOwnerReference owner, ushort channel, WorldInputFacet facet, WorldInputEventKind kind, int occurrence, int pattern = -1, bool fixedValue = false)
        : base(fixedValue || facet is WorldInputFacet.Time or WorldInputFacet.Age or WorldInputFacet.HeldSince or WorldInputFacet.MatchFirstTime or WorldInputFacet.MatchLastTime
            ? CellKind.Fixed : facet is WorldInputFacet.Edge or WorldInputFacet.Match ? CellKind.Bool : CellKind.Int) {
        Owner = owner; Channel = channel; Facet = facet; Kind = kind; Occurrence = occurrence; Pattern = pattern;
    }
    /// <summary>Admitted seat/session identity.</summary>
    public WorldInputOwnerReference Owner { get; }
    /// <summary>Logical action id.</summary>
    public ushort Channel { get; }
    /// <summary>The requested history fact.</summary>
    public WorldInputFacet Facet { get; }
    /// <summary>Semantic event kind.</summary>
    public WorldInputEventKind Kind { get; }
    /// <summary>Newest-first occurrence within the current epoch.</summary>
    public int Occurrence { get; }
    /// <summary>Compiled pattern index, or -1 for an event/hold read.</summary>
    public int Pattern { get; }
}

/// <summary>Transactional consumption of one named pattern's selected range.</summary>
public sealed class WorldInputConsumeEffect(WorldInputOwnerReference owner, int pattern, long work, string describe) : WorldFactEffect(describe) {
    /// <summary>Semantic input owner.</summary>
    public WorldInputOwnerReference Owner { get; } = owner;
    /// <summary>Compiled pattern index.</summary>
    public int Pattern { get; } = pattern;
    /// <inheritdoc/>
    public override bool SubmitsMutation => false;
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => work;
    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => Owner.CollectReads(into);
}
