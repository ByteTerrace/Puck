namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// A ledger-gated Tier-B stage: discovers a suite's cases from the resolved corpus root and hands them to
/// <see cref="LedgerEvaluator"/>. One instance per suite; the discovery function is the only thing that varies
/// between suites (blargg's <c>$A000</c> singles, mooneye's serial Fibonacci signature, a screenshot suite's
/// device-tagged expected images, and so on all become one <see cref="LedgerCase"/> shape here).
/// </summary>
internal sealed class LedgerRomStage : IPostStage<PostContext> {
    private readonly Func<PostContext, IReadOnlyList<LedgerCase>> m_discover;
    private readonly string m_name;

    /// <summary>Initializes a new instance of the <see cref="LedgerRomStage"/> class.</summary>
    /// <param name="name">The stage's stable display name.</param>
    /// <param name="discover">Discovers the suite's cases from the run context.</param>
    public LedgerRomStage(string name, Func<PostContext, IReadOnlyList<LedgerCase>> discover) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: discover);

        m_discover = discover;
        m_name = name;
    }

    /// <inheritdoc/>
    public string Name =>
        m_name;
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        LedgerEvaluator.Evaluate(
        cases: m_discover(context),
        context: context
    );
}
