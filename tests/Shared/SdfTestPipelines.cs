using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.Testing;

// How a harness gets an engine's pipelines: a kernel set, a set leased and waited for inline for an engine it drives
// directly, or the frames a node produces while its build runs on the thread pool.
internal static class SdfTestPipelines {
    // The build's own SPIR-V kernels, deployed beside the test assembly, read once.
    private static readonly Lazy<ReadOnlyMemory<byte>[]> Compiled = new(valueFactory: static () => [.. SdfKernelSet.Kernels.Select(selector: static kernel => ((ReadOnlyMemory<byte>)File.ReadAllBytes(path: Path.Combine(
        path1: SdfKernelSet.DefaultDirectory,
        path2: $"{SdfKernelSet.StemOf(kernel: kernel)}.comp.spv"
    ))))]);

    // Leases a set from a pass-pipeline cache and blocks the calling thread until it is built; the cache counts what it
    // creates.
    public static SdfWorldPipelines Build(IGpuDeviceContext device, SdfKernelSet kernels, GpuPassPipelineCache cache, bool includeBrickPipelines = false) {
        var set = SdfWorldPipelines.Acquire(
            cache: cache,
            device: device,
            includeBrickPipelines: includeBrickPipelines,
            kernels: kernels
        );

        try {
            set.WaitAsync(cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

            return set;
        } catch {
            set.Dispose();

            throw;
        }
    }
    // A composition's pipeline catalog whose region-copy pipelines are created from a one-byte kernel of the caller's
    // choosing (UploadModelGpu.RegionCopyBytecode for a GPU that runs the copies), and whose mesh pass pipelines from
    // one-byte stages.
    public static SdfWorldPipelineCatalog Cache(byte regionCopy = 1) {
        var pipelines = new GpuPassPipelineCache();

        return new(
            meshRaster: new SdfMeshRasterPass(
                fragment: new byte[] { 1 },
                impostorFragment: new byte[] { 1 },
                pipelines: pipelines,
                vertex: new byte[] { 1 }
            ),
            regionCopy: new GpuRegionCopyPass(
                kernel: new byte[] { regionCopy },
                pipelines: pipelines
            )
        );
    }
    // Builds a region-copy pipeline on the calling thread for an engine a harness drives directly, counting into the
    // ledger the engine will count into; the engine records with its Compute.
    public static GpuPassPipeline RegionCopy(IGpuDeviceContext device, GpuWorkLedger ledger, byte kernel = 1) =>
        GpuPassPipelineCache.Build(
            device: device,
            key: GpuRegionCopyPass.KeyOf(kernel: new byte[] { kernel }),
            ledger: ledger
        );
    // Builds a mesh pass pipeline from one-byte stages on the calling thread for an engine a harness drives directly,
    // counting into the ledger the engine will count into.
    public static GpuPassPipeline MeshRaster(IGpuDeviceContext device, GpuWorkLedger ledger) =>
        GpuPassPipelineCache.Build(
            device: device,
            key: SdfMeshRasterPass.KeyOf(
                fragment: new byte[] { 1 },
                vertex: new byte[] { 1 }
            ),
            ledger: ledger
        );
    // Builds the impostor card pipeline from one-byte stages on the calling thread, as MeshRaster builds the mesh pass's.
    public static GpuPassPipeline ImpostorRaster(IGpuDeviceContext device, GpuWorkLedger ledger) =>
        GpuPassPipelineCache.Build(
            device: device,
            key: SdfMeshRasterPass.ImpostorKeyOf(
                fragment: new byte[] { 1 },
                vertex: new byte[] { 1 }
            ),
            ledger: ledger
        );
    // The build's own SPIR-V kernel set, whose beam kernel's generator word the caller chooses so two sets can differ by
    // one kernel whose bindings are the same, and whose brick kernel is empty, so no set built from it has brick pipelines.
    // Every kernel reads the host's interface, so a reload of it passes the interface check.
    public static SdfKernelSet Kernels(byte beam = 1) =>
        new(bytecode: [.. SdfKernelSet.Kernels.Select(selector: kernel => kernel switch {
            SdfKernel.Beam => SpirvEdits.WithGenerator(generator: beam, module: Compiled.Value[((int)kernel)].Span),
            SdfKernel.BrickBake => ReadOnlyMemory<byte>.Empty,
            _ => Compiled.Value[((int)kernel)],
        })]);
    // A reflector for a reload's interface check; the kernels a harness reloads are SPIR-V, which needs no tool.
    public static ShaderBytecodeReflector Reflector() =>
        new(toolchain: new ShaderToolchain());
    // Produces frames until the residency's pipeline build has built its tables, then submits that frame's upload, as a
    // view's first pass of the frame does.
    public static void ProduceFirstFrame(this SdfWorldResidency residency, in FrameContext context) {
        var copy = context;

        TestLiveness.Until(
            step: () => {
                residency.BeginFrame();

                return residency.Prepare(context: in copy);
            },
            reason: () => residency.NotReadyReason,
            wait: residency.WaitPipelineBuilds
        );
        _ = residency.Submit(context: in context);
    }
    // Produces one frame of a ready residency: captures and packs it and submits its upload, as a view's first pass of the
    // frame does.
    public static void ProduceFrame(this SdfWorldResidency residency, in FrameContext context) {
        residency.BeginFrame();
        _ = residency.Submit(context: in context);
    }
    // Produces one frame when the residency can: starts it, captures and packs it, and submits its upload once its tables
    // exist, returning whether it did.
    public static bool Produce(this SdfWorldResidency residency, in FrameContext context) {
        residency.BeginFrame();

        if (!residency.Prepare(context: in context)) {
            return false;
        }

        _ = residency.Submit(context: in context);

        return true;
    }
}
