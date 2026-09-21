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
            if (token.Fold is { } fold) {
                for (var ordinal = fold.RowOrdinal; (ordinal < (fold.RowOrdinal + fold.Members)); ordinal++) {
                    into.Add(item: new CellAccess(
                        Key: default,
                        RowOrdinal: ordinal
                    ));
                }

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
    private static void CollectExpressionFacts(CompiledExpressionToken[]? tokens, RuleNeedsBuilder into, bool includeKeyDependencies) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in (tokens ?? [])) {
            into.AddFact(fact: token.Operand);
            // Tick is a library operand, not an effect or facet, so its compilation-time marker is not carried by
            // ICompiledFact. This walk also builds local memo schedules after compilation, where it must restore
            // that volatility from the token itself.
            if (token.Operand is TickOperand) {
                into.MarkReadsTick();
            }
            // A local operand has no facet of its own; its dependency is the earlier binding it reads. Follow it
            // here as CollectExpression already does for row reach, so a memoized alias stays volatile whenever
            // its source is volatile and advertises the source's host facets.
            if (token.Operand is LocalOperand { Source: { } source }) {
                CollectExpressionFacts(
                    includeKeyDependencies: includeKeyDependencies,
                    into: into,
                    tokens: source.Expression
                );
            }
            if (includeKeyDependencies && (token.Operand is IStateAddressedOperand { KeyFrom: { } keyFrom })) {
                CompiledCellRef.CollectFacts(
                    into: into,
                    reference: keyFrom
                );
            }
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
}
