using System.Runtime.InteropServices;
using Puck.Abstractions.Collections;
using Puck.Hosting;

namespace Puck.Shaders;

// The frame's readers. The packages' frame start (PackagesBeginFrame) serves every package's upkeep and its host's
// capture, which places the cameras and windows the frame's roots and footprints name. The runtime then seals those
// lists (RenderGraphFrame.Seal) and prepares only what the frame reads (IRenderGraphPackageFactory.PrepareRead): the
// instances a schedule of the frame could render (RenderGraphScheduler.Readers), visited in turn, since a reader's
// capture can name a reader of its own (a routed scene fitting its windows), until a round prepares nothing new. An
// instance nothing reads is not prepared, and its package is not asked whether it is unchanged (CollectUnchanged).
//
// Invariant: every instance the frame's schedule renders was marked read and prepared here. It holds because the marks
// are RenderGraphScheduler.Readers over the sealed lists, which follows every edge Schedule passes demand along, and
// Schedule reads the same sealed lists. What it does not cover: a graph that installs mid-frame records against a
// package the walk never prepared, which the package prepares on that recording (SdfWorldResidency.Staged counts it).
public sealed partial class RenderGraphRuntime {
    // Whether each instance is read this frame, the read ordinals in render order, their sort keys, and the ordinals already prepared.
    private bool[] m_read = [];
    private int[] m_readOrderKeys = [];
    private readonly List<int> m_readOrder = [];
    private readonly MarkedIndexSet m_readPrepared = new();

    // Seals the frame's lists and prepares the package reads of every instance the frame reads, in turn until none is new.
    private void PrepareReaders(in RenderGraphFrame frame, in FrameContext context) {
        var count = m_set.Instances.Count;

        if (m_read.Length != count) {
            m_read = new bool[count];
            m_readOrderKeys = new int[count];
        }
        m_readPrepared.Clear();
        var scratchSchedule = m_schedules[m_turn];

        for (var preparing = true; preparing;) {
            frame.Seal?.Invoke();
            RenderGraphScheduler.ReadersInto(
                frame: in frame,
                reached: m_read,
                reachedIndices: m_readOrder,
                schedule: scratchSchedule,
                set: m_set
            );
            var readCount = m_readOrder.Count;

            if (readCount > 1) {
                var items = CollectionsMarshal.AsSpan(list: m_readOrder);
                var keys = m_readOrderKeys.AsSpan(start: 0, length: readCount);

                for (var position = 0; (position < readCount); position++) {
                    keys[position] = m_orderPosition[items[position]];
                }
                keys.Sort(items: items);
            }
            preparing = false;
            for (var position = 0; (position < readCount); position++) {
                var index = m_readOrder[position];

                if (!m_readPrepared.Add(index: index)) { continue; }
                preparing |= PrepareRead(context: in context, index: index);
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
