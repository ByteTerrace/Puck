using Puck.Commands;
using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// A consumer of live pointer state. Implementations are driven by <see cref="WorldPointerSink"/> once per raw
/// pointer event, AFTER <see cref="WorldPointer"/> has been updated, and read what they need from the store rather
/// than from the event — so a consumer never has to reconstruct held-button or cursor state the store already
/// carries, and adding one costs no second <see cref="IWindowInputObserver"/>.
/// </summary>
internal interface IWorldPointerConsumer {
    /// <summary>Reacts to the seat's freshly-updated pointer state.</summary>
    /// <param name="slot">The 0-based seat slot the pointer currently rides.</param>
    void OnPointer(int slot);
}

/// <summary>Marks an <see cref="IWorldPointerConsumer"/> that reads <see cref="WorldPointer.TakeWheel"/>, so
/// <see cref="WorldPointerSink"/> knows a real reader exists and must not drain-and-discard the wheel accumulator on
/// its behalf. <see cref="WorldWheelFeed"/> is the one implementation — the radial action menu's ring cycling is
/// what the wheel drives; a second drainer would starve it, which is exactly what this marker exists to make
/// structural.</summary>
internal interface IWorldWheelConsumer : IWorldPointerConsumer {
}

/// <summary>
/// The one <see cref="IWindowInputObserver"/> the pointer has. It writes every raw pointer event into
/// <see cref="WorldPointer"/> and then drives each registered <see cref="IWorldPointerConsumer"/>, so the whole
/// engine reads pointer state from one store instead of each interested party parsing the raw event stream for
/// itself. Nothing here names an intent: this is pointer infrastructure, and what a drag or a scroll means is a
/// consumer's business.
/// </summary>
/// <remarks>
/// <para>The mouse carries no <see cref="InputDeviceId"/> of its own — there is no per-mouse device identity to
/// resolve a slot from, unlike a pad or the keyboard — so every event lands on whichever seat the keyboard
/// currently owns: <see cref="WorldPointerSlot.Resolve"/>, re-resolved per event rather than cached, so a live
/// <c>player.assign</c> that moves the keyboard to another seat carries the mouse with it. Falls back to slot 0 in
/// the unreachable-in-practice case the keyboard is itself unmapped (it is committed to slot 0 from boot and never
/// leaves).</para>
/// <para><see cref="WindowInputKind.FocusLost"/> is handled too, though it is not a pointer kind: a button pressed
/// here whose release is delivered to another window would stay held forever, so focus loss drops every seat's held
/// buttons (<see cref="WorldPointer.ReleaseAllButtons"/> — not just the seat the mouse currently rides, since a
/// keyboard reassignment could have moved on since the button went down; see the next paragraph). It does not clear
/// the accumulators — motion already reported really did happen, and a consumer that has not drained it yet is owed
/// it.</para>
/// <para>The keyboard itself changing seats gets the identical all-seats release, subscribed on
/// <see cref="PlayerRoster.DeviceSlotChanging"/> exactly as <see cref="Puck.Commands.InputRouter"/> subscribes to
/// release held keys on the same edge: the mouse always rides whichever seat currently owns the keyboard, so once
/// the keyboard moves, the old seat's held pointer buttons can never be resolved by a real button-up again — the
/// event stream has already moved to the new seat. Left alone, a button held mid-drag at the moment of a
/// <c>player.assign</c> would stay latched on the old seat forever, arming a phantom drag the instant that seat ever
/// regains the keyboard.</para>
/// </remarks>
internal sealed class WorldPointerSink : IWindowInputObserver {
    private readonly IWorldPointerConsumer[] m_consumers;
    private readonly bool m_hasWheelConsumer;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;

    /// <summary>Initializes a new instance of the <see cref="WorldPointerSink"/> class.</summary>
    /// <param name="pointer">The store this sink is the sole writer of.</param>
    /// <param name="roster">The roster this sink resolves the mouse's seat against.</param>
    /// <param name="consumers">The consumers to drive after each pointer event.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldPointerSink(WorldPointer pointer, PlayerRoster roster, IEnumerable<IWorldPointerConsumer> consumers) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: consumers);

        m_pointer = pointer;
        m_roster = roster;
        m_consumers = consumers.ToArray();
        m_hasWheelConsumer = Array.Exists(array: m_consumers, match: static consumer => (consumer is IWorldWheelConsumer));
        pointer.SetWheelConsumerRegistered(registered: m_hasWheelConsumer);

        roster.DeviceSlotChanging += OnDeviceSlotChanging;
    }

    // The keyboard-reassignment half of the cross-slot latch fix — see the class remarks' second paragraph. Fires
    // for every device, not just the keyboard, so it filters; a pad changing seats carries no mouse with it and
    // leaves no pointer state to clean up.
    private void OnDeviceSlotChanging(InputDeviceId device) {
        if (device == PlayerRoster.KeyboardDevice) {
            m_pointer.ReleaseAllButtons();
        }
    }

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        var slot = WorldPointerSlot.Resolve(roster: m_roster);

        switch (inputEvent.Kind) {
            case WindowInputKind.PointerMove:
                m_pointer.AddMotion(slot: slot, delta: inputEvent.Vector);

                break;
            case WindowInputKind.PointerPosition:
                m_pointer.SetPosition(slot: slot, position: inputEvent.Vector);

                break;
            case WindowInputKind.PointerButton:
                // Vector.X carries the button index (0=left, 1=right, 2=middle), the edge its phase.
                m_pointer.SetButton(slot: slot, button: (int)inputEvent.Vector.X, down: (inputEvent.Phase == CommandPhase.Started));

                break;
            case WindowInputKind.PointerWheel:
                // Vector.Y carries the rotation in notches, positive away from the user.
                m_pointer.AddWheel(slot: slot, notches: inputEvent.Vector.Y);

                if (!m_hasWheelConsumer) {
                    // A composition without a wheel consumer (none exists in the shipped one — WorldWheelFeed is
                    // always registered) still must not let the accumulator bank PAST one event: this sink drains
                    // it itself at the point of arrival, mirroring WorldCameraOrbitDrag's drain-and-discard for
                    // motion when its drag is not armed. DrainWheelUnconsumed (not the public TakeWheel, which
                    // refuses without a registered consumer — see its remarks) is this sink's own privileged
                    // access, never a stand-in for a real reader.
                    _ = m_pointer.DrainWheelUnconsumed(slot: slot);
                }

                break;
            case WindowInputKind.FocusLost:
                m_pointer.ReleaseAllButtons();

                // Not a pointer act — nothing downstream should react to it as one.
                return;
            default:
                // Keys and text are not this sink's concern.
                return;
        }

        foreach (var consumer in m_consumers) {
            consumer.OnPointer(slot: slot);
        }
    }
}
