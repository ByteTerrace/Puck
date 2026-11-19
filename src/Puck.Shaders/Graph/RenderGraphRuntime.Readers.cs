using Puck.Hosting;

namespace Puck.Shaders;

// The frame's readers. The packages' frame start (PackagesBeginFrame) serves every package's upkeep and its host's
// capture, which places the cameras and windows the frame's roots and footprints name. The runtime then seals those
// lists (RenderGraphFrame.Seal) and prepares only what the frame reads (IRenderGraphPackageFactory.PrepareRead): the
// instances a schedule of the frame could render (RenderGraphScheduler.Readers), visited in turn, since a reader's
// capture can name a reader of its own (a routed scene fitting its windows), until a round prepares nothing new. An
// instance nothing reads is not prepared, and its package is not asked whether it is unchanged (WithUnchanged).
//
// Invariant: every instance the frame's schedule renders was marked read and prepared here. It holds because the marks
// are RenderGraphScheduler.Readers over the sealed lists, which follows every edge Schedule passes demand along, and
// Schedule reads the same sealed lists. What it does not cover: a graph that installs mid-frame records against a
// package the walk never prepared, which the package prepares on that recording (SdfWorldResidency.Staged counts it).
public sealed partial class RenderGraphRuntime {
    // Whether each instance is read this frame, and whether its package reads were prepared this frame.
    private bool[] m_read = [];
    private bool[] m_readPrepared = [];
    // The factories already asked to prepare the instance being visited, reused instance to instance.
    private readonly List<IRenderGraphPackageFactory> m_readFactories = [];

    // Seals the frame's lists and prepares the package reads of every instance the frame reads, in turn until none is new.
    private void PrepareReaders(in RenderGraphFrame frame, in FrameContext context) {
        var count = m_set.Instances.Count;

        if (m_read.Length != count) {
            m_read = new bool[count];
            m_readPrepared = new bool[count];
        }
        Array.Clear(array: m_readPrepared);
        for (var preparing = true; preparing;) {
            frame.Seal?.Invoke();
            RenderGraphScheduler.Readers(frame: in frame, reached: m_read, set: m_set);
            preparing = false;
            for (var position = 0; (position < m_set.Order.Count); position++) {
                var index = m_set.Order[position];

                if (!m_read[index] || m_readPrepared[index]) { continue; }
                m_readPrepared[index] = true;
                preparing |= PrepareRead(context: in context, index: index);
            }
        }
    }
    // Asks each factory of an instance's package passes, once, to prepare what the instance reads; false for an instance
    // that runs no package pass (an external producer, an uploaded source, a graph of shader passes alone).
    private bool PrepareRead(int index, in FrameContext context) {
        if ((m_producers[index] is not null) || (m_sources[index] is not null) || (m_graphs[index] is not { } graph)) { return false; }

        var instance = m_set.Instances[index].Name;

        FactoriesOf(graph: graph, into: m_readFactories);
        foreach (var factory in m_readFactories) { factory.PrepareRead(context: in context, instance: instance); }
        return (m_readFactories.Count > 0);
    }
    // Whether the frame reads an instance: false for one PrepareReaders left unmarked, whose package is not asked.
    private bool IsRead(int index) => ((index < m_read.Length) && m_read[index]);
}
