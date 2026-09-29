using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

public sealed class GpuStandingPassLawTests {
    [Fact]
    public void StandingHasItsOwnWireStateWithoutInventingAnExecutedZeroCount() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var ledger = new GpuWorkLedger(framesInFlight: 1, name: "gpu.test");
        var services = GpuWorkCounting.Wrap(ledger: ledger, services: gpu.Services);

        ledger.Configure(passLabels: ["retained"], revision: 1);
        ledger.StandPass(pass: 0);
        Assert.Throws<InvalidOperationException>(testCode: () => ledger.EnterPass(pass: 0));
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);
        var sample = new GpuWorkSample();

        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(GpuPassState.Standing, sample.GetPassState(pass: 0));
        Assert.False(condition: sample.TryGetPassCount(column: 0, pass: 0, value: out _));
        var text = GpuWorkReport.AppendSample(builder: new StringBuilder(), sample: sample).ToString();

        Assert.Contains(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "work retained standing\n");
        Assert.DoesNotContain(actualString: text, comparisonType: StringComparison.Ordinal, expectedSubstring: "work retained standing:");
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer)) {
            GpuWorkReport.WriteNode(writer: writer, node: new GpuWorkNode(Lifetime: ledger, Name: "test", Work: ledger), sample: sample);
        }
        using var json = JsonDocument.Parse(buffer.WrittenMemory);
        var pass = json.RootElement.GetProperty(propertyName: "sample").GetProperty(propertyName: "passes")[0];

        Assert.Equal("standing", pass.GetProperty(propertyName: "state").GetString());
        Assert.False(condition: pass.TryGetProperty(propertyName: "counts", value: out _));
    }
}
