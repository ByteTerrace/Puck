using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfWorldResidency.RequestShaderReload"/> over <see cref="FakeGpuDevice"/>, from a kernel tree on
/// disk, the build's own SPIR-V kernels: a tree whose kernel was compiled against another instruction set, beside this
/// host's own <c>isa/sdf-isa.hlsli</c>, or binds the program words and the frame's instance grid in each other's places,
/// is refused by the interface check, the request reports it failed and the residency keeps rendering with its kernels;
/// a tree whose changed kernel reads this host's interface applies.
/// </summary>
public sealed class SdfWorldResidencyShaderReloadLawTests {
    private const uint Extent = 32;

    [Fact]
    public void AReloadWhoseKernelsDoNotReadTheHostsInterfaceFailsAndTheResidencyKeepsItsKernels() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            var context = Context(gpu: new FakeGpuDevice());
            var changed = SdfTestPipelines.Kernels(beam: 2);
            using var node = Node();

            node.ProduceFirstFrame(context: in context);

            foreach (var (kernels, reason) in ((ReadOnlySpan<(SdfKernelSet, string)>)[
                (changed.With(
                    bytecode: SpirvEdits.Renamed(
                        from: ("passGroup" + SdfIsaHlsl.Stamp),
                        module: changed[SdfKernel.Beam].Span,
                        to: ("passGroup" + SdfIsaHlsl.StampOf(fingerprint: SdfIsaHlsl.Fingerprint ^ 1U))
                    ),
                    kernel: SdfKernel.Beam
                ), "stamped"),
                (changed.With(
                    bytecode: SpirvEdits.BindingsSwapped(
                        first: SdfWorldPackage.ProgramWords,
                        module: changed[SdfKernel.InstanceCull].Span,
                        second: SdfWorldPackage.FrameInstanceGrid
                    ),
                    kernel: SdfKernel.InstanceCull
                ), SdfWorldPackage.ProgramWords),
            ])) {
                Tree(
                    kernels: kernels,
                    root: root
                );

                var refused = Reload(
                    context: in context,
                    node: node,
                    root: root
                );

                Assert.Equal(expected: ("failed", 0L, 0), actual: (refused.State, refused.Generation, refused.ChangedPipelines));
                Assert.Contains(actualString: refused.Error, expectedSubstring: reason);
                Assert.True(condition: node.Produce(context: in context));
            }

            Tree(
                kernels: changed,
                root: root
            );

            var applied = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("applied", 1L, 1), actual: (applied.State, applied.Generation, applied.ChangedPipelines));
            Assert.True(condition: node.Produce(context: in context));
        } finally {
            Directory.Delete(
                path: root,
                recursive: true
            );
        }
    }

    private static FrameContext Context(FakeGpuDevice gpu) => new(
        AccumulatorTicks: 0UL,
        DeltaTicks: 0UL,
        ElapsedTicks: 0UL,
        FrameDeltaTicks: 0UL,
        Host: new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = gpu,
        }),
        StepTicks: 0UL,
        TargetHeight: Extent,
        TargetWidth: Extent
    );
    private static SdfWorldResidency Node() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        var frame = new SdfFrame(
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
        );

        return new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            frameSource: new FixedFrameSource(frame: frame),
            height: Extent,
            kernels: SdfTestPipelines.Kernels(beam: 1),
            name: "world",
            pipelines: SdfTestPipelines.Cache(),
            width: Extent
        );
    }
    // Requests a reload of the tree and produces frames until it finishes. The bound is liveness for the pipeline build on
    // the thread pool; it decides nothing.
    private static SdfShaderReloadStatus Reload(in FrameContext context, SdfWorldResidency node, string root) {
        var copy = context;

        Assert.True(condition: node.RequestShaderReload(tree: root));
        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                _ = node.Produce(context: in copy);

                return (node.ShaderReloadStatus.State != "pending");
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return node.ShaderReloadStatus;
    }
    // Writes a kernel tree under the root: a kernel set's SPIR-V in its passes directory, and this host's own generated
    // instruction-set include beside it, which a reload never reads: kernels carry their instruction set themselves.
    private static void Tree(SdfKernelSet kernels, string root) {
        var passes = Directory.CreateDirectory(path: SdfKernelSet.PassesDirectory(tree: root)).FullName;
        var isa = Directory.CreateDirectory(path: Path.Combine(path1: root, path2: "isa")).FullName;

        foreach (var kernel in SdfKernelSet.Kernels) {
            File.WriteAllBytes(
                bytes: kernels[kernel].ToArray(),
                path: Path.Combine(
                    path1: passes,
                    path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.spv"
                )
            );
        }

        File.WriteAllText(
            contents: SdfIsaHlsl.Generate(),
            path: Path.Combine(path1: isa, path2: SdfIsaHlsl.FileName)
        );
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
