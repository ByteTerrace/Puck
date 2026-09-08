namespace Puck.GamingBricks.Post;

/// <summary>What a battery stage returns: a verdict, a one-line human-readable detail, and — for a stage made of many
/// cases — the per-case rows, so the report can name each one. A battery pairs this with the stage's name, tier, and
/// duration to form a <see cref="PostStageResult"/>.</summary>
/// <param name="Verdict">The stage's verdict.</param>
/// <param name="Detail">A one-line success summary or failure reason.</param>
/// <param name="Cases">The per-case rows, or <see langword="null"/> for a stage that is one check.</param>
public readonly record struct PostStageOutcome(PostVerdict Verdict, string Detail, IReadOnlyList<PostCaseResult>? Cases = null) {
    /// <summary>Creates a passing outcome.</summary>
    /// <param name="detail">The success summary.</param>
    /// <param name="cases">The per-case rows, when the stage has them.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Pass(string detail, IReadOnlyList<PostCaseResult>? cases = null) =>
        new(
        Cases: cases,
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
    /// <param name="cases">The per-case rows, when the stage has them.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Fail(string detail, IReadOnlyList<PostCaseResult>? cases = null) =>
        new(
        Cases: cases,
        Detail: detail,
        Verdict: PostVerdict.Fail
    );
    /// <summary>Creates an infrastructure-failure outcome (the stage could not complete; exit code 2).</summary>
    /// <param name="detail">The failure reason.</param>
    /// <param name="cases">The per-case rows, when the stage has them.</param>
    /// <returns>The outcome.</returns>
    public static PostStageOutcome Infra(string detail, IReadOnlyList<PostCaseResult>? cases = null) =>
        new(
        Cases: cases,
        Detail: detail,
        Verdict: PostVerdict.Infra
    );
}
