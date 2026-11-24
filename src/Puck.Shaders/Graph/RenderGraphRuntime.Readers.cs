using Puck.Abstractions.Collections;
using Puck.Hosting;

namespace Puck.Shaders;

// The frame's readers. The packages' frame start (PackagesBeginFrame) serves every package's upkeep and its host's
// capture, which places the cameras and windows the frame's roots and footprints name. The runtime then seals those
// lists (RenderGraphFrame.Seal) and prepares packages of the instances that could render. A capture can place a
// new footprint, so the runtime asks the scheduler again until a round prepares nothing new.
//
// Reader discovery and scheduling share the same demand calculation. Once the footprints stand, unchanged inputs
// and the budget can only remove renders. A late graph install or a forced rerender after a release can add work;
// the runtime prepares scheduled packages before any view records in either case.
public sealed partial class RenderGraphRuntime {
    // The due ordinals in render order and the ordinals already prepared.
    private readonly List<int> m_readOrder = [];
    private readonly MarkedIndexSet m_readPrepared = new();

    // Seals the frame's lists and prepares the package reads of every instance the frame reads, in turn until none is new.
    private void PrepareReaders(in RenderGraphFrame frame, in FrameContext context) {
        m_readPrepared.Clear();
        var scratchSchedule = m_schedules[m_turn];

        for (var preparing = true; preparing;) {
            frame.Seal?.Invoke();
            RefreshConvergenceDemand(captured: m_captureInstance);
            var overlay = new RenderGraphOrdinalOverlay { Rerender = CollectRerenders(captured: CapturedInstance()), Stale = StaleOrdinals };

            RenderGraphScheduler.ReadersInto(
                frame: in frame,
                history: m_history,
                overlay: in overlay,
                readers: m_readOrder,
                schedule: scratchSchedule,
                set: m_set
            );
            preparing = false;
            for (var position = 0; (position < m_readOrder.Count); position++) {
                var index = m_readOrder[position];

                if (!m_readPrepared.Add(index: index)) { continue; }
                preparing |= PrepareRead(context: in context, index: index);
            }
            if (preparing) {
                var factories = m_packages.Factories;

                for (var position = 0; (position < factories.Count); position++) {
                    factories[position].ReadRoundPrepared(context: in context, jobs: m_jobs);
                }
            }
        }
    }
    // Asks each distinct factory of an instance's package passes, once, to prepare what the instance reads; false for
    // an instance that runs no package pass (an external producer, an uploaded source, a graph of shader passes alone).
    private bool PrepareRead(int index, in FrameContext context) {
        if ((m_producers[index] is not null) || (m_sources[index] is not null)) { return false; }

        var factories = m_instanceFactories[index];

        if (factories.Length == 0) { return false; }

        var instance = m_set.Instances[index].Name;

        for (var position = 0; (position < factories.Length); position++) {
            factories[position].PrepareRead(context: in context, instance: instance);
        }

        return true;
    }
}
