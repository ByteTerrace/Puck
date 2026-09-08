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
    /// <summary>The dynamic key prefix; <c>$zone:&lt;row&gt;:first|last</c> returns the endpoint member's
    /// original string key from an ordered zone in the active store. An empty zone resolves to the empty key,
    /// which reads absent and cannot address a write. The zone may be a live <c>$zones[&lt;index&gt;]</c>.</summary>
    public const string ZoneKeyPrefix = "$zone:";
    /// <summary>The prefix a row position may carry in place of a literal row name: <c>$zones[&lt;index&gt;]</c>
    /// selects, before each read or firing, the entry of the enclosing rule's <see cref="Rule.Zones"/> table the
    /// index names. The index is an infix cell key — <c>game[from]</c>, <c>$each</c>, <c>$bind:&lt;name&gt;</c>, or
    /// any expression — never a literal number, which would only name a row the long way. Admitted wherever a row is
    /// named: a <c>compareState</c> <c>state</c>/<c>comparandState</c>, a <c>$reduce:</c> or <c>$match:</c> row, a
    /// <c>$zone:</c> endpoint's zone, a transfer's <c>from</c>/<c>to</c>, and an expression's row. An index outside
    /// the table or at an empty entry selects no zone, and the rule's evaluation is not for it: the gate reads closed
    /// before any conjunct is consulted, and the rule trace names what each spelling selected.</summary>
    public const string LiveZonePrefix = "$zones[";
    /// <summary>The <see cref="Rule.ForEach"/> spelling that iterates the rule's own zone table rather than a row:
    /// each non-empty index in turn, bound to <c>$each</c>, so <c>$zones[$each]</c> visits every zone.</summary>
    public const string ForEachZones = "$zones";
    /// <summary>The prefix of a key computed by an expression — <c>row[from + 1]</c> in the infix spelling — which
    /// compiles to an implicit rule binding evaluated before the gate and read back as the cell key; the text after
    /// the prefix is the expression's canonical infix spelling.</summary>
    public const string ExpressionKeyPrefix = "$expr:";
    /// <summary>The prefix of a rule-scoped bound value: <c>$bind:&lt;name&gt;</c> reads the value the enclosing
    /// rule's same-named binding computed for this evaluation.</summary>
    public const string BindPrefix = "$bind:";
    /// <summary>The prefix of a static table read: <c>$table:&lt;name&gt;:&lt;key&gt;</c>, where the key is an
    /// integer literal, a <c>$cell:&lt;row&gt;:&lt;key&gt;</c> indirection, or the bound <c>$each</c> key.</summary>
    public const string TablePrefix = "$table:";
    /// <summary>The prefix; <c>$reduce:&lt;op&gt;:&lt;row&gt;</c> aggregates every cell a keyed (or slot) row
    /// declares — <c>max</c>/<c>min</c>/<c>sum</c> read the row's own <c>CellKind</c>, <c>count</c> is always
    /// integer (the number of cells present, regardless of what they hold), and <c>arrangementRank</c> is an ordered
    /// zone's order as one integer (the Lehmer rank relative to its token domain's order, k ≤ 20). The reserved-channel exemption from
    /// the compiler's ordinary (row, key) pair rule: a reduction addresses the whole row rather
    /// than one cell, so it is the one place a keyed row is read with no key at all — admitted deliberately, not a
    /// hole in the pair rule (the compiler's reduce branch). The optional suffix
    /// <c>:where:&lt;filterRow&gt;</c> restricts the aggregate to matching keys whose numeric filter cell is nonzero.
    /// <c>:between:&lt;lower&gt;:&lt;upper&gt;</c> restricts it to live values within inclusive bounds, lowered as
    /// numeric literals in the source row's kind. Both suffixes may appear once, in either order; arrangementRank
    /// admits neither filter.</summary>
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

    /// <summary>Splits a reserved channel on its colons, keeping a bracketed live-zone index
    /// (<see cref="LiveZonePrefix"/>) whole — <c>$match:run:$zones[game[from]]:prefix</c> is four tokens, however
    /// many colons the index carries.</summary>
    /// <param name="name">The channel spelling.</param>
    public static string[] SplitChannel(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        if (!name.Contains(value: '[')) {
            return name.Split(separator: ':');
        }

        var parts = new List<string>();
        var depth = 0;
        var start = 0;

        for (var index = 0; index < name.Length; index++) {
            switch (name[index]) {
                case '[':
                    depth++;
                    break;
                case ']':
                    depth--;
                    break;
                case ':' when (depth == 0):
                    parts.Add(item: name[start..index]);
                    start = (index + 1);
                    break;
            }
        }

        parts.Add(item: name[start..]);

        return [.. parts];
    }
}
