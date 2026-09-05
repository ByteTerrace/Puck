using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The transfer-count ceiling every <see cref="WorldStateTransform.Transfer"/> is validated against.</summary>
public static class WorldStateTransferCapacity {
    /// <summary>The most tokens one transfer moves in a single mutation — <see cref="WorldTopologyCompilation.MaxCells"/>,
    /// the ceiling an uncapacitied <see cref="WorldStateDomain.KeysOf"/> pile row's own cell count is bounded by.</summary>
    public const int MaxTransferCount = WorldTopologyCompilation.MaxCells;
}

/// <summary>Marks a plain integer row as a guarded submission stamp: the row's own generation
/// <see cref="Sequence"/>, the sole state a <see cref="WorldPhaseGuard"/> checks and the mutation pipeline advances.
/// Nothing about who may act, in what order, or under what deadline is engine knowledge any more — a turn order, a
/// round counter, a ready or skipped bitset, and a deadline are all ordinary rows a world's own rules author and
/// advance, and eligibility is the ordinary grant/admission system over whichever rows a rule ties to this one via
/// <see cref="WorldStateRow.PhaseOf"/>. Submitting any mutation whose <see cref="WorldPhaseGuard"/> matches this
/// generation both admits the submission and, on success, advances the generation by one: the guard's presence on a
/// mutation IS the turn's completion, so a world that wants several ungated moves before a turn ends simply leaves
/// those rows untagged and reserves <see cref="WorldStateRow.PhaseOf"/> for the one row that ends it.</summary>
/// <param name="Sequence">The generation. Advanced by the mutation pipeline after a guarded mutation naming this row
/// succeeds; never written directly.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStatePhase(long Sequence = 0);

/// <summary>Admission guard for a submitted gameplay operation: reduces a turn-taking protocol to the one thing the
/// engine still enforces, a monotonic sequence a submission must match. See <see cref="WorldStatePhase"/> for what
/// a match does on success.</summary>
/// <param name="Row">The phase row.</param>
/// <param name="Sequence">The observed generation.</param>
/// <param name="Participant">World-program-only participant attribution; outside callers always use their stamp.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPhaseGuard(string Row, long Sequence, string? Participant = null);

/// <summary>Selection of a single token from a zone.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldZoneSelector>))]
public enum WorldZoneSelector : byte {
    /// <summary>Select by stable token identity.</summary>
    Key,
    /// <summary>Select the first cell of an ordered zone.</summary>
    First,
    /// <summary>Select the last cell of an ordered zone.</summary>
    Last,
    /// <summary>Select by one draw from an explicitly named stream-draw state row.</summary>
    Random,
}

/// <summary>The closed set of atomic state transforms. Each folds one candidate document and journals once.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(WorldStateTransform.Transfer), "transfer")]
[JsonDerivedType(typeof(WorldStateTransform.SetRay), "setRay")]
[JsonDerivedType(typeof(WorldStateTransform.Shuffle), "shuffle")]
[JsonDerivedType(typeof(WorldStateTransform.SortZone), "sortZone")]
[JsonDerivedType(typeof(WorldStateTransform.SortKeyed), "sortKeyed")]
[JsonDerivedType(typeof(WorldStateTransform.WriteSet), "writeSet")]
[JsonDerivedType(typeof(WorldStateTransform.Push), "push")]
[JsonDerivedType(typeof(WorldStateTransform.Observe), "observe")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record WorldStateTransform {
    /// <summary>Refreshes a knowledge board from its declared source and visibility mask; authority only.</summary>
    public sealed record Observe(string Row) : WorldStateTransform;
    /// <summary>Moves one token, preserving identity. A random draw advances only when the whole transfer commits.</summary>
    /// <param name="From">The source zone.</param>
    /// <param name="To">The destination zone.</param>
    /// <param name="Selector">The source selector.</param>
    /// <param name="Key">The token key for key selection.</param>
    /// <param name="InsertFirst">Insert at the first position rather than the last.</param>
    /// <param name="Draw">A streamDraw site for random selection; absent for other selectors.</param>
    /// <param name="Count">How many tokens move in this one transfer, 1..<see cref="WorldStateTransferCapacity.MaxTransferCount"/>,
    /// each selected afresh from what remains (a five-card deal is one mutation); a key selection moves exactly one.</param>
    public sealed record Transfer(string From, string To, WorldZoneSelector Selector = WorldZoneSelector.Key,
        string? Key = null, bool InsertFirst = false, string? Draw = null, int Count = 1) : WorldStateTransform;

    /// <summary>Writes the longest run a <c>patterns</c> row accepts, walked from the origin outward: the same
    /// prefix semantics as the <c>$match</c> operand's <c>prefix</c> facet, landed back on the board instead of
    /// read as a fact. Refuses when the accepted prefix is empty, so an author closes a run with the required
    /// symbol (a bracket capture is <c>plus(through) . symbol(until)</c>) rather than an unbounded one running off
    /// the board.</summary>
    /// <param name="Row">The board row.</param>
    /// <param name="From">The origin key, excluded from the read word and the write.</param>
    /// <param name="Direction">A direction in the board's topology.</param>
    /// <param name="Pattern">A <c>patterns</c> row over the board's own raw values (kind Int).</param>
    /// <param name="Value">The replacement value written to every cell of the accepted prefix.</param>
    public sealed record SetRay(string Row, string From, string Direction, string Pattern, long Value) : WorldStateTransform;

    /// <summary>Reorders a row's cells by value in place by one Fisher-Yates pass over the named redrawable integer
    /// <c>streamDraw</c> site: n cells consume n - 1 samples, so the site's cursor advances by exactly that and a
    /// replay reproduces the permutation.</summary>
    /// <param name="Row">Any ordered zone or keyed row.</param>
    /// <param name="Draw">The integer streamDraw site supplying the samples.</param>
    public sealed record Shuffle(string Row, string Draw) : WorldStateTransform;
    /// <summary>Reorders an ordered zone by attribute rows over its token domain, stably: the first key decides and
    /// each later key breaks the ties before it. The canonical order a pattern reads a hand in.</summary>
    /// <param name="Row">The ordered zone.</param>
    /// <param name="By">The attribute keys, 1..<see cref="WorldStateCapacity.MaxSortKeys"/> distinct numeric rows
    /// keyed over the zone's token domain, in precedence order; each carries its own direction.</param>
    public sealed record SortZone(string Row, IReadOnlyList<WorldSortKey> By) : WorldStateTransform;
    /// <summary>Reorders a keyed numeric row by its own cell values, stably.</summary>
    /// <param name="Row">The keyed numeric row.</param>
    /// <param name="Descending">Whether the greatest value comes first.</param>
    public sealed record SortKeyed(string Row, bool Descending = false) : WorldStateTransform;

    /// <summary>Writes one value into every cell of a board whose bit is set in a cell-set mask read from a state
    /// cell: the way a set built from <c>$board:mask</c> and the and/or/xor/not/shift/image expression ops lands
    /// back on the board. The one board-writing form for every topology of at most 64 cells; a wider topology has no
    /// transform of its own and composes through per-cell rules instead.</summary>
    /// <param name="Row">The board row, over a topology of at most 64 cells.</param>
    /// <param name="Set">The integer row the cell-set mask is read from.</param>
    /// <param name="SetKey">The cell of that row, or null for its slot cell.</param>
    /// <param name="Value">The value written to every masked cell.</param>
    public sealed record WriteSet(string Row, string Set, string? SetKey = null, long Value = 0) : WorldStateTransform;

    /// <summary>Appends one value to a history row's ring, overwriting the oldest slot once the ring is full, and
    /// advances its cursor by one.</summary>
    /// <param name="Row">The history row.</param>
    /// <param name="Value">The raw value pushed, in the row's kind.</param>
    public sealed record Push(string Row, long Value) : WorldStateTransform;
}

/// <summary>One key of a zone <c>sort</c>: a keyed numeric attribute row over the zone's token domain.</summary>
/// <param name="Row">The attribute row.</param>
/// <param name="Descending">Whether the greatest value comes first under this key.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSortKey(string Row, bool Descending = false);
