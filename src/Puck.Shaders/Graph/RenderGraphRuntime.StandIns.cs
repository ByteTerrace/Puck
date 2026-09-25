using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// What an image input binds while its producer has no completed output of the frame it reads: before the producer
// first renders, while its graph builds, and after a device loss. One transparent-black image per format, one texel,
// cleared once in a submission of its own that precedes every instance submission sampling it on the one queue, so a
// consumer renders on schedule and never waits for a producer. The stand-ins are the runtime's own and never reach a
// capture, which reads the root instance's output; they are released with the device like every instance's objects.
public sealed partial class RenderGraphRuntime {
    private readonly Dictionary<GpuPixelFormat, StandIn> m_standIns = [];

    private ShaderPipelineExternalImage StandInFor(GpuPixelFormat format) {
        if (!m_standIns.TryGetValue(
            key: format,
            value: out var standIn
        )) {
            standIn = StandIn.Create(
                format: format,
                gpu: m_device.Services
            );
            m_standIns.Add(
                key: format,
                value: standIn
            );
        }

        var image = standIn.Image;

        return new ShaderPipelineExternalImage(
            Format: format,
            Height: image.Height,
            ImageHandle: image.ImageHandle,
            ImageViewHandle: image.ImageViewHandle,
            Layout: GpuImageLayout.ShaderReadOnly,
            Width: image.Width
        );
    }
    // Waits out each clear before releasing its image, unless the device that ran it is gone.
    private void ReleaseStandIns(bool wait) {
        foreach (var standIn in m_standIns.Values) {
            standIn.Dispose(wait: wait);
        }

        m_standIns.Clear();
    }

    private sealed class StandIn(IGpuImage image, IGpuCommandPool pool, IGpuSubmissionFence fence) {
        private static readonly GpuObjectName StandInName = new(
            owner: "render-graph",
            part: "stand-in"
        );

        public IGpuImage Image { get; } = image;

        public static StandIn Create(GpuDeviceServices gpu, GpuPixelFormat format) {
            var image = gpu.ImageFactory.Create(
                format: format,
                height: 1,
                name: StandInName,
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: 1
            );
            IGpuCommandPool? pool = null;
            IGpuSubmissionFence? fence = null;

            try {
                pool = gpu.CommandPoolFactory.Create(name: StandInName);
                fence = gpu.QueueSubmitter.CreateSubmissionFence();

                var command = pool.CommandBufferHandle;
                var recorder = gpu.Recorder;
                var cleared = ShaderPipelineBarrier.Between(
                    kind: Hosting.ShaderPipelineResourceKind.Image,
                    prior: ShaderPipelineAccessState.Fresh,
                    use: ShaderPipelineAccessState.Cleared
                );
                var sampled = ShaderPipelineBarrier.Between(
                    kind: Hosting.ShaderPipelineResourceKind.Image,
                    prior: ShaderPipelineAccessState.Cleared,
                    use: ShaderPipelineAccessState.Handover(layout: GpuImageLayout.ShaderReadOnly)
                );

                recorder.BeginCommandBuffer(commandBufferHandle: command);
                Transition(
                    barrier: cleared,
                    command: command,
                    image: image.ImageHandle,
                    recorder: recorder
                );
                recorder.ClearStorageImage(
                    commandBufferHandle: command,
                    format: format,
                    imageHandle: image.ImageHandle
                );
                Transition(
                    barrier: sampled,
                    command: command,
                    image: image.ImageHandle,
                    recorder: recorder
                );
                recorder.EndCommandBuffer(commandBufferHandle: command);
                gpu.QueueSubmitter.Submit(
                    commandBufferHandles: [command],
                    fence: fence
                );

                return new StandIn(
                    fence: fence,
                    image: image,
                    pool: pool
                );
            } catch {
                fence?.Dispose();
                pool?.Dispose();
                image.Dispose();

                throw;
            }
        }
        public void Dispose(bool wait) {
            if (wait) {
                fence.Wait();
            }

            fence.Dispose();
            pool.Dispose();
            Image.Dispose();
        }

        private static void Transition(ShaderPipelineBarrier barrier, nint command, nint image, IGpuRecorder recorder) => recorder.TransitionImageLayout(
            commandBufferHandle: command,
            destinationAccessMask: barrier.DestinationAccess,
            destinationStageMask: barrier.DestinationStage,
            imageHandle: image,
            newLayout: barrier.NewLayout,
            oldLayout: barrier.OldLayout,
            sourceAccessMask: barrier.SourceAccess,
            sourceStageMask: barrier.SourceStage
        );
    }
}
