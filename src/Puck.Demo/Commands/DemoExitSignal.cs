namespace Puck.Demo.Commands;

internal sealed class DemoExitSignal : IDemoExitSignal {
    private bool m_exitRequested;

    public void RequestExit() {
        m_exitRequested = true;
    }
    public bool TryConsumeExit() {
        if (!m_exitRequested) {
            return false;
        }

        m_exitRequested = false;
        return true;
    }
}
