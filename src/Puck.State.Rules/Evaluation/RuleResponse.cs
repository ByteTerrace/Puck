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

    /// <summary>The firing settles (<see cref="RuleResponse.Settles"/>): a second firing over unmoved reads writes what
    /// the first wrote, so the evaluator takes the binding as settled and visits it again only when something its
    /// firing reads or writes moves.</summary>
    Settles,
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
    /// <summary>Gets a value indicating whether the line is a sweep over a row family that a member's change alone
    /// opens: nothing fires at rest, and every read at rest reaches the binding through its member (a family selection
    /// or a cell under <c>$member</c>), none under <c>$each</c> or shared. Its firings in one tick are then one member's,
    /// so a gate pin on a cell under <c>$member</c> discriminates it from another such line
    /// (<see cref="RuleWorkBudget.PinnedCells"/>).</summary>
    public bool PinsByMember { get; init; }
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

        // A gate the host proves can never hold is judged where it is visited and fires nothing there.
        var holdsNever = NeverHolds(context: context, rule: rule);
        var firings = (holdsNever ? 0L : RuleWorkBudget.Firings(multiplier: multiplier, rule: rule));
        var closure = ((rule.Mode == ActionTriggerMode.Edge) ? Closure.None : Prove(context: context, rule: rule));
        var latching = ((rule.Mode == ActionTriggerMode.Edge)
            ? RuleLatching.Edge
            : ((closure != Closure.None) ? RuleLatching.ClosesItself : (Settles(rule: rule) ? RuleLatching.Settles : RuleLatching.None)));
        var never = (holdsNever ? "; its gate never holds on this host" : string.Empty);

        // A sweep of this tick's events binds nothing at rest, and one event, the change the sheet prices, binds one
        // binding whatever it reads. A maxFirings bound or a host that judges in place keeps the capacity price.
        if (!inPlace && (rule.MaxFirings <= 0) && (rule.Iteration is { BindsEvents: true })) {
            return new RuleResponse(
                Fired: Math.Min(val1: firings, val2: 1L),
                Judged: Math.Min(val1: multiplier, val2: 1L),
                Latching: latching,
                Why: $"binds only this tick's events: none at rest, one binding per event{never}"
            );
        }

        // The lines a due list visits every tick in full (RuleDueList.Classify): a sweep bounded by maxFirings, one
        // over a live row selection, and one whose key set no version names.
        if (
            inPlace ||
            (rule.MaxFirings > 0) ||
            (rule.Zones is { References.Count: > 0, PerBinding: false }) ||
            (rule.PoolForEach is not null) ||
            rule.ForEachZones ||
            (rule.ForEachBodies && !context.LiveBodiesVersioned) ||
            ((rule.Iteration is { } iteration) && !iteration.Versioned)
        ) {
            return new RuleResponse(Fired: firings, Judged: multiplier, Latching: latching, Why: $"judged in place every tick{never}");
        }

        var reads = new List<CellAccess>();
        var needs = new RuleNeedsBuilder();
        var deadline = RuleDeadline.Find(gate: rule.Gate);

        RuleDataflow.CollectJudgement(deadline: deadline, facts: needs, reads: reads, rule: rule);

        var built = needs.Build();
        string? moving = null;
        // The most bindings one move of something at rest can open, and whether it reaches every binding's judgement.
        // A deadline coming due opens the bindings whose own due tick it is: one when each binding reads its due tick
        // under its own key. A keyed sweep's due list wakes those bindings alone (RuleDueList.Wake); any other rule
        // is woken whole, so the deadline judges every binding.
        var opened = 0L;
        var shared = false;
        // A sweep over a row family binds a member's keys: a move a member's bindings read whole (a family selection or
        // a cell under $member) opens that member's bindings, and a move of the cell under one $each key opens that
        // key's binding in every member. Any other sweep's own reads open one binding.
        var members = ((rule.ForEachFamily is { } family) ? MemberShape(context: context, family: family) : (Count: 1L, Capacity: 1L));

        long Own(CellAccess access) => (access.Members ? 1L : (access.Each ? members.Count : ((access.IsMember || access.MemberKey) ? members.Capacity : 1L)));
        // A read at rest under $each, or a host fact read for $each's body, opens that key in every member.
        var eachRead = false;

        if (deadline is not null) {
            var due = new List<CellAccess>();
            var token = rule.Gate[deadline.Token];

            (deadline.TickOnLeft ? token.RightSource : token.LeftSource).CollectReads(into: due);

            var ownDue = ((due.Count > 0) && due.TrueForAll(match: static access => (access.Each || access.IsMember || access.MemberKey)));

            opened = (ownDue ? due.Max(selector: Own) : multiplier);
            shared = (!ownDue || (RuleDueList.SweepKind(rule: rule) == RuleDueList.Visit.Whole));
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
            opened = Math.Max(val1: opened, val2: (each ? members.Count : multiplier));
            shared |= !each;
            eachRead |= each;
        }
        foreach (var access in reads) {
            if (!context.RowRests(rowOrdinal: access.RowOrdinal)) {
                moving ??= $"reads row {context.FindRowAt(rowOrdinal: access.RowOrdinal)?.Name.Value ?? $"#{access.RowOrdinal}"}, which moves with time";

                continue;
            }
            if (access.Each || access.Members || access.IsMember || access.MemberKey) {
                opened = Math.Max(val1: opened, val2: Own(access: access));
                eachRead |= access.Each;

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

        // A mirrored closure holds only while the value the gate compares against stands still: one that moves with
        // time reopens the gate every tick, so the line is priced as a Level gate nothing latches.
        if ((closure == Closure.Mirror) && (moving is not null)) {
            latching = RuleLatching.None;
        }
        // A settled firing stands on everything the firing reads (RuleEvaluator's firing schedule): one that reads
        // something moving with time never settles, so the line is priced as a Level gate nothing latches.
        if ((latching == RuleLatching.Settles) && ((moving is not null) || FiringMoves(context: context, deadline: deadline, rule: rule))) {
            latching = RuleLatching.None;
        }

        // At rest: a Level gate nothing latches fires every tick it holds, so every binding may be judged and fire;
        // an Edge over a moving input can cross again and again.
        // A gate that never holds neither holds again nor crosses again, so only a moving read revisits it at rest.
        var refires = (!holdsNever && ((latching == RuleLatching.None) || ((latching == RuleLatching.Edge) && (moving is not null))));
        var judgedAtRest = (((moving is not null) || refires) ? multiplier : 0L);
        var firedAtRest = (refires ? firings : 0L);
        // A settling binding fires twice for a change: once with it, and once more on the next tick, which proves the
        // firing changed nothing and settles it.
        var echo = ((latching == RuleLatching.Settles) ? 2L : 1L);
        var judged = Math.Min(val1: multiplier, val2: BinaryIntegerFunctions.AddSaturating(left: judgedAtRest, right: (shared ? multiplier : Math.Min(val1: opened, val2: multiplier))));
        var fired = Math.Min(val1: firings, val2: BinaryIntegerFunctions.AddSaturating(left: firedAtRest, right: BinaryIntegerFunctions.MultiplySaturating(left: opened, right: echo)));
        var rest = ((judgedAtRest == 0L)
            ? "not visited at rest"
            : ((moving is not null) ? $"judged every tick ({moving})" : "judged every tick (a Level gate that holds fires again)"));
        var fire = (holdsNever ? "its gate never holds on this host" : latching switch {
            RuleLatching.ClosesItself => "its firing closes its own gate",
            RuleLatching.Settles => "its firing settles after one more",
            RuleLatching.Edge => ((moving is null) ? "an Edge fires on the crossing" : "an Edge over a moving input can cross every tick"),
            _ => "a Level gate that holds fires every tick",
        });

        return new RuleResponse(
            Fired: fired,
            Judged: judged,
            Latching: latching,
            Why: $"{rest}; {fire}; one change opens {opened} of {multiplier} binding(s)"
        ) { PinsByMember = ((rule.ForEachFamily is not null) && !shared && !eachRead && (judgedAtRest == 0L) && (firedAtRest == 0L)) };
    }
    /// <summary>Returns whether a rule's gate provably never holds on the host its context describes: a host that
    /// answers one search ply (<see cref="RuleCompileContext.SearchPlyIsFixed"/>) answers the live ply, zero
    /// (<see cref="IStateReader.SearchPly"/>), so a gate a comparison of the ply against a literal closes at zero,
    /// whatever its other comparisons answer, never holds there. Such a line is judged where it is visited and
    /// fires nothing; its firings are a search judge's, which prices them apart.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="context">The context the rule compiled in.</param>
    /// <returns><see langword="true"/> when the proof holds; <see langword="false"/> proves nothing either way.</returns>
    public static bool NeverHolds(CompiledRule rule, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: context);

        if (!context.SearchPlyIsFixed || (rule.Gate.Length == 0)) {
            return false;
        }

        var gate = rule.Gate;

        return (GateTruth.Fold(gate: gate, leaf: index => AtLivePly(token: gate[index])) == GateTruth.False);

        // A comparison of the ply against a literal, answered at the live ply's raw value, zero in either numeric
        // kind; any other comparison is unknown.
        static byte AtLivePly(in GateToken token) {
            var live = FixedQ4816.FromRawBits(value: 0L);

            if ((token.Op != GateOp.Compare) ||
                (token.Comparison is not (ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.LessOrEqual or ExpressionOp.Greater or ExpressionOp.GreaterOrEqual))) {
                return GateTruth.Unknown;
            }
            if ((Operand(source: token.LeftSource) is SearchPlyOperand) && token.RightSource.IsLiteral) {
                return (token.Comparison.Holds(expected: FixedQ4816.FromRawBits(value: token.RightSource.RawValue), value: live) ? GateTruth.True : GateTruth.False);
            }
            if ((Operand(source: token.RightSource) is SearchPlyOperand) && token.LeftSource.IsLiteral) {
                return (token.Comparison.Holds(expected: live, value: FixedQ4816.FromRawBits(value: token.LeftSource.RawValue)) ? GateTruth.True : GateTruth.False);
            }

            return GateTruth.Unknown;
        }
    }
    // A swept family's member count and its widest member's capacity, read from declarations.
    private static (long Count, long Capacity) MemberShape(RuleCompileContext context, RowFamily family) {
        var count = 0L;
        var capacity = 0L;

        foreach (var member in family.Ordinals()) {
            count++;
            capacity = Math.Max(val1: capacity, val2: context.RowCapacity(rowOrdinal: member));
        }

        return (Math.Max(val1: count, val2: 1L), Math.Max(val1: capacity, val2: 1L));
    }
    /// <summary>Returns whether a rule's firing provably closes its own gate, so a binding that fired stays closed
    /// until another line writes: the gate is one comparison, or a conjunction one of whose terms is a comparison,
    /// that tests a cell the binding itself names (a literal key or the key bound to <c>$each</c>); the rule's one write to that cell's row
    /// is an unconditional set of that cell; and the value written fails the comparison. Either the comparison is
    /// against a literal the written literal fails, or it is <c>cell != source</c> and the write is
    /// <c>cell = source</c>, the same read (one plain cell, or one local of the rule), of rows and facts the firing
    /// does not write. With <paramref name="context"/>, the cell may also be a host fact an effect sets exactly
    /// (<see cref="IFactSettingEffect"/>), such as a body's register.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="context">The context the rule compiled in, or <see langword="null"/> to prove over arena cells
    /// alone.</param>
    /// <returns><see langword="true"/> when the proof holds; <see langword="false"/> proves nothing either way.</returns>
    public static bool ClosesItself(CompiledRule rule, RuleCompileContext? context = null) => (Prove(context: context, rule: rule) != Closure.None);
    /// <summary>Returns whether a Level rule's firing provably settles: every effect, at any depth of its transactions
    /// and branches, is a plain set of an arena cell, and nothing the firing computes its values, keys, rows or
    /// branches from (its locals and its effects' reads) is a row the firing writes. A second firing over reads that
    /// have not moved then writes exactly what the first wrote, and <see cref="RuleEvaluator"/> takes a Level firing
    /// that moved nothing as settled, so the binding is not visited again until something its firing reads or writes
    /// moves. Whether the reads rest is the caller's to judge.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns><see langword="true"/> when the proof holds; <see langword="false"/> proves nothing either way.</returns>
    public static bool Settles(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        if ((rule.Mode == ActionTriggerMode.Edge) || (rule.Effects.Length == 0) || !PlainSets(effects: rule.Effects)) {
            return false;
        }

        var reads = new List<CellAccess>();
        var writes = RuleDataflow.Writes(rule: rule);

        foreach (var local in (rule.Locals ?? [])) {
            RuleDataflow.CollectExpression(into: reads, tokens: local.Expression);
        }
        foreach (var effect in rule.Effects) {
            effect.CollectReads(into: reads);
        }

        return !reads.Exists(match: read => writes.Exists(match: write => (write.RowOrdinal == read.RowOrdinal)));

        static bool PlainSets(IReadOnlyList<IRuleEffect> effects) {
            foreach (var effect in effects) {
                switch (effect) {
                    case WriteEffect { Write: StateWriteKind.Set, IsTextEdit: false }:
                        break;
                    case TransactionEffect or IfEffect:
                        foreach (var arm in effect.Arms) {
                            if (!PlainSets(effects: arm)) {
                                return false;
                            }
                        }
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }
    }

    // Whether anything a firing reads beyond its judgement moves with time: the tick, the search ply, a host-owned row,
    // a host fact that does not rest or a row carrying a value-over-time trait.
    private static bool FiringMoves(CompiledRule rule, RuleCompileContext context, RuleDeadline? deadline) {
        var reads = new List<CellAccess>();
        var needs = new RuleNeedsBuilder();

        RuleDataflow.CollectFiring(deadline: deadline, facts: needs, reads: reads, rule: rule);

        var built = needs.Build();

        if (built.ReadsTick || (built.ReadsSearchPly && !context.SearchPlyIsFixed) || (built.HostOwnedRows.Count > 0)) {
            return true;
        }
        foreach (var fact in needs.FacetFacts) {
            if (!context.FactRests(each: out _, fact: fact)) {
                return true;
            }
        }

        return !reads.TrueForAll(match: access => context.RowRests(rowOrdinal: access.RowOrdinal));
    }

    // How a firing closes its own gate: against a literal, which holds whatever moves, or by writing the value the gate
    // compares against, which holds only while that value stands.
    private enum Closure : byte { None, Literal, Mirror }

    private static Closure Prove(CompiledRule rule, RuleCompileContext? context) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var gate = Array.FindAll(array: rule.Gate.Tokens, match: static token => (token.Op != GateOp.ShortCircuit));

        if (gate.Length == 0) {
            return Closure.None;
        }

        var writes = RuleDataflow.Writes(rule: rule);
        var last = (gate.Length - 1);

        // The gate's top-level terms: the gate itself when it is one comparison, or each operand of the conjunction it
        // ends in. A term that is itself a disjunction or a negation proves nothing and is passed over; the gate
        // holds only while every term does, so one comparison the firing falsifies closes it.
        if (gate[last].Op == GateOp.Compare) {
            return ((gate.Length == 1) ? Falsified(token: gate[0]) : Closure.None);
        }
        if (gate[last].Op != GateOp.All) {
            return Closure.None;
        }

        var best = Closure.None;

        for (var (operand, end) = (0, (last - 1)); ((operand < gate[last].Arity) && (end >= 0)); operand++) {
            var start = TermStart(gate: gate, end: end);

            if ((start == end) && (gate[end].Op == GateOp.Compare)) {
                var term = Falsified(token: gate[end]);

                if (term == Closure.Literal) {
                    return term;
                }
                best = ((term == Closure.Mirror) ? term : best);
            }
            end = (start - 1);
        }

        return best;

        Closure Falsified(GateToken token) {
            var closure = Closes(cell: token.LeftSource, comparison: token.Comparison, context: context, other: token.RightSource, rule: rule, writes: writes);

            return (((closure == Closure.None) && (token.Comparison == ExpressionOp.NotEqual))
                ? Closes(cell: token.RightSource, comparison: token.Comparison, context: context, other: token.LeftSource, rule: rule, writes: writes)
                : closure);
        }
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

    // Whether the rule's one write to what `cell` reads is an unconditional set of it to a value `cell <comparison>
    // other` rejects: an arena cell the binding names, or a host fact an effect sets exactly.
    private static Closure Closes(in CompiledValueSource cell, ExpressionOp comparison, RuleCompileContext? context, in CompiledValueSource other, CompiledRule rule, List<CellAccess> writes) {
        CompiledValueSource written;
        CellKind kind;

        if ((Cell(source: cell) is { } read) && Owned(read: read)) {
            if (!Fixed(key: read.KeyFrom) || !SetsCell(read: read, rule: rule, writes: writes, written: out written)) {
                return Closure.None;
            }
            kind = read.ValueKind;
        } else if ((context is not null) && (Operand(source: cell) is { } fact and not (StateCellOperand or LocalOperand)) && SetsFact(context: context, fact: fact, rule: rule, written: out written)) {
            kind = fact.ValueKind;
        } else {
            return Closure.None;
        }
        if (other.IsLiteral && written.IsLiteral) {
            // Raw values of one cell kind order as the fixed-point values of those bits do. An operation that is
            // no ordering proves nothing.
            return (((comparison is (ExpressionOp.Equal or ExpressionOp.NotEqual or ExpressionOp.Less or ExpressionOp.LessOrEqual or ExpressionOp.Greater or ExpressionOp.GreaterOrEqual)) && !comparison.Holds(expected: FixedQ4816.FromRawBits(value: other.RawValue), value: FixedQ4816.FromRawBits(value: written.RawValue)))
                ? Closure.Literal
                : Closure.None);
        }
        if (comparison != ExpressionOp.NotEqual) {
            return Closure.None;
        }

        // The same read on both sides, of nothing the firing writes: once written, the cell equals it until another
        // line moves one of them.
        var reads = new List<CellAccess>();

        if ((Cell(source: other) is { } mirrored) && Owned(read: mirrored) && (Cell(source: written) is { } source) && Owned(read: source)) {
            if (!(SameRow(left: mirrored.RowFrom, leftOrdinal: mirrored.RowOrdinal, right: source.RowFrom, rightOrdinal: source.RowOrdinal) && (mirrored.Key == source.Key) && Nullable.Equals(n1: mirrored.KeyFrom, n2: source.KeyFrom) &&
                (mirrored.ValueKind == kind) && Plain(key: mirrored.KeyFrom))) {
                return Closure.None;
            }
            mirrored.CollectReads(into: reads);
        } else if ((Operand(source: other) is LocalOperand local) && (Operand(source: written) is LocalOperand same) && (local.Ordinal == same.Ordinal) && (local.ValueKind == kind)) {
            // One local, bound once per judgement and read by both the gate and the write. Its program may read any
            // local before it, so every local's facts are tested against what the firing writes.
            local.CollectReads(into: reads);

            var facts = new RuleNeedsBuilder();

            foreach (var bound in (rule.Locals ?? [])) {
                RuleDataflow.CollectExpressionSchedulingFacts(into: facts, tokens: bound.Expression);
            }
            foreach (var observed in facts.FacetFacts) {
                if (WritesFact(effects: rule.Effects, fact: observed)) {
                    return Closure.None;
                }
            }
        } else {
            return Closure.None;
        }

        return (reads.Exists(match: access => writes.Exists(match: write => (write.RowOrdinal == access.RowOrdinal))) ? Closure.None : Closure.Mirror);
    }
    // Whether the rule's one write to `read`'s row is an unconditional set of that cell.
    private static bool SetsCell(StateCellOperand read, CompiledRule rule, List<CellAccess> writes, out CompiledValueSource written) {
        written = default;

        var count = 0;
        var row = RowOf(live: read.RowFrom, own: read.RowOrdinal);

        foreach (var write in writes) {
            if (write.RowOrdinal == row) {
                count++;
            }
        }
        if (count != 1) {
            return false;
        }

        return Find(effects: rule.Effects, read: read, written: out written);

        // A savepoint with no refusal branch lands its steps whenever the firing lands, as the firing's top-level steps
        // do; one with a refusal branch can land that branch instead.
        static bool Find(IReadOnlyList<IRuleEffect> effects, StateCellOperand read, out CompiledValueSource written) {
            foreach (var effect in effects) {
                if ((effect is WriteEffect { Write: StateWriteKind.Set, Text: null } set) &&
                    SameRow(left: set.RowFrom, leftOrdinal: set.RowOrdinal, right: read.RowFrom, rightOrdinal: read.RowOrdinal) && (set.Key == read.Key) && Nullable.Equals(n1: set.KeyFrom, n2: read.KeyFrom)) {
                    written = set.Source;

                    return true;
                }
                if ((effect is TransactionEffect { OnFailure.Length: 0 } savepoint) && Find(effects: savepoint.Effects, read: read, written: out written)) {
                    return true;
                }
            }
            written = default;

            return false;
        }
    }
    // Whether the rule's one write to what a host fact reads is an unconditional, exact set of it. Every effect at any
    // depth is an arena effect or a fact setter, so no host effect the proof cannot see moves the fact.
    private static bool SetsFact(IRuleOperand fact, RuleCompileContext context, CompiledRule rule, out CompiledValueSource written) {
        written = default;

        if (!Transparent(effects: rule.Effects) || (CountWrites(effects: rule.Effects, fact: fact) != 1)) {
            return false;
        }
        foreach (var effect in rule.Effects) {
            if ((effect is IFactSettingEffect setter) && setter.Writes(fact: fact)) {
                written = setter.Source;

                return setter.SetsExactly(context: context, fact: fact);
            }
        }

        return false;

        static bool Transparent(IReadOnlyList<IRuleEffect> effects) {
            foreach (var effect in effects) {
                if (effect is not (RuleEffect or IFactSettingEffect)) {
                    return false;
                }
                foreach (var arm in effect.Arms) {
                    if (!Transparent(effects: arm)) {
                        return false;
                    }
                }
            }

            return true;
        }
    }
    private static int CountWrites(IReadOnlyList<IRuleEffect> effects, ICompiledFact fact) {
        var count = 0;

        foreach (var effect in effects) {
            count += (((effect is IFactSettingEffect setter) && setter.Writes(fact: fact)) ? 1 : 0);
            foreach (var arm in effect.Arms) {
                count += CountWrites(effects: arm, fact: fact);
            }
        }

        return count;
    }
    private static bool WritesFact(IReadOnlyList<IRuleEffect> effects, ICompiledFact fact) => (CountWrites(effects: effects, fact: fact) > 0);
    // The cell a source reads when it is one plain cell read: the operand itself, or a program of that one read.
    private static StateCellOperand? Cell(in CompiledValueSource source) => (Operand(source: source) as StateCellOperand);
    // The one operand a source reads: the operand itself, or a program of that one read.
    private static IRuleOperand? Operand(in CompiledValueSource source) => (source.Operand ?? ((source.Expression is [{ Operand: { } operand }]) ? operand : null));
    // One cell the binding names whatever moves: a fixed row's, or the cell of the family member the binding's own key
    // selects (`turn[$member]` over `slot turn[n]`), which no other binding's firing reaches.
    private static bool Owned(StateCellOperand read) => ((read.RowFrom is null) || (read.RowFrom is { Family: not null, SelectsOwnMember: true }));
    // Whether two cell addresses name one row for every binding: one fixed row, or one family's member the binding's own
    // key selects.
    private static bool SameRow(LiveRow? left, int leftOrdinal, LiveRow? right, int rightOrdinal) => (((left is null) && (right is null))
        ? (leftOrdinal == rightOrdinal)
        : ((left is { Family: { } leftFamily, SelectsOwnMember: true }) && (right is { Family: { } rightFamily, SelectsOwnMember: true }) &&
            (leftFamily.FirstOrdinal == rightFamily.FirstOrdinal) && (leftFamily.Count == rightFamily.Count)));
    // The row a write to an address is counted against: a fixed row's own, or its family's first member, which every
    // write to a member the binding selects names once.
    private static int RowOf(LiveRow? live, int own) {
        if (live is not { Family: { } family }) {
            return own;
        }
        for (var index = 0; (index <= RuleCompiler.FamilySpan(family: family)); index++) {
            if (family.TryGetOrdinal(index: index, ordinal: out var ordinal)) {
                return ordinal;
            }
        }

        return own;
    }
    // A key the binding itself fixes: a literal, or the key bound to $each or $member.
    private static bool Fixed(CompiledCellRef? key) => ((key is null) || key.Value.IsEach || key.Value.IsMember);
    // A key no program or key fact computes, so two equal references name the same cell.
    private static bool Plain(CompiledCellRef? key) => ((key is null) || ((key.Value.Program is null) && (key.Value.Custom is null)));
}
