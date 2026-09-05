using System.Diagnostics;

namespace Puck.GamingBricks.Post;

/// <summary>Runs an ordered list of <see cref="IPostStage{TContext}"/> once each, isolating failures so one stage's
/// infrastructure failure (an exception, recorded as <see cref="PostVerdict.Infra"/>) never aborts the rest, timing
/// each, and gathering the results into a <see cref="PostReport"/>. Stages run one after another: a stage that
/// measures many cases parallelizes inside itself, where the cases are independent by construction, while the stages
/// themselves may own threads, measure throughput, or hold the console.</summary>
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
        var batteryStart = Stopwatch.GetTimestamp();

        foreach (var stage in m_stages) {
            var stageStart = Stopwatch.GetTimestamp();
            PostStageOutcome outcome;

            try {
                outcome = stage.Run(context: context);
            } catch (Exception exception) {
                outcome = PostStageOutcome.Infra(detail: $"threw {exception.GetType().Name}: {exception.Message}");
            }

            var duration = Stopwatch.GetElapsedTime(startingTimestamp: stageStart);

            Console.Out.WriteLine(value: $"[{stage.Tier}] {stage.Name}: {outcome.Verdict} | {PostReport.FormatDuration(duration: duration)} | {outcome.Detail}");
            results.Add(item: new PostStageResult(
                Duration: duration,
                Name: stage.Name,
                Outcome: outcome,
                Tier: stage.Tier
            ));
        }

        return new PostReport(
            banner: m_banner,
            duration: Stopwatch.GetElapsedTime(startingTimestamp: batteryStart),
            results: results
        );
    }
}
