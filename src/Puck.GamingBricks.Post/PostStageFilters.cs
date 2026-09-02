namespace Puck.GamingBricks.Post;

/// <summary>Shared <c>--tier</c>/<c>--filter</c> predicates for a POST battery's stage list, generic over the
/// battery's per-run context type so both machine families' entry points can select the same way.</summary>
public static class PostStageFilters {
    /// <summary>Returns <see langword="true"/> when <paramref name="tierFilter"/> is absent, or matches
    /// <paramref name="stage"/>'s <see cref="IPostStage{TContext}.Tier"/> case-insensitively.</summary>
    /// <param name="stage">The stage being considered.</param>
    /// <param name="tierFilter">The <c>--tier</c> value, or <see langword="null"/>/empty to match every tier.</param>
    public static bool TierMatches<TContext>(IPostStage<TContext> stage, string? tierFilter) =>
        (string.IsNullOrEmpty(value: tierFilter) || string.Equals(
        a: stage.Tier.ToString(),
        b: tierFilter,
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));
    /// <summary>Returns <see langword="true"/> when <paramref name="nameFilter"/> is absent, or is contained in
    /// <paramref name="stage"/>'s <see cref="IPostStage{TContext}.Name"/> case-insensitively.</summary>
    /// <param name="stage">The stage being considered.</param>
    /// <param name="nameFilter">The <c>--filter</c> value, or <see langword="null"/>/empty to match every name.</param>
    public static bool NameMatches<TContext>(IPostStage<TContext> stage, string? nameFilter) =>
        (string.IsNullOrEmpty(value: nameFilter) || stage.Name.Contains(
        comparisonType: StringComparison.OrdinalIgnoreCase,
        value: nameFilter
    ));
}
