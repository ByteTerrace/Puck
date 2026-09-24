using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfWorldPipelines"/> over <see cref="FakeGpuDevice"/>: a build creates one pipeline per engine
/// kernel (brick pipelines only when asked for and present) and counts them into the owner's ledger; a build and a
/// reload that created pipelines each write the device's persistent cache once, from the thread that built them; a
/// reload creates only the pipelines whose bytecode changed; and a canceled build throws before creating anything.
/// </summary>
public sealed class SdfWorldPipelinesLawTests {
    [Fact]
    public void ABuildCreatesEveryEnginePipelineAndPersistsTheDeviceCacheOnce() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var device = new PersistingDevice();
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        using var pipelines = SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: device,
            gpu: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            ledger: ledger
        );

        Assert.Equal(expected: 12L, actual: Created(ledger: ledger));
        Assert.Equal(expected: 1, actual: device.Persisted);

        using (var unchanged = pipelines.PrepareReload(
            cancellationToken: CancellationToken.None,
            kernels: SdfTestPipelines.Kernels(beam: 1)
        )) {
            Assert.Equal(expected: 0, actual: unchanged.ChangedPipelines);
        }

        using (var changed = pipelines.PrepareReload(
            cancellationToken: CancellationToken.None,
            kernels: SdfTestPipelines.Kernels(beam: 2)
        )) {
            Assert.Equal(expected: 1, actual: changed.ChangedPipelines);
        }

        Assert.Equal(expected: 13L, actual: Created(ledger: ledger));
        Assert.Equal(expected: 3, actual: device.Persisted);
    }
    [Fact]
    public void ABuildWithABrickPoolAddsTheBrickPipelinesItsKernelsCarry() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        using var pipelines = SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: gpu,
            gpu: gpu,
            includeBrickPipelines: true,
            kernels: SdfTestPipelines.Kernels(beam: 1) with { BrickBake = new byte[] { 1 }, BrickUpload = new byte[] { 1 } },
            ledger: ledger
        );

        Assert.True(condition: pipelines.IncludesBrickPipelines);
        Assert.Equal(expected: 14L, actual: Created(ledger: ledger));
    }
    [Fact]
    public void ACanceledBuildThrowsBeforeCreatingAnything() {
        var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
        var ledger = new GpuWorkLedger(
                framesInFlight: SdfWorldEngine.FrameRingSize,
                name: "gpu.sdf-engine"
            );

        Assert.Throws<OperationCanceledException>(testCode: () => SdfWorldPipelines.Build(
            cancellationToken: new CancellationToken(canceled: true),
            device: gpu,
            gpu: gpu,
            includeBrickPipelines: false,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            ledger: ledger
        ));
        Assert.Equal(expected: 0L, actual: Created(ledger: ledger));
    }

    private static long Created(GpuWorkLedger ledger) {
        Assert.True(condition: ledger.TryRead(kind: GpuWork.PipelinesCreated, value: out var value));

        return value;
    }

    // A device whose persistent cache counts the writes asked of it.
    private sealed class PersistingDevice : IGpuDeviceContext, IGpuPipelineCache {
        private int m_persisted;

        public long AdapterLuid => 0L;
        public nint DeviceHandle => 1;
        public GpuDeviceIdentity? Identity => null;
        public GpuMemoryProfile MemoryProfile => default;
        public int Persisted => Volatile.Read(location: ref m_persisted);

        public void Persist() => Interlocked.Increment(location: ref m_persisted);
        public void WaitIdle() { }
    }
}
