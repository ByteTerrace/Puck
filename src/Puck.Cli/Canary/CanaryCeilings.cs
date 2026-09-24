namespace Puck.Cli.Canary;

/// <summary>The most one gate selection of <c>puck canary</c> may cost: World boots and the summed leg budget in
/// seconds, both as <see cref="CanaryCommand.Plan"/> counts them from the selected manifests.</summary>
/// <param name="WorldBoots">The most World processes the selection's legs may boot.</param>
/// <param name="LegBudgetSeconds">The most the selection's per-leg timeouts may sum to.</param>
internal sealed record CanaryCeiling(int WorldBoots, int LegBudgetSeconds);
/// <summary>
/// The declared cost ceilings of the two gate selections. A run or <c>--plan</c> of a gate selection whose plan
/// exceeds its ceiling is refused before anything builds, so a gate cannot grow without a reviewed change to this
/// file. Raise a ceiling in the same change that deliberately adds the cost, stating the new plan counts
/// (<c>puck canary --merge --plan</c>) in its commit.
/// </summary>
internal static class CanaryCeilings {
    /// <summary>The automatic set: a bare <c>puck canary</c>, <c>puck landing</c>, and <c>--capability automatic</c>.</summary>
    public static readonly CanaryCeiling Automatic = new(
        LegBudgetSeconds: 2860,
        WorldBoots: 74
    );
    /// <summary>The merge gate: <c>puck canary --merge</c>.</summary>
    public static readonly CanaryCeiling Merge = new(
        LegBudgetSeconds: 7700,
        WorldBoots: 156
    );
}
