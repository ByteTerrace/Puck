using System.Numerics;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// <para>Decides, at each pointer event, what a seat's pointer motion does to a camera drag. The router applies a
/// drag's press and release (<c>player.orbit</c>, <c>player.steer</c>) at the next fixed tick, while the window reports
/// motion every frame, so the drag the router holds lags the buttons the pointer holds by up to a tick. The motion
/// follows the buttons instead: it counts from the press that starts a drag and stops at the release that ends it.</para>
/// <para>While no drag holds, the motion since the latest pointer-button press is kept for as long as a button stays
/// down, and the drag command's press edge (<see cref="Hold"/>) turns the camera by it. Motion with no button down, or
/// from before the press, is discarded. While a drag holds, motion turns the camera until a release leaves no pointer
/// button down; from then on it is discarded until the router lets go of the drag or another press comes.</para>
/// </summary>
/// <param name="pointer">The pointer store whose motion and buttons this reads.</param>
/// <param name="steering">Answers whether the router holds a camera drag on a seat slot.</param>
/// <param name="turn">Turns a seat slot's camera by pointer motion, in client pixels.</param>
public sealed class WorldDragMotion(WorldPointer pointer, Func<int, bool> steering, Action<int, Vector2> turn) {
    // The pointer buttons each seat held at its last event, so a press or release shows as the button it adds or drops.
    private readonly int[] m_buttons = new int[PlayerRoster.MaxSlots];
    // Whether a release has left a seat with no pointer button down while its drag still holds.
    private readonly bool[] m_released = new bool[PlayerRoster.MaxSlots];

    private int ButtonsDown(int slot) {
        var buttons = 0;

        for (var button = 0; (button < 3); button++) {
            if (pointer.IsButtonDown(
                button: button,
                slot: slot
            )) {
                buttons |= (1 << button);
            }
        }

        return buttons;
    }
    private void Turn(int slot) {
        var motion = pointer.TakeMotion(slot: slot);

        if (motion != Vector2.Zero) {
            turn(
                arg1: slot,
                arg2: motion
            );
        }
    }

    /// <summary>Turns a seat's camera by the motion kept since the press that started its drag, unless a release has
    /// already ended it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    private void BeginDrag(int slot) {
        if (
            (((uint)slot) < PlayerRoster.MaxSlots) &&
            !m_released[slot] &&
            steering(arg: slot)
        ) {
            Turn(slot: slot);
        }
    }
    /// <summary>Applies one edge of a held drag command (<c>player.orbit</c>, <c>player.steer</c>) to a seat: sets the
    /// hold, and on the press edge turns the camera by the motion that came with the press. A boot with no pointer (a
    /// headless one) has no gate and sets the hold alone.</summary>
    /// <param name="drag">The seat camera's drag gate, or <see langword="null"/> for a boot with no pointer.</param>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="phase">The command's edge.</param>
    /// <param name="held">Sets the seat's hold: <see langword="true"/> on the press and while held.</param>
    /// <exception cref="ArgumentNullException"><paramref name="held"/> is <see langword="null"/>.</exception>
    public static void Hold(WorldDragMotion? drag, int slot, CommandPhase phase, Action<bool> held) {
        ArgumentNullException.ThrowIfNull(argument: held);

        held(obj: (phase is CommandPhase.Started or CommandPhase.Active));

        if (phase == CommandPhase.Started) {
            drag?.BeginDrag(slot: slot);
        }
    }
    /// <summary>Takes a seat's freshly updated pointer state: turns its camera, keeps the motion for a drag a press may
    /// start, or discards it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public void OnPointer(int slot) {
        if (((uint)slot) >= PlayerRoster.MaxSlots) {
            return;
        }

        var buttons = ButtonsDown(slot: slot);
        var pressed = ((buttons & ~m_buttons[slot]) != 0);
        var releasedAll = ((m_buttons[slot] != 0) && (buttons == 0));
        var steers = steering(arg: slot);

        m_buttons[slot] = buttons;
        // A drag held with no pointer button (a key, a typed command) is never cut: only a release ends the stream.
        m_released[slot] = (
            steers &&
            !pressed &&
            (m_released[slot] || releasedAll)
        );

        if (
            steers &&
            !m_released[slot]
        ) {
            Turn(slot: slot);
        } else if (
            steers ||
            pressed ||
            (buttons == 0)
        ) {
            _ = pointer.TakeMotion(slot: slot);
        }
    }
}
