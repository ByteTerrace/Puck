namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// A Tier-B stage over one corpus suite: discovers the suite's cases from the resolved corpus root and hands them to
/// <see cref="SuiteEvaluator"/>, which runs each and reports what it did. One instance per suite; the discovery
/// function is the only thing that varies between suites (blargg's <c>$A000</c> singles, mooneye's serial Fibonacci
/// signature, a screenshot suite's device-tagged expected images, and so on all become one <see cref="SuiteCase"/>
/// shape here).
/// </summary>
internal sealed class SuiteRomStage : IPostStage<PostContext> {
    private readonly Func<PostContext, IReadOnlyList<SuiteCase>> m_discover;
    private readonly string m_name;

    /// <summary>Initializes a new instance of the <see cref="SuiteRomStage"/> class.</summary>
    /// <param name="name">The stage's stable display name, also the directory its mismatch images land in.</param>
    /// <param name="discover">Discovers the suite's cases from the run context.</param>
    public SuiteRomStage(string name, Func<PostContext, IReadOnlyList<SuiteCase>> discover) {
        ArgumentException.ThrowIfNullOrEmpty(argument: name);
        ArgumentNullException.ThrowIfNull(argument: discover);

        m_discover = discover;
        m_name = name;
    }

    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        m_name;
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.B;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) =>
        SuiteEvaluator.Evaluate(
            cases: m_discover(context),
            context: context,
            name: m_name
        );
}
