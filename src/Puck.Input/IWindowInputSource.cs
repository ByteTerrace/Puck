namespace Puck.Input;

/// <summary>
/// A native window's INPUT-SOURCE role: drains the <see cref="WindowInputEvent"/>s the window translated from raw OS
/// keyboard/pointer messages (the window's <c>PollEvents</c> fills the queue). Split out of the windowing contract so a
/// window's "give me a surface" role stays free of the input vocabulary — the concrete native windows implement both,
/// and the launcher run loop drains input through this seam.
/// </summary>
public interface IWindowInputSource {
    /// <summary>Dequeues the next translated window input event, if any.</summary>
    /// <param name="inputEvent">The dequeued event when this returns <see langword="true"/>; <see langword="default"/> otherwise.</param>
    /// <returns><see langword="true"/> when an event was dequeued; <see langword="false"/> when the queue is empty.</returns>
    bool TryDequeueInput(out WindowInputEvent inputEvent);
}
