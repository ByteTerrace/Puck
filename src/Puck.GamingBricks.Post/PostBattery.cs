using System.Collections.Concurrent;
using System.Diagnostics;

namespace Puck.GamingBricks.Post;

/// <summary>Runs an ordered list of <see cref="IPostStage{TContext}"/> once each, isolating failures so one stage's
/// infrastructure failure (an exception, recorded as <see cref="PostVerdict.Infra"/>) never aborts the rest, timing
/// each, and gathering the results into a <see cref="PostReport"/>. The run has two phases. Every stage that declares
/// <see cref="IPostStage{TContext}.IsConcurrent"/> runs first, all of them sharing the processors at once and started
/// in list order; then every other stage runs alone, one at a time in list order, on a quiet process whose code the
/// first phase has already warmed. Both the announced lines and the report keep list order whatever order the stages
/// finish in, so a run's output is independent of scheduling.
/// <para>A <see cref="PostProgressGuard"/> watches the run. Stages are never cancelled for taking long; only a window
/// in which no stage finishes and the process spends no processor time abandons the run, reporting every unfinished
/// stage as hung and returning a report whose <see cref="PostReport.Abandoned"/> is set.</para></summary>
/// <typeparam name="TContext">The battery's per-run context type.</typeparam>
public sealed class PostBattery<TContext> {
    private readonly string m_banner;
    private readonly PostProgressGuard m_guard;
    private readonly IReadOnlyList<IPostStage<TContext>> m_stages;

    /// <summary>Initializes a new instance of the <see cref="PostBattery{TContext}"/> class under
    /// <see cref="PostProgressGuard.Default"/>.</summary>
    /// <param name="banner">The report's first line — the caller's own battery/machine identification.</param>
    /// <param name="stages">The stages to run, in order.</param>
    /// <exception cref="ArgumentException"><paramref name="banner"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> is <see langword="null"/>.</exception>
    public PostBattery(string banner, IReadOnlyList<IPostStage<TContext>> stages) : this(
        banner: banner,
        guard: PostProgressGuard.Default,
        stages: stages
    ) { }
    /// <summary>Initializes a new instance of the <see cref="PostBattery{TContext}"/> class.</summary>
    /// <param name="banner">The report's first line — the caller's own battery/machine identification.</param>
    /// <param name="stages">The stages to run, in order.</param>
    /// <param name="guard">The hang guard watching the run.</param>
    /// <exception cref="ArgumentException"><paramref name="banner"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> or <paramref name="guard"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="guard"/>'s window or minimum processor time is not
    /// positive.</exception>
    public PostBattery(string banner, IReadOnlyList<IPostStage<TContext>> stages, PostProgressGuard guard) {
        ArgumentException.ThrowIfNullOrEmpty(argument: banner);
        ArgumentNullException.ThrowIfNull(argument: stages);
        ArgumentNullException.ThrowIfNull(argument: guard);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            other: TimeSpan.Zero,
            value: guard.Window
        );
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            other: TimeSpan.Zero,
            value: guard.MinimumProcessorTime
        );

        m_banner = banner;
        m_guard = guard;
        m_stages = stages;
    }

    private static void Announce(PostStageResult result) =>
        Console.Out.WriteLine(value: $"[{result.Tier}] {result.Name}: {result.Outcome.Verdict} | {PostReport.FormatDuration(duration: result.Duration)} | {result.Outcome.Detail}");
    private static PostStageResult RunOne(IPostStage<TContext> stage, TContext context) {
        var stageStart = Stopwatch.GetTimestamp();
        PostStageOutcome outcome;

        try {
            outcome = stage.Run(context: context);
        } catch (Exception exception) {
            outcome = PostStageOutcome.Infra(detail: $"threw {exception.GetType().Name}: {exception.Message}");
        }

        return new PostStageResult(
            Duration: Stopwatch.GetElapsedTime(startingTimestamp: stageStart),
            Name: stage.Name,
            Outcome: outcome,
            Tier: stage.Tier
        );
    }

    /// <summary>Runs every stage and returns the aggregate report.</summary>
    /// <param name="context">The shared run context.</param>
    /// <returns>The aggregate report. When the guard abandoned the run, unfinished stages are still running on
    /// background threads and the caller ends the process once the report is written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public PostReport Run(TContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var results = new PostStageResult?[m_stages.Count];
        var announced = 0;
        var finished = 0;
        var abandoned = false;
        var announceLock = new Lock();
        var batteryStart = m_guard.Time.GetTimestamp();

        // Announces the longest finished prefix of the list, so a line appears as soon as every stage before it has
        // one and the console reads in list order however the phases interleave.
        void AnnouncePrefix() {
            while (
                (announced < results.Length) &&
                (results[announced] is { } next)
            ) {
                Announce(result: next);
                ++announced;
            }
        }
        void Complete(int index, PostStageResult result) {
            lock (announceLock) {
                if (abandoned) {
                    return;
                }

                results[index] = result;
                ++finished;
                AnnouncePrefix();
            }
        }

        var concurrent = new List<int>(capacity: m_stages.Count);
        var isolated = new List<int>(capacity: m_stages.Count);

        for (var index = 0; (index < m_stages.Count); ++index) {
            (m_stages[index].IsConcurrent
                ? concurrent
                : isolated
            ).Add(item: index);
        }

        var phases = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Thread(start: () => {
            try {
                // One stage per processor, handed out in list order as each finishes: the long self-contained stages
                // at the head of a registry start first, and a stage that measures many cases itself shares the thread
                // pool with the rest rather than claiming every processor for its own duration.
                _ = Parallel.ForEach(
                    body: index => Complete(
                        index: index,
                        result: RunOne(
                            context: context,
                            stage: m_stages[index]
                        )
                    ),
                    parallelOptions: new ParallelOptions {
                        MaxDegreeOfParallelism = Environment.ProcessorCount,
                    },
                    source: Partitioner.Create(
                        partitionerOptions: EnumerablePartitionerOptions.NoBuffering,
                        source: concurrent
                    )
                );

                foreach (var index in isolated) {
                    Complete(
                        index: index,
                        result: RunOne(
                            context: context,
                            stage: m_stages[index]
                        )
                    );
                }

                phases.SetResult();
            } catch (Exception exception) {
                phases.SetException(exception: exception);
            }
        }) {
            IsBackground = true,
            Name = "post-battery",
        };

        runner.Start();

        var sampledFinished = 0;
        var sampledProcessorTime = m_guard.ProcessorTime();

        while (true) {
            using var windowEnd = new CancellationTokenSource();
            var window = Task.Delay(
                cancellationToken: windowEnd.Token,
                delay: m_guard.Window,
                timeProvider: m_guard.Time
            );

            if (Task.WhenAny(
                task1: phases.Task,
                task2: window
            ).GetAwaiter().GetResult() == phases.Task) {
                windowEnd.Cancel();
                phases.Task.GetAwaiter().GetResult();

                break;
            }

            var processorTime = m_guard.ProcessorTime();
            var spent = (processorTime - sampledProcessorTime);

            lock (announceLock) {
                if (
                    (finished != sampledFinished) ||
                    (spent >= m_guard.MinimumProcessorTime)
                ) {
                    sampledFinished = finished;
                    sampledProcessorTime = processorTime;

                    continue;
                }

                abandoned = true;

                var elapsed = m_guard.Time.GetElapsedTime(startingTimestamp: batteryStart);
                var hung = PostStageOutcome.Infra(detail: $"hung: no stage finished and the battery spent {spent.TotalMilliseconds:F0} ms of processor time in the last {m_guard.Window.TotalSeconds:F0} s (progress is at least {m_guard.MinimumProcessorTime.TotalMilliseconds:F0} ms); abandoned {elapsed.TotalSeconds:F0} s into the run");

                for (var index = 0; (index < results.Length); ++index) {
                    results[index] ??= new PostStageResult(
                        Duration: elapsed,
                        Name: m_stages[index].Name,
                        Outcome: hung,
                        Tier: m_stages[index].Tier
                    );
                }

                AnnouncePrefix();

                return new PostReport(
                    abandoned: true,
                    banner: m_banner,
                    duration: elapsed,
                    results: Array.ConvertAll(
                        array: results,
                        converter: static result => result!
                    )
                );
            }
        }

        return new PostReport(
            abandoned: false,
            banner: m_banner,
            duration: m_guard.Time.GetElapsedTime(startingTimestamp: batteryStart),
            results: Array.ConvertAll(
                array: results,
                converter: static result => result!
            )
        );
    }
}
