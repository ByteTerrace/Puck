using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// An instance nothing reads any more gives its graph back. A frame's schedule marks an instance unread when neither the
// display nor any instance rendering that frame shows or reads it: no layout places it, no seat views through it and no
// reader names it. That is the one rule, whatever stopped the demand. A frame that only skips an instance, because its
// refresh is not due, the budget defers it or its readers wait, keeps everything it has.
//
// The released instance keeps its node, its installed pipeline and the regions its host bound, and rebuilds at the extent
// it is demanded at the next frame something shows it, with fresh history. Until it has rendered again its readers bind a
// stand-in, as they do before an instance first renders.
public sealed partial class RenderGraphRuntime {
    // Releases the graph of every graph instance the schedule leaves unread that still holds one, once the device has
    // finished every submission that may read it: the instance's own and its consumers'. A source keeps its conversion
    // graph, sized by its descriptor rather than by demand, and an instance a capture waits on keeps its graph for the
    // capture.
    private void ReleaseUnread(RenderGraphSchedule schedule) {
        var drained = false;

        for (var index = 0; (index < m_nodes.Length); index++) {
            if (
                (schedule.Instances[index].Status != RenderGraphInstanceStatus.Unread) ||
                (m_sources[index] is not null) ||
                (m_nodes[index] is not { HoldsGraphObjects: true, PendingCapturePath: null } node)
            ) {
                continue;
            }

            if (!drained) {
                m_device.TryWaitIdle();
                drained = true;
            }

            node.ReleaseUnread();
            m_current[index] = Output.None;
            m_previous[index] = Output.None;
            m_producerTainted[index] = false;
            schedule.Next.Forget(index: index);
        }
    }
}
