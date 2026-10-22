using System.Numerics;

namespace Puck.Input;

/// <summary>
/// The whole desktop a monitor capture shows, as a passthrough target. A window is reached by messages and never moves
/// the system cursor; the desktop is reached through the system's own input, so the taskbar, the Start menu and every
/// window take it as they take the mouse and keyboard, and it lands wherever Windows puts it. The host's own window is
/// on the desktop too, so the picture shows it: input that would land on it is delivered nowhere, which is what keeps a
/// click on the mirrored host from reaching the host again and again.
/// </summary>
/// <remarks>
/// <para>A pointer press on the picture is replayed at the desktop point the picture shows when it is released: the
/// cursor goes there, the press, its drag and its release land there, and the cursor comes back, so the pointer stays
/// where the local user's hand put it and the host never reads its own replay as the user's. The desktop takes no
/// keyboard from the host: keys go wherever Windows sends them, to the window a replayed press brought forward, and to
/// the host's game while the host's window is in front.</para>
/// <para>A pad is the desktop's mouse (<see cref="MovePointer"/>, <see cref="PressPointer"/>, <see cref="TurnWheel"/>,
/// <see cref="PressKey"/>): it moves the system cursor across the desktop and presses at it, whichever window is in
/// front. While the host claims the reserved chord (<see cref="ClaimsChord"/>) it is the host's system-wide, so the
/// local user can leave from any window.</para>
/// <para>Window-pump-thread only.</para>
/// </remarks>
public interface ISourcePassthroughDesktop : ISourcePassthroughWindow {
    /// <summary>Gets or sets whether the reserved chord, Control+Alt+Escape, is the host's system-wide: while set, the
    /// chord reaches no window, and <see cref="TakeChord"/> reports each press. Clearing it gives the chord back.</summary>
    bool ClaimsChord { get; set; }
    /// <summary>Gets the desktop's height in physical pixels, the unit a pad's pointer speed is measured in.</summary>
    int Height { get; }

    /// <summary>Moves the system cursor across the desktop by a distance in physical pixels, from where it is, clamped
    /// to the desktop.</summary>
    /// <param name="distance">The distance, +X right and +Y down.</param>
    void MovePointer(Vector2 distance);
    /// <summary>Presses or releases a key as the keyboard would, for whichever window Windows gives it to.</summary>
    /// <param name="key">The key.</param>
    /// <param name="pressed">Whether the key went down.</param>
    void PressKey(KeyCode key, bool pressed);
    /// <summary>Presses or releases a pointer button at the system cursor, as the mouse would. On the host's own window
    /// it is that window's press, which the host routes like any other (a press on the picture is replayed where the
    /// picture shows it).</summary>
    /// <param name="button">The pointer button: 0 left, 1 right, 2 middle, 3 back, 4 forward.</param>
    /// <param name="pressed">Whether the button went down.</param>
    void PressPointer(int button, bool pressed);
    /// <summary>Brings the host's window to the front, so the keyboard is the game's again.</summary>
    void ReturnForeground();
    /// <summary>Reports whether the reserved chord was pressed since the last call, while it is claimed.</summary>
    /// <returns><see langword="true"/> once per press.</returns>
    bool TakeChord();
    /// <summary>Turns the wheel at the system cursor.</summary>
    /// <param name="notches">The turn in notches, +X right and +Y away from the user.</param>
    void TurnWheel(Vector2 notches);
}
