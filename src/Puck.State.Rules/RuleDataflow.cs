namespace Puck.State.Rules;

/// <summary>The walks that fold a compiled shape into the cells it touches and its state-value observations. The
/// compiler uses them to form <see cref="RuleNeeds"/>; the evaluator uses the scheduling variant for local memo
/// schedules, which also follows identity-address dependencies.</summary>
public static class RuleDataflow {
    /// <summary>Appends every fact a compiled effect list carries, including the facts of the arms its branches and
    /// savepoints hold.</summary>
    /// <param name="effects">The compiled effects.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectEffectFacts(IRuleEffect[] effects, RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var effect in effects) {
            into.AddFact(fact: effect);
            if (effect is IfEffect branch) {
                CollectGateFacts(
                    gate: branch.Condition,
                    into: into
                );
            }
            foreach (var arm in effect.Arms) {
                CollectEffectFacts(
                    effects: arm,
                    into: into
                );
            }
        }
    }
    /// <summary>Appends every state cell a compiled expression reads.</summary>
    /// <param name="tokens">The compiled postfix program, or <see langword="null"/>.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectExpression(CompiledExpressionToken[]? tokens, List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in (tokens ?? [])) {
            token.Operand?.CollectReads(into: into);
            // A fold reads every cell of its one row, whatever cells the row was authored with.
            if (token.Fold is { } fold) {
                into.Add(item: new CellAccess(
                    Key: default,
                    RowOrdinal: fold.RowOrdinal
                ));

                CollectExpression(
                    into: into,
                    tokens: fold.Body
                );
            }
            if (token.Call is { } call) {
                CollectExpression(
                    into: into,
                    tokens: call.Body
                );
            }
        }
    }
    /// <summary>Appends the facts a compiled expression observes as values.</summary>
    /// <param name="tokens">The compiled postfix program, or <see langword="null"/>.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectExpressionFacts(CompiledExpressionToken[]? tokens, RuleNeedsBuilder into) {
        CollectExpressionFacts(
            includeKeyDependencies: false,
            into: into,
            tokens: tokens
        );
    }
    /// <summary>Appends an expression's value observations and key indirections for a local memo schedule.</summary>
    /// <param name="tokens">The compiled postfix program, or <see langword="null"/>.</param>
    /// <param name="into">The needs being built.</param>
    /// <remarks>Key-fact facets describe how a host resolves a key, not what a state-backed expression observes.
    /// They therefore stay out of compile-time rule admission and movement-pass safety checks, while this evaluator-
    /// only walk still makes a local volatile when its lookup key depends on the clock or a host fact.</remarks>
    public static void CollectExpressionSchedulingFacts(CompiledExpressionToken[]? tokens, RuleNeedsBuilder into) {
        CollectExpressionFacts(
            includeKeyDependencies: true,
            into: into,
            tokens: tokens
        );
    }
    /// <summary>Appends everything one judgement of a rule observes — its locals (declared and implicit, all evaluated
    /// before the gate), its live-row selection and its gate, never its effects, which run only once the verdict is
    /// in — as the rows it reads and the facts it observes: what a read schedule is built from.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <param name="reads">The read set being collected.</param>
    /// <param name="facts">The needs being built, with every facet-bearing fact instance recorded.</param>
    /// <param name="deadline">The gate's deadline conjunct, whose tick side is the schedule's wake rather than a read,
    /// or <see langword="null"/>.</param>
    public static void CollectJudgement(CompiledRule rule, List<CellAccess> reads, RuleNeedsBuilder facts, RuleDeadline? deadline = null) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        ArgumentNullException.ThrowIfNull(argument: reads);
        ArgumentNullException.ThrowIfNull(argument: facts);

        for (var index = 0; (index < rule.Gate.Length); index++) {
            var token = rule.Gate[index];
            var deadlineHere = ((deadline is not null) && (deadline.Token == index));

            if (!deadlineHere || !deadline!.TickOnLeft) {
                token.LeftSource.CollectReads(into: reads);
                CollectSourceFacts(into: facts, source: token.LeftSource);
            }
            if (!deadlineHere || deadline!.TickOnLeft) {
                token.RightSource.CollectReads(into: reads);
                CollectSourceFacts(into: facts, source: token.RightSource);
            }
        }
        foreach (var local in (rule.Locals ?? [])) {
            CollectExpression(into: reads, tokens: local.Expression);
            CollectExpressionSchedulingFacts(into: facts, tokens: local.Expression);
        }
        foreach (var live in (rule.Zones?.References ?? [])) {
            live.CollectReads(into: reads);
            live.CollectFacts(into: facts);
        }
    }
    /// <summary>Appends what one value source observes, its key indirections included: the walk a schedule is built
    /// from.</summary>
    /// <param name="source">The compiled value source.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectSourceFacts(in CompiledValueSource source, RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        CollectOperandFacts(includeKeyDependencies: true, into: into, operand: source.Operand);
        CollectExpressionSchedulingFacts(into: into, tokens: source.Expression);
    }

    private static void CollectExpressionFacts(CompiledExpressionToken[]? tokens, RuleNeedsBuilder into, bool includeKeyDependencies) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in (tokens ?? [])) {
            CollectOperandFacts(
                includeKeyDependencies: includeKeyDependencies,
                into: into,
                operand: token.Operand
            );
            if (token.Fold is { } fold) {
                CollectExpressionFacts(
                    includeKeyDependencies: includeKeyDependencies,
                    into: into,
                    tokens: fold.Body
                );
            }
            if (token.Call is { } call) {
                CollectExpressionFacts(
                    includeKeyDependencies: includeKeyDependencies,
                    into: into,
                    tokens: call.Body
                );
            }
        }
    }
    // One operand's observations: its own fact, the tick and the search ply (library operands whose compile-time
    // markers ICompiledFact does not carry), and every program it evaluates inside itself — a local's source, a
    // pattern's token expression, a history read's age — so a schedule built from this walk is volatile wherever its
    // operand reads are.
    private static void CollectOperandFacts(IRuleOperand? operand, RuleNeedsBuilder into, bool includeKeyDependencies) {
        into.AddFact(fact: operand);
        switch (operand) {
            case TickOperand:
                into.MarkReadsTick();
                break;
            case SearchPlyOperand:
                into.MarkReadsSearchPly();
                break;
            case LocalOperand { Source: { } source }:
                CollectExpressionFacts(includeKeyDependencies: includeKeyDependencies, into: into, tokens: source.Expression);
                break;
            case PatternOperand { TokenExpression: { } tokens }:
                CollectExpressionFacts(includeKeyDependencies: includeKeyDependencies, into: into, tokens: tokens);
                break;
            case HistoryOperand { AgeFrom: { Program: { } age } }:
                CollectExpressionFacts(includeKeyDependencies: includeKeyDependencies, into: into, tokens: age);
                break;
        }
        if (includeKeyDependencies && (operand is IStateAddressedOperand { KeyFrom: { } keyFrom })) {
            CompiledCellRef.CollectFacts(
                into: into,
                reference: keyFrom
            );
        }
    }

    /// <summary>Appends every state cell a compiled gate reads.</summary>
    /// <param name="gate">The compiled postfix gate.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectGate(GateToken[] gate, List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in gate) {
            token.LeftSource.CollectReads(into: into);
            token.RightSource.CollectReads(into: into);
        }
    }
    /// <summary>Appends every fact a compiled gate carries.</summary>
    /// <param name="gate">The compiled postfix gate.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectGateFacts(GateToken[] gate, RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in gate) {
            token.LeftSource.CollectFacts(into: into);
            token.RightSource.CollectFacts(into: into);
        }
    }
    /// <summary>Returns every state cell one evaluation of a rule reads.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns>The read set.</returns>
    public static List<CellAccess> Reads(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var reads = new List<CellAccess>();

        rule.CollectReads(into: reads);

        return reads;
    }
    /// <summary>Returns every state cell one firing of a rule writes.</summary>
    /// <param name="rule">The compiled rule.</param>
    /// <returns>The write set.</returns>
    public static List<CellAccess> Writes(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var writes = new List<CellAccess>();

        rule.CollectWrites(into: writes);

        return writes;
    }
    /// <summary>Returns the rows a rule evaluation and optional expression depend on, including rule writes whose
    /// scratch values later rules may read, in ordinal order.</summary>
    /// <param name="rules">The rules evaluated in one frame.</param>
    /// <param name="score">An expression evaluated after the rules, or null.</param>
    public static int[] KeyRows(CompiledRule[] rules, CompiledExpressionToken[]? score) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var accesses = new List<CellAccess>();
        var rows = new SortedSet<int>();

        foreach (var rule in rules) {
            accesses.Clear();
            rule.CollectReads(into: accesses);
            rule.CollectWrites(into: accesses);

            foreach (var access in accesses) {
                if (access.RowOrdinal >= 0) {
                    _ = rows.Add(item: access.RowOrdinal);
                }
            }
        }

        accesses.Clear();

        RuleDataflow.CollectExpression(
            into: accesses,
            tokens: score
        );

        foreach (var access in accesses) {
            if (access.RowOrdinal >= 0) {
                _ = rows.Add(item: access.RowOrdinal);
            }
        }

        return [.. rows];
    }
}
