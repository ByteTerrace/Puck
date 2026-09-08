namespace Puck.GamingBricks.Post;

/// <summary>One self-contained check in a POST battery, generic over the machine-specific run context. A stage runs
/// once, synchronously, and returns its outcome; <see cref="PostBattery{TContext}"/> catches any exception it throws
/// and records an <see cref="PostVerdict.Infra"/> failure, so a stage may surface a failure either by returning
/// <see cref="PostStageOutcome.Fail"/>/<see cref="PostStageOutcome.Infra"/> or by throwing.</summary>
/// <typeparam name="TContext">The battery's per-run context type (artifacts directory, corpus roots, and whatever
/// else the owning machine's stages need).</typeparam>
public interface IPostStage<TContext> {
    /// <summary>The stage's stable display name (used in the report and for the <c>--filter</c> option).</summary>
    string Name { get; }
    /// <summary>The tier this stage belongs to (drives ordering and <c>--tier</c> selection).</summary>
    PostTier Tier { get; }
    /// <summary>Whether the battery may run this stage at the same time as its concurrent neighbours. A stage says so
    /// when it owns every machine it builds, shares nothing through static state, and neither measures wall-clock
    /// time nor needs a quiet processor; a stage that reports throughput or allocation, or that already saturates every
    /// processor itself, leaves this false and runs alone.</summary>
    bool IsConcurrent => false;

    /// <summary>Runs the stage's checks once and returns the outcome.</summary>
    /// <param name="context">The shared run context.</param>
    /// <returns>The stage's outcome.</returns>
    PostStageOutcome Run(TContext context);
}
