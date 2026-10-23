namespace Puck.Input;

/// <summary>
/// An OPTIONAL policy the window pump reads after each batch of window events, resolved as a HELD root
/// <c>IHostContext</c> capability the way <see cref="IWindowInputFilter"/> is, and applies to a window that can change
/// its cursor (<c>INativeWindowCursor</c>). With none registered, the window keeps the cursor it was created with.
/// </summary>
public interface IWindowCursorPolicy {
    /// <summary>Gets whether a drag that reads only the pointer's motion holds it, such as a held button that turns a
    /// seat's camera, so the window locks the pointer and its motion never ends at an edge.</summary>
    bool LocksPointer { get; }
    /// <summary>Gets whether the pointer rests where the host sends it outside the game, such as over a pane that passes
    /// it to an external window, so the window shows the system cursor there in place of the game's.</summary>
    bool ShowsSystemCursor { get; }

    /// <summary>Takes the client point the host moves the cursor to now, as a pad driving the pointer does; the pump hands
    /// it to <c>INativeWindowCursor.WarpPointer</c>.</summary>
    /// <param name="x">The client-area x coordinate, in client pixels, when this returns <see langword="true"/>.</param>
    /// <param name="y">The client-area y coordinate, in client pixels, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the cursor moves this frame.</returns>
    bool TryTakeWarp(out int x, out int y);
}
