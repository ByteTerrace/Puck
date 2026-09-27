using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Xunit;

namespace Puck.Testing;

// How a harness gets an engine's pipelines: a fake kernel set, a set built inline for an engine it drives directly,
// or the frames a node produces while its build runs on the thread pool.
internal static class SdfTestPipelines {
    // Builds a set on the calling thread, counting into the ledger the engine will count into.
    public static SdfWorldPipelines Build(IGpuDeviceContext device, SdfWorldKernels kernels, GpuWorkLedger ledger) =>
        SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: device,
            includeBrickPipelines: false,
            kernels: kernels,
            ledger: ledger
        );
    // A composition's pipeline cache whose region-copy pipelines are created from a one-byte kernel of the caller's
    // choosing (UploadModelGpu.RegionCopyBytecode for a GPU that runs the copies), and whose mesh pass pipelines from
    // one-byte stages.
    public static SdfWorldPipelineCache Cache(byte regionCopy = 1) {
        var pipelines = new GpuPassPipelineCache();

        return new(
            meshRaster: new SdfMeshRasterPass(
                fragment: new byte[] { 1 },
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
    // A kernel set whose every kernel is one byte, the beam's chosen by the caller so two sets can differ by one kernel,
    // and whose brick kernels are empty, so no set built from it has brick pipelines.
    public static SdfWorldKernels Kernels(byte beam = 1) {
        ReadOnlyMemory<byte> code = new byte[] { 1 };

        return new SdfWorldKernels(
            Ambient: code,
            Beam: new byte[] { beam },
            BrickBake: ReadOnlyMemory<byte>.Empty,
            CullArgs: code,
            InstanceCull: code,
            Primary: code,
            Sky: code,
            Surface: code,
            Views: code,
            ViewsCore: code,
            ViewsFolds: code
        );
    }
    // Produces frames until the residency's pipeline build has built its tables, then submits that frame's upload, as a
    // view's first pass of the frame does. The bound is liveness for a build over a fake device; it decides nothing.
    public static void ProduceFirstFrame(this SdfWorldResidency residency, in FrameContext context) {
        var copy = context;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                residency.BeginFrame();

                return residency.Prepare(context: in copy);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ), userMessage: residency.NotReadyReason);
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
