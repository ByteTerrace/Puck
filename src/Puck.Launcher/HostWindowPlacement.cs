using Puck.Abstractions.Windowing;

namespace Puck.Launcher;

/// <summary>
/// The one door to the host window's live placement (<see cref="INativeWindowPlacement"/>): its size, position, monitor
/// and fullscreen mode, changed while the host runs without a restart. The window pump attaches the window it opens and
/// detaches it as the window closes; a console verb reads it through <see cref="TryEnter"/>, which admits only the thread
/// that owns the window. Console lines drain on that thread (the host loop's control phase), so a verb's placement call
/// reaches the windowing system directly and a size change flows through the window's own resize report, the path a
/// drag of its edge takes.
/// </summary>
public sealed class HostWindowPlacement {
    private INativeWindowPlacement? m_window;
    private string m_absence = "this host shows no window (its presentation is headless or offscreen)";
    private int m_ownerThread;

    /// <summary>Returns the window to place when the caller runs on the thread that owns it.</summary>
    /// <param name="window">The window, when this returns <see langword="true"/>.</param>
    /// <param name="refusal">Why no window can be placed from this call, when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when a placeable window is attached and the caller owns it.</returns>
    public bool TryEnter(out INativeWindowPlacement window, out string? refusal) {
        var attached = Volatile.Read(location: ref m_window);

        if (attached is null) {
            window = null!;
            refusal = m_absence;
            return false;
        }
        if (Environment.CurrentManagedThreadId != m_ownerThread) {
            window = null!;
            refusal = $"the window is placed only from the thread that pumps its events (thread {m_ownerThread}), not thread {Environment.CurrentManagedThreadId}";
            return false;
        }

        window = attached;
        refusal = null;
        return true;
    }

    // The window pump's side, on the thread that created the window.
    internal void Attach(INativeWindow window) {
        if (window is not INativeWindowPlacement placement) {
            m_absence = $"the {window.DisplayKind} window cannot be placed while it runs";
            return;
        }

        m_ownerThread = Environment.CurrentManagedThreadId;
        Volatile.Write(location: ref m_window, value: placement);
    }
    internal void Detach() {
        Volatile.Write(location: ref m_window, value: null);
        m_absence = "the window has closed";
    }
}
