using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>Selection of a single token from a zone.</summary>
[JsonConverter(typeof(StrictEnumConverter<ZoneSelector>))]
public enum ZoneSelector : byte {
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
[JsonDerivedType(typeof(StateTransform.Transfer), "transfer")]
[JsonDerivedType(typeof(StateTransform.SetRay), "setRay")]
[JsonDerivedType(typeof(StateTransform.Shuffle), "shuffle")]
[JsonDerivedType(typeof(StateTransform.SortZone), "sortZone")]
[JsonDerivedType(typeof(StateTransform.SortKeyed), "sortKeyed")]
[JsonDerivedType(typeof(StateTransform.WriteSet), "writeSet")]
[JsonDerivedType(typeof(StateTransform.Push), "push")]
[JsonDerivedType(typeof(StateTransform.Observe), "observe")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record StateTransform {
    /// <summary>Refreshes a knowledge board from its declared source and visibility mask; authority only.</summary>
    public sealed record Observe(string Row) : StateTransform;
    /// <summary>Moves one token, preserving identity. A random draw advances only when the whole transfer commits.</summary>
    /// <param name="From">The source zone.</param>
    /// <param name="To">The destination zone.</param>
    /// <param name="Selector">The source selector.</param>
    /// <param name="Key">The token key for key selection.</param>
    /// <param name="InsertFirst">Insert at the first position rather than the last.</param>
    /// <param name="Draw">A streamDraw site for random selection; absent for other selectors.</param>
    /// <param name="Count">How many tokens move in this one transfer, 1..<c>MaxTransferCount</c>,
    /// each selected afresh from what remains (a five-card deal is one mutation); a key selection moves exactly one.</param>
    public sealed record Transfer(string From, string To, ZoneSelector Selector = ZoneSelector.Key,
        string? Key = null, bool InsertFirst = false, string? Draw = null, int Count = 1) : StateTransform;

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
    public sealed record SetRay(string Row, string From, string Direction, string Pattern, long Value) : StateTransform;

    /// <summary>Reorders a row's cells by value in place by one Fisher-Yates pass over the named redrawable integer
    /// <c>streamDraw</c> site: n cells consume n - 1 samples, so the site's cursor advances by exactly that and a
    /// replay reproduces the permutation.</summary>
    /// <param name="Row">Any ordered zone or keyed row.</param>
    /// <param name="Draw">The integer streamDraw site supplying the samples.</param>
    public sealed record Shuffle(string Row, string Draw) : StateTransform;
    /// <summary>Reorders an ordered zone by attribute rows over its token domain, stably: the first key decides and
    /// each later key breaks the ties before it. The canonical order a pattern reads a hand in.</summary>
    /// <param name="Row">The ordered zone.</param>
    /// <param name="By">The attribute keys, 1..<c>MaxSortKeys</c> distinct numeric rows
    /// keyed over the zone's token domain, in precedence order; each carries its own direction.</param>
    public sealed record SortZone(string Row, IReadOnlyList<SortKey> By) : StateTransform;
    /// <summary>Reorders a keyed numeric row by its own cell values, stably.</summary>
    /// <param name="Row">The keyed numeric row.</param>
    /// <param name="Descending">Whether the greatest value comes first.</param>
    public sealed record SortKeyed(string Row, bool Descending = false) : StateTransform;

    /// <summary>Writes one value into every cell of a board whose bit is set in a cell-set mask read from a state
    /// cell: the way a set built from <c>$board:mask</c> and the and/or/xor/not/shift/image expression ops lands
    /// back on the board. The one board-writing form for every topology of at most 64 cells; a wider topology has no
    /// transform of its own and composes through per-cell rules instead.</summary>
    /// <param name="Row">The board row, over a topology of at most 64 cells.</param>
    /// <param name="Set">The integer row the cell-set mask is read from.</param>
    /// <param name="SetKey">The cell of that row, or null for its slot cell.</param>
    /// <param name="Value">The value written to every masked cell.</param>
    public sealed record WriteSet(string Row, string Set, string? SetKey = null, long Value = 0) : StateTransform;

    /// <summary>Appends one value to a history row's ring, overwriting the oldest slot once the ring is full, and
    /// advances its cursor by one.</summary>
    /// <param name="Row">The history row.</param>
    /// <param name="Value">The raw value pushed, in the row's kind.</param>
    public sealed record Push(string Row, long Value) : StateTransform;
}

/// <summary>One key of a zone <c>sort</c>: a keyed numeric attribute row over the zone's token domain.</summary>
/// <param name="Row">The attribute row.</param>
/// <param name="Descending">Whether the greatest value comes first under this key.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SortKey(string Row, bool Descending = false);
