namespace Puck.Shaders;

// The renders this node found complete since the summary was last taken. Completions follow submission order on the one
// queue, so the span from the last folded submission to the work ledger's newest completed one is exactly what completed
// since; each render's grid comes from the record NoteRenderGrid keeps.
public sealed partial class ShaderPipelineRenderNode {
    private ShaderPipelineCompletions m_completions;
    private long m_foldedSubmission;

    /// <summary>Gets whether a submission's work counters or timestamps are still to be read back: a sealed submission
    /// waits on its fence, or a timed frame slot holds a fence or a pool to retire. A node that owes nothing has nothing
    /// for <see cref="PollReadbacks"/> to read.</summary>
    public bool OwesReadbacks => (!m_disposed && (m_work.HasPending || TimingPending));

    /// <summary>Reads and clears the summary of the renders this node found complete since the previous read: how many,
    /// and the one grid every one of them recorded, or zero when they recorded different grids. Every render in the span
    /// counts, not only the newest, so a render at another grid between two reads is never hidden by a later one. A
    /// submission that rendered nothing (a republished image) is no render; a render whose grid is no longer recorded
    /// names no grid.</summary>
    /// <returns>The completions since the previous read.</returns>
    public ShaderPipelineCompletions TakeCompletions() {
        FoldCompletions();

        var completions = m_completions;

        m_completions = default;

        return completions;
    }

    private void FoldCompletions() {
        var completed = m_work.CompletedSubmission;

        if (completed <= m_foldedSubmission) {
            return;
        }

        var length = m_renderGrids.Length;
        var first = (m_foldedSubmission + 1L);

        m_foldedSubmission = completed;

        // A node that never rendered completed no render.
        if (length == 0) {
            return;
        }
        // Past the record's reach a render's grid is unknown, so the span names no grid.
        if ((completed - first) >= length) {
            m_completions = m_completions.Then(later: new ShaderPipelineCompletions(Grid: 0d, Renders: 1));
            first = ((completed - length) + 1L);
        }

        for (var submission = first; (submission <= completed); submission++) {
            var entry = m_renderGrids[((int)(submission % length))];

            // An entry of an older submission means this one rendered nothing; a newer one, that its record was reused.
            if (entry.Submission == submission) {
                m_completions = m_completions.Then(later: new ShaderPipelineCompletions(Grid: entry.Grid, Renders: 1));
            } else if (entry.Submission > submission) {
                m_completions = m_completions.Then(later: new ShaderPipelineCompletions(Grid: 0d, Renders: 1));
            }
        }
    }
}
