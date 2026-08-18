namespace Puck.World.Server;

/// <summary>The cadence a hosted row's checkpoint is captured on — a representation/bookkeeping constant, never a
/// feel value: one minute of engine time, independent of any row's own simulation rate.</summary>
public static class WorldAuthorityCheckpointCadence {
    /// <summary>The number of engine ticks between cadence-armed capture requests (one minute).</summary>
    public const ulong EngineTicks = (60UL * Puck.Hosting.EngineTicks.PerSecond);
}
/// <summary>Counts master steps toward <see cref="WorldAuthorityCheckpointCadence.EngineTicks"/> and arms a capture
/// request the tick thread honours at the next boundary. Counted on the tick thread from master steps, independent
/// of any grain's own simulation rate — its arming decision is a pure function of the cumulative engine-tick total
/// <see cref="NoteMasterStep"/> has been fed, never of how many master steps, or what size, delivered that total, so
/// two silos stepping the same input at different master cadences arm at the same cumulative-tick milestones. A
/// request also arms immediately from <see cref="RequestNow"/> — an explicit <c>silo.checkpoint</c>, a row
/// retiring, or silo shutdown — independent of the cadence counter. Neither this counter nor
/// <see cref="WorldAuthorityCheckpointCadence"/> reads a wall clock, an RNG stream, or a float: cadence never enters
/// simulation state (falsifier F5), and this type carries none.</summary>
/// <remarks>This type only ARMS and reports; it never decides whether a capture may proceed right now (the row's own
/// pending-transfer slice must be empty at the capture point, §3.1) and never takes the row's <c>m_authorityGate</c>
/// itself. The caller checks <see cref="IsArmed"/> at a master boundary, captures when its own precondition holds,
/// and calls <see cref="Clear"/> only once the capture actually landed — an armed-but-deferred request (the pending
/// slice was non-empty) stays armed across the boundary it could not be honoured at, because the caller never
/// clears it.</remarks>
public sealed class WorldAuthorityCheckpointCadenceCounter {
    private ulong m_accumulatedTicks;
    private bool m_armed;

    /// <summary>Gets whether a capture request is currently armed, awaiting a boundary at which the caller's own
    /// capture precondition holds.</summary>
    public bool IsArmed => m_armed;

    /// <summary>Clears the armed request — the caller's signal that a capture actually landed (whether cadence-armed
    /// or explicitly requested) and the cadence accumulator restarts from zero at this point, not earlier.</summary>
    public void Clear() {
        m_armed = false;
        m_accumulatedTicks = 0UL;
    }
    /// <summary>Advances the counter by one master step's own engine-tick width, arming a capture request once the
    /// accumulated total reaches <see cref="WorldAuthorityCheckpointCadence.EngineTicks"/>. Idempotent while already
    /// armed: the accumulator keeps advancing (so a caller reading <see cref="IsArmed"/> late still sees it true),
    /// but arming itself is a one-way latch until <see cref="Clear"/> runs.</summary>
    /// <param name="stepTicks">The master step's own engine-tick width (<c>FixedStepContext.StepTicks</c>).</param>
    public void NoteMasterStep(ulong stepTicks) {
        m_accumulatedTicks += stepTicks;

        if (m_accumulatedTicks >= WorldAuthorityCheckpointCadence.EngineTicks) {
            m_armed = true;
        }
    }
    /// <summary>Arms a capture request immediately, outside the cadence — <c>silo.checkpoint</c>, a row retiring, or
    /// silo shutdown. Does not touch the cadence accumulator; <see cref="Clear"/> still resets it once the resulting
    /// capture lands, restarting the next cadence period from that point.</summary>
    public void RequestNow() => m_armed = true;
}
