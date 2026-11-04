using System.Text.Json;
using Puck.Cli.Sweep;
using Xunit;

namespace Puck.Cli.Runs.Tests;

public sealed class GpuTransferWindowLawTests {
    [Fact]
    public void AliasesCountOnceAndEndpointDifferencesRequireTheSameDomains() {
        var one = Node(1, 10);
        var before = Read(one, one, Node(2, 20));
        var after = Read(Node(1, 30), Node(2, 40));
        Assert.Equal("30", before["gpu.transfers.submitted.buffer-copy-bytes"]);
        Assert.Equal("2", before["gpu.transfers.domain-count"]);
        Assert.Null(SweepSignals.TransferWindowProblem(before, after));
        Assert.NotNull(SweepSignals.TransferWindowProblem(before, Read(Node(1, 30))));
        Assert.NotNull(SweepSignals.TransferWindowProblem(before, Read(Node(1, 30), Node(3, 40))));
        Assert.NotNull(SweepSignals.TransferWindowProblem(after, before));
    }

    [Fact]
    public void MissingSourcesAndChangingAliasesAreUnavailableRatherThanZero() {
        foreach (var bad in new[] { Read(), Read(new { name = "unknown", transfers = (object?)null }), Read(Node(1, 10), Node(1, 11)) }) {
            Assert.Equal("unavailable", bad["gpu.transfers.coverage"]);
            Assert.NotNull(SweepSignals.TransferWindowProblem(bad, Read(Node(1, 10))));
        }
        var malformed = SweepSignals.Read("gpu-transfers", "not JSON", null);
        Assert.Equal("unavailable", malformed["gpu.transfers.coverage"]);
    }

    private static SortedDictionary<string, string> Read(params object[] nodes) => SweepSignals.Read("gpu-transfers",
        "[world.counters: " + JsonSerializer.Serialize(new { gpu = new Dictionary<string, object> { ["transfer-scope"] = "registered-render-device-ledgers", ["nodes"] = nodes } }) + "]", null);

    private static object Node(long domain, long bytes) {
        Dictionary<string, long> Counts() => new() { ["host-write-operations"] = 1, ["host-write-bytes"] = bytes,
            ["copy-operations"] = 2, ["buffer-copy-bytes"] = bytes };
        return new { name = "node", transfers = new Dictionary<string, object> { ["domain"] = domain,
            ["submitted-through"] = bytes, ["completed-through"] = bytes / 2,
            ["recorded"] = Counts(), ["submitted"] = Counts(), ["completed"] = Counts(), ["discarded"] = Counts() } };
    }
}
