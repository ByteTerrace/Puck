using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Covers how <c>puck affected --run</c> runs its suites side by side under the host-load capacity rules: at
/// most <c>--suite-jobs</c> at once, never two heavy suites at once, none beside another while free memory is under its
/// floor, and a lone suite always starts.</summary>
public sealed class AffectedSuiteSchedulingLawTests {
    private static readonly AffectedSuite Heavy = new(Heavy: true, Name: "Heavy.Tests");
    private static readonly AffectedSuite Light = new(Heavy: false, Name: "Light.Tests");

    [Fact]
    public void ALoneSuiteAlwaysStartsWhateverTheMachineHasFree() {
        foreach (var suite in ((AffectedSuite[])[Heavy, Light])) {
            Assert.True(condition: AffectedSuites.Admits(freeRamGb: 0.5, heavyRunning: false, jobs: 1, running: 0, suite: suite, totalRamGb: 32));
        }
    }
    [Fact]
    public void BesideOthersASuiteNeedsASlotNoOtherHeavySuiteAndFreeMemoryAboveItsFloor() {
        // A slot: two of two are taken.
        Assert.False(condition: AffectedSuites.Admits(freeRamGb: 20, heavyRunning: false, jobs: 2, running: 2, suite: Light, totalRamGb: 32));
        // Never two heavy suites at once, however much is free.
        Assert.False(condition: AffectedSuites.Admits(freeRamGb: 20, heavyRunning: true, jobs: 4, running: 1, suite: Heavy, totalRamGb: 32));
        Assert.True(condition: AffectedSuites.Admits(freeRamGb: 20, heavyRunning: true, jobs: 4, running: 1, suite: Light, totalRamGb: 32));
        // A heavy suite waits for a quarter of the machine free (8 GB of 32), a light one for an eighth (4 GB).
        Assert.False(condition: AffectedSuites.Admits(freeRamGb: 7.9, heavyRunning: false, jobs: 4, running: 1, suite: Heavy, totalRamGb: 32));
        Assert.True(condition: AffectedSuites.Admits(freeRamGb: 8.1, heavyRunning: false, jobs: 4, running: 1, suite: Heavy, totalRamGb: 32));
        Assert.False(condition: AffectedSuites.Admits(freeRamGb: 3.9, heavyRunning: false, jobs: 4, running: 1, suite: Light, totalRamGb: 32));
        Assert.True(condition: AffectedSuites.Admits(freeRamGb: 4.1, heavyRunning: false, jobs: 4, running: 1, suite: Light, totalRamGb: 32));
        // A machine whose memory cannot be read is judged on slots and weight alone.
        Assert.True(condition: AffectedSuites.Admits(freeRamGb: double.NaN, heavyRunning: false, jobs: 4, running: 1, suite: Light, totalRamGb: 32));
    }
    [Fact]
    public void HeavySuitesStartFirstAndTheRestKeepThePlansOrder() {
        AffectedSuite[] suites = [
            new(Heavy: false, Name: "A"),
            new(Heavy: true, Name: "B"),
            new(Heavy: false, Name: "C"),
            new(Heavy: true, Name: "D"),
        ];

        Assert.Equal(expected: ["B", "D", "A", "C"], actual: AffectedSuites.StartOrder(suites: suites).Select(selector: static suite => suite.Name));
    }
    // The real runner over real threads: suites overlap up to the bound, two heavy suites never overlap, and every suite
    // is reported once with its wall time.
    [Fact]
    public void TheRunnerOverlapsSuitesUpToTheBoundAndNeverTwoHeavyOnes() {
        var gate = new object();

        var (inside, peak, heavyInside, heavyPeak) = (0, 0, 0, 0);
        AffectedSuite[] suites = [
            new(Heavy: true, Name: "H1"),
            new(Heavy: false, Name: "L1"),
            new(Heavy: true, Name: "H2"),
            new(Heavy: false, Name: "L2"),
            new(Heavy: false, Name: "L3"),
            new(Heavy: false, Name: "L4"),
        ];
        var reported = new List<string>();

        AffectedSuites.RunConcurrently(
            completed: (suite, result, elapsed) => {
                Assert.Equal(expected: 0, actual: result.ExitCode);
                Assert.True(condition: (elapsed > TimeSpan.Zero));
                reported.Add(item: suite.Name);
            },
            freeRamGb: static () => 20,
            jobs: 3,
            run: suite => {
                lock (gate) {
                    inside++;
                    peak = Math.Max(val1: peak, val2: inside);
                    heavyInside += (suite.Heavy ? 1 : 0);
                    heavyPeak = Math.Max(val1: heavyPeak, val2: heavyInside);
                }

                Thread.Sleep(millisecondsTimeout: 50);

                lock (gate) {
                    inside--;
                    heavyInside -= (suite.Heavy ? 1 : 0);
                }

                return new AffectedSuiteResult(ExitCode: 0, Report: ["Passed!"]);
            },
            suites: suites,
            totalRamGb: 32
        );

        Assert.Equal(expected: suites.Select(selector: static suite => suite.Name).Order(), actual: reported.Order());
        Assert.Equal(actual: peak, expected: 3);
        Assert.Equal(actual: heavyPeak, expected: 1);
    }
    // A suite held back by memory alone starts once the machine frees it, even while another suite still runs.
    [Fact]
    public void ASuiteHeldBackByMemoryStartsOnceTheMachineFreesIt() {
        using var secondStarted = new ManualResetEventSlim();
        var reads = 0;

        AffectedSuites.RunConcurrently(
            completed: static (_, _, _) => { },
            // Short of memory for the first few reads after the first suite starts, then plenty.
            freeRamGb: () => ((Interlocked.Increment(location: ref reads) <= 2) ? 1 : 20),
            jobs: 2,
            run: suite => {
                if (suite.Name == "First") {
                    // Ends only once the second suite has started beside it.
                    Assert.True(condition: secondStarted.Wait(timeout: TimeSpan.FromSeconds(seconds: 30)));
                } else {
                    secondStarted.Set();
                }

                return new AffectedSuiteResult(ExitCode: 0, Report: ["Passed!"]);
            },
            suites: [new(Heavy: false, Name: "First"), new(Heavy: false, Name: "Second")],
            totalRamGb: 32
        );

        Assert.True(condition: secondStarted.IsSet);
    }
}
