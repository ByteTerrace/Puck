namespace Puck.Post;

/// <summary>The ordered POST stage registry. The battery runs these in declaration order (tiers A→D); the
/// <c>--tier</c> and <c>--filter</c> options select a subset. Stages are added here as each milestone lands.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IReadOnlyList<IPostStage> Create() {
        return [
            // Tier A — CPU pre-flight. The self-tests run first: a determinism gate cannot catch a
            // wrong-but-deterministic operation, so correctness is proven before reproducibility.
            new FixedPointStage(),
            new WorldCoord3Stage(),
            new DeterminismStage(),
            new CliDeterminismStage(),

            // Tier B — same-device GPU smoke on the offscreen Vulkan host.
            new ComputeStage(),
        ];
    }
}
