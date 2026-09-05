namespace Puck.World;

/// <summary>State-layout-specific flock expressions. Recompile on document installation, even when the
/// physical population does not rebuild, so state handles remain current.</summary>
public sealed class CompiledWorldFlockAffinities {
    /// <summary>Compiles the two independent neighbor weights against the current world.</summary>
    /// <param name="profile">The authored flock profile.</param>
    /// <param name="definition">The current world.</param>
    /// <exception cref="WorldRuleException">An expression is invalid.</exception>
    /// <exception cref="ArgumentNullException">The profile or definition is null.</exception>
    public CompiledWorldFlockAffinities(WorldFlockProfile profile, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(definition);
        Cohesion = profile.CohesionAffinity is { } cohesion ? WorldRuleCompiler.CompileFlockAffinity(cohesion, definition) : null;
        Alignment = profile.AlignmentAffinity is { } alignment ? WorldRuleCompiler.CompileFlockAffinity(alignment, definition) : null;
        WorkUnitsPerNeighbor = (Cohesion is null ? 0 : WorldRuleWorkBudget.ExpressionCost(Cohesion, definition)) +
            (Alignment is null ? 0 : WorldRuleWorkBudget.ExpressionCost(Alignment, definition));
    }
    /// <summary>Gets the centroid expression, or null for uniform influence.</summary>
    public CompiledWorldExpressionToken[]? Cohesion { get; }
    /// <summary>Gets the heading expression, or null for uniform influence.</summary>
    public CompiledWorldExpressionToken[]? Alignment { get; }
    /// <summary>Gets the conservative token and state-candidate-visit cost of evaluating both expressions once.</summary>
    public long WorkUnitsPerNeighbor { get; }
}
