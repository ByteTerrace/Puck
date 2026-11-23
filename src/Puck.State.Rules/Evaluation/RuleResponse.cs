using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>What stops a binding whose gate holds from firing again on the next tick.</summary>
public enum RuleLatching : byte {
    /// <summary>Nothing: a Level gate that holds fires every tick it holds.</summary>
    None,

    /// <summary>The rule is an Edge: a binding fires on the crossing alone and re-arms once its gate has closed.</summary>
    Edge,

    /// <summary>The firing closes its own gate (<see cref="RuleResponse.ClosesItself"/>): a binding fires once, and
    /// again only after another line's write reopens it.</summary>
    ClosesItself,
}
/// <summary>How many of a line's bindings the work sheet charges a check and a firing for: the bindings one tick at
/// rest runs, plus the bindings one change reaches. It is read off the same judgement walk a
/// <see cref="RuleSchedule"/> is built from, so the sheet prices what a due list runs.</summary>
/// <remarks>
/// <para>A tick <b>at rest</b> is one in which time passes and bodies move but no state cell and no event-like host
/// fact moves. A binding is judged at rest only when its judgement reads something that moves with time: the tick
/// outside a deadline, a row carrying a value-over-time trait, or a host fact that is not at rest
/// (<see cref="RuleCompileContext.FactRests"/>). A line whose judgement reads none of those is not visited at rest.
/// A binding fires at rest when nothing latches it: a Level gate that held fires again, and an Edge over a moving
/// input can cross again; a firing that closes its own gate cannot, whatever moves.</para>
/// <para><b>One change</b> is one moved cell or host fact among the things at rest the judgement reads. It reaches
/// one binding when every such thing is read under the binding's own key, and every binding when one of them is
/// shared. A shared cell reached through a per-binding index (<c>goal[group[$each]]</c>) opens the bindings that
/// index one cell of it, taken as an even share of the row.</para>
/// </remarks>
/// <param name="Judged">The bindings charged a check.</param>
/// <param name="Fired">The bindings charged a firing.</param>
/// <param name="Latching">What stops a binding that fired from firing again.</param>
/// <param name="Why">The derivation, for the read-back.</param>
public readonly record struct RuleResponse(long Judged, long Fired, RuleLatching Latching, string Why) {
    /// <summary>Derives a line's response.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="multiplier">The bindings one sweep can hold.</param>
    /// <param name="context">The context the rule compiled in, which says which rows and host facts rest.</param>
    /// <param name="inPlace">Whether the line is judged in place every tick whatever its reads: one its host
    /// evaluates itself.</param>
    /// <returns>The response.</returns>
    public static RuleResponse Of(CompiledRule rule, long multiplier, RuleCompileContext context, bool inPlace = false) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        var firings = RuleWorkBudget.Firings(multiplier: multiplier, rule: rule);
        var latching = ((rule.Mode == ActionTriggerMode.Edge) ? RuleLatching.Edge : (ClosesItself(rule: rule) ? RuleLatching.ClosesItself : RuleLatching.None));

        // The lines a due list visits every tick in full (RuleDueList.Classify): a sweep bounded by maxFirings, one
        // over a live row selection, and one whose key set no version names.
        if (
            inPlace ||
            (rule.MaxFirings > 0) ||
            (rule.Zones is { References.Count: > 0 }) ||
            (rule.PoolForEach is not null) ||
            rule.ForEachZones ||
            (rule.ForEachBodies && !context.LiveBodiesVersioned) ||
            ((rule.Iteration is { } iteration) && !iteration.Versioned)
        ) {
            return new RuleResponse(Fired: firings, Judged: multiplier, Latching: latching, Why: "judged in place every tick");
        }

        var reads = new List<CellAccess>();
        var needs = new RuleNeedsBuilder();
        var deadline = RuleDeadline.Find(gate: rule.Gate);

        RuleDataflow.CollectJudgement(deadline: deadline, facts: needs, reads: reads, rule: rule);

        var built = needs.Build();
        string? moving = null;
        // The most bindings one move of something at rest can open, and whether it reaches every binding's judgement.
        // A deadline coming due judges every binding (a due list wakes the rule whole), and opens the bindings whose
        // own due tick it is: one when each binding reads its due tick under its own key.
        var opened = 0L;
        var shared = (deadline is not null);

        if (deadline is not null) {
            var due = new List<CellAccess>();
            var token = rule.Gate[deadline.Token];

            (deadline.TickOnLeft ? token.RightSource : token.LeftSource).CollectReads(into: due);
            opened = (((due.Count > 0) && due.TrueForAll(match: static access => access.Each)) ? 1L : multiplier);
        }

        if (built.ReadsTick) {
            moving = $"reads {RuleFacts.Tick}";
        } else if (built.ReadsSearchPly && !context.SearchPlyIsFixed) {
            moving = $"reads {RuleFacts.SearchPly}";
        } else if (built.HostOwnedRows.Count > 0) {
            moving = "reads a host-owned row";
        }
        foreach (var fact in needs.FacetFacts) {
            if (!context.FactRests(each: out var each, fact: fact)) {
                moving ??= $"reads {fact}, which moves with time";

                continue;
            }
            opened = Math.Max(val1: opened, val2: (each ? 1L : multiplier));
            shared |= !each;
        }
        foreach (var access in reads) {
            if (!context.RowRests(rowOrdinal: access.RowOrdinal)) {
                moving ??= $"reads row {context.FindRowAt(rowOrdinal: access.RowOrdinal)?.Name.Value ?? $"#{access.RowOrdinal}"}, which moves with time";

                continue;
            }
            if (access.Each || access.Members) {
                opened = Math.Max(val1: opened, val2: 1L);

                continue;
            }
            shared = true;
            opened = Math.Max(val1: opened, val2: (access.Indexed
                ? multiplier.CeilingDivide(divisor: Math.Max(val1: 1L, val2: context.RowCapacity(rowOrdinal: access.RowOrdinal)))
                : multiplier
            ));
        }
        // A sweep's own set is something every binding rests on: a key that joins or leaves is a change.
        if (multiplier > 1L) {
            opened = Math.Max(val1: opened, val2: 1L);
        }

        // At rest: a Level gate nothing latches fires every tick it holds, so every binding may be judged and fire;
        // an Edge over a moving input can cross again and again.
        var refires = ((latching == RuleLatching.None) || ((latching == RuleLatching.Edge) && (moving is not null)));
        var judgedAtRest = (((moving is not null) || refires) ? multiplier : 0L);
        var firedAtRest = (refires ? firings : 0L);
        var judged = Math.Min(val1: multiplier, val2: BinaryIntegerFunctions.AddSaturating(left: judgedAtRest, right: (shared ? multiplier : Math.Min(val1: opened, val2: 1L))));
        var fired = Math.Min(val1: firings, val2: BinaryIntegerFunctions.AddSaturating(left: firedAtRest, right: opened));
        var rest = ((judgedAtRest == 0L)
            ? "not visited at rest"
            : ((moving is not null) ? $"judged every tick ({moving})" : "judged every tick (a Level gate that holds fires again)"));
        var fire = latching switch {
            RuleLatching.ClosesItself => "its firing closes its own gate",
            RuleLatching.Edge => ((moving is null) ? "an Edge fires on the crossing" : "an Edge over a moving input can cross every tick"),
            _ => "a Level gate that holds fires every tick",
        };

        return new RuleResponse(
            Fired: fired,
            Judged: judged,
            Latching: latching,
            Why: $"{rest}; {fire}; one change opens {opened} of {multiplier} binding(s)"
        );
    }
    /// <summary>Returns whether a rule's firing provably closes its own gate, so a binding that fired stays closed
    /// until another line writes: the gate is one comparison, or a conjunction one of whose terms is a comparison,
    /// that tests a cell the binding itself names (a literal key or the key bound to <c>$each</c>); the rule's one write to that cell's row
    /// is an unconditional set of that cell; and the value written fails the comparison. Either the comparison is
    /// against a literal the written literal fails, or it is <c>cell != source</c> and the write is
    /// <c>cell = source</c>, the same read, of rows the firing does not write.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns><see langword="true"/> when the proof holds; <see langword="false"/> proves nothing either way.</returns>
    public static bool ClosesItself(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var gate = Array.FindAll(array: rule.Gate, match: static token => (token.Op != GateOp.ShortCircuit));

        if (gate.Length == 0) {
            return false;
        }

        var writes = RuleDataflow.Writes(rule: rule);
        var last = (gate.Length - 1);

        // The gate's top-level terms: the gate itself when it is one comparison, or each operand of the conjunction it
        // ends in. A term that is itself a disjunction or a negation proves nothing and is passed over; the gate
        // holds only while every term does, so one comparison the firing falsifies closes it.
        if (gate[last].Op == GateOp.Compare) {
            return ((gate.Length == 1) && Falsified(token: gate[0]));
        }
        if (gate[last].Op != GateOp.All) {
            return false;
        }
        for (var (operand, end) = (0, (last - 1)); ((operand < gate[last].Arity) && (end >= 0)); operand++) {
            var start = TermStart(gate: gate, end: end);

            if ((start == end) && (gate[end].Op == GateOp.Compare) && Falsified(token: gate[end])) {
                return true;
            }
            end = (start - 1);
        }

        return false;

        bool Falsified(GateToken token) => (
            Closes(cell: token.LeftSource, comparison: token.Comparison, other: token.RightSource, rule: rule, writes: writes) ||
            ((token.Comparison == ExpressionOp.NotEqual) && Closes(cell: token.RightSource, comparison: token.Comparison, other: token.LeftSource, rule: rule, writes: writes))
        );
    }

    // Where the postfix term ending at `end` starts: a comparison is one token, and a connective follows its operands.
    private static int TermStart(GateToken[] gate, int end) {
        var operands = (gate[end].Op switch {
            GateOp.All or GateOp.Any => gate[end].Arity,
            GateOp.Not => 1,
            _ => 0,
        });
        var start = end;

        for (var operand = 0; ((operand < operands) && (start > 0)); operand++) {
            start = TermStart(end: (start - 1), gate: gate);
        }

        return start;
    }

    // Whether the rule's one write to `cell`'s row is an unconditional set of that cell to a value `cell <comparison>
    // other` rejects.
    private static bool Closes(in CompiledValueSource cell, ExpressionOp comparison, in CompiledValueSource other, CompiledRule rule, List<CellAccess> writes) {
        if (Cell(source: cell) is not { RowFrom: null } read || !Fixed(key: read.KeyFrom)) {
            return false;
        }

        var written = 0;

        foreach (var write in writes) {
            if (write.RowOrdinal == read.RowOrdinal) {
                written++;
            }
        }
        if (written != 1) {
            return false;
        }
        foreach (var effect in rule.Effects) {
            if ((effect is not WriteEffect { Write: StateWriteKind.Set, RowFrom: null, Text: null } set) ||
                (set.RowOrdinal != read.RowOrdinal) || (set.Key != read.Key) || !Nullable.Equals(n1: set.KeyFrom, n2: read.KeyFrom)) {
                continue;
            }
            if (other.IsLiteral && set.Source.IsLiteral) {
                // Raw values of one cell kind order as the fixed-point values of those bits do. An operation that is
                // no ordering proves nothing.
                return (comparison is (ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.LessOrEqual or ExpressionOp.Greater or ExpressionOp.GreaterOrEqual)) && !comparison.Holds(expected: FixedQ4816.FromRawBits(value: other.RawValue), value: FixedQ4816.FromRawBits(value: set.Source.RawValue));
            }
            if ((comparison != ExpressionOp.NotEqual) || (Cell(source: other) is not { RowFrom: null } mirrored) || (Cell(source: set.Source) is not { RowFrom: null } source)) {
                return false;
            }

            // The same read on both sides, of nothing the firing writes: once written, the cell equals it until
            // another line moves one of them.
            var reads = new List<CellAccess>();

            mirrored.CollectReads(into: reads);

            return (
                (mirrored.RowOrdinal == source.RowOrdinal) && (mirrored.Key == source.Key) && Nullable.Equals(n1: mirrored.KeyFrom, n2: source.KeyFrom) &&
                (mirrored.ValueKind == read.ValueKind) && Plain(key: mirrored.KeyFrom) &&
                !reads.Exists(match: access => writes.Exists(match: write => (write.RowOrdinal == access.RowOrdinal)))
            );
        }

        return false;
    }
    // The cell a source reads when it is one plain cell read: the operand itself, or a program of that one read.
    private static StateCellOperand? Cell(in CompiledValueSource source) => (source.Operand as StateCellOperand) ?? ((source.Expression is [{ Operand: StateCellOperand operand }]) ? operand : null);
    // A key the binding itself fixes: a literal, or the key bound to $each.
    private static bool Fixed(CompiledCellRef? key) => ((key is null) || key.Value.IsEach);
    // A key no program or key fact computes, so two equal references name the same cell.
    private static bool Plain(CompiledCellRef? key) => ((key is null) || ((key.Value.Program is null) && (key.Value.Custom is null)));
}
