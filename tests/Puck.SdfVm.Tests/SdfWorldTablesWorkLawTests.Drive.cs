using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Gpu;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// A held frame's drive: its jobs reach the queue before it sleeps on their completion, it yields once its slice has
// passed however many wakes come, and a job is handed back only while it has consumed nothing.
public sealed partial class SdfWorldTablesWorkLawTests {
    [Fact]
    public void ADriveInsideTheFramesOpenBatchHandsItsJobsToTheQueueAndRunsThemAtTheGpusPace() {
        var gpu = new FakeGpuDevice(clock: VirtualClock.ForDevice()) { DefersBatches = true };
        using var rig = new Rig(device: gpu);
        rig.Render();
        var client = new SerialClient(gpu.Services) { Remaining = 8 };
        var work = rig.Engine.GpuWork;
        work.Add(client);

        // The render graph stages a residency, and so drives a held frame's work, inside the frame's open batch.
        gpu.Services.QueueSubmitter.BeginBatch();
        work.Drive(rig.Engine);

        // Each job is offered only once the one before it was answered, as the lighting drain's are: every one ran within
        // the one drive, so its submissions reached the queue before the batch's flush.
        Assert.Equal(expected: 8, actual: client.Answered);
        Assert.True(condition: (gpu.BatchSplits >= 1), userMessage: "the drive never handed its jobs to the queue");
        gpu.Services.QueueSubmitter.FlushBatch();
    }
    [Fact]
    public void ADriveStartsNoStepAfterItsSliceHowEverManyWakesAreReady() {
        using var rig = new Rig();
        rig.Render();
        // Every job's fence reads complete as it is submitted, so a wake is always ready; each recording takes time.
        var clock = Assert.IsType<VirtualClock>(rig.Gpu.Clock);
        var client = new SerialClient(rig.Gpu.Services) { Remaining = 1000, OnRecord = () => clock.Advance(by: SdfGpuWork.DriveSlice) };
        var work = rig.Engine.GpuWork;
        work.Add(client);

        work.Drive(rig.Engine);

        // The first recording consumes the whole slice. Even its ready fence waits for the next step.
        Assert.Equal(expected: 999, actual: client.Remaining);
        Assert.Equal(expected: 0, actual: client.Answered);
    }
    [Fact]
    public void AJobIsHandedBackOnlyWhileItHasConsumedNothing() {
        using var rig = new Rig(holdFences: true);
        rig.Render();
        var services = rig.Gpu.Services;
        using var region = new GpuRegion(GpuResidencyPolicy.Ring, GpuHostVisibleMemory.Host, 64, 1, services.BufferFactory, services.Bindings, services.Recorder, null, default);
        var ordered = new ConsumingClient(region: region, disordered: false);
        var disordered = new ConsumingClient(region: region, disordered: true);
        var first = new SdfGpuWork(device: rig.Gpu, name: "ordered");
        var second = new SdfGpuWork(device: rig.Gpu, name: "disordered");

        try {
            // A job that asks for every buffer, its readback reserved, before it copies a region records and submits: a
            // refusal could still hand it back whole, and its copy into the readback creates nothing.
            first.Add(ordered);
            first.Pump(rig.Engine);
            Assert.Equal(expected: 1, actual: first.InFlight);

            // One that asks for a buffer after the copy took the region's owed words could only be handed back by losing
            // them: the slot refuses to create it and the service faults.
            second.Add(disordered);
            _ = Assert.Throws<InvalidOperationException>(() => second.Pump(rig.Engine));
            Assert.Equal(expected: 0, actual: second.InFlight);
        } finally {
            foreach (var fence in rig.Gpu.SubmittedFences) { fence.Completed = true; }
            first.Dispose();
            second.Dispose();
        }
    }

    // A client whose next job is offered only once the one before it was answered, as the lighting drain offers its
    // batches; it reads and releases each answer at once.
    private sealed class SerialClient(GpuDeviceServices gpu) : ISdfGpuWorkClient {
        private readonly IGpuBuffer m_source = gpu.BufferFactory.CreateDeviceLocal(sizeBytes: 4, usage: GpuBufferUsage.Storage,
            name: new GpuObjectName(owner: "law", part: "serial"));
        private bool m_flying;
        public int Remaining { get; set; }
        public int Answered { get; private set; }
        public Action? OnRecord { get; init; }
        public GpuMemoryBytes OwnedBytes => default;
        public GpuQueueCategory Category => GpuQueueCategory.Probe;
        public bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
            estimatedSeconds = 0.001;
            job = null;
            if (m_flying || (Remaining == 0)) { return false; }
            Remaining--;
            m_flying = true;
            job = new object();
            return true;
        }
        public void Record(object job, in SdfGpuWorkRecording recording) {
            OnRecord?.Invoke();
            recording.CopyToReadback(m_source, 4);
        }
        public void Complete(object job, SdfGpuWorkReadback readback) {
            Answered++;
            m_flying = false;
            readback.Release();
        }
        public void Abandon(object job) => m_flying = false;
        public void Dispose() => m_source.Dispose();
    }
    // A client of one job that copies a region and holds a buffer of the slot, asking for the buffer and reserving its
    // readback before the copy or, disordered, after it.
    private sealed class ConsumingClient(GpuRegion region, bool disordered) : ISdfGpuWorkClient {
        private bool m_taken;
        public GpuMemoryBytes OwnedBytes => default;
        public GpuQueueCategory Category => GpuQueueCategory.Probe;
        public bool TryTake(SdfWorldTables tables, int uploadBytes, SdfGpuWorkAllowance allowance, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
            estimatedSeconds = 0.0;
            job = (m_taken ? null : new object());
            m_taken = true;
            return (job is not null);
        }
        public void Record(object job, in SdfGpuWorkRecording recording) {
            if (disordered) {
                recording.CopyRegion(region, 0, GpuStage.ComputeShader);
                recording.CopyToReadback(recording.DeviceBuffer(part: "scratch", bytes: 64, usage: GpuBufferUsage.Storage), 4);
                return;
            }
            var scratch = recording.DeviceBuffer(part: "scratch", bytes: 64, usage: GpuBufferUsage.Storage);

            recording.ReserveReadback(bytes: 4);
            recording.CopyRegion(region, 0, GpuStage.ComputeShader);
            recording.CopyToReadback(scratch, 4);
        }
        public void Complete(object job, SdfGpuWorkReadback readback) => readback.Release();
        public void Abandon(object job) { }
        public void Dispose() { }
    }
}
