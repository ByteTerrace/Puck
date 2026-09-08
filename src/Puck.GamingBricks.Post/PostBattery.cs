using System.Diagnostics;

namespace Puck.GamingBricks.Post;

/// <summary>Runs an ordered list of <see cref="IPostStage{TContext}"/> once each, isolating failures so one stage's
/// infrastructure failure (an exception, recorded as <see cref="PostVerdict.Infra"/>) never aborts the rest, timing
/// each, and gathering the results into a <see cref="PostReport"/>. A run of adjacent stages that declare
/// <see cref="IPostStage{TContext}.IsConcurrent"/> executes at once and reports in list order; every other stage runs
/// alone, because it measures many cases on every processor itself, measures throughput, or holds the console.</summary>
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
        var index = 0;

        while (index < m_stages.Count) {
            var groupEnd = index;

            while (
                (groupEnd < m_stages.Count) &&
                m_stages[groupEnd].IsConcurrent
            ) {
                ++groupEnd;
            }

            if (groupEnd == index) {
                results.Add(item: RunOne(
                    context: context,
                    stage: m_stages[index]
                ));
                ++index;

                continue;
            }

            var group = new PostStageResult[(groupEnd - index)];
            var first = index;

            _ = Parallel.For(
                body: offset => group[offset] = RunOne(
                    context: context,
                    stage: m_stages[(first + offset)],
                    announce: false
                ),
                fromInclusive: 0,
                toExclusive: group.Length
            );

            foreach (var result in group) {
                Announce(result: result);
                results.Add(item: result);
            }

            index = groupEnd;
        }

        return new PostReport(
            banner: m_banner,
            duration: Stopwatch.GetElapsedTime(startingTimestamp: batteryStart),
            results: results
        );
    }

    private static void Announce(PostStageResult result) =>
        Console.Out.WriteLine(value: $"[{result.Tier}] {result.Name}: {result.Outcome.Verdict} | {PostReport.FormatDuration(duration: result.Duration)} | {result.Outcome.Detail}");
    private static PostStageResult RunOne(IPostStage<TContext> stage, TContext context, bool announce = true) {
        var stageStart = Stopwatch.GetTimestamp();
        PostStageOutcome outcome;

        try {
            outcome = stage.Run(context: context);
        } catch (Exception exception) {
            outcome = PostStageOutcome.Infra(detail: $"threw {exception.GetType().Name}: {exception.Message}");
        }

        var result = new PostStageResult(
            Duration: Stopwatch.GetElapsedTime(startingTimestamp: stageStart),
            Name: stage.Name,
            Outcome: outcome,
            Tier: stage.Tier
        );

        if (announce) {
            Announce(result: result);
        }

        return result;
    }
}
