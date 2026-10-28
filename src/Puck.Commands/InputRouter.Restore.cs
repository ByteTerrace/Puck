namespace Puck.Commands;

public sealed partial class InputRouter {
    /// <summary>Drops input belonging to a replaced timeline, without synthesizing releases into its checkpoint.</summary>
    /// <remarks>The host calls this between snapshots, with device capture stopped. The pump's monotonic clock remains.</remarks>
    public void ResetTimeline() {
        var abandoned = new List<CapturedInjection>();

        lock (m_captureGate) {
            m_capturedInjections.DrainAll(items: abandoned);
            m_capturedSignals.Clear();
            m_pendingInjections.Clear();
            m_sequence = 0;
        }
        foreach (var captured in abandoned) { RefuseInjection(injection: captured.Injection, reason: "the input timeline was restored"); }
        m_freeHeldStates.Clear();
        m_heldBySlot.Clear();
        m_lastInputTickBySlot.Clear();
        m_pressedControls.Clear();
        m_toggleLatches.Clear();
        m_workingBySlot.Clear();
        m_sustained.Clear();
        m_modalityBySlot.Clear();
        m_activation = 0;
        m_simulationOffset = null;
        m_bindings.ResetAll();
    }
}
