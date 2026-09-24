using Puck.GamingBricks.Post;
using Puck.Testing;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>CONTRACT UNDER TEST: the battery's hang guard never decides a verdict by elapsed time. Its windows run on
/// an injected clock that only these laws advance; a window in which a stage finishes or the process spends processor
/// time is progress, however many windows pass, and only a window with neither abandons the unfinished stages as
/// hung.</summary>
public sealed class PostBatteryHangGuardLawTests {
    private static readonly TimeSpan MinimumProcessorTime = TimeSpan.FromSeconds(seconds: 1);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(minutes: 1);

    private sealed class GatedStage(string name) : IPostStage<object> {
        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Gate { get; } = new();

        public string Name => name;
        public PostTier Tier => PostTier.A;

        public PostStageOutcome Run(object context) {
            Entered.TrySetResult();
            Gate.Wait();

            return PostStageOutcome.Pass(detail: "released");
        }
    }
    private sealed class QuickStage : IPostStage<object> {
        public string Name => "quick";
        public PostTier Tier => PostTier.A;

        public PostStageOutcome Run(object context) => PostStageOutcome.Pass(detail: "done");
    }

    // Expires the next window, or fails if the battery ends first: a regression that ends the run early leaves no
    // window to expire, and must fail here rather than wait for one that is never armed.
    private static async Task ExpireWindowAsync(VirtualClock clock, Task<PostReport> run) {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);
        var armed = clock.WhenArmedAsync(
            count: 1,
            ct: abandon.Token,
            dueTime: Window
        );

        if (await Task.WhenAny(
            task1: armed,
            task2: run
        ) == run) {
            await abandon.CancelAsync();
            var report = await run;

            Assert.Fail(message: $"the battery ended before the next window was armed (abandoned: {report.Abandoned}, exit {report.ExitCode})");
        }

        await armed;
        clock.Advance(by: Window);
    }
    private static PostBattery<object> Battery(VirtualClock clock, Func<TimeSpan> processorTime, params IPostStage<object>[] stages) => new(
        banner: "hang guard law",
        guard: new PostProgressGuard(
            MinimumProcessorTime: MinimumProcessorTime,
            ProcessorTime: processorTime,
            Time: clock,
            Window: Window
        ),
        stages: stages
    );

    [Fact]
    public async Task AWindowWithNoFinishedStageAndNoProcessorTimeAbandonsTheUnfinishedStagesAsHung() {
        var clock = new VirtualClock();
        var stuck = new GatedStage(name: "stuck");
        var battery = Battery(
            clock,
            static () => TimeSpan.FromSeconds(seconds: 5),
            new QuickStage(),
            stuck
        );
        var run = Task.Run(function: () => battery.Run(context: new object()));

        try {
            await stuck.Entered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            // The quick stage finished inside the first window, which is progress.
            await ExpireWindowAsync(
                clock: clock,
                run: run
            );
            await ExpireWindowAsync(
                clock: clock,
                run: run
            );

            var report = await run.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(condition: report.Abandoned);
            Assert.Equal(
                actual: report.ExitCode,
                expected: 2
            );
            Assert.Equal(
                actual: report.Results[0].Outcome.Verdict,
                expected: PostVerdict.Pass
            );
            Assert.Equal(
                actual: report.Results[1].Outcome.Verdict,
                expected: PostVerdict.Infra
            );
            Assert.StartsWith(
                actualString: report.Results[1].Outcome.Detail,
                expectedStartString: "hung: no stage finished",
                comparisonType: StringComparison.Ordinal
            );
        } finally {
            stuck.Gate.Set();
        }
    }
    [Fact]
    public async Task AStageSpendingProcessorTimeIsNeverAbandonedHoweverManyWindowsPass() {
        var clock = new VirtualClock();
        var slow = new GatedStage(name: "slow");
        var samples = 0L;
        var battery = Battery(
            clock,
            () => (MinimumProcessorTime * Interlocked.Increment(location: ref samples)),
            slow
        );
        var run = Task.Run(function: () => battery.Run(context: new object()));

        try {
            await slow.Entered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
            for (var window = 0; (window < 8); window++) {
                await ExpireWindowAsync(
                    clock: clock,
                    run: run
                );
            }
        } finally {
            slow.Gate.Set();
        }

        var report = await run.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: report.Abandoned);
        Assert.Equal(
            actual: report.ExitCode,
            expected: 0
        );
        Assert.Equal(
            actual: report.Results[0].Outcome.Detail,
            expected: "released"
        );
    }
}
