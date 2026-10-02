using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

public sealed class GpuKernelDetailLawTests {
    [Fact]
    public void GrowingOneWaitedSlotPreservesOtherPairsAndReadsEveryNamedKind() {
        var gpu = new UploadModelGpu();
        var ledger = new GpuWorkLedger("gpu.details", 3);
        var services = GpuWorkCounting.Wrap(gpu.Services, ledger);
        using var counters = new GpuKernelCounters(services.BufferFactory, 3, 2, "detail", "work");
        var first = counters.RowOf(0, 0).Buffer.BufferHandle;
        var second = counters.RowOf(1, 0).Buffer.BufferHandle;
        var bytes = counters.TotalBytes;
        counters.EnsureRows(slot: 2, rows: 5);
        Assert.Equal(first, counters.RowOf(0, 0).Buffer.BufferHandle);
        Assert.Equal(second, counters.RowOf(1, 0).Buffer.BufferHandle);
        Assert.Equal(bytes + 6UL * (ulong)GpuKernelCounters.RowBytes, counters.TotalBytes);
        var grown = counters.RowOf(2, 0).Buffer.BufferHandle;
        counters.EnsureRows(slot: 2, rows: 4);
        Assert.Equal(grown, counters.RowOf(2, 0).Buffer.BufferHandle);
        ledger.Configure(passLabels: ["sky", "shadow"], revision: 1);
        ledger.ConfigureDetails([new(0, "stars"), new(0, "clouds"), new(1, "sun")]);
        using var commandPool = services.CommandPoolFactory.Create(name: default);
        var command = commandPool.CommandBufferHandle;
        counters.RecordClear(services.Recorder, command, 2);
        var memory = gpu.Memory(grown);
        // Independent scalar oracle: five 64-bit words per row, ordinary rows then detail rows.
        for (var row = 0; row < 5; row++) {
            for (var kind = 0; kind < 5; kind++) {
                BinaryPrimitives.WriteUInt64LittleEndian(memory.AsSpan(row * 40 + kind * 8), (ulong)(0x1_0000_0000L + row * 10 + kind));
            }
        }
        ledger.EnterPass(0); ledger.LeavePass(); ledger.EnterPass(1); ledger.LeavePass();
        counters.RecordCopy(services.Recorder, command, 2);
        ledger.ReadOnCompletion(counters, 2);
        using var fence = services.QueueSubmitter.CreateSubmissionFence();
        services.QueueSubmitter.Submit([command], fence); fence.Wait();
        var sample = new GpuWorkSample();
        Assert.True(ledger.TryReadCompleted(sample));
        Assert.Equal(3, sample.Details.Length);
        var kinds = GpuWork.KernelKinds;
        for (var detail = 0; detail < 3; detail++) {
            for (var kind = 0; kind < 5; kind++) {
                Assert.True(sample.TryGetDetailCount(detail, GpuWork.SubmissionKinds.IndexOf(kinds[kind]), out var value));
                Assert.Equal(0x1_0000_0000L + (detail + 2) * 10 + kind, value);
            }
        }
        Assert.Equal("sun", sample.Details[2].Detail);
        Assert.Empty(gpu.StateConflicts);
    }
}
