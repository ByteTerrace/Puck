using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The partial-construction law for <see cref="SdfWorldEngine"/>, driven through <see cref="GpuCreationFaults"/>, the
/// decorator the backends wrap their services with, over a <see cref="FakeGpuDevice"/> that tracks every object it
/// creates: every creation the engine's construction makes, of every kind the decorator can fail, is failed in turn. The
/// failed construction releases exactly what it created, once each, holds no device-local memory, and leaves the
/// pipeline set it was handed untouched; the same construction then succeeds. A refusal that is not a creation fault,
/// the ISA handshake's, releases the same way.
/// </summary>
public sealed class SdfWorldEngineCreationFaultLawTests {
    private const uint Extent = 16;

    [Fact]
    public void EveryCreationOfAnEngineFaultedInTurnReleasesExactlyWhatItCreated() {
        var expected = new Dictionary<GpuCreationKind, long>();

        using (var measured = new Rig()) {
            using var engine = measured.Construct();

            foreach (var kind in GpuCreationFaults.Kinds) {
                expected[kind] = measured.Faults.SeenOf(kind: kind);
            }
        }

        // The engine creates no pipeline, shader module, render pass or framebuffer: it records with the set it is
        // handed. It creates buffers, images (the ISA handshake's two included), a command pool per ring slot and one
        // descriptor pool.
        Assert.Equal(actual: expected[GpuCreationKind.Pipeline], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.ShaderModule], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.RenderPass], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.Framebuffer], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.CommandPool], expected: ((long)SdfWorldEngine.FrameRingSize));
        Assert.Equal(actual: expected[GpuCreationKind.BindingsPool], expected: 1L);
        Assert.True(condition: (expected[GpuCreationKind.Buffer] > SdfBrickPoolLayout.MaxBricks));
        Assert.True(condition: (expected[GpuCreationKind.Image] > 2L));

        var faulted = 0;

        foreach (var kind in GpuCreationFaults.Kinds) {
            for (var nth = 1; (nth <= expected[kind]); nth++) {
                using var rig = new Rig();
                var handed = rig.Gpu.Created.Count;

                rig.Faults.Arm(
                    kind: kind,
                    nth: nth
                );

                var fault = Assert.Throws<GpuCreationFaultException>(testCode: () => rig.Construct());

                Assert.Equal(
                    actual: fault.Kind,
                    expected: kind
                );
                Assert.Equal(
                    actual: fault.Creation,
                    expected: nth
                );
                rig.AssertReleasedExactly(handed: handed);

                // The fault fired once: the same construction succeeds and its disposal holds nothing either.
                rig.Construct().Dispose();
                rig.AssertReleasedExactly(handed: handed);
                faulted++;
            }
        }

        Assert.Equal(
            actual: faulted,
            expected: expected.Values.Sum()
        );
    }
    [Fact]
    public void AnIsaHandshakeRefusalReleasesEverythingTheConstructionCreated() {
        using var rig = new Rig(reportVersion: unchecked((byte)(SdfIsa.Version + 1)));
        var handed = rig.Gpu.Created.Count;
        var refusal = Assert.Throws<InvalidOperationException>(testCode: () => rig.Construct());

        Assert.Contains(
            expectedSubstring: "SDF ISA version mismatch",
            actualString: refusal.Message
        );
        Assert.True(condition: (rig.Gpu.Created.Count > handed));
        rig.AssertReleasedExactly(handed: handed);
    }

    // The fake as a device context whose services pass through creation faults, as a backend's do.
    private sealed class FaultingDevice(FakeGpuDevice gpu, GpuCreationFaults faults) : IGpuDeviceContext {
        public long AdapterLuid => gpu.AdapterLuid;
        public GpuDeviceCapabilities? Capabilities => gpu.Capabilities;
        public GpuDeviceIdentity? Identity => gpu.Identity;
        public GpuMemoryProfile MemoryProfile => gpu.MemoryProfile;
        public GpuDeviceServices Services { get; } = GpuCreationFaults.Wrap(
            faults: faults,
            services: gpu.Services
        );

        public void WaitIdle() => gpu.WaitIdle();
    }
    // A tracking fake behind creation faults, with a pipeline set (brick pipelines included, so the brick pool's request
    // and staging buffers are created too) already built on it and the faults' counts cleared.
    private sealed class Rig : IDisposable {
        private readonly SdfWorldPipelines m_pipelines;
        private readonly SdfProgram m_program;
        private readonly GpuWorkLedger m_work = new(
            framesInFlight: SdfWorldEngine.FrameRingSize,
            name: "gpu.sdf-engine"
        );

        public Rig(byte reportVersion = SdfIsa.Version) {
            ReadOnlyMemory<byte> code = new byte[] { 1 };
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );
            m_program = builder.Build();
            Gpu = new FakeGpuDevice(
                reportVersion: reportVersion,
                trackObjects: true
            );
            Faults = new GpuCreationFaults();
            Device = new FaultingDevice(
                faults: Faults,
                gpu: Gpu
            );
            m_pipelines = SdfWorldPipelines.Build(
                cancellationToken: CancellationToken.None,
                device: Device,
                includeBrickPipelines: true,
                kernels: (SdfTestPipelines.Kernels() with {
                    BrickBake = code,
                    BrickUpload = code,
                }),
                ledger: m_work
            );
            Faults.Disarm();
        }

        public IGpuDeviceContext Device { get; }
        public GpuCreationFaults Faults { get; }
        public FakeGpuDevice Gpu { get; }

        // Every object created after the first `handed` (the pipeline set's) was released exactly once, the set's own
        // objects were not touched, and no device-local memory is held: the device's teardown refuses nothing.
        public void AssertReleasedExactly(int handed) {
            Assert.All(
                action: static created => Assert.Equal(
                    actual: created.DisposeCount,
                    expected: 0
                ),
                collection: Gpu.Created.Take(count: handed)
            );
            Assert.All(
                action: static created => Assert.Equal(
                    actual: created.DisposeCount,
                    expected: 1
                ),
                collection: Gpu.Created.Skip(count: handed)
            );
            Assert.Equal(
                actual: Gpu.Memory.Held,
                expected: 0L
            );
            Gpu.Memory.EndDevice(device: FakeGpuDevice.DeviceHandle);
        }
        public SdfWorldEngine Construct() =>
            new(
                device: Device,
                height: Extent,
                options: new SdfWorldEngineOptions(
                    BrickPoolVoxelCapacity: SdfBrickPoolLayout.VoxelsPerBrick,
                    Program: m_program,
                    ViewportCapacity: 2,
                    WorkLedger: m_work
                ),
                pipelines: m_pipelines,
                width: Extent
            );
        public void Dispose() => m_pipelines.Dispose();
    }
}
