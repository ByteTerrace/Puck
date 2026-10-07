using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>Device-free laws for detail readback, mixed rows, row growth and publication across frames.</summary>
public sealed class GpuWorkDetailLawTests {
    [Fact]
    public void MixedPlainAndDetailedRowsReconcileWithCarryAcrossFrames() {
        var gpu = new UploadModelGpu();
        var ledger = new GpuWorkLedger(framesInFlight: 2, name: "gpu.detail");
        var services = GpuWorkCounting.Wrap(services: gpu.Services, ledger: ledger);

        ledger.Configure(revision: 1, passLabels: ["sky"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "clouds", Pass: 0)]);
        using var counters = new GpuKernelCounters(buffers: services.BufferFactory, slots: 2, rows: 3, owner: "test", part: "details");
        var command = services.CommandPoolFactory.Create(name: default).CommandBufferHandle;
        var first = services.QueueSubmitter.CreateSubmissionFence();

        counters.RecordClear(recorder: services.Recorder, commandBuffer: command, slot: 0);
        var memory = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle);

        Set(kind: 0, memory: memory, row: 0, value: 7UL);
        Set(kind: 0, memory: memory, row: 2, value: 0x1_0000_0009UL);
        Set(kind: 3, memory: memory, row: 2, value: 13UL);
        ledger.EnterPass(pass: 0);
        services.Recorder.Dispatch(commandBufferHandle: command, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
        ledger.LeavePass();
        counters.RecordCopy(recorder: services.Recorder, commandBuffer: command, slot: 0);
        ledger.ReadOnCompletion(readback: counters, slot: 0);
        services.QueueSubmitter.Submit(commandBufferHandles: [command], fence: first);

        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "clouds", Pass: 0), new(Detail: "stars", Pass: 0)]);
        counters.EnsureRows(rows: 4, slot: 1);
        first.Wait();
        var sample = new GpuWorkSample();

        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 2, actual: sample.Details.Length);
        Assert.True(condition: sample.TryGetPassCount(pass: 0, column: Column(kind: GpuWork.MarchSteps), value: out var total));
        Assert.Equal(actual: total, expected: 0x1_0000_0010L);
        Assert.True(condition: sample.TryGetDetailCount(detail: 0, column: Column(kind: GpuWork.MarchSteps), value: out var plain));
        Assert.Equal(actual: plain, expected: 7L);
        Assert.True(condition: sample.TryGetPassCount(pass: 0, column: Column(kind: GpuWork.SkyHashes), value: out var hashes));
        Assert.Equal(actual: hashes, expected: 13L);
        Reconciles(sample: sample);

        counters.RecordClear(recorder: services.Recorder, commandBuffer: command, slot: 1);
        memory = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 1).Buffer.BufferHandle);
        Set(kind: 4, memory: memory, row: 3, value: 17UL);
        ledger.EnterPass(pass: 0);
        ledger.LeavePass();
        counters.RecordCopy(recorder: services.Recorder, commandBuffer: command, slot: 1);
        ledger.ReadOnCompletion(readback: counters, slot: 1);
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(expected: 3, actual: sample.Details.Length);
        Assert.True(condition: sample.TryGetPassCount(pass: 0, column: Column(kind: GpuWork.MarchSteps), value: out total));
        Assert.Equal(actual: total, expected: 0L);
        Assert.True(condition: sample.TryGetPassCount(pass: 0, column: Column(kind: GpuWork.SkyTextureLoads), value: out total));
        Assert.Equal(actual: total, expected: 17L);
        Reconciles(sample: sample);
    }
    [Fact]
    public void DetailCapacityAndIdentitiesOnlyGrow() {
        var gpu = new UploadModelGpu();
        using var counters = new GpuKernelCounters(buffers: gpu.Services.BufferFactory, slots: 2, rows: 1, owner: "test", part: "details");
        var other = counters.RowOf(row: 0, slot: 1).Buffer;

        counters.EnsureRows(rows: 5, slot: 0);
        var grown = counters.RowOf(row: 4, slot: 0).Buffer;

        counters.EnsureRows(rows: 2, slot: 0);
        Assert.Same(expected: grown, actual: counters.RowOf(row: 4, slot: 0).Buffer);
        Assert.Same(expected: other, actual: counters.RowOf(row: 0, slot: 1).Buffer);
        Assert.Equal(expected: ((ulong)(12 * GpuKernelCounters.RowBytes)), actual: counters.TotalBytes);
        var ledger = new GpuWorkLedger(framesInFlight: 1, name: "gpu.detail");

        ledger.Configure(revision: 1, passLabels: ["sky", "shadow"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "layer", Pass: 0), new(Detail: "plain", Pass: 1), new(Detail: "layer", Pass: 1)]);
        Assert.Throws<ArgumentException>(testCode: () => ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "layer", Pass: 0)]));
        Assert.Throws<ArgumentException>(testCode: () => ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "plain", Pass: 0)]));
    }
    [Fact]
    public void DetailRowsReachTextAndJsonAndSkippedRowsHaveNoCounts() {
        var gpu = new FakeGpuDevice();
        var ledger = new GpuWorkLedger(framesInFlight: 1, name: "gpu.detail");
        var services = GpuWorkCounting.Wrap(services: gpu.Services, ledger: ledger);

        ledger.Configure(revision: 1, passLabels: ["sky", "shadow"]);
        ledger.ConfigureDetails(details: [new(Detail: "plain", Pass: 0), new(Detail: "stars", Pass: 0), new(Detail: "plain", Pass: 1), new(Detail: "slot:0", Pass: 1)]);
        ledger.EnterPass(pass: 0);
        services.Recorder.Dispatch(commandBufferHandle: 1, groupCountX: 1, groupCountY: 1, groupCountZ: 1);
        ledger.LeavePass();
        ledger.SkipPass(pass: 1);
        services.QueueSubmitter.SubmitAndWait(commandBufferHandles: []);
        var sample = new GpuWorkSample();
        var text = GpuWorkReport.AppendCompleted(builder: new StringBuilder(), source: ledger, sample: sample).ToString();

        Assert.Contains(actualString: text, expectedSubstring: "work sky detail=plain executed: dispatches=1");
        Assert.Contains(actualString: text, expectedSubstring: "work shadow detail=slot:0 skipped");
        var bytes = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: bytes)) {
            GpuWorkReport.WriteNode(writer: writer, node: new GpuWorkNode(Name: "world", Work: ledger, Lifetime: null), sample: sample);
        }
        using var json = JsonDocument.Parse(utf8Json: bytes.WrittenMemory);
        var passes = json.RootElement.GetProperty(propertyName: "sample").GetProperty(propertyName: "passes");

        Assert.Equal(expected: 1L, actual: passes[0].GetProperty(propertyName: "details")[0].GetProperty(propertyName: "counts").GetProperty(propertyName: "gpu.dispatches").GetInt64());
        Assert.False(condition: passes[1].GetProperty(propertyName: "details")[1].TryGetProperty(propertyName: "counts", value: out _));
    }

    private static void Reconciles(GpuWorkSample sample) {
        for (var column = 0; (column < GpuWork.SubmissionKinds.Length); column++) {
            Assert.True(condition: sample.TryGetPassCount(column: column, pass: 0, value: out var total));
            var sum = 0L;

            for (var detail = 0; (detail < sample.Details.Length); detail++) {
                Assert.True(condition: sample.TryGetDetailCount(column: column, detail: detail, value: out var value));
                sum += value;
            }
            Assert.Equal(actual: sum, expected: total);
        }
    }
    private static int Column(WorkKind kind) => GpuWork.SubmissionKinds.ToArray().ToList().IndexOf(item: kind);
    private static void Set(byte[] memory, int row, int kind, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(
        destination: memory.AsSpan(start: ((row * GpuKernelCounters.RowBytes) + (kind * sizeof(ulong)))), value: value);
}
