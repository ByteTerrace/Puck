using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The partial-construction law for <see cref="SdfWorldTables"/>, driven through <see cref="GpuCreationFaults"/>, the
/// decorator the backends wrap their services with, over a <see cref="FakeGpuDevice"/> that tracks every object it
/// creates: every creation the engine's construction makes, of every kind the decorator can fail, is failed in turn. The
/// failed construction releases exactly what it created, once each, holds no device-local memory, and leaves the
/// pipeline set it was handed untouched; the same construction then succeeds. The first frame that draws a mesh, whose
/// mesh pass attachments no construction creates, releases the same way.
/// </summary>
public sealed class SdfWorldTablesCreationFaultLawTests {
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

        // The engine creates no pipeline, shader module, render pass or framebuffer: it records with the set and the mesh
        // pass pipeline it is handed, and the first frame that draws a mesh creates the mesh pass's attachments and the
        // framebuffer binding them. It creates buffers, its two filler images, a command pool per ring slot, its own descriptor pool, and whatever policy the device selects the one copy pool it reserves for all its
        // regions: the eight tables, the mesh region and the brick staging.
        Assert.Equal(actual: expected[GpuCreationKind.Pipeline], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.ShaderModule], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.RenderPass], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.Framebuffer], expected: 0L);
        Assert.Equal(actual: expected[GpuCreationKind.CommandPool], expected: ((long)SdfWorldTables.FrameRingSize));
        Assert.Equal(actual: expected[GpuCreationKind.BindingsPool], expected: 2L);
        Assert.True(condition: (expected[GpuCreationKind.Buffer] > SdfBrickPoolLayout.MaxBricks));
        Assert.Equal(actual: expected[GpuCreationKind.Image], expected: 2L);

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

    // One quad at the origin, drawn once.
    private static readonly SdfMeshDraw[] MeshDraws = [new(
        Material: 0,
        Mesh: new SdfMesh(
            indices: new uint[] { 0, 1, 2, 0, 2, 3 },
            positions: new Vector3[] { new(x: 0f, y: 0f, z: 0f), new(x: 1f, y: 0f, z: 0f), new(x: 1f, y: 1f, z: 0f), new(x: 0f, y: 1f, z: 0f) }
        ),
        ObjectToWorld: Matrix4x4.Identity
    )];

    // One view over the engine's extent, looking at the origin, drawing the given meshes.
    private static SdfFrame Frame(SdfMeshDraw[] meshDraws) {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return new SdfFrame(
            Program: builder.Build(),
            ProgramChanged: false,
            Time: 0f,
            Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(
                    fieldOfViewRadians: 1f,
                    position: new Vector3(x: 0f, y: 0f, z: -5f),
                    target: Vector3.Zero,
                    viewportHeight: Extent,
                    viewportWidth: Extent
                ),
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            )]
        ) {
            EnableCadenceGate = false,
            MeshDraws = meshDraws,
        };
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
        private readonly GpuPassPipeline m_meshRaster;
        private readonly GpuPassPipeline m_regionCopy;
        private readonly SdfProgram m_program;
        private readonly GpuWorkLedger m_work = new(
            framesInFlight: SdfWorldTables.FrameRingSize,
            name: "gpu.sdf-engine"
        );

        public Rig() {
            ReadOnlyMemory<byte> code = new byte[] { 1 };
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );
            m_program = builder.Build();
            Gpu = new FakeGpuDevice(trackObjects: true);
            Faults = new GpuCreationFaults();
            Device = new FaultingDevice(
                faults: Faults,
                gpu: Gpu
            );
            m_regionCopy = SdfTestPipelines.RegionCopy(
                device: Device,
                ledger: m_work
            );
            m_meshRaster = SdfTestPipelines.MeshRaster(
                device: Device,
                ledger: m_work
            );
            m_pipelines = SdfTestPipelines.Build(
                device: Device,
                includeBrickPipelines: true,
                kernels: SdfTestPipelines.Kernels().With(bytecode: code, kernel: SdfKernel.BrickBake),
                cache: new GpuPassPipelineCache()
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
        public SdfWorldTables Construct() =>
            new(
                device: Device,
                options: new SdfWorldTablesOptions(
                    BrickPoolVoxelCapacity: SdfBrickPoolLayout.VoxelsPerBrick,
                    Program: m_program,
                    WorkLedger: m_work
                ),
                pipelines: m_pipelines,
                meshRaster: m_meshRaster,
                regionCopy: m_regionCopy.Compute!
            );
        public void Dispose() {
            m_pipelines.Dispose();
            m_regionCopy.Dispose();
            m_meshRaster.Dispose();
        }
    }
}
