namespace Puck.Abstractions.Windowing;

/// <summary>
/// A native window the host sizes and places while it runs, beside the extent it was created with
/// (<see cref="NativeWindowOptions.Width"/>, <see cref="NativeWindowOptions.Height"/>). A size change is the windowing
/// system's own resize, the one a user dragging the window's edge makes: the window reports it through
/// <see cref="INativeWindow.Width"/>, <see cref="INativeWindow.Height"/> and <see cref="INativeWindow.ResizeCount"/>, and
/// the presenter follows it as it follows a drag. Read and called only on the thread that pumps the window's events.
/// </summary>
public interface INativeWindowPlacement {
    /// <summary>Gets where the window stands now: its client size, where its client area sits on the desktop and on
    /// which monitor, and whether it fills that monitor.</summary>
    NativeWindowPlacement Placement { get; }

    /// <summary>Sets the client area's size, leaving the window where it stands. A window filling its monitor returns to
    /// a window first. The windowing system may grant another size (a compositor's constraint); <see cref="Placement"/>
    /// reports what it granted.</summary>
    /// <param name="width">The client width, in pixels; at least 1.</param>
    /// <param name="height">The client height, in pixels; at least 1.</param>
    /// <param name="refusal">Why the window cannot be sized, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the request reached the windowing system.</returns>
    bool TrySetClientSize(uint width, uint height, out string? refusal);
    /// <summary>Moves the window so its client area's top-left corner lands on a desktop point, keeping its size.</summary>
    /// <param name="x">The client area's left edge, in desktop pixels.</param>
    /// <param name="y">The client area's top edge, in desktop pixels.</param>
    /// <param name="refusal">Why the window cannot be moved (a windowing system that places its toplevels itself), when
    /// this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the request reached the windowing system.</returns>
    bool TryMoveClient(int x, int y, out string? refusal);
    /// <summary>Centres the window, its frame included, in the work area of the monitor it stands on, keeping its
    /// size.</summary>
    /// <param name="refusal">Why the window cannot be placed, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the request reached the windowing system.</returns>
    bool TryCenter(out string? refusal);
    /// <summary>Makes the window fill its monitor without a frame, or returns it to the window it was.</summary>
    /// <param name="fullscreen">Whether the window fills its monitor.</param>
    /// <param name="refusal">Why the window has no such mode, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the window is in the asked mode.</returns>
    bool TrySetFullscreen(bool fullscreen, out string? refusal);
}
