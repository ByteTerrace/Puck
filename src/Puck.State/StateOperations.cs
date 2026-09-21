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
    /// <summary>Select the keyed token and every token after it in the zone — a cascade's tail, moved in order as one
    /// run (a solitaire column's run from a card to its top).</summary>
    Slice,
}
/// <summary>What <see cref="StateTransform.BoardCombine"/> writes into its board, cell by cell. A cell is a member of a
/// board when its value is not the board's declared <c>empty</c>.</summary>
[JsonConverter(typeof(StrictEnumConverter<BoardCombineOp>))]
public enum BoardCombineOp : byte {
    /// <summary>Every cell of <c>left</c>, value for value, including its empty value even when the target's differs.</summary>
    Copy,
    /// <summary>Every cell a member.</summary>
    Fill,
    /// <summary>No cell a member.</summary>
    Clear,
    /// <summary>Members of both <c>left</c> and <c>right</c>.</summary>
    And,
    /// <summary>Members of either.</summary>
    Or,
    /// <summary>Members of exactly one.</summary>
    Xor,
    /// <summary>Members of <c>left</c> that are not members of <c>right</c>.</summary>
    AndNot,
    /// <summary>Every cell that is not a member of <c>left</c>.</summary>
    Not,
    /// <summary>Each member of <c>left</c> moved one step along <c>direction</c>; a member with no neighbour that way
    /// drops — the same move <c>boardShift</c> makes on a 64-bit mask.</summary>
    Shift,
    /// <summary>Each member of <c>left</c> carried through the point-group <c>element</c> — the same move
    /// <c>boardImage</c> makes.</summary>
    Image,
}
/// <summary>The closed set of atomic state transforms. Each folds one candidate document and journals once.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(StateTransform.Transfer), "transfer")]
[JsonDerivedType(typeof(StateTransform.SetRay), "setRay")]
[JsonDerivedType(typeof(StateTransform.PushRay), "pushRay")]
[JsonDerivedType(typeof(StateTransform.Shuffle), "shuffle")]
[JsonDerivedType(typeof(StateTransform.Sort), "sort")]
[JsonDerivedType(typeof(StateTransform.WriteSet), "writeSet")]
[JsonDerivedType(typeof(StateTransform.BoardCombine), "boardCombine")]
[JsonDerivedType(typeof(StateTransform.Arrange), "arrange")]
[JsonDerivedType(typeof(StateTransform.Push), "push")]
[JsonDerivedType(typeof(StateTransform.ClearEnclosed), "clearEnclosed")]
[JsonDerivedType(typeof(StateTransform.Observe), "observe")]
[JsonDerivedType(typeof(StateTransform.Mix), "mix")]
[JsonDerivedType(typeof(StateTransform.Mean), "mean")]
[JsonDerivedType(typeof(StateTransform.Nearest), "nearest")]
[JsonDerivedType(typeof(StateTransform.Remember), "remember")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record StateTransform {
    /// <summary>Refreshes a knowledge board from its declared source and visibility mask; authority only.</summary>
    public sealed record Observe(StateChannelRef Row) : StateTransform;
    /// <summary>Moves selected tokens, preserving identity. A random draw advances only when the whole transfer commits.</summary>
    /// <param name="From">The source zone — a zone name, or in an authored rule a live zone
    /// (<c>$zones[&lt;index&gt;]</c>, an entry of the rule's <see cref="Rule.Zones"/> table selected before each
    /// firing; an index selecting none refuses the transfer by name).</param>
    /// <param name="To">The destination zone, on the same terms as <paramref name="From"/>.</param>
    /// <param name="Selector">The source selector.</param>
    /// <param name="Key">The token key for key or slice selection. In an authored rule this may be a dynamic
    /// key, resolved from the active store before each transfer; direct mutations carry the resolved literal.</param>
    /// <param name="InsertFirst">Insert at the first position rather than the last.</param>
    /// <param name="Draw">A streamDraw site for random selection; absent for other selectors.</param>
    /// <param name="Count">How many tokens move in this one transfer, 1..<c>MaxTransferCount</c>,
    /// each selected afresh from what remains (a five-card deal is one mutation); a key selection moves exactly one,
    /// and a slice selection moves the keyed token's whole tail (its count is 1).</param>
    public sealed record Transfer(StateChannelRef From, StateChannelRef To, ZoneSelector Selector = ZoneSelector.Key,
        StateChannelRef? Key = null, bool InsertFirst = false, StateChannelRef? Draw = null, int Count = 1) : StateTransform;
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
    public sealed record SetRay(StateChannelRef Row, StateChannelRef From, CellName Direction, StateChannelRef Pattern, long Value) : StateTransform;
    /// <summary>Moves one live origin token and the outward tokens selected by <paramref name="PushPattern"/> one topology
    /// cell. A token selected by <paramref name="StopPattern"/> blocks the whole move, including when it is also selected by
    /// <paramref name="PushPattern"/>. Other occupants remain in place. The mover is the first run symbol; pushed tokens
    /// follow in ascending pool-slot order within each cell, and the first cell without a pushed token contributes
    /// its passable occupants, or <paramref name="Empty"/> when physically empty, as the terminator. The whole word
    /// must match <paramref name="Pattern"/>, otherwise no token moves.</summary>
    /// <param name="Pool">The sole pool whose live instances participate.</param>
    /// <param name="Cell">The integer field holding each token's topology cell ordinal.</param>
    /// <param name="Value">The scalar field supplying each token's pattern symbol.</param>
    /// <param name="From">The selected pool's live <paramref name="Cell"/> field, which identifies the one mover;
    /// other tokens sharing its origin remain in place.</param>
    /// <param name="Topology">The lattice the cell ordinals address.</param>
    /// <param name="Direction">A direction in that topology.</param>
    /// <param name="Pattern">The pattern matched against the movable run followed by the passable or empty terminator.</param>
    /// <param name="PushPattern">The integer pattern selecting occupants that join the moving run.</param>
    /// <param name="StopPattern">The integer pattern selecting occupants that block the move.</param>
    /// <param name="Empty">The explicit symbol contributed by the first unoccupied cell.</param>
    public sealed record PushRay(CellName Pool, CellName Cell, CellName Value, StateChannelRef From, CellName Topology, CellName Direction, StateChannelRef Pattern, StateChannelRef PushPattern, StateChannelRef StopPattern, long Empty) : StateTransform;
    /// <summary>Reorders a row's cells by value in place by one Fisher-Yates pass over the named redrawable integer
    /// <c>streamDraw</c> site: n cells consume n - 1 samples, so the site's cursor advances by exactly that and a
    /// replay reproduces the permutation.</summary>
    /// <param name="Row">Any ordered zone or keyed row.</param>
    /// <param name="Draw">The integer streamDraw site supplying the samples.</param>
    public sealed record Shuffle(StateChannelRef Row, StateChannelRef Draw) : StateTransform;
    /// <summary>Stably reorders a row by numeric keys in precedence order. Naming the target row as the sole key
    /// orders its own values; otherwise the target is an ordered zone and the keys are token attributes.</summary>
    /// <param name="Row">A keyed or ordered numeric row for own-value sorting, or an ordered zone for attributes.</param>
    /// <param name="By">One or more distinct numeric key rows, each carrying its own direction. Attribute rows
    /// must share the zone's token domain. A sole key naming the target reads its own stored values.</param>
    public sealed record Sort(StateChannelRef Row, IReadOnlyList<SortKey> By) : StateTransform;
    /// <summary>Writes one value into every cell of a board whose bit is set in a cell-set mask read from a state
    /// cell: the way a set built from <c>$board:mask</c> and the and/or/xor/not/shift/image expression ops lands
    /// back on the board. The one board-writing form for every topology of at most 64 cells; a wider topology has no
    /// transform of its own and composes through per-cell rules instead.</summary>
    /// <param name="Row">The board row, over a topology of at most 64 cells.</param>
    /// <param name="Set">The integer row the cell-set mask is read from.</param>
    /// <param name="SetKey">The cell of that row, or null for its slot cell: a literal cell key, or any dynamic
    /// key spelling a write accepts (a binding token, a registered key family, an expression key, or a
    /// <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection).</param>
    /// <param name="Value">The value written to every masked cell.</param>
    public sealed record WriteSet(StateChannelRef Row, StateChannelRef Set, StateChannelRef? SetKey = null, long Value = 0) : StateTransform;
    /// <summary>Rewrites a board from one or two boards over the same topology, cell by cell, in one journaled
    /// mutation: the set algebra <c>$board:mask</c> and the bit operators give a board of at most 64 cells, for a board
    /// of any size. A cell is a member when its value is not its board's <c>empty</c>; every member of the result is
    /// written as <see cref="Value"/> and every other cell as the board's <c>empty</c>.</summary>
    /// <param name="Row">The board written.</param>
    /// <param name="Operation">What is written.</param>
    /// <param name="Left">The first source board, over the same topology; absent for <see cref="BoardCombineOp.Fill"/>
    /// and <see cref="BoardCombineOp.Clear"/>.</param>
    /// <param name="Right">The second source board for the two-board operations.</param>
    /// <param name="Direction">The direction a <see cref="BoardCombineOp.Shift"/> steps along.</param>
    /// <param name="Element">The point-group element an <see cref="BoardCombineOp.Image"/> carries through.</param>
    /// <param name="Value">The value written to every member; never the board's own <c>empty</c>.</param>
    public sealed record BoardCombine(StateChannelRef Row, BoardCombineOp Operation, StateChannelRef? Left = null, StateChannelRef? Right = null,
        string? Direction = null, string? Element = null, long Value = 1) : StateTransform;
    /// <summary>Reorders an ordered zone of at most 20 tokens into the arrangement at a Lehmer rank read from an
    /// integer cell — the inverse of <c>$reduce:arrangementRank</c>: rank 0 is the token domain's own order, and a
    /// rank at or past k! refuses.</summary>
    /// <param name="Row">The ordered zone.</param>
    /// <param name="From">The integer row the rank is read from.</param>
    /// <param name="FromKey">The cell of that row, or null for its slot cell.</param>
    public sealed record Arrange(StateChannelRef Row, StateChannelRef From, StateChannelRef? FromKey = null) : StateTransform;
    /// <summary>Appends one value to a history row's ring, overwriting the oldest slot once the ring is full, and
    /// advances its cursor by one. The value comes from the spellings <see cref="ActionEffect.PushState"/> takes:
    /// the raw literal, a live cell, or an expression evaluated in the ring's kind.</summary>
    /// <param name="Row">The history row.</param>
    /// <param name="Value">The raw value pushed, in the row's kind; read only when neither
    /// <paramref name="FromState"/> nor <paramref name="Expression"/> is named, and refused as ambiguous when a
    /// live source is named beside a non-zero literal.</param>
    /// <param name="FromState">A state row or reserved channel read live at every firing.</param>
    /// <param name="FromKey">The cell of <paramref name="FromState"/>, or null for its slot.</param>
    /// <param name="Expression">A bounded numeric expression evaluated in the row's kind.</param>
    public sealed record Push(
        StateChannelRef Row,
        long Value = 0L,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateChannelRef? FromState = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateChannelRef? FromKey = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ExpressionProgram? Expression = null
    ) : StateTransform;
    /// <summary>Clears every group of cells valued <see cref="Lower"/>..<see cref="Upper"/> beside the cell
    /// <see cref="From"/> names that has no empty cell beside it, writing the board's empty value over their members.
    /// The write-path twin of <c>$board:enclosedAt</c>, applied after the value lands.</summary>
    /// <param name="Row">The board.</param>
    /// <param name="From">The placed value's cell: a literal cell key, or any dynamic key spelling a write accepts.</param>
    /// <param name="Lower">The enclosed range's inclusive low end; the board's empty value lies outside it.</param>
    /// <param name="Upper">The enclosed range's inclusive high end.</param>
    public sealed record ClearEnclosed(StateChannelRef Row, StateChannelRef From, long Lower, long Upper) : StateTransform;
    /// <summary>Writes the normalized sum of 1 to 8 weighted vector terms.</summary>
    /// <param name="Into">The target vector cell or slot.</param>
    /// <param name="Terms">The weighted vector terms.</param>
    public sealed record Mix(string Into, IReadOnlyList<VectorTerm> Terms) : StateTransform;
    /// <summary>Writes the normalized sum of every candidate cell of a table.</summary>
    /// <param name="From">The source vector table.</param>
    /// <param name="Into">The target vector cell or slot.</param>
    /// <param name="Where">Optional boolean filter row.</param>
    public sealed record Mean(string From, string Into, StateChannelRef? Where = null) : StateTransform;
    /// <summary>Writes the closest cells of a table.</summary>
    /// <param name="From">The source vector table.</param>
    /// <param name="Query">The query vector cell, slot, or literal.</param>
    /// <param name="Into">The destination table or slot.</param>
    /// <param name="K">How many nearest results to recall.</param>
    /// <param name="Threshold">Optional threshold score.</param>
    /// <param name="Where">Optional boolean row filter.</param>
    /// <param name="Exclude">Optional key to exclude from candidates.</param>
    /// <param name="Farthest">Whether to rank lower scores first.</param>
    public sealed record Nearest(string From, string Query, StateChannelRef Into, int K, string? Threshold = null, StateChannelRef? Where = null, StateChannelRef? Exclude = null, bool Farthest = false) : StateTransform;
    /// <summary>Stores a vector unless the table already holds a near-duplicate.</summary>
    /// <param name="Into">The destination vector table.</param>
    /// <param name="Key">The key to write.</param>
    /// <param name="From">The source vector.</param>
    /// <param name="UnlessWithin">Cosine similarity threshold in [0, 1].</param>
    public sealed record Remember(StateChannelRef Into, StateChannelRef Key, string From, string UnlessWithin) : StateTransform;
}
/// <summary>One numeric key of a <see cref="StateTransform.Sort"/>, in its declared precedence order.</summary>
/// <param name="Row">The target itself for own-value sorting, or an attribute row over the zone's token domain.</param>
/// <param name="Descending">Whether the greatest value comes first under this key.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SortKey(StateChannelRef Row, bool Descending = false);
/// <summary>One term of a <see cref="StateTransform.Mix"/>: a vector operand with an integer weight.</summary>
/// <param name="From">The vector operand.</param>
/// <param name="Weight">The integer weight in [-1000, 1000] and non-zero.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VectorTerm(string From, int Weight);
