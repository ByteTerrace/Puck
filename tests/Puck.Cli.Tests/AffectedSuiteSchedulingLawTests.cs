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
        using var cohorts = new SchedulingCohorts(bound: 3, cohorts: 3);
        AffectedSuite[] suites = [.. Enumerable.Range(count: 9, start: 0).Select(selector: static index => new AffectedSuite(Heavy: (index == 0), Name: $"S{index}"))];
        var reported = new List<string>();

        AffectedSuites.RunConcurrently(
            completed: (suite, result, elapsed) => {
                cohorts.Completed();
                Assert.Equal(expected: 0, actual: result.ExitCode);
                reported.Add(item: suite.Name);
            },
            jobs: 3,
            run: _ => {
                cohorts.Run();

                return new AffectedSuiteResult(ExitCode: 0, Report: ["Passed!"]);
            },
            suites: suites,
            started: _ => cohorts.Started()
        );

        Assert.Equal(expected: suites.Select(selector: static suite => suite.Name).Order(), actual: reported.Order());
        Assert.Equal(actual: cohorts.Peak, expected: 3);
        Assert.Equal(actual: cohorts.AdmissionPeak, expected: 3);
    }
}
