namespace Puck.Post;

/// <summary>One self-contained check in the POST battery. A stage runs once, synchronously, and returns its outcome;
/// the battery catches any exception it throws and records an <see cref="PostVerdict.Infra"/> failure, so a stage may
/// surface a failure either by returning <see cref="PostStageOutcome.Fail"/>/<see cref="PostStageOutcome.Infra"/> or by
/// throwing.</summary>
internal interface IPostStage {
    /// <summary>The stage's stable display name (used in the report and for the <c>--filter</c> option).</summary>
    string Name { get; }

    /// <summary>The tier this stage belongs to (drives ordering and <c>--tier</c> selection).</summary>
    PostTier Tier { get; }

    /// <summary>Runs the stage's checks once and returns the outcome.</summary>
    /// <param name="context">The shared run context (services, artifacts directory).</param>
    /// <returns>The stage's outcome.</returns>
    PostStageOutcome Run(PostContext context);
}
