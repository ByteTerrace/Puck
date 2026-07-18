namespace Puck.HumbleGamingBrick.Post;

/// <summary>One row of the <see cref="PostReport"/>: a stage's identity paired with its <see cref="PostStageOutcome"/>.</summary>
/// <param name="Name">The stage's display name.</param>
/// <param name="Tier">The tier the stage ran in.</param>
/// <param name="Outcome">The stage's outcome.</param>
internal sealed record PostStageResult(string Name, PostTier Tier, PostStageOutcome Outcome);
