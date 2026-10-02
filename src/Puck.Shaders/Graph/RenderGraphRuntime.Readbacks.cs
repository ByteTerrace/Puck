namespace Puck.Shaders;

// The instances whose nodes owe a readback (ShaderPipelineRenderNode.OwesReadbacks), each listed once in
// m_owing[..m_owingCount] and flagged in m_owes, both sized with the nodes, so a frame polls only nodes with work
// outstanding and allocates nothing.
public sealed partial class RenderGraphRuntime {
    private int[] m_owing = [];
    private int m_owingCount;
    private bool[] m_owes = [];
    private long m_readbackPolls;

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
