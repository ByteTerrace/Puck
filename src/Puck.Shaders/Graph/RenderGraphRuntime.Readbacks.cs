namespace Puck.Shaders;

// The instances whose nodes owe a readback (ShaderPipelineRenderNode.OwesReadbacks), each listed once in
// m_owing[..m_owingCount] and flagged in m_owes, both sized with the nodes, so a frame polls only nodes with work
// outstanding and allocates nothing. A node that leaves the graph hands its completed renders to the runtime, by
// instance name, until a reader takes them: one disposed at its retirement has waited out its submissions, and one a
// kept consumer still holds stays listed in m_retiredOwing, polled like any other, until it owes nothing or is released.
public sealed partial class RenderGraphRuntime {
    private int[] m_owing = [];
    private int m_owingCount;
    private bool[] m_owes = [];
    private long m_readbackPolls;

    private readonly List<(string Instance, ShaderPipelineCompletions Completions)> m_retiredCompletions = [];
    private readonly List<(string Instance, ShaderPipelineRenderNode Node)> m_retiredOwing = [];

    private bool m_releasingLostDevice;

    /// <summary>Reads and clears the renders completed by nodes that have left the graph since the previous read, folded
    /// over the retired instances <paramref name="instance"/> selects
    /// (<see cref="ShaderPipelineCompletions.Then"/>). A node that leaves keeps handing its renders over until it has
    /// none in flight, across any number of reconfigurations, a held one included; a render is handed over once, so an
    /// instance added again under the same name counts its new node's renders only through that node.</summary>
    /// <param name="instance">Selects the retired instances, by name, whose completions are read; the others stay.</param>
    /// <returns>The selected retired instances' completions since the previous read.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="instance"/> is <see langword="null"/>.</exception>
    public ShaderPipelineCompletions TakeRetiredCompletions(Predicate<string> instance) {
        ArgumentNullException.ThrowIfNull(instance);

        var completions = default(ShaderPipelineCompletions);

        for (var index = (m_retiredCompletions.Count - 1); (index >= 0); index--) {
            var (name, retired) = m_retiredCompletions[index];

            if (instance(obj: name)) {
                completions = completions.Then(later: retired);
                m_retiredCompletions.RemoveAt(index: index);
            }
        }

        return completions;
    }

    /// <summary>Gets how many times the runtime has polled a node's readbacks between its renders
    /// (<see cref="ShaderPipelineRenderNode.PollReadbacks"/>): once a frame for each node that owes a completed
    /// submission's counters or timestamps, and never for one that owes nothing, however many instances stand.</summary>
    public long ReadbackPolls => m_readbackPolls;

    // Polls each node that owes a readback, before scheduling: a standing node produces no frame until its inputs
    // change, so this is where its last submissions are read back. A node that owes nothing more leaves the set.
    private void PollOwedReadbacks() {
        var kept = 0;

        for (var position = 0; (position < m_owingCount); position++) {
            var index = m_owing[position];

            if (m_nodes[index] is { } node) {
                node.PollReadbacks();
                m_readbackPolls++;

                if (node.OwesReadbacks) {
                    m_owing[kept++] = index;

                    continue;
                }
            }

            m_owes[index] = false;
        }

        m_owingCount = kept;

        for (var position = (m_retiredOwing.Count - 1); (position >= 0); position--) {
            var (instance, node) = m_retiredOwing[position];

            node.PollReadbacks();
            m_readbackPolls++;
            HandOver(instance: instance, node: node);

            if (!node.OwesReadbacks) {
                m_retiredOwing.RemoveAt(index: position);
            }
        }
    }
    // Takes a retired node's completed renders into the runtime's account under its instance name.
    private void HandOver(string instance, ShaderPipelineRenderNode node) {
        var completions = node.TakeCompletions();

        if (completions.Renders == 0) {
            return;
        }

        for (var index = 0; (index < m_retiredCompletions.Count); index++) {
            if (string.Equals(a: m_retiredCompletions[index].Instance, b: instance, comparisonType: StringComparison.Ordinal)) {
                m_retiredCompletions[index] = (instance, m_retiredCompletions[index].Completions.Then(later: completions));

                return;
            }
        }

        m_retiredCompletions.Add(item: (instance, completions));
    }
    // Retires a node that left the graph once the device is idle: a disposed node waits out its submissions first, so
    // every render it made is handed over; a held one hands over what has completed and stays polled while it owes more.
    private void RetireNode(string instance, ShaderPipelineRenderNode node, bool held) {
        if (held) {
            node.PollReadbacks();
            HandOver(instance: instance, node: node);

            if (node.OwesReadbacks) {
                m_retiredOwing.Add(item: (instance, node));
            }

            return;
        }

        node.Dispose();
        HandOver(instance: instance, node: node);
    }
    // Disposes a held retired node when its last hold is released, handing over what it completed. A device loss
    // releases it with renders that will never complete, so nothing more is read from it then.
    private void DisposeHeld(string instance, ShaderPipelineRenderNode node) {
        for (var position = (m_retiredOwing.Count - 1); (position >= 0); position--) {
            if (ReferenceEquals(objA: m_retiredOwing[position].Node, objB: node)) {
                m_retiredOwing.RemoveAt(index: position);
            }
        }
        if (!m_releasingLostDevice) {
            node.PollReadbacks();
            HandOver(instance: instance, node: node);
        }

        node.DisposeRetired();
    }
    // Lists an instance whose node has just produced, when it owes a readback and is not listed yet.
    private void NoteOwedReadbacks(int index) {
        if (m_owes[index] || (m_nodes[index] is not { OwesReadbacks: true })) {
            return;
        }

        m_owes[index] = true;
        m_owing[m_owingCount++] = index;
    }
    // Sizes the set to the nodes and lists every node that owes a readback: when the runtime is made and whenever it
    // runs another set of nodes.
    private void ResetOwedReadbacks() {
        m_owing = new int[m_nodes.Length];
        m_owes = new bool[m_nodes.Length];
        m_owingCount = 0;

        for (var index = 0; (index < m_nodes.Length); index++) {
            NoteOwedReadbacks(index: index);
        }
    }
}
