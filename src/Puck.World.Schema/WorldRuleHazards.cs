namespace Puck.World;

/// <summary>The <c>world.rule.hazards</c> read-back: <see cref="RuleHazards"/> over a document's compiled rules.</summary>
public static class WorldRuleHazards {
    /// <summary>Analyzes a validated definition's rules in document order.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>Every hazard, earliest pair first.</returns>
    public static IReadOnlyList<RuleHazard> Analyze(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return RuleHazards.Analyze(rules: WorldRuleCompiler.CompileAll(definition: definition));
    }
}
