namespace Puck.GamingBricks.Post;

/// <summary>One row of a battery report: a stage's identity paired with its <see cref="PostStageOutcome"/> and the
/// wall-clock time it took.</summary>
/// <param name="Name">The stage's display name.</param>
/// <param name="Tier">The tier the stage ran in.</param>
/// <param name="Outcome">The stage's outcome.</param>
/// <param name="Duration">The wall-clock time the stage took, including its own discovery and setup.</param>
public sealed record PostStageResult(string Name, PostTier Tier, PostStageOutcome Outcome, TimeSpan Duration);
