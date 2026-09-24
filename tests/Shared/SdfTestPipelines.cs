using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Xunit;

namespace Puck.Testing;

// How a harness gets an engine's pipelines: a fake kernel set, a set built inline for an engine it drives directly,
// or the frames a node produces while its build runs on the thread pool.
internal static class SdfTestPipelines {
    // Builds a set on the calling thread, counting into the ledger the engine will count into.
    public static SdfWorldPipelines Build(IGpuComputeServices gpu, IGpuDeviceContext device, SdfWorldKernels kernels, GpuWorkLedger ledger) =>
        SdfWorldPipelines.Build(
            cancellationToken: CancellationToken.None,
            device: device,
            gpu: gpu,
            includeBrickPipelines: false,
            kernels: kernels,
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
            BrickUpload: ReadOnlyMemory<byte>.Empty,
            Composite: code,
            CullArgs: code,
            FrameUpload: code,
            InstanceCull: code,
            Primary: code,
            Sky: code,
            Surface: code,
            Views: code,
            ViewsCore: code,
            ViewsFolds: code
        );
    }
    // Produces frames until the node's pipeline build has installed its engine, and returns the surface of that first
    // real frame. The bound is liveness for a build over a fake device; it decides nothing.
    public static Surface ProduceFirstFrame(this SdfEngineNode node, in FrameContext context) {
        var surface = default(Surface);
        var copy = context;

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                surface = node.ProduceFrame(context: in copy);

                return node.IsReady;
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

        return surface;
    }
}
