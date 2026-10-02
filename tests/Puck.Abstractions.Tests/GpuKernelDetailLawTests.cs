using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

public sealed class GpuKernelDetailLawTests {
    [Fact]
    public void GrowingOneWaitedSlotPreservesOtherPairsAndReadsEveryNamedKind() {
        var gpu = new UploadModelGpu();
        var ledger = new GpuWorkLedger(framesInFlight: 3, name: "gpu.details");
        var services = GpuWorkCounting.Wrap(gpu.Services, ledger);
        using var counters = new GpuKernelCounters(services.BufferFactory, 3, 2, "detail", "work");
        var first = counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle;
        var second = counters.RowOf(row: 0, slot: 1).Buffer.BufferHandle;
        var bytes = counters.TotalBytes;

        counters.EnsureRows(rows: 5, slot: 2);
        Assert.Equal(first, counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle);
        Assert.Equal(second, counters.RowOf(row: 0, slot: 1).Buffer.BufferHandle);
        Assert.Equal((bytes + (6UL * ((ulong)GpuKernelCounters.RowBytes))), counters.TotalBytes);
        var grown = counters.RowOf(row: 0, slot: 2).Buffer.BufferHandle;

        counters.EnsureRows(rows: 4, slot: 2);
        Assert.Equal(grown, counters.RowOf(row: 0, slot: 2).Buffer.BufferHandle);
        ledger.Configure(passLabels: ["sky", "shadow"], revision: 1);
        ledger.ConfigureDetails(details: [new(Detail: "stars", Pass: 0), new(Detail: "clouds", Pass: 0), new(Detail: "sun", Pass: 1)]);
        using var commandPool = services.CommandPoolFactory.Create(name: default);
        var command = commandPool.CommandBufferHandle;

        counters.RecordClear(services.Recorder, command, 2);
        var memory = gpu.Memory(bufferHandle: grown);
        // Independent scalar oracle: five 64-bit words per row, ordinary rows then detail rows.
        for (var row = 0; (row < 5); row++) {
            for (var kind = 0; (kind < 5); kind++) {
                BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: ((row * 40) + (kind * 8))), value: ((ulong)((0x1_0000_0000L + (row * 10)) + kind)));
            }
        }
        ledger.EnterPass(pass: 0); ledger.LeavePass(); ledger.EnterPass(pass: 1); ledger.LeavePass();
        counters.RecordCopy(services.Recorder, command, 2);
        ledger.ReadOnCompletion(readback: counters, slot: 2);
        using var fence = services.QueueSubmitter.CreateSubmissionFence();

        services.QueueSubmitter.Submit(commandBufferHandles: [command], fence: fence); fence.Wait();
        var sample = new GpuWorkSample();

        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(3, sample.Details.Length);
        var kinds = GpuWork.KernelKinds;

        for (var detail = 0; (detail < 3); detail++) {
            for (var kind = 0; (kind < 5); kind++) {
                Assert.True(condition: sample.TryGetDetailCount(detail, GpuWork.SubmissionKinds.IndexOf(kinds[kind]), out var value));
                Assert.Equal(actual: value, expected: ((0x1_0000_0000L + ((detail + 2) * 10)) + kind));
            }
        }
        Assert.Equal("sun", sample.Details[2].Detail);
        Assert.Empty(collection: gpu.StateConflicts);
    }
}
