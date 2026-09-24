using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The row ordinals whose stored values moved since the last delivery, each once, and whether the arena was rebuilt
    // so the moved rows cannot be named. Filled wherever the document and the arena come to agree (MarkPublished).
    private int[] m_movedRows = [];
    private bool[] m_movedRowNoted = [];
    private int m_movedRowCount;
    private bool m_movedEverything;

    /// <summary>Returns the stamp the next state delivery carries: the tick the step in progress produces, the engine
    /// tick it ends at, and the rows whose values moved since the last delivery.</summary>
    /// <returns>The stamp, whose moved rows alias this server's buffer until <see cref="ForgetMovedRows"/>.</returns>
    internal WorldStateStamp TakeStateStamp() => new(
        EngineTick: CompletedEngineTicks,
        Everything: m_movedEverything,
        MovedRows: m_movedRows.AsMemory(
            length: m_movedRowCount,
            start: 0
        ),
        Tick: (CompletedTick + 1UL)
    );
    /// <summary>Clears the moved rows once a delivery has carried them.</summary>
    internal void ForgetMovedRows() {
        for (var index = 0; (index < m_movedRowCount); index++) {
            m_movedRowNoted[m_movedRows[index]] = false;
        }

        m_movedRowCount = 0;
        m_movedEverything = false;
    }

    private void NoteMovedRow(int ordinal, int rowCount) {
        if (m_movedRowNoted.Length < rowCount) {
            Array.Resize(
                array: ref m_movedRowNoted,
                newSize: rowCount
            );
            Array.Resize(
                array: ref m_movedRows,
                newSize: rowCount
            );
        }

        if (m_movedRowNoted[ordinal]) {
            return;
        }

        m_movedRowNoted[ordinal] = true;
        m_movedRows[m_movedRowCount++] = ordinal;
    }
}
