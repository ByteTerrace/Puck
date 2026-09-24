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
    /// <summary>Gets a value indicating whether the battery may run this stage while other stages run. A stage says so
    /// when it owns every machine it builds and shares nothing through static state or the console, whether it
    /// measures its cases on one thread or on many. A stage whose result is a measurement of the processor — wall-clock
    /// throughput, or allocation that depends on how far the JIT has tiered the code — leaves this false and runs alone
    /// after every concurrent stage has finished.</summary>
    bool IsConcurrent => false;

    /// <summary>Runs the stage's checks once and returns the outcome.</summary>
    /// <param name="context">The shared run context.</param>
    /// <returns>The stage's outcome.</returns>
    PostStageOutcome Run(TContext context);
}
