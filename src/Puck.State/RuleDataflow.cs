namespace Puck.State;

/// <summary>The read and write sets of a compiled rule — what a work sheet's writer count and the hazard read-back
/// both rest on. Each rule walks its own gate, bindings, and effects through <see cref="CompiledRule.CollectReads"/>
/// and <see cref="CompiledRule.CollectWrites"/>; a document project's rule adds the branches it alone carries.</summary>
public static class RuleDataflow {
    /// <summary>Lists every state cell the rule reads, each once.</summary>
    /// <param name="rule">The compiled rule.</param>
    public static IReadOnlyList<RuleAccess> Reads(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var reads = new List<RuleAccess>();

        rule.CollectReads(into: reads);

        return Distinct(accesses: reads);
    }

    /// <summary>Lists every state cell the rule writes, each once.</summary>
    /// <param name="rule">The compiled rule.</param>
    public static IReadOnlyList<RuleAccess> Writes(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        var writes = new List<RuleAccess>();

        rule.CollectWrites(into: writes);

        return Distinct(accesses: writes);
    }

    /// <summary>Returns whether a rule reads a fact only the document host answers — anywhere in its bindings, gate,
    /// or effects — so a frame over the section alone cannot evaluate it faithfully.</summary>
    /// <param name="rule">The compiled rule.</param>
    public static bool ReadsHost(CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        foreach (var binding in (rule.Bindings ?? [])) {
            if (ExpressionReadsHost(tokens: binding.Expression)) {
                return true;
            }
        }
        foreach (var token in rule.Gate) {
            if ((token.Left is { HostOnly: true }) || (token.Comparand is { HostOnly: true }) || ExpressionReadsHost(tokens: token.LeftExpression) || ExpressionReadsHost(tokens: token.RightExpression)) {
                return true;
            }
        }
        foreach (var effect in rule.Effects) {
            if (effect.ReadsHost) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether an expression program reads a fact only the document host answers — the same
    /// primitive <see cref="ReadsHost"/> applies to a rule's gate, bindings, and effects, exposed for a document
    /// project compiling a bare expression outside a rule (a search job's score).</summary>
    /// <param name="tokens">The compiled postfix program, or <see langword="null"/>.</param>
    public static bool ExpressionReadsHost(CompiledExpressionToken[]? tokens) {
        if (tokens is null) {
            return false;
        }

        foreach (var token in tokens) {
            if (token.Operand is { HostOnly: true }) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Appends the cells a gate reads: every operand and expression of every token.</summary>
    public static void CollectGate(GateToken[] gate, List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in gate) {
            token.Left?.CollectReads(into: into);
            token.Comparand?.CollectReads(into: into);
            if (token.LeftExpression is { } left) { CollectExpression(tokens: left, into: into); }
            if (token.RightExpression is { } right) { CollectExpression(tokens: right, into: into); }
        }
    }

    /// <summary>Appends the cells an expression reads.</summary>
    public static void CollectExpression(CompiledExpressionToken[] tokens, List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: tokens);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var token in tokens) {
            token.Operand?.CollectReads(into: into);
        }
    }

    /// <summary>Appends the cells a list of effects reads.</summary>
    public static void CollectEffectReads(EffectFact[] effects, List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var effect in effects) {
            effect.CollectReads(into: into);
        }
    }

    /// <summary>Appends the cells a list of effects writes.</summary>
    public static void CollectEffectWrites(EffectFact[] effects, List<RuleAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var effect in effects) {
            effect.CollectWrites(into: into);
        }
    }

    private static List<RuleAccess> Distinct(List<RuleAccess> accesses) {
        var seen = new HashSet<RuleAccess>();
        var result = new List<RuleAccess>(capacity: accesses.Count);

        foreach (var access in accesses) {
            if (seen.Add(item: access)) {
                result.Add(item: access);
            }
        }

        return result;
    }
}
