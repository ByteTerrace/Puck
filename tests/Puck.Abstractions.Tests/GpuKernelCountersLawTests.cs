using System.Buffers.Binary;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuKernelCounters"/> and the ledger's readback (<see cref="GpuWorkLedger.ReadOnCompletion"/>),
/// over <see cref="UploadModelGpu"/>, whose buffers hold bytes, whose clear zeroes them and whose copy copies them. A frame
/// clears its slot's counters, the passes' kernels add to their rows, and the copy carries the rows into the slot's
/// readback, which the ledger reads into the passes' kernel columns once the submission completes: each row one pass's
/// 64-bit counts, its march steps and then its texels written. The clear, the copy and their three barriers count outside
/// every pass, and the Direct3D 12 buffer states the barriers name replay without a conflict.
/// </summary>
public sealed class GpuKernelCountersLawTests {
    [Fact]
    public void IndirectKindsHaveIndependentColumnsAndReadTheirFullCounts() {
        var gpu = new UploadModelGpu();
        using var counters = new GpuKernelCounters(gpu.Services.BufferFactory, 1, 1, "indirect-law", "counters");
        var kinds = new[] { GpuWork.IndirectHits, GpuWork.IndirectSamples, GpuWork.IndirectUnresolved };
        var memory = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle);

        for (var index = 0; (index < kinds.Length); index++) {
            Assert.Equal(WorkClass.PerBackendDeterministic, kinds[index].Class);
            var offset = (GpuWork.KernelKinds.IndexOf(kinds[index]) * sizeof(ulong));

            BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: offset), value: (0x1_0000_0001UL + ((ulong)index)));
        }
        var command = gpu.Services.CommandPoolFactory.Create(name: default).CommandBufferHandle;

        counters.RecordCopy(gpu.Services.Recorder, command, 0);
        var counts = new long[(GpuWork.SubmissionKinds.Length * 2)];

        counters.AddTo(counts: counts, rowCount: 1, slot: 0);
        for (var index = 0; (index < kinds.Length); index++) {
            Assert.Equal((0x1_0000_0001L + index), counts[(GpuWork.SubmissionKinds.Length + GpuWork.SubmissionKinds.IndexOf(kinds[index]))]);
        }
    }

    private static int MarchStepsColumn => GpuWork.SubmissionKinds.ToArray().ToList().IndexOf(item: GpuWork.MarchSteps);
    private static int SkyEvaluationsColumn => GpuWork.SubmissionKinds.ToArray().ToList().IndexOf(item: GpuWork.SkyEvaluations);
    private static int TexelsWrittenColumn => GpuWork.SubmissionKinds.ToArray().ToList().IndexOf(item: GpuWork.TexelsWritten);

    [Fact]
    public void ACompletedSubmissionReadsItsSlotIntoItsPassesKernelColumns() {
        var gpu = new UploadModelGpu();
        var ledger = new GpuWorkLedger(
            framesInFlight: 2,
            name: "gpu.test"
        );
        var services = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: gpu.Services
        );

        using var counters = new GpuKernelCounters(
            buffers: services.BufferFactory,
            owner: "test",
            part: "kernel counters",
            rows: 3,
            slots: 2
        );

        ledger.Configure(
            passLabels: ["a", "b", "c"],
            revision: 1L
        );

        var fence = services.QueueSubmitter.CreateSubmissionFence();
        var command = services.CommandPoolFactory.Create(name: default).CommandBufferHandle;

        counters.RecordClear(
            commandBuffer: command,
            recorder: services.Recorder,
            slot: 1
        );

        // What the passes' kernels add, one 64-bit count per kind in each pass's row: pass a five steps, seven texels and
        // eleven sky evaluations, pass c a step count past 32 bits.
        var memory = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 1).Buffer.BufferHandle);

        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: 0), value: 5UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: 8), value: 7UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: 16), value: 11UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: 120), value: 0x2_0000_0013UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: 128), value: 23UL);
        BinaryPrimitives.WriteUInt64LittleEndian(destination: memory.AsSpan(start: (2 * GpuKernelCounters.RowBytes)), value: 0x1_0000_0003UL);

        for (var pass = 0; (pass < 3); pass++) {
            ledger.EnterPass(pass: pass);
            ledger.LeavePass();
        }

        counters.RecordCopy(
            commandBuffer: command,
            recorder: services.Recorder,
            slot: 1
        );
        ledger.ReadOnCompletion(
            readback: counters,
            slot: 1
        );
        services.QueueSubmitter.Submit(
            commandBufferHandles: [command],
            fence: fence
        );
        fence.Wait();

        var sample = new GpuWorkSample();

        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: Kernel(pass: 0, sample: sample),
            expected: (5L, 7L, 11L)
        );
        Assert.Equal(
            actual: Kernel(pass: 1, sample: sample),
            expected: (0L, 0L, 0L)
        );
        Assert.Equal(
            actual: Kernel(pass: 2, sample: sample),
            expected: (0x1_0000_0003L, 0L, 0L)
        );
        Assert.True(condition: sample.TryGetPassCount(column: Column(kind: GpuWork.ShapesEvaluated), pass: 0, value: out var shapes));
        Assert.True(condition: sample.TryGetPassCount(column: Column(kind: GpuWork.ShapeGradients), pass: 0, value: out var gradients));
        Assert.Equal(actual: shapes, expected: 0x2_0000_0013L);
        Assert.Equal(actual: gradients, expected: 23L);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: GpuWork.ShapesEvaluated.Class);
        Assert.Equal(expected: WorkClass.PerBackendDeterministic, actual: GpuWork.ShapeGradients.Class);
        Assert.Equal(actual: sample.GetOutsidePassCount(column: Column(kind: GpuWork.Clears)), expected: 1L);
        Assert.Equal(actual: sample.GetOutsidePassCount(column: Column(kind: GpuWork.Copies)), expected: 1L);
        Assert.Equal(actual: sample.GetOutsidePassCount(column: Column(kind: GpuWork.BufferBarriers)), expected: 3L);
        Assert.Empty(collection: gpu.StateConflicts);
    }
    // The clear is ordered before every compute and fragment addition, those additions before the copy, and the copy
    // before the host's read of the readback, each by its own barrier.
    [Fact]
    public void TheBarriersOrderTheClearTheAdditionsTheCopyAndTheHostRead() {
        var gpu = new UploadModelGpu();

        using var counters = new GpuKernelCounters(
            buffers: gpu.Services.BufferFactory,
            owner: "test",
            part: "kernel counters",
            rows: 1,
            slots: 1
        );

        var command = gpu.Services.CommandPoolFactory.Create(name: default).CommandBufferHandle;
        var counter = counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle;

        counters.RecordClear(commandBuffer: command, recorder: gpu.Services.Recorder, slot: 0);
        counters.RecordCopy(commandBuffer: command, recorder: gpu.Services.Recorder, slot: 0);

        var readback = Assert.Single(collection: gpu.BufferBarriers, predicate: barrier => (barrier.Buffer != counter)).Buffer;

        Assert.Equal(
            actual: gpu.BufferBarriers,
            expected: [
                new UploadModelBufferBarrier(
                    Buffer: counter,
                    DestinationAccess: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                    DestinationStages: GpuStage.ComputeShader | GpuStage.FragmentShader,
                    SourceAccess: GpuAccess.TransferWrite,
                    SourceStages: GpuStage.Transfer
                ),
                new UploadModelBufferBarrier(
                    Buffer: counter,
                    DestinationAccess: GpuAccess.TransferRead,
                    DestinationStages: GpuStage.Transfer,
                    SourceAccess: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
                    SourceStages: GpuStage.ComputeShader | GpuStage.FragmentShader
                ),
                new UploadModelBufferBarrier(
                    Buffer: readback,
                    DestinationAccess: GpuAccess.HostRead,
                    DestinationStages: GpuStage.Host,
                    SourceAccess: GpuAccess.TransferWrite,
                    SourceStages: GpuStage.Transfer
                ),
            ]
        );
    }
    [Fact]
    public void ASubmissionNamingNoSlotReadsNoKernelCounts() {
        var gpu = new UploadModelGpu();
        var ledger = new GpuWorkLedger(
            framesInFlight: 1,
            name: "gpu.test"
        );
        var services = GpuWorkCounting.Wrap(
            ledger: ledger,
            services: gpu.Services
        );

        using var counters = new GpuKernelCounters(
            buffers: services.BufferFactory,
            owner: "test",
            part: "kernel counters",
            rows: 1,
            slots: 1
        );

        ledger.Configure(
            passLabels: ["a"],
            revision: 1L
        );

        var command = services.CommandPoolFactory.Create(name: default).CommandBufferHandle;
        var counter = gpu.Memory(bufferHandle: counters.RowOf(row: 0, slot: 0).Buffer.BufferHandle);

        // The slot's readback holds counts, but the submission names no readback slot to its ledger.
        BinaryPrimitives.WriteUInt64LittleEndian(destination: counter.AsSpan(start: 0), value: 9UL);
        counters.RecordCopy(commandBuffer: command, recorder: services.Recorder, slot: 0);

        var fence = services.QueueSubmitter.CreateSubmissionFence();

        ledger.EnterPass(pass: 0);
        ledger.LeavePass();
        services.QueueSubmitter.Submit(
            commandBufferHandles: [command],
            fence: fence
        );
        fence.Wait();

        var sample = new GpuWorkSample();

        Assert.True(condition: ledger.TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: Kernel(pass: 0, sample: sample),
            expected: (0L, 0L, 0L)
        );
    }
    [Fact]
    public void EverySlotHoldsACounterAndAReadbackOfOneRowAPass() {
        var gpu = new UploadModelGpu();
        var bytesBefore = gpu.BufferBytes;

        using var counters = new GpuKernelCounters(
            buffers: gpu.Services.BufferFactory,
            owner: "test",
            part: "kernel counters",
            rows: 10,
            slots: 3
        );

        Assert.Equal(actual: GpuKernelCounters.RowBytes, expected: ((GpuWork.KernelKinds.Length * GpuKernelCounters.CountWords) * sizeof(uint)));
        Assert.Equal(actual: counters.SizeBytes, expected: (10UL * ((ulong)GpuKernelCounters.RowBytes)));
        Assert.Equal(actual: counters.TotalBytes, expected: (6UL * counters.SizeBytes));
        Assert.Equal(actual: (gpu.BufferBytes - bytesBefore), expected: counters.TotalBytes);
        Assert.NotSame(expected: counters.RowOf(row: 0, slot: 0).Buffer, actual: counters.RowOf(row: 0, slot: 1).Buffer);
        Assert.Equal(actual: counters.RowOf(row: 9, slot: 2).Row, expected: 9U);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => counters.RowOf(row: 10, slot: 0));
    }

    private static int Column(WorkKind kind) => GpuWork.SubmissionKinds.ToArray().ToList().IndexOf(item: kind);
    private static (long Steps, long Texels, long Sky) Kernel(GpuWorkSample sample, int pass) {
        Assert.True(condition: sample.TryGetPassCount(column: MarchStepsColumn, pass: pass, value: out var steps));
        Assert.True(condition: sample.TryGetPassCount(column: TexelsWrittenColumn, pass: pass, value: out var texels));
        Assert.True(condition: sample.TryGetPassCount(column: SkyEvaluationsColumn, pass: pass, value: out var sky));

        return (steps, texels, sky);
    }
}
