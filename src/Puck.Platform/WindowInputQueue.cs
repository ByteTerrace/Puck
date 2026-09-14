using Puck.Input;

namespace Puck.Platform;

/// <summary>
/// The events one native window has decoded from its OS message stream but not yet handed to the host. Every window
/// backend decodes different bytes and ends up with the same queue, so the queue itself lives here once: the backend
/// enqueues while it pumps, and the host drains through <see cref="TryDequeue"/> until it reports empty.
/// </summary>
/// <remarks>Single-threaded by construction — a window pumps and drains on its own thread, so no synchronization is
/// taken. The disposal guard stays with the backend, because whether the window is still open is the backend's state,
/// not the queue's.</remarks>
public sealed class WindowInputQueue {
    private readonly Queue<WindowInputEvent> m_events = [];

    /// <summary>Appends one decoded event to the tail of the queue.</summary>
    /// <param name="item">The event the backend decoded.</param>
    public void Enqueue(WindowInputEvent item) => m_events.Enqueue(item: item);
    /// <summary>Takes the oldest pending event.</summary>
    /// <param name="inputEvent">The oldest pending event, or <see langword="default"/> when none is pending.</param>
    /// <returns><see langword="true"/> when an event was taken; <see langword="false"/> when the queue is empty.</returns>
    public bool TryDequeue(out WindowInputEvent inputEvent) => m_events.TryDequeue(result: out inputEvent);
}
