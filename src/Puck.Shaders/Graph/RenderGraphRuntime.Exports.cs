using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// Exports: a node that writes an image another device reads leaves its completion (the shared fence's signal and the
// reader's publication) to the runtime, which makes it once the frame's batch has reached the queue
// (ShaderPipelineRenderNode.CompleteOwedExport), so completing one never hands the open batch to the queue early.
public sealed partial class RenderGraphRuntime {
    // Hands the frame's batch to the queue, then completes every export the frame wrote. Every write a node began is ended
    // whatever fails: a batch that did not reach the queue ends its writes unwritten, and a completion that fails does
    // not leave a later node's write open.
    private void CompleteOwedExports(IGpuQueueSubmitter queue) {
        var submitted = false;

        try {
            queue.FlushBatch();
            submitted = true;
        } finally {
            ExceptionDispatchInfo? failure = null;

            foreach (var node in m_exportsOwed) {
                try {
                    node.CompleteOwedExport(submitted: submitted);
                } catch (Exception error) {
                    failure ??= ExceptionDispatchInfo.Capture(source: error);
                }
            }

            m_exportsOwed.Clear();

            // A failed flush is already propagating; its own failure is the frame's.
            if (submitted) { failure?.Throw(); }
        }
    }
}
