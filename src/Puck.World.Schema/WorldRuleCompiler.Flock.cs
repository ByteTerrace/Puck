namespace Puck.World;

public static partial class WorldRuleCompiler {
    /// <summary>Compiles a Fixed flock-affinity expression using the ordinary postfix evaluator. Left binds the
    /// observer and right the retained neighbor. Only state-backed and social facts are available: unlike body
    /// channels, poses, navigation, and machine facts, these do not change during the population movement pass.</summary>
    /// <param name="expression">The authored nonempty expression.</param>
    /// <param name="definition">The world declaring state rows and the social policy.</param>
    /// <returns>The validated, bounded expression.</returns>
    /// <exception cref="WorldRuleException">The expression, kind, binding, or fact is invalid in this context.</exception>
    /// <exception cref="ArgumentNullException">The expression or definition is null.</exception>
    public static CompiledWorldExpressionToken[] CompileFlockAffinity(WorldValueExpression expression, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(definition);
        var bindings = s_bindingScope;
        var policy = s_socialPolicy;
        s_bindingScope = [RuleBinding.Left, RuleBinding.Right];
        s_socialPolicy = null;
        try {
            var tokens = CompileExpression(expression, CellKind.Fixed, "flock affinity", "affinity", definition);
            foreach (var token in tokens) {
                if (token.Operand is { Kind: not (WorldRuleFactKind.StateCell or WorldRuleFactKind.Reduction or
                    WorldRuleFactKind.Symmetry or WorldRuleFactKind.Social) } operand) {
                    throw new WorldRuleException(WorldRuleRefusal.EffectKindInadmissible, "flock affinity",
                        $"{operand.Kind} is not a state-backed or social fact; movement-pass observations must be order-independent");
                }
            }
            return tokens;
        } finally { s_bindingScope = bindings; s_socialPolicy = policy; }
    }
}
