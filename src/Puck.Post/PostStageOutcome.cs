namespace Puck.Post;

/// <summary>What an <see cref="IPostStage"/> returns: a verdict, a one-line human-readable detail, and an optional path
/// to an artifact the stage wrote. The battery pairs this with the stage's name and tier to form a
/// <see cref="PostStageResult"/>.</summary>
/// <param name="Verdict">The stage's verdict.</param>
/// <param name="Detail">A one-line success summary or failure reason.</param>
/// <param name="ArtifactPath">A path to an artifact the stage wrote (e.g. a parity image), or <see langword="null"/>.</param>
internal readonly record struct PostStageOutcome(PostVerdict Verdict, string Detail, string? ArtifactPath = null) {
    /// <summary>Creates a passing outcome.</summary>
    /// <param name="detail">The success summary.</param>
    /// <param name="artifactPath">An optional artifact path.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Pass(string detail, string? artifactPath = null) =>
        new(Verdict: PostVerdict.Pass, Detail: detail, ArtifactPath: artifactPath);
    /// <summary>Creates a skipped outcome (neutral to the aggregate verdict).</summary>
    /// <param name="detail">The reason the stage was skipped.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Skip(string detail) =>
        new(Verdict: PostVerdict.Skip, Detail: detail);
    /// <summary>Creates a failing outcome (a correctness divergence; exit code 1).</summary>
    /// <param name="detail">The failure reason.</param>
    /// <param name="artifactPath">An optional artifact path.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Fail(string detail, string? artifactPath = null) =>
        new(Verdict: PostVerdict.Fail, Detail: detail, ArtifactPath: artifactPath);
    /// <summary>Creates an infrastructure-failure outcome (the stage could not complete; exit code 2).</summary>
    /// <param name="detail">The failure reason.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Infra(string detail) =>
        new(Verdict: PostVerdict.Infra, Detail: detail);
}
