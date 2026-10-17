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
// readers bind a stand-in, as they do before an instance first renders. An output of another instance that stands for its
// output (a package that drew nothing) resolves to nothing with it, and that instance renders again over the stand-in in
// the same frame (RenderGraphRuntime.Standing.cs).
public sealed partial class RenderGraphRuntime {
    // Releases the graph of every graph instance the schedule leaves unnamed that still holds one. A source keeps its
    // conversion graph, sized by its descriptor rather than by demand, and an instance a capture waits on keeps its graph
    // for the capture, as does every instance its latest output stands for through a chain, which the capture reads.
    //
    // The instance leaves at once: its readers bind a stand-in from this frame, and its history is forgotten. Its graph
    // is released at once when no submission is in flight, and otherwise only once a submission ordered after every one
    // that may read it has completed, the instance's own and its consumers' (RenderGraphRuntime.Retiring.cs), and only if
    // it is still unnamed and has rendered nothing since; an instance named again first renders again with the graph it
    // kept.
    private void ReleaseUnnamed(RenderGraphSchedule schedule) {
        var captured = CapturedInstance();
        bool? idle = null;

        m_released.Clear();
        ForgetRenamed(schedule: schedule);

        for (var index = 0; (index < m_nodes.Length); index++) {
            if (
                (schedule.Instances[index].Status != RenderGraphInstanceStatus.Unnamed) ||
                KeepsCapturedOutput(captured: captured, index: index) ||
                (m_sources[index] is not null) ||
                (m_nodes[index] is not { HoldsGraphObjects: true, PendingCapturePath: null } node)
            ) {
                continue;
            }

            if (m_unnamed.TryGetValue(key: node, value: out var waiting)) {
                if (!waiting.Drained) {
                    continue;
                }

                _ = m_unnamed.Remove(key: node);

                if (node.SubmissionCount == waiting.Submissions) {
                    ReleaseGraph(index: index, node: node);
                }

                continue;
            }

            // With nothing in flight, nothing can still read the graph, so it goes at once.
            idle ??= NothingInFlight();

            if (idle.Value) {
                ReleaseGraph(index: index, node: node);
            } else {
                m_unnamed.Add(key: node, value: new Retiring(instance: m_set.Instances[index].Name, node: node, producer: null));
            }

            m_current[index] = Output.None;
            m_previous[index] = Output.None;
            m_producerTainted[index] = false;
            schedule.Next.Forget(index: index);
            m_released.Add(item: index);
        }
    }
    // Releases an unnamed instance's graph and tells every package its instance gave its graph back.
    private void ReleaseGraph(int index, ShaderPipelineRenderNode node) {
        node.ReleaseUnnamed();

        var factories = m_packages.Factories;

        for (var factory = 0; (factory < factories.Count); factory++) {
            factories[factory].OnGraphReleased(instance: m_set.Instances[index].Name);
        }
    }
    // Forgets the waiting release of every node the frame names again or the runtime no longer runs: a named node keeps
    // its graph and renders with it, and a retired one is disposed whole.
    private void ForgetRenamed(RenderGraphSchedule schedule) {
        if (m_unnamed.Count == 0) {
            return;
        }

        List<ShaderPipelineRenderNode>? forgotten = null;

        foreach (var node in m_unnamed.Keys) {
            var index = Array.IndexOf(array: m_nodes, value: node);

            if (
                (index < 0) ||
                (schedule.Instances[index].Status != RenderGraphInstanceStatus.Unnamed)
            ) {
                (forgotten ??= []).Add(item: node);
            }
        }

        foreach (var node in (forgotten ?? [])) {
            _ = m_unnamed.Remove(key: node);
        }
    }
    // A pending capture keeps the instance it reads and the complete chain its current output stands for. Bound the
    // walk because previous-frame graph reads can close a loop of standing outputs.
    private bool KeepsCapturedOutput(int captured, int index) {
        for (var depth = 0; ((captured >= 0) && (depth <= m_nodes.Length)); depth++) {
            if (captured == index) {
                return true;
            }

            captured = m_current[captured].StandsFor.Producer;
        }

        return false;
    }
}
