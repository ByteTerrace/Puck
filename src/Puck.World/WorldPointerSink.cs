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
/// itself. This remains the presentation projection; the window pump independently maps relative motion, wheel
/// motion, and buttons into bindable mouse command sources.
/// </summary>
/// <remarks>
/// <para>Every physical mouse carries its own <see cref="InputDeviceId"/> (see <c>Win32NativeWindow</c>'s Raw Input
/// resolution), so each pointer event resolves its OWN seat from ITS OWN device — <c>roster.DeviceSlot</c>,
/// re-resolved per event rather than cached, so a live <c>player.assign</c> that moves a mouse to another seat
/// carries its pointer with it. A device the roster cannot yet place (unclassified, or a platform that stamps no
/// per-mouse identity) falls back to <see cref="WorldPointerSlot.Resolve"/>.</para>
/// <para><see cref="WindowInputKind.FocusLost"/> is handled too, though it is not a pointer kind: a button pressed
/// here whose release is delivered to another window would stay held forever, so focus loss drops every seat's held
/// buttons (<see cref="WorldPointer.ReleaseAllButtons"/>). It does not clear the accumulators — motion already
/// reported really did happen, and a consumer that has not drained it yet is owed it.</para>
/// <para>ANY device's seat reassignment gets the identical all-seats release, subscribed on
/// <see cref="PlayerRoster.DeviceSlotChanging"/>: a device moving seats mid-drag can strand a held pointer button on
/// the seat it just left (the event stream has already moved to the new seat, so no real button-up can ever reach
/// the old one), so this releases broadly rather than trying to pinpoint which seat, if any, actually held a mouse
/// button through the move.</para>
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
        m_hasWheelConsumer = Array.Exists(
            array: m_consumers,
            match: static consumer => (consumer is IWorldWheelConsumer)
        );
        pointer.SetWheelConsumerRegistered(registered: m_hasWheelConsumer);

        roster.DeviceSlotChanging += OnDeviceSlotChanging;
    }

    // A device reassignment can strand a held pointer button on the seat it just left — see the class remarks'
    // final paragraph. Broad by design: releasing every seat's buttons on ANY device's move is never wrong (a
    // button that was not actually held stays not-held), only occasionally redundant.
    private void OnDeviceSlotChanging(InputDeviceId _) {
        m_pointer.ReleaseAllButtons();
    }
    // Each mouse resolves its OWN seat from its OWN device id (see the class remarks); a device the roster cannot
    // yet place falls back to WorldPointerSlot's fixed seat.
    private int ResolveSlot(InputDeviceId device) {
        return (m_roster.DeviceSlot(device: device) ?? WorldPointerSlot.Resolve(roster: m_roster));
    }

    /// <inheritdoc/>
    public void Observe(in WindowInputEvent inputEvent) {
        if (inputEvent.Kind == WindowInputKind.FocusLost) {
            m_pointer.ReleaseAllButtons();

            // Not a pointer act — nothing downstream should react to it as one.
            return;
        }

        var slot = ResolveSlot(device: inputEvent.DeviceId);

        switch (inputEvent.Kind) {
            case WindowInputKind.PointerMove:
                m_pointer.AddMotion(
                    slot: slot,
                    delta: inputEvent.Vector
                );

                break;
            case WindowInputKind.PointerPosition:
                m_pointer.SetPosition(
                    slot: slot,
                    position: inputEvent.Vector
                );

                break;
            case WindowInputKind.PointerButton:
                m_pointer.SetButton(
                    slot: slot,
                    button: inputEvent.ButtonIndex,
                    down: (inputEvent.Phase == CommandPhase.Started)
                );

                break;
            case WindowInputKind.PointerWheel:
                // Vector.Y carries the rotation in notches, positive away from the user.
                m_pointer.AddWheel(
                    slot: slot,
                    notches: inputEvent.Vector.Y
                );

                if (!m_hasWheelConsumer) {
                    // A composition without a wheel consumer (none exists in the shipped one — WorldWheelFeed is
                    // always registered) still must not let the accumulator bank PAST one event: this sink drains
                    // it itself at the point of arrival, mirroring WorldSeatViewInput's drain-and-discard for
                    // motion when its drag is not armed. DrainWheelUnconsumed (not the public TakeWheel, which
                    // refuses without a registered consumer — see its remarks) is this sink's own privileged
                    // access, never a stand-in for a real reader.
                    _ = m_pointer.DrainWheelUnconsumed(slot: slot);
                }

                break;
            default:
                // Keys and text are not this sink's concern.
                return;
        }

        foreach (var consumer in m_consumers) {
            consumer.OnPointer(slot: slot);
        }
    }
}
