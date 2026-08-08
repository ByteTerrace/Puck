namespace Puck.Post;

/// <summary>Runs an ordered list of <see cref="IPostStage"/> once each, isolating failures so one stage's
/// infrastructure failure (an exception, recorded as <see cref="PostVerdict.Infra"/>) never aborts the rest, and
/// gathers the results into a <see cref="PostReport"/>.</summary>
internal sealed class PostBattery {
    private readonly IReadOnlyList<IPostStage> m_stages;

    /// <summary>Initializes a new instance of the <see cref="PostBattery"/> class.</summary>
    /// <param name="stages">The stages to run, in order.</param>
    public PostBattery(IReadOnlyList<IPostStage> stages) {
        ArgumentNullException.ThrowIfNull(stages);

        m_stages = stages;
    }

    /// <summary>Runs every stage and returns the aggregate report.</summary>
    /// <param name="context">The shared run context.</param>
    /// <returns>The aggregate report.</returns>
    public PostReport Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<PostStageResult>(capacity: m_stages.Count);

        foreach (var stage in m_stages) {
            PostStageOutcome outcome;

            try {
                outcome = stage.Run(context: context);
            } catch (Exception exception) {
                outcome = PostStageOutcome.Infra(detail: $"threw {exception.GetType().Name}: {exception.Message}");
            }

            Console.Out.WriteLine(value: $"[{stage.Tier}] {stage.Name}: {outcome.Verdict} | {outcome.Detail}");
            results.Add(item: new PostStageResult(Name: stage.Name, Tier: stage.Tier, Outcome: outcome));
        }

        return new PostReport(results: results);
    }
}
