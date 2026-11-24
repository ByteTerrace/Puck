using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// Hand-overs: a node rendered inside the frame's batch commits its frame (history, initialization, publication) while
// its submission is still deferred, and leaves an export's completion (the shared fence's signal and the reader's
// publication) to the runtime, so completing one never hands the open batch to the queue early. Once the batch is
// flushed, every such node settles against its own submission's hand-over (ShaderPipelineRenderNode.SettleHandOver).
public sealed partial class RenderGraphRuntime {
    // Rollback can happen at a split, at a slot wait, or during the final flush. Withdraw the runtime's
    // copy at the same door as the node's. A retired node can no longer publish for its old ordinal.
    private void ForgetAbandonedOutput(ShaderPipelineRenderNode node) {
        var index = Array.IndexOf(array: m_nodes, value: node);

        if (index < 0) { return; }
        m_outputs.Forget(index: index);
        m_historySucceeded[index] = false;
        MarkStale(index: index, reason: "the submission did not reach the device queue");
    }
    private void TrackHandOver(ShaderPipelineRenderNode node) {
        if (node.OwesSettlement) { m_settlementsOwed.Add(item: node); }
    }
    // Hands the frame's batch to the queue, then settles every node that rendered into it. Each node asks its own
    // submission whether it reached the queue, never the flush, since the batch may have gone over in several hand-overs,
    // any of which can fail apart from the flush. Every node is settled whatever fails, so no export write is left open
    // and no unrun frame's history stands.
    private void SettleHandOvers(IGpuQueueSubmitter queue) {
        var submitted = false;

        try {
            queue.FlushBatch();
            submitted = true;
        } finally {
            ExceptionDispatchInfo? failure = null;

            foreach (var node in m_settlementsOwed) {
                try {
                    node.SettleHandOver();
                } catch (Exception error) {
                    failure ??= ExceptionDispatchInfo.Capture(source: error);
                }
            }

            m_settlementsOwed.Clear();

            // A failed flush is already propagating; its own failure is the frame's.
            if (submitted) { failure?.Throw(); }
        }
    }
}
