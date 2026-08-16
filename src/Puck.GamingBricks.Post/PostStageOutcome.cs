namespace Puck.GamingBricks.Post;

/// <summary>What a battery stage returns: a verdict and a one-line human-readable detail. A battery pairs this with the
/// stage's name and tier to form a <see cref="PostStageResult"/>.</summary>
/// <param name="Verdict">The stage's verdict.</param>
/// <param name="Detail">A one-line success summary or failure reason.</param>
public readonly record struct PostStageOutcome(PostVerdict Verdict, string Detail) {
    /// <summary>Creates a passing outcome.</summary>
    /// <param name="detail">The success summary.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Pass(string detail) =>
        new(
        Detail: detail,
        Verdict: PostVerdict.Pass
    );
    /// <summary>Creates a skipped outcome (neutral to the aggregate verdict).</summary>
    /// <param name="detail">The reason the stage was skipped.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Skip(string detail) =>
        new(
        Detail: detail,
        Verdict: PostVerdict.Skip
    );
    /// <summary>Creates a failing outcome (a correctness divergence; exit code 1).</summary>
    /// <param name="detail">The failure reason.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Fail(string detail) =>
        new(
        Detail: detail,
        Verdict: PostVerdict.Fail
    );
    /// <summary>Creates an infrastructure-failure outcome (the stage could not complete; exit code 2).</summary>
    /// <param name="detail">The failure reason.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Infra(string detail) =>
        new(
        Detail: detail,
        Verdict: PostVerdict.Infra
    );
}
