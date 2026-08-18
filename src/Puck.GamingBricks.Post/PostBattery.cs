namespace Puck.GamingBricks.Post;

/// <summary>Runs an ordered list of <see cref="IPostStage{TContext}"/> once each, isolating failures so one stage's
/// infrastructure failure (an exception, recorded as <see cref="PostVerdict.Infra"/>) never aborts the rest, and
/// gathers the results into a <see cref="PostReport"/>.</summary>
/// <typeparam name="TContext">The battery's per-run context type.</typeparam>
public sealed class PostBattery<TContext> {
    private readonly string m_banner;
    private readonly IReadOnlyList<IPostStage<TContext>> m_stages;

    /// <summary>Initializes a new instance of the <see cref="PostBattery{TContext}"/> class.</summary>
    /// <param name="banner">The report's first line — the caller's own battery/machine identification.</param>
    /// <param name="stages">The stages to run, in order.</param>
    /// <exception cref="ArgumentException"><paramref name="banner"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is <see langword="null"/>.</exception>
    public PostBattery(string banner, IReadOnlyList<IPostStage<TContext>> stages) {
        ArgumentException.ThrowIfNullOrEmpty(argument: banner);
        ArgumentNullException.ThrowIfNull(argument: stages);

        m_banner = banner;
        m_stages = stages;
    }

    /// <summary>Runs every stage and returns the aggregate report.</summary>
    /// <param name="context">The shared run context.</param>
    /// <returns>The aggregate report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PostReport Run(TContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var results = new List<PostStageResult>(capacity: m_stages.Count);

        foreach (var stage in m_stages) {
            PostStageOutcome outcome;

            try {
                outcome = stage.Run(context: context);
            } catch (Exception exception) {
                outcome = PostStageOutcome.Infra(detail: $"threw {exception.GetType().Name}: {exception.Message}");
            }

            Console.Out.WriteLine(value: $"[{stage.Tier}] {stage.Name}: {outcome.Verdict} | {outcome.Detail}");
            results.Add(item: new PostStageResult(
                Name: stage.Name,
                Tier: stage.Tier,
                Outcome: outcome
            ));
        }

        return new PostReport(
            banner: m_banner,
            results: results
        );
    }
}
