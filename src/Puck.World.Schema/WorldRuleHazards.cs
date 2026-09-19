namespace Puck.World;

/// <summary>The <c>world.rule.hazards</c> read-back: <see cref="Puck.State.Rules.RuleHazards"/> over a document's
/// compiled rules.</summary>
public static class WorldRuleHazards {
    /// <summary>Analyzes a validated definition's rules in document order.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>Every hazard, earliest pair first.</returns>
    public static IReadOnlyList<Puck.State.Rules.RuleHazard> Analyze(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Puck.State.Rules.RuleHazards.Analyze(
            catalog: definition.StateCatalog,
            rules: WorldFactsCompiler.CompileAll(definition: definition)
        );
    }
}
