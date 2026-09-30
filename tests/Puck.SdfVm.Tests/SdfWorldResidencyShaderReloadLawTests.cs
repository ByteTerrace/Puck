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
/// host's own <c>isa/sdf-isa.hlsli</c>, or binds the program words and the frame's instance grid, or the cull bounds and
/// the views' dispatch arguments (two buffers of one shape), in each other's places, is refused by the interface check, the request reports it failed and the residency keeps rendering with its kernels;
/// a tree whose changed kernel reads this host's interface applies. A tree carries only the kernels it replaces, as
/// bytecode or as sources the reload compiles; a source that does not compile fails the request with its file and line.
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
                (changed.With(
                    bytecode: SpirvEdits.BindingsSwapped(
                        first: SdfWorldPackage.CullBoundsWritten,
                        module: changed[SdfKernel.CullArgs].Span,
                        second: SdfWorldPackage.ViewsArgsWritten
                    ),
                    kernel: SdfKernel.CullArgs
                ), SdfWorldPackage.CullBoundsWritten),
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
    [Fact]
    public void ATreeCarriesTheKernelsItReplacesAsBytecodeOrAsSourcesTheReloadCompiles() {
        var root = Directory.CreateTempSubdirectory(prefix: "puck-test-").FullName;

        try {
            var context = Context(gpu: new FakeGpuDevice());
            var passes = SdfKernelSet.PassesDirectory(tree: root);
            var cullArgs = Path.Combine(
                path1: passes,
                path2: $"{SdfKernelSet.StemOf(kernel: SdfKernel.CullArgs)}.comp.hlsl"
            );
            using var node = Node();

            node.ProduceFirstFrame(context: in context);
            Directory.CreateDirectory(path: passes);

            var empty = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("failed", 0L), actual: (empty.State, empty.Generation));
            Assert.Contains(actualString: empty.Error, expectedSubstring: "carries no kernel");

            // The changed beam alone: every other kernel keeps its bytecode.
            File.WriteAllBytes(
                bytes: SdfTestPipelines.Kernels(beam: 2)[SdfKernel.Beam].ToArray(),
                path: Path.Combine(
                    path1: passes,
                    path2: $"{SdfKernelSet.StemOf(kernel: SdfKernel.Beam)}.comp.spv"
                )
            );

            var bytecode = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("applied", 1L, 1), actual: (bytecode.State, bytecode.Generation, bytecode.ChangedPipelines));

            // A cull-args source beside it: the reload compiles it, and the beam it already installed is unchanged.
            File.WriteAllText(
                contents: CullArgsSource(body: "viewsArgsRW[0] = passGroup.extent.x;"),
                path: cullArgs
            );

            var compiled = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("applied", 2L, 1), actual: (compiled.State, compiled.Generation, compiled.ChangedPipelines));

            File.WriteAllText(
                contents: CullArgsSource(body: "viewsArgsRW[0] = passGroup.extent.w;"),
                path: cullArgs
            );

            var broken = Reload(
                context: in context,
                node: node,
                root: root
            );

            Assert.Equal(expected: ("failed", 2L), actual: (broken.State, broken.Generation));
            Assert.Contains(actualString: broken.Error, expectedSubstring: $"{Path.GetFileName(path: cullArgs)}:5:");
            Assert.True(condition: node.Produce(context: in context));
        } finally {
            Directory.Delete(
                path: root,
                recursive: true
            );
        }
    }

    // A cull-args kernel that reads this host's world interface and runs one statement, on its fifth line.
    private static string CullArgsSource(string body) =>
        $$"""
        #include "{{RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory).Replace(newChar: '/', oldChar: '\\')}}/isa/sdf-world.interface.hlsli"

        [numthreads(1, 1, 1)]
        void CSMain() {
            {{body}}
        }
        """;
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
    // Requests a reload of the tree and produces frames until it finishes.
    private static SdfShaderReloadStatus Reload(in FrameContext context, SdfWorldResidency node, string root) {
        var copy = context;

        Assert.True(condition: node.RequestShaderReload(
            compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: Path.GetTempPath(), path2: "puck-test-shader-cache")),
            tree: root
        ));
        SdfTestPipelines.ProduceUntil(
            frame: () => {
                _ = node.Produce(context: in copy);

                return (node.ShaderReloadStatus.State != "pending");
            },
            reason: () => $"the reload is still {node.ShaderReloadStatus.State}",
            wait: node.WaitPipelineBuilds
        );

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
