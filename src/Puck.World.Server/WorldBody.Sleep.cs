namespace Puck.World.Server;

/// <summary>
/// A non-rigid body's idle floor: once its motion program has produced no motion and it has received no intent for
/// the authored <c>bodies.sleepAfterTicks</c> engine ticks, <see cref="WorldPopulation.AdvanceSimulated"/> stops
/// staging and advancing it outright — no motion program, no contact solve — until something wakes it. A rigid or
/// carried body never reaches this: both already return from <see cref="Advance"/> before <see cref="Advance"/>'s
/// own sleep bookkeeping runs, and a rigid kit's rest latch (<see cref="Resting"/>) is a distinct, older mechanism.
/// </summary>
public sealed partial class WorldBody {
    // 0 while awake; otherwise the simulation tick this body fell asleep at (see AsleepSinceTick's own read-back
    // contract). sleepAfterTicks is validated non-negative and this only ever gets a value at least that large, so
    // 0 as the sentinel never collides with a genuine sleep tick.
    private ulong m_asleepSinceTick;
    // Consecutive engine ticks (stepTicks accumulated) this body has produced no motion and received no intent —
    // reset to 0 the instant either happens. Meaningless once m_asleepSinceTick is set.
    private ulong m_sleepIdleTicks;
    // The population's ContactFieldVersion as of this body's last Advance (ordinary or deferred) — compared on the
    // next tick to decide whether the surface it sleeps against could now answer differently.
    private ulong m_lastContactFieldVersion;

    /// <summary>Gets a value indicating whether this body is asleep.</summary>
    public bool Asleep => (m_asleepSinceTick != 0UL);
    /// <summary>Gets the simulation tick this body fell asleep at, or 0 while awake — the <c>body.where</c> read-back.</summary>
    public ulong AsleepSinceTick => m_asleepSinceTick;

    /// <summary>Wakes this body immediately, clearing both the sleep latch and the idle floor it would otherwise
    /// resume counting from. Every path that can make this body's program or contact answer differently than the
    /// one it fell asleep under calls this: a hard teleport (<see cref="CommitTeleport"/>), a transfer
    /// (<see cref="ApplyTransferState"/>), an admission change resuming a parked body, or a targeted effect
    /// (<see cref="ApplyTargetedEffect"/>). A no-op while already awake.</summary>
    internal void WakeUp() {
        m_asleepSinceTick = 0UL;
        m_sleepIdleTicks = 0UL;
    }
    /// <summary>Returns <see langword="true"/> when this body is asleep and nothing this tick wakes it, so the
    /// caller may skip <see cref="Advance"/> outright. Wakes (and returns <see langword="false"/>) on a changed
    /// <paramref name="contactFieldVersion"/> or a tape/submitted/producer intent staged for this tick — every other
    /// wake path (<see cref="WakeUp"/>) already cleared the latch before this runs.</summary>
    /// <param name="contactFieldVersion">The population's current <see cref="WorldPopulation.ContactFieldVersion"/>.</param>
    internal bool TryDeferSleepingAdvance(ulong contactFieldVersion) {
        if (m_asleepSinceTick == 0UL) {
            return false;
        }

        if (
            (contactFieldVersion != m_lastContactFieldVersion) ||
            m_hasSubmittedIntent ||
            m_hasProducerIntent ||
            (m_tapeCount > 0)
        ) {
            WakeUp();
            m_lastContactFieldVersion = contactFieldVersion;

            return false;
        }

        m_lastContactFieldVersion = contactFieldVersion;

        return true;
    }
    // Folds one ordinary Advance's outcome into the idle floor. Called at the end of Advance, after every effect the
    // program could have fired this tick, so "moved" reflects where the body actually ended up.
    private void UpdateSleepEligibility(ulong tick, ulong stepTicks, ulong sleepAfterTicks, ulong contactFieldVersion, bool hadIncomingIntent, bool moved) {
        m_lastContactFieldVersion = contactFieldVersion;

        if (
            (sleepAfterTicks == 0UL) ||
            hadIncomingIntent ||
            moved
        ) {
            m_sleepIdleTicks = 0UL;

            return;
        }

        m_sleepIdleTicks += stepTicks;

        if (m_sleepIdleTicks >= sleepAfterTicks) {
            m_asleepSinceTick = tick;
        }
    }
}
