using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// An instance nothing names any more gives its graph back. A frame's schedule marks an instance unnamed when the display
// does not reach it, its host does not name it (RenderGraphFrame.Named) and nothing named shows or reads it: a seat's view
// once its seat leaves, a pane once no layout slot places it. That is the one rule, whatever stopped the naming. An
// instance that is named but not shown this frame (a screen out of view, a parked camera, a view whose consumer waits) is
// unread or waiting, and keeps everything it has, so it shows its last image the moment it is shown again.
//
// The released instance keeps its node, its installed pipeline and the regions its host bound, and rebuilds at the extent
// it is demanded at the next frame something names and shows it, with fresh history. Until it has rendered again its
// readers bind a stand-in, as they do before an instance first renders.
public sealed partial class RenderGraphRuntime {
    // Releases the graph of every graph instance the schedule leaves unnamed that still holds one, once the device has
    // finished every submission that may read it: the instance's own and its consumers'. A source keeps its conversion
    // graph, sized by its descriptor rather than by demand, and an instance a capture waits on keeps its graph for the
    // capture.
    private void ReleaseUnnamed(RenderGraphSchedule schedule) {
        var drained = false;

        for (var index = 0; (index < m_nodes.Length); index++) {
            if (
                (schedule.Instances[index].Status != RenderGraphInstanceStatus.Unnamed) ||
                (m_sources[index] is not null) ||
                (m_nodes[index] is not { HoldsGraphObjects: true, PendingCapturePath: null } node)
            ) {
                continue;
            }

            if (!drained) {
                m_device.TryWaitIdle();
                drained = true;
            }

            node.ReleaseUnnamed();
            m_current[index] = Output.None;
            m_previous[index] = Output.None;
            m_producerTainted[index] = false;
            schedule.Next.Forget(index: index);
        }
    }
}
