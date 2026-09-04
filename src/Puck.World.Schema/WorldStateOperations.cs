using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Declares the stable token identities shared by attribute and zone rows.</summary>
/// <param name="Capacity">The greatest token count, 1 through 4096.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateTokens(int Capacity = 256) {
    /// <summary>The most tokens one domain declares, and the most one transfer moves.</summary>
    public const int MaxCapacity = 256;
}

/// <summary>A ring of the last <paramref name="Capacity"/> values pushed into the row, oldest overwritten first:
/// the temporal twin of a board ray, read by <c>$history:</c> facts and matched by <c>$match:</c> in push order.
/// Cell keys are the ring slots <c>0..Capacity-1</c>; the row's <c>historyCursor</c> counts every push.</summary>
/// <param name="Capacity">How many pushes the ring keeps, 1..128.</param>
/// <param name="Empty">The value read for an age older than the ring holds.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateHistory(int Capacity, long Empty = 0);

/// <summary>A bounded zone whose cell keys identify tokens. Cell order is pile order; values are membership bits.</summary>
/// <param name="Tokens">The token-domain row. Every token belongs to exactly one zone of its domain.</param>
/// <param name="Ordered">Whether first/last selection and insertion order have gameplay meaning.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStateZone(string Tokens, bool Ordered = true);

/// <summary>How participants complete a phase.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldPhaseMode>))]
public enum WorldPhaseMode : byte {
    /// <summary>Participants act in declaration order, with multiple actions before explicitly completing.</summary>
    Sequential,
    /// <summary>All participants may act until each declares readiness.</summary>
    Together,
    /// <summary>Only the world program resolves this phase.</summary>
    Resolution,
}

/// <summary>One node in a finite phase protocol.</summary>
/// <param name="Name">The phase name.</param>
/// <param name="Mode">Who may complete the phase.</param>
/// <param name="Next">The next phase name.</param>
/// <param name="TimeoutSeconds">The deadline interval, or zero for no deadline.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPhaseDefinition(string Name, WorldPhaseMode Mode, string Next, decimal TimeoutSeconds = 0);

/// <summary>Authored phase protocol and persisted progression. Participants are authenticated principal tokens;
/// readiness does not change their grants. One completion performs at most one phase transition.</summary>
/// <param name="Participants">Distinct principal tokens in deterministic activation order, at most 32.</param>
/// <param name="Phases">The finite phase table, at most 32.</param>
/// <param name="Current">The current phase ordinal.</param>
/// <param name="Active">The current participant ordinal for sequential phases.</param>
/// <param name="Ready">The ready-participant bits for together phases.</param>
/// <param name="Sequence">The generation incremented on changing activation or phase; readiness alone preserves it.</param>
/// <param name="Round">The round, incremented on returning to phase zero.</param>
/// <param name="DeadlineTick">The absolute deadline; zero at sequence zero derives from the initial phase timeout.</param>
/// <param name="Direction">The sequential activation step, 1 or -1; a sequential phase ends when the turn passes the
/// last participant in this direction.</param>
/// <param name="Skipped">The participant bits activation passes over and readiness never waits for (a fold, an
/// elimination); persists across phases until a <c>turnOrder</c> transform clears them.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldStatePhase(IReadOnlyList<string> Participants, IReadOnlyList<WorldPhaseDefinition> Phases,
    int Current = 0, int Active = 0, uint Ready = 0, long Sequence = 0, long Round = 0, long DeadlineTick = 0,
    int Direction = 1, uint Skipped = 0);

/// <summary>Admission guard for a submitted gameplay operation.</summary>
/// <param name="Row">The phase row.</param>
/// <param name="Sequence">The observed activation/phase generation.</param>
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
[JsonDerivedType(typeof(WorldStateTransform.CompletePhase), "completePhase")]
[JsonDerivedType(typeof(WorldStateTransform.TurnOrder), "turnOrder")]
[JsonDerivedType(typeof(WorldStateTransform.Shuffle), "shuffle")]
[JsonDerivedType(typeof(WorldStateTransform.Sort), "sort")]
[JsonDerivedType(typeof(WorldStateTransform.SetMask), "setMask")]
[JsonDerivedType(typeof(WorldStateTransform.Combine), "combine")]
[JsonDerivedType(typeof(WorldStateTransform.Push), "push")]
[JsonDerivedType(typeof(WorldStateTransform.MapBoard), "mapBoard")]
[JsonDerivedType(typeof(WorldStateTransform.MoveToken), "moveToken")]
[JsonDerivedType(typeof(WorldStateTransform.Observe), "observe")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record WorldStateTransform {
    /// <summary>Refreshes a knowledge board from its declared source and visibility mask; authority only.</summary>
    public sealed record Observe(string Row) : WorldStateTransform;
    /// <summary>Moves a token along an affordable route, debiting movement points in the same candidate.</summary>
    /// <param name="Positions">The token-keyed position row; valuesFrom names the terrain topology.</param>
    /// <param name="Token">The stable token key.</param>
    /// <param name="Destination">The destination cell ordinal.</param>
    /// <param name="Terrain">The board row containing nonnegative entry costs; negative values are impassable.</param>
    /// <param name="Allowance">The token-keyed movement-points row.</param>
    /// <param name="MaxVisits">The route search's settled-node bound.</param>
    public sealed record MoveToken(string Positions, string Token, int Destination, string Terrain, string Allowance, int MaxVisits) : WorldStateTransform;
    /// <summary>Moves one token, preserving identity. A random draw advances only when the whole transfer commits.</summary>
    /// <param name="From">The source zone.</param>
    /// <param name="To">The destination zone.</param>
    /// <param name="Selector">The source selector.</param>
    /// <param name="Key">The token key for key selection.</param>
    /// <param name="InsertFirst">Insert at the first position rather than the last.</param>
    /// <param name="Draw">A streamDraw site for random selection; absent for other selectors.</param>
    /// <param name="Count">How many tokens move in this one transfer, 1..256, each selected afresh from what remains
    /// (a five-card deal is one mutation); a key selection moves exactly one.</param>
    public sealed record Transfer(string From, string To, WorldZoneSelector Selector = WorldZoneSelector.Key,
        string? Key = null, bool InsertFirst = false, string? Draw = null, int Count = 1) : WorldStateTransform;

    /// <summary>Writes only a nonempty run of matching cells closed by a required terminator; otherwise refuses.</summary>
    /// <param name="Row">The board row.</param>
    /// <param name="From">The origin key, excluded from the write.</param>
    /// <param name="Direction">A direction in the board's topology.</param>
    /// <param name="Through">Every intervening cell must have this value.</param>
    /// <param name="Until">The closing value, excluded from the write.</param>
    /// <param name="Value">The replacement value.</param>
    public sealed record SetRay(string Row, string From, string Direction, long Through, long Until, long Value) : WorldStateTransform;

    /// <summary>Completes the acting participant's activation or readiness, guarded against stale submissions.</summary>
    /// <param name="Row">The phase row.</param>
    /// <param name="ExpectedSequence">The exact observed progression sequence; only the world program may omit it.</param>
    /// <param name="Timeout">World-only completion after the current deadline.</param>
    /// <param name="Participant">World-only named participant on whose completion the authored rule acts.</param>
    /// <param name="Next">World-only branch: the declared phase a transition enters instead of the current phase's
    /// authored <c>next</c>. Ignored when the completion does not transition.</param>
    public sealed record CompletePhase(string Row, long? ExpectedSequence = null, bool Timeout = false, string? Participant = null,
        string? Next = null) : WorldStateTransform;
    /// <summary>Reshapes a phase row's turn order without completing anything. World-only.</summary>
    /// <param name="Row">The phase row.</param>
    /// <param name="Direction">The new sequential step, 1 or -1, or null to keep it.</param>
    /// <param name="Skip">Participant tokens activation passes over from now on.</param>
    /// <param name="Unskip">Participant tokens restored to the order.</param>
    /// <param name="Active">A participant token to activate now in a sequential phase, or null to keep the current
    /// activation (moved past a newly skipped participant when it was the one active).</param>
    public sealed record TurnOrder(string Row, int? Direction = null, IReadOnlyList<string>? Skip = null, IReadOnlyList<string>? Unskip = null,
        string? Active = null) : WorldStateTransform;
    /// <summary>Reorders an ordered zone in place by one Fisher-Yates pass over the named redrawable integer
    /// <c>streamDraw</c> site: a pile of n tokens consumes n - 1 samples, so the site's cursor advances by exactly
    /// that and a replay reproduces the permutation.</summary>
    /// <param name="Row">The ordered zone.</param>
    /// <param name="Draw">The integer streamDraw site supplying the samples.</param>
    public sealed record Shuffle(string Row, string Draw) : WorldStateTransform;
    /// <summary>Reorders a row's cells by value, stably: an ordered zone by attribute rows over its token domain, the
    /// first key deciding and each later key breaking the ties before it; a keyed numeric row by its own cell values.
    /// The canonical order a pattern reads a hand in.</summary>
    /// <param name="Row">The ordered zone or keyed numeric row.</param>
    /// <param name="By">The attribute keys for a zone, 1..8 in precedence order; null (required) for a keyed row.</param>
    /// <param name="Descending">Whether a keyed row's greatest value comes first; a zone's direction sits on each key.</param>
    public sealed record Sort(string Row, IReadOnlyList<WorldSortKey>? By = null, bool Descending = false) : WorldStateTransform;

    /// <summary>Writes one value into every cell of a board whose bit is set in a mask read from a state cell: the
    /// way a mask computed by <c>$board:mask</c> and the bit operators lands back on the board.</summary>
    /// <param name="Row">The board row, over a topology of at most 64 cells.</param>
    /// <param name="Mask">The integer row the mask is read from.</param>
    /// <param name="MaskKey">The cell of that row, or null for its slot cell.</param>
    /// <param name="Value">The value written to every masked cell.</param>
    public sealed record SetMask(string Row, string Mask, string? MaskKey = null, long Value = 0) : WorldStateTransform;

    /// <summary>Writes a board carried through one point-group element of its topology into another board over the
    /// same topology: the half turn is how one side's rules read the other side's position.</summary>
    /// <param name="Target">The board row written.</param>
    /// <param name="Source">The board row read.</param>
    /// <param name="Element">An element name <c>world.topology</c> lists for the topology.</param>
    public sealed record MapBoard(string Target, string Source, string Element) : WorldStateTransform;

    /// <summary>Appends one value to a history row's ring, overwriting the oldest slot once the ring is full, and
    /// advances its cursor by one.</summary>
    /// <param name="Row">The history row.</param>
    /// <param name="Value">The raw value pushed, in the row's kind.</param>
    public sealed record Push(string Row, long Value) : WorldStateTransform;

    /// <summary>Combines two board rows over one topology cell by cell as sets (a nonzero cell is a member) and
    /// writes 1 or 0 into every cell of the target: board algebra for topologies too large for one 64-bit mask.</summary>
    /// <param name="Target">The board row written, over the same topology as the operands.</param>
    /// <param name="Left">The left operand board.</param>
    /// <param name="Operation">The set operation.</param>
    /// <param name="Right">The right operand board; null for <see cref="WorldBoardCombine.Not"/> only.</param>
    public sealed record Combine(string Target, string Left, WorldBoardCombine Operation, string? Right = null) : WorldStateTransform;
}

/// <summary>The cell-wise set operations of a <see cref="WorldStateTransform.Combine"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldBoardCombine>))]
public enum WorldBoardCombine : byte {
    /// <summary>Members of both.</summary>
    And,
    /// <summary>Members of either.</summary>
    Or,
    /// <summary>Members of exactly one.</summary>
    Xor,
    /// <summary>Members of the left that are not members of the right.</summary>
    AndNot,
    /// <summary>Cells that are not members of the left; the right is absent.</summary>
    Not,
}

/// <summary>One key of a zone <c>sort</c>: a keyed numeric attribute row over the zone's token domain.</summary>
/// <param name="Row">The attribute row.</param>
/// <param name="Descending">Whether the greatest value comes first under this key.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSortKey(string Row, bool Descending = false);
