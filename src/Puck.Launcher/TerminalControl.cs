using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>The terminal's held capabilities, implemented by the one object that owns the window: the
/// <see cref="ITerminalControl"/> baton (lifecycle — the primary engine requests exit through it; the
/// window loop drains it via <see cref="TryConsumeExit"/>) and <see cref="IInputFocus"/> (the right to
/// receive the terminal's input; the loop routes input only while it is active). Exit is a one-shot latch;
/// input focus is a simple held/released flag.</summary>
internal sealed class TerminalControl : ITerminalControl, IInputFocus {
    private volatile bool m_exitRequested;
    private volatile bool m_inputReleased;

    /// <inheritdoc />
    public bool IsActive => !m_inputReleased;

    /// <inheritdoc />
    public void RequestExit() {
        m_exitRequested = true;
    }
    /// <summary>Consumes a pending exit request, returning whether one was set since the last call.</summary>
    public bool TryConsumeExit() {
        if (!m_exitRequested) {
            return false;
        }

        m_exitRequested = false;
        return true;
    }
    /// <inheritdoc />
    public void Release() {
        m_inputReleased = true;
    }
}
