namespace Puck.Physics;

/// <summary>Structural work performed by one gravity solve, independent of machine timing.</summary>
/// <param name="BodyCount">The number of target bodies.</param>
/// <param name="TreeNodeCount">The number of non-empty octree nodes built; zero for the pairwise solver.</param>
/// <param name="ExactSourceEvaluations">The number of individual source-to-target evaluations.</param>
/// <param name="ApproximatedNodeEvaluations">The number of monopoles accepted during per-target tree walks; zero for
/// solvers that share one acceptance across a whole cell, whose translation work is
/// <paramref name="MultipoleToLocalTranslations"/>.</param>
/// <param name="ApproximatedSourceCount">The total source population represented by accepted approximations.</param>
/// <param name="MultipoleToMultipoleTranslations">The number of child moments reduced into adaptive FMM parents.</param>
/// <param name="MultipoleToLocalTranslations">The number of directional cell-to-cell translations.</param>
/// <param name="LocalToLocalTranslations">The number of parent expansions propagated toward children, including
/// expansions deferred to leaf evaluation because their gradients could not be combined.</param>
/// <param name="LocalExpansionEvaluations">The number of target bodies evaluated from a leaf local expansion.</param>
/// <param name="DeferredLocalExpansionEvaluations">The number of ancestor expansions evaluated directly at targets
/// because their gradients could not be combined in Q32.32 during the downward pass.</param>
public readonly record struct GravitySolveStatistics(
    int BodyCount,
    int TreeNodeCount,
    long ExactSourceEvaluations,
    long ApproximatedNodeEvaluations,
    long ApproximatedSourceCount,
    long MultipoleToMultipoleTranslations = 0L,
    long MultipoleToLocalTranslations = 0L,
    long LocalToLocalTranslations = 0L,
    long LocalExpansionEvaluations = 0L,
    long DeferredLocalExpansionEvaluations = 0L
);
