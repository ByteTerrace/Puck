using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for <see cref="SdfWorldResidency.RequestShaderReload"/> over <see cref="FakeGpuDevice"/>, from a kernel tree on
/// disk: a tree whose generated <c>isa/sdf-isa.hlsli</c> records no fingerprint or another instruction set's is refused,
/// the request reports it failed and the residency keeps rendering with its kernels, and the same bytecode beside this
/// host's include applies.
/// </summary>
public sealed class SdfWorldResidencyShaderReloadLawTests {
    private const uint Extent = 32;

    [Fact]
    public void AReloadWithoutThisHostsInstructionSetFailsAndTheResidencyKeepsItsKernels() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            var context = Context(gpu: new FakeGpuDevice());
            var foreign = SdfIsaHlsl.Fingerprint ^ 1U;
            using var node = Node();

            node.ProduceFirstFrame(context: in context);
            Tree(
                include: "",
                root: root
            );

            var unrecorded = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("failed", 0L), actual: (unrecorded.State, unrecorded.Generation));
            Assert.Contains(actualString: unrecorded.Error, expectedSubstring: "SDF_ISA_FINGERPRINT");
            Tree(
                include: SdfIsaHlsl.Generate().Replace(
                    comparisonType: StringComparison.Ordinal,
                    newValue: $"0x{foreign:X8}u",
                    oldValue: $"0x{SdfIsaHlsl.Fingerprint:X8}u"
                ),
                root: root
            );

            var refused = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("failed", 0L, 0), actual: (refused.State, refused.Generation, refused.ChangedPipelines));
            Assert.Contains(actualString: refused.Error, expectedSubstring: "another instruction set");
            Assert.True(condition: node.Produce(context: in context));

            Tree(
                include: SdfIsaHlsl.Generate(),
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
    // Writes a kernel tree under the root: a kernel set differing from the residency's by its beam kernel, and the given
    // instruction-set include beside it.
    private static void Tree(string include, string root) {
        var passes = Directory.CreateDirectory(path: SdfKernelSet.PassesDirectory(tree: root)).FullName;
        var kernels = SdfTestPipelines.Kernels(beam: 2);

        foreach (var kernel in SdfKernelSet.Kernels) {
            File.WriteAllBytes(
                bytes: kernels[kernel].ToArray(),
                path: Path.Combine(
                    path1: passes,
                    path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.spv"
                )
            );
        }

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: SdfKernelSet.IsaIncludeOf(passesDirectory: passes))!);
        File.WriteAllText(
            contents: include,
            path: SdfKernelSet.IsaIncludeOf(passesDirectory: passes)
        );
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}
