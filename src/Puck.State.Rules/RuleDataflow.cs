namespace Puck.State.Rules;

/// <summary>The walks that fold a compiled shape into the cells it touches and the facts it carries. Both are
/// compile-time walks: the read set feeds scheduling, the fact walk feeds <see cref="RuleNeeds"/>.</summary>
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
    /// <summary>Appends every fact a compiled expression carries.</summary>
    /// <param name="tokens">The compiled postfix program, or <see langword="null"/>.</param>
    /// <param name="into">The needs being built.</param>
    public static void CollectExpressionFacts(CompiledExpressionToken[]? tokens, RuleNeedsBuilder into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in (tokens ?? [])) {
            into.AddFact(fact: token.Operand);
            if (token.Fold is { } fold) {
                CollectExpressionFacts(
                    into: into,
                    tokens: fold.Body
                );
            }
            if (token.Call is { } call) {
                CollectExpressionFacts(
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
