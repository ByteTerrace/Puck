using Puck.Cli.Counters;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Every recorded off workload forbids every indirect GPU kind at each recorded identity.</summary>
public sealed class CountersIndirectLawTests {
    [Fact]
    public void RecordedOffWorkloadsRequireEveryIndirectKindToStayZero() {
        Assert.True(CliPaths.TryGetRepositoryRoot(out var root));
        foreach (var path in Directory.GetFiles(Path.Combine(root, "tests/Puck.Counters"), "*.ceilings.json")) {
            Assert.True(CountersCeilings.TryRead(path, out var ledger, out var reason), reason);
            foreach (var run in ledger.Runs) {
                foreach (var row in run.Ceilings.GroupBy(item => (item.Node, item.Pass, item.Detail))) {
                    foreach (var kind in new[] { "gpu.indirect.hits", "gpu.indirect.samples", "gpu.indirect.unresolved" }) {
                        var zero = Assert.Single(row, item => (item.Kind == kind));

                        Assert.Equal(0, zero.Ceiling);
                        Assert.True(zero.RequiredZero);
                    }
                }
            }
        }
    }
}
