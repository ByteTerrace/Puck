namespace Puck.State;

/// <summary>The reserved <c>compareState</c> channels a world rule may compare against instead
/// of a declared a state row — time, population, region occupancy, a screen-machine's live memory,
/// row aggregates/extrema, spatial and navigation facts for named bodies, a body's own reconnect-park state, and a
/// local seat's own channel value, all folded into the same string channel <c>State</c> already carries, never a
/// second predicate language or scheduler subsystem.
/// </summary>
/// <remarks>Every one of them carries the reserved name prefix that no authored row name may
/// spell, so a reserved channel can never be shadowed by (or mistaken for) a real row — the validator refuses such a
/// row before a rule could ever resolve ambiguously.</remarks>
public static class RuleFacts {
    /// <summary>The prefix; <c>$match:&lt;pattern&gt;:&lt;row&gt;[:&lt;direction&gt;]</c> runs a <c>patterns</c> row
    /// over a word: a board ray from the operand key's origin (exclusive) in the direction (or every direction under
    /// <c>any</c>), an ordered zone's attribute values in pile order, a history ring in push order, or a keyed row's
    /// own cells. Reads acceptance 1 or 0, or under a facet the longest accepted prefix or the accepting
    /// directions.</summary>
    public const string MatchPrefix = "$match:";
    /// <summary>The prefix; <c>$history:&lt;row&gt;:&lt;age&gt;</c> reads the value pushed <c>age</c> pushes ago into a
    /// history row (0 is the latest), or the ring's empty value past what it holds; age is 0..capacity-1.</summary>
    public const string HistoryPrefix = "$history:";
    /// <summary>The prefix a cell KEY may carry in place of a literal: <c>$cell:&lt;row&gt;:&lt;key&gt;</c> resolves, at
    /// every read and every firing, to the integer value of that cell spelled as a key — so an effect or operand
    /// addresses "the cell named by another cell" (the target a body's <c>target</c> cell currently names). Admitted
    /// on a <c>compareState</c> <c>key</c>/<c>comparandKey</c> and on a world-scope effect's <c>key</c>/<c>fromKey</c>;
    /// a body-reference token spells the same indirection as <c>cell:&lt;row&gt;:&lt;key&gt;</c>.</summary>
    public const string CellKeyPrefix = "$cell:";
    /// <summary>The prefix of a rule-scoped bound value: <c>$bind:&lt;name&gt;</c> reads the value the enclosing
    /// rule's same-named binding computed for this evaluation.</summary>
    public const string BindPrefix = "$bind:";
    /// <summary>The prefix of a static table read: <c>$table:&lt;name&gt;:&lt;key&gt;</c>, where the key is an
    /// integer literal, a <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection, or the bound <c>$each</c> key.</summary>
    public const string TablePrefix = "$table:";
    /// <summary>The prefix; <c>$reduce:&lt;op&gt;:&lt;row&gt;</c> aggregates every cell a keyed (or slot) row
    /// declares — <c>max</c>/<c>min</c>/<c>sum</c> read the row's own <c>CellKind</c>, <c>count</c> is always
    /// integer (the number of cells present, regardless of what they hold). The reserved-channel exemption from
    /// the compiler's ordinary (row, key) pair rule: a reduction addresses the whole row rather
    /// than one cell, so it is the one place a keyed row is read with no key at all — admitted deliberately, not a
    /// hole in the pair rule (the compiler's reduce branch). The optional suffix
    /// <c>:where:&lt;filterRow&gt;</c> restricts the aggregate to matching keys whose numeric filter cell is nonzero.</summary>
    public const string ReducePrefix = "$reduce:";
    /// <summary>The prefix; <c>$symmetry:&lt;function&gt;[:&lt;argument&gt;]:&lt;row&gt;</c> reads a cell holding a
    /// symmetry-lattice node (0..239, <c>Puck.Maths.SymmetryLattice</c>) through one of the lattice's own maps — the
    /// row is named last and the operand's <c>key</c> addresses the cell exactly as an ordinary row read does, so a
    /// per-body node table reads through <c>$each</c> or a <c>$cell:</c> indirection unchanged. Functions:
    /// <c>ring</c> (the node's ring, 0..7), <c>antipode</c>, <c>canonicalRay</c> (the smaller node of the antipodal
    /// pair), <c>cycle:&lt;steps&gt;</c> (the node carried that many positions around its ring, negative walks back),
    /// <c>reflect:&lt;other&gt;</c> (the node reflected through the other node's hyperplane), <c>orthogonal:&lt;other&gt;</c>
    /// (1 when the two nodes' rays are orthogonal, else 0), <c>innerProduct:&lt;other&gt;</c> (the two roots' exact
    /// pairing, -2..2 — 1 names one of the 56 neighbours at sixty degrees), and <c>projectionX</c>/<c>projectionY</c>
    /// (the node's point on the plane of eight rings, a fixed value). <c>&lt;other&gt;</c> is a node literal or
    /// <c>cell:&lt;row&gt;[.&lt;key&gt;]</c>, a second cell read live. A source cell holding no node (outside 0..239)
    /// reads <c>-1</c> for the node-valued functions, <c>0</c> for <c>orthogonal</c>, <c>innerProduct</c> and the projections — the
    /// ordinary "absent reads as the neutral value" convention. A <c>fixed</c> source cell's node is the whole part of
    /// its value, the same reading a cycle lattice output takes. With
    /// a cycle lattice's <c>Node</c> output driving a row and this channel reading it, a rule can gate
    /// on the ring a walk has reached, reflect one player's arrangement onto another's, or test two placements for
    /// orthogonality — the lattice's whole symmetry group, reached through <c>compareState</c>/<c>fromState</c>.</summary>
    public const string SymmetryPrefix = "$symmetry:";
    /// <summary>Compares the server's own completed-tick counter — <c>compareState("$tick", greaterOrEqual, 600)</c>
    /// is "at 2.5 seconds", with no clock read anywhere.</summary>
    public const string Tick = "$tick";
}
