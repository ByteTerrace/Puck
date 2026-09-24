namespace Puck.SdfVm;

/// <summary>
/// Tracks the last use of each <see cref="SdfFrameBuffer"/> within one command list and reports the transitions each
/// entering pass owes. Allocation-free after construction; <see cref="Reset"/> starts a new command list.
/// </summary>
public sealed class SdfFrameBufferHazards {
    private readonly SdfBufferAccess[] m_accesses = new SdfBufferAccess[BufferCount];
    private readonly SdfFramePass[] m_passes = new SdfFramePass[BufferCount];
    private readonly bool[] m_touched = new bool[BufferCount];

    private static int BufferCount => (((int)SdfFrameBuffer.PrimaryHits) + 1);

    /// <summary>Forgets every use, as a new command list begins.</summary>
    public void Reset() => Array.Clear(array: m_touched);
    /// <summary>Records <paramref name="pass"/>'s uses and writes the transitions they owe to <paramref name="edges"/>.</summary>
    /// <param name="pass">The dispatch about to be recorded.</param>
    /// <param name="edges">Receives the owed transitions; at least <see cref="SdfFrameBufferPlan.MaxUsesPerPass"/> long.</param>
    /// <returns>The number of transitions written.</returns>
    public int Enter(SdfFramePass pass, Span<SdfBufferEdge> edges) {
        var count = 0;

        foreach (var use in SdfFrameBufferPlan.Uses(pass: pass)) {
            var index = ((int)use.Buffer);

            if (
                m_touched[index] &&
                SdfFrameBufferPlan.NeedsTransition(
                    after: use.Access,
                    before: m_accesses[index]
                )
            ) {
                edges[count++] = new SdfBufferEdge(
                    After: use.Access,
                    Before: m_accesses[index],
                    Buffer: use.Buffer,
                    Consumer: pass,
                    Producer: m_passes[index]
                );
            }

            m_accesses[index] = use.Access;
            m_passes[index] = pass;
            m_touched[index] = true;
        }

        return count;
    }
}
