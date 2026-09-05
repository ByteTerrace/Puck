namespace Puck.AdvancedGamingBrick;

// Derived readiness shared by one bus and its built-in timer/interrupt controllers. Hardware mutations publish
// barriers here, so ordinary accesses need not re-query each controller or repeatedly schedule settled timers.
// The scheduler's deadline is still checked at every charge. This cache carries no serialized hardware state.
internal sealed class AgbClockState {
    private const int TimerBarrier = 1;
    private const int InterruptBarrier = 2;
    private int m_barriers = (TimerBarrier | InterruptBarrier);

    internal bool CanAdvance => m_barriers == 0;

    internal void SetTimers(bool scheduled, bool pending) {
        m_barriers = ((m_barriers & ~TimerBarrier) | ((scheduled && !pending) ? 0 : TimerBarrier));
    }

    internal void SetInterrupts(bool quiescent) {
        m_barriers = ((m_barriers & ~InterruptBarrier) | (quiescent ? 0 : InterruptBarrier));
    }
}
