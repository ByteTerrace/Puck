using Puck.Cli.Counters;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>Every recorded off workload forbids every indirect GPU kind at each recorded identity.</summary>
public sealed class CountersIndirectLawTests {
    [Fact]
    public void RecordedOffWorkloadsRequireEveryIndirectKindToStayZero() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        foreach (var path in Directory.GetFiles(path: Path.Combine(path1: root, path2: "tests/Puck.Counters"), searchPattern: "*.ceilings.json")) {
            Assert.True(condition: CountersCeilings.TryRead(ceilings: out var ledger, path: path, reason: out var reason), userMessage: reason);
            Assert.NotEmpty(collection: ledger.Backends);
            foreach (var backend in ledger.Backends) {
                Assert.NotEmpty(collection: backend.Devices);
                foreach (var device in backend.Devices) {
                    foreach (var row in backend.Ceilings.Concat(second: device.Ceilings).GroupBy(keySelector: item => (item.Node, item.Pass, item.Detail))) {
                        foreach (var kind in new[] { "gpu.indirect.hits", "gpu.indirect.samples", "gpu.indirect.unresolved" }) {
                            var zero = Assert.Single(collection: row, predicate: item => (item.Kind == kind));

                            Assert.Equal(0, zero.Ceiling);
                        }
                    }
                }
            }
        }
    }
}
