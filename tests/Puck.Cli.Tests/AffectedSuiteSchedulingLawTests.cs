using Puck.Cli.Affected;
using Puck.Cli.Host;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Covers how <c>puck affected --run</c> runs its suites side by side: at most <c>--suite-jobs</c> at once, heavy
/// suites first, and every suite reported once with its wall time. Which suite is heavy, and when one may run, is the one
/// machine-wide rule <see cref="HostProcesses.IsHeavyTestAssembly"/> and <see cref="HostAdmission"/> own.</summary>
public sealed class AffectedSuiteSchedulingLawTests {
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
    // The real runner over real threads: suites overlap up to the bound and never past it, and every suite is reported
    // once with its wall time.
    [Fact]
    public void TheRunnerOverlapsSuitesUpToTheBound() {
        var gate = new object();

        var (inside, peak) = (0, 0);
        AffectedSuite[] suites = [.. Enumerable.Range(count: 7, start: 0).Select(selector: static index => new AffectedSuite(Heavy: (index == 0), Name: $"S{index}"))];
        var reported = new List<string>();

        AffectedSuites.RunConcurrently(
            completed: (suite, result, elapsed) => {
                Assert.Equal(expected: 0, actual: result.ExitCode);
                Assert.True(condition: (elapsed > TimeSpan.Zero));
                reported.Add(item: suite.Name);
            },
            jobs: 3,
            run: _ => {
                lock (gate) {
                    inside++;
                    peak = Math.Max(val1: peak, val2: inside);
                }

                Thread.Sleep(millisecondsTimeout: 50);

                lock (gate) {
                    inside--;
                }

                return new AffectedSuiteResult(ExitCode: 0, Report: ["Passed!"]);
            },
            suites: suites
        );

        Assert.Equal(expected: suites.Select(selector: static suite => suite.Name).Order(), actual: reported.Order());
        Assert.Equal(actual: peak, expected: 3);
    }
}
