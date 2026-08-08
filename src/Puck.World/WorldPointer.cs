using System.Numerics;

using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Every local seat's live POINTER state — where the cursor is, how far it moved, which buttons are down, and how
/// far the wheel turned — one slot per <see cref="PlayerRoster.MaxSlots"/> entry. This is BROWSING state, the
/// pointer's twin of <see cref="WorldCameraOrbit"/>: session-only, never persisted, never authored on a document,
/// and never an input the deterministic simulation reads. A pointer act reaches the simulation only when a consumer
/// of this state dispatches an ordinary console verb, through the same door a typed line uses.
/// </summary>
/// <remarks>
/// <para>Written by <see cref="WorldPointerSink"/> alone (the window-pump thread) and read by any number of
/// consumers, so cross-thread safety is the only contract. Position and button state are plain
/// <see cref="Volatile"/> reads/writes on independent per-slot scalars — no lock, exactly as
/// <see cref="WorldCameraOrbit"/> reasons — while the two ACCUMULATORS (motion and wheel) are interlocked, because
/// each is a read-modify-write a concurrent producer could otherwise lose an increment from.</para>
/// <para>Motion and wheel DRAIN on read (<see cref="TakeMotion"/>, <see cref="TakeWheel"/>): they answer "what
/// happened since you last asked", so two consumers of the same seat's motion would each see part of it. That is
/// deliberate and is why a drained accumulator is not a general read-back — position and buttons, which every
/// consumer may read freely, are the non-destructive half.</para>
/// <para>The mouse carries no <see cref="Puck.Commands.InputDeviceId"/> of its own, so it rides whichever seat the
/// KEYBOARD currently owns; <see cref="WorldPointerSink"/> resolves that slot per event rather than caching it.</para>
/// </remarks>
internal sealed class WorldPointer {
    // 0=left, 1=right, 2=middle — the same index WindowInputEvent.PointerButton carries in Vector.X, held here as
    // one bit each so a consumer asks "is this button down" without a per-button array.
    private readonly int[] m_buttons = new int[PlayerRoster.MaxSlots];
    // Latched by the first SetPosition and never cleared: (0,0) is a legal cursor position, so "has the platform
    // ever reported one" needs its own bit rather than a sentinel value.
    private readonly int[] m_hasPosition = new int[PlayerRoster.MaxSlots];
    private readonly float[] m_motionX = new float[PlayerRoster.MaxSlots];
    private readonly float[] m_motionY = new float[PlayerRoster.MaxSlots];
    private readonly float[] m_positionX = new float[PlayerRoster.MaxSlots];
    private readonly float[] m_positionY = new float[PlayerRoster.MaxSlots];
    // Monotonic per-slot: see SystemReleaseCount's remarks for the consumer contract this exists for.
    private readonly int[] m_systemReleaseCount = new int[PlayerRoster.MaxSlots];
    private readonly float[] m_wheel = new float[PlayerRoster.MaxSlots];
    // 0 or 1: see SetWheelConsumerRegistered's remarks for why TakeWheel gates on this.
    private int m_wheelConsumerRegistered;
    // 0 until SetWheelConsumerRegistered runs — the once-only latch its exception documents.
    private int m_wheelConsumerDeclared;

    /// <summary>Gets a seat's last known absolute cursor position in client pixels, or
    /// <see cref="Vector2.Zero"/> before the platform has reported one.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <remarks>X and Y are independent <see cref="Volatile"/> scalars, so a read racing a concurrent
    /// <see cref="SetPosition"/> can observe a TORN pair — one axis from the new report, the other still from the
    /// prior one. Acceptable here: this is presentation-only browsing state (see the class remarks), the tear is at
    /// most one frame's worth of drift, and the very next read is consistent again — no consumer accumulates
    /// position across reads the way <see cref="TakeMotion"/>'s callers do.</remarks>
    public Vector2 Position(int slot) {
        return (InRange(slot: slot)
            ? new Vector2(x: Volatile.Read(location: ref m_positionX[slot]), y: Volatile.Read(location: ref m_positionY[slot]))
            : Vector2.Zero);
    }

    /// <summary>Indicates whether the platform has ever reported an absolute cursor position for a seat — the
    /// discriminator between <see cref="Position"/>'s pre-first-report <see cref="Vector2.Zero"/> and a cursor
    /// genuinely at the client origin.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public bool HasPosition(int slot) {
        return (InRange(slot: slot) && (Volatile.Read(location: ref m_hasPosition[slot]) != 0));
    }

    /// <summary>Indicates whether a seat currently holds a pointer button.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="button">The button index (0=left, 1=right, 2=middle).</param>
    public bool IsButtonDown(int slot, int button) {
        return (InRange(slot: slot) && ((uint)button < 3) && ((Volatile.Read(location: ref m_buttons[slot]) & (1 << button)) != 0));
    }

    /// <summary>Gets a seat's SYSTEM-RELEASE generation — a monotonic count of how many times this store has
    /// force-cleared the seat's held buttons WITHOUT a genuine release event (<see cref="ReleaseButtons"/>, and so
    /// <see cref="ReleaseAllButtons"/>'s two triggers: OS focus loss, a keyboard seat reassignment). Non-destructive,
    /// like <see cref="Position"/> and <see cref="IsButtonDown"/>.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <remarks>The consumer contract: capture this value when a press arms something, and compare it again when a
    /// later frame's diff observes the matching release edge. If the count advanced in between, that release is
    /// SYNTHETIC — the button state was force-cleared by the store, never genuinely lifted by the user — and an
    /// edge-deriving consumer (a drag-drop gesture reading held-state diffs, say) must treat it as a CANCEL, never a
    /// COMMIT: composing a real release-triggered action (a drop, at wherever the cursor happens to sit) onto a
    /// focus-loss or seat reassignment the user never intended as a release would author state they never asked
    /// for.</remarks>
    public int SystemReleaseCount(int slot) {
        return (InRange(slot: slot) ? Volatile.Read(location: ref m_systemReleaseCount[slot]) : 0);
    }

    /// <summary>Takes and clears a seat's accumulated relative motion in client pixels — everything the platform
    /// reported since the last call. DRAINS: a second call with no intervening motion answers
    /// <see cref="Vector2.Zero"/>.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public Vector2 TakeMotion(int slot) {
        return (InRange(slot: slot)
            ? new Vector2(x: Interlocked.Exchange(location1: ref m_motionX[slot], value: 0f), y: Interlocked.Exchange(location1: ref m_motionY[slot], value: 0f))
            : Vector2.Zero);
    }

    /// <summary>Takes and clears a seat's accumulated wheel rotation in notches (positive away from the user) —
    /// everything the platform reported since the last call. DRAINS, exactly as <see cref="TakeMotion"/> does.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <exception cref="InvalidOperationException">No <see cref="IWorldWheelConsumer"/> is registered. While that is
    /// true, <see cref="WorldPointerSink"/> drains this accumulator itself on arrival (see its <c>Observe</c>
    /// method's <c>PointerWheel</c> case), so a caller reaching this method would silently read zero forever rather
    /// than a real answer — implement <see cref="IWorldWheelConsumer"/> to become the registered reader instead.
    /// </exception>
    public float TakeWheel(int slot) {
        if (Volatile.Read(location: ref m_wheelConsumerRegistered) == 0) {
            throw new InvalidOperationException(
                message: $"WorldPointer.TakeWheel: no {nameof(IWorldWheelConsumer)} is registered, so WorldPointerSink is draining the wheel accumulator itself on arrival — this call would read zero forever. Implement {nameof(IWorldWheelConsumer)} to register as the real reader instead of calling TakeWheel directly."
            );
        }

        return DrainWheel(slot: slot);
    }

    /// <summary>Declares whether a real <see cref="IWorldWheelConsumer"/> is registered — called once by
    /// <see cref="WorldPointerSink"/> at construction, never afterward (the consumer list is fixed for the sink's
    /// lifetime). Gates <see cref="TakeWheel"/>'s refusal.</summary>
    /// <param name="registered"><see langword="true"/> when at least one registered consumer implements
    /// <see cref="IWorldWheelConsumer"/>.</param>
    /// <exception cref="InvalidOperationException">The declaration was already made — "called once, never
    /// afterward" is enforced, not merely documented: a second sink (or a re-declaration) would silently re-decide
    /// who owns the drainable wheel accumulator mid-session.</exception>
    internal void SetWheelConsumerRegistered(bool registered) {
        if (Interlocked.Exchange(location1: ref m_wheelConsumerDeclared, value: 1) != 0) {
            throw new InvalidOperationException(message: $"{nameof(WorldPointer)}.{nameof(SetWheelConsumerRegistered)} was already called — the wheel-consumer declaration is made once, at the one {nameof(WorldPointerSink)}'s construction, never re-decided.");
        }

        Volatile.Write(location: ref m_wheelConsumerRegistered, value: (registered ? 1 : 0));
    }

    // WorldPointerSink's OWN drain-and-discard when no consumer is registered — the one caller TakeWheel's refusal
    // above is not aimed at; this bypasses it because it IS the sink taking responsibility for the accumulator on
    // an unconsumed seat, not a consumer trying to read through the sink's back.
    internal float DrainWheelUnconsumed(int slot) {
        return DrainWheel(slot: slot);
    }

    private float DrainWheel(int slot) {
        return (InRange(slot: slot) ? Interlocked.Exchange(location1: ref m_wheel[slot], value: 0f) : 0f);
    }

    /// <summary>Records a seat's new absolute cursor position.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="position">The absolute position in client pixels.</param>
    public void SetPosition(int slot, Vector2 position) {
        if (!InRange(slot: slot)) {
            return;
        }

        Volatile.Write(location: ref m_positionX[slot], value: position.X);
        Volatile.Write(location: ref m_positionY[slot], value: position.Y);
        Volatile.Write(location: ref m_hasPosition[slot], value: 1);
    }

    /// <summary>Adds one report of relative motion to a seat's drainable accumulator.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="delta">The relative motion in client pixels.</param>
    public void AddMotion(int slot, Vector2 delta) {
        if (!InRange(slot: slot)) {
            return;
        }

        Add(location: ref m_motionX[slot], amount: delta.X);
        Add(location: ref m_motionY[slot], amount: delta.Y);
    }

    /// <summary>Adds one wheel report to a seat's drainable accumulator.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="notches">The rotation in notches (positive away from the user).</param>
    public void AddWheel(int slot, float notches) {
        if (InRange(slot: slot)) {
            Add(location: ref m_wheel[slot], amount: notches);
        }
    }

    /// <summary>Records a seat's pointer button edge.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="button">The button index (0=left, 1=right, 2=middle); any other index is ignored.</param>
    /// <param name="down"><see langword="true"/> for a press, <see langword="false"/> for a release.</param>
    public void SetButton(int slot, int button, bool down) {
        if (!InRange(slot: slot) || ((uint)button >= 3)) {
            return;
        }

        var mask = (1 << button);
        int held, updated;

        // Two buttons can be edged from different reports, so the whole word is a read-modify-write like the
        // accumulators above rather than a plain Volatile.Write of one bit.
        do {
            held = Volatile.Read(location: ref m_buttons[slot]);
            updated = (down ? held | mask : held & ~mask);
        } while (Interlocked.CompareExchange(location1: ref m_buttons[slot], value: updated, comparand: held) != held);
    }

    /// <summary>Drops every held button for a seat WITHOUT a genuine release event — the OS-focus-loss primitive,
    /// also reused by <see cref="ReleaseAllButtons"/> for a keyboard seat reassignment. A press whose release is
    /// delivered to another window (or whose seat the mouse no longer rides) would otherwise leave the button held
    /// forever, arming a drag that the user has already let go of. Advances <see cref="SystemReleaseCount"/> so a
    /// consumer can tell this apart from a real button-up.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void ReleaseButtons(int slot) {
        if (InRange(slot: slot)) {
            Volatile.Write(location: ref m_buttons[slot], value: 0);
            _ = Interlocked.Increment(location: ref m_systemReleaseCount[slot]);
        }
    }

    /// <summary>Drops every held button for EVERY seat (each seat's own <see cref="SystemReleaseCount"/> advances by
    /// one). The mouse carries no device identity of its own and always rides whichever seat currently owns the
    /// keyboard (see the class remarks), so only that one seat's button state is ever live; every other seat's bits
    /// are already leftover from whenever it last owned the keyboard. Clearing all of them can therefore never
    /// disturb a real in-progress drag, which is what makes this safe to use for a trigger where pinpointing the one
    /// stale seat is not worth it: OS focus loss, and the keyboard itself changing seats (the latter would otherwise
    /// strand the OLD seat's held bit forever — the mouse event stream has already moved to the new seat, so no
    /// ordinary button-up can ever reach it again).</summary>
    public void ReleaseAllButtons() {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            ReleaseButtons(slot: slot);
        }
    }

    private static bool InRange(int slot) {
        return ((uint)slot < PlayerRoster.MaxSlots);
    }
    private static void Add(ref float location, float amount) {
        float seen, updated;

        do {
            seen = Volatile.Read(location: ref location);
            updated = (seen + amount);
        } while (Interlocked.CompareExchange(location1: ref location, value: updated, comparand: seen) != seen);
    }
}
