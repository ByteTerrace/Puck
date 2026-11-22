using System.Collections.Concurrent;
using Puck.Abstractions.Pacing;

namespace Puck.World.Protocol;

/// <summary>Why <see cref="WorldLinkBoundary.Refusal"/> refuses the calling thread a read of the authorities its host
/// steps.</summary>
public enum WorldBoundaryRefusal : byte {
    /// <summary>The calling thread may read.</summary>
    None,
    /// <summary>The calling thread is the registered pump and its window is closed.</summary>
    OutsideWindow,
    /// <summary>The calling thread handed a tick to the host's step thread and has not collected it.</summary>
    TickInFlight,
}

/// <summary>
/// One host's boundary window: the only span in which the host loop's thread, the pump, touches the authorities the
/// host steps. The loop registers the pump before its first iteration and opens the window around its control phase
/// and around every step; everything else the pump does (input capture, the frame, its pacing wait) is outside it. A
/// tick the pump hands to the host's step thread closes the window until the pump collects it, wherever in the loop
/// that is, so the pump never reads an authority while its tick executes.
/// <para>
/// Three things rest on it. An authority refuses a guarded read from the pump outside the window (<see cref="Refusal"/>),
/// whether or not a tick happens to be executing, so a reader that slipped out of the window throws on its first frame
/// and names itself. A delivery is handed to a receiving sink at once only on the pump inside the window, and is
/// recorded for the pump's next collect otherwise (<see cref="DeliversHere"/>). A loopback submission mints at once
/// only there, and queues otherwise (<see cref="IsOpen"/>).
/// </para>
/// <para>
/// Before a pump registers, a host is booting on one thread and nothing is refused or deferred. After the pump
/// unregisters, that thread tears the host down and still receives deliveries at once; every other thread's are
/// recorded and never replayed.
/// </para>
/// </summary>
public sealed class WorldLinkBoundary : IHostTickBoundary {
    [ThreadStatic] private static WorldLinkBoundary? OpenWindow;

    private readonly ConcurrentQueue<Action> m_waiting = new();
    private int m_pumpThread;
    // The thread that was the pump last, kept once it unregisters: the one thread deliveries still reach at once.
    private int m_lastPump;
    // The managed id of the thread whose handed-over tick the host's step thread holds, from the hand-off until that
    // thread collects it; zero otherwise.
    private int m_lentBy;
    private Action? m_closing;

    /// <summary>Gets whether a loopback submission made now on the calling thread mints at once: no pump is registered
    /// (a host that is booting, or one nothing loops), or the calling thread is the pump inside its window with no
    /// tick in flight.</summary>
    internal bool IsOpen => ((Volatile.Read(location: ref m_pumpThread) == 0) || (ReferenceEquals(objA: OpenWindow, objB: this) && (Volatile.Read(location: ref m_lentBy) == 0)));

    /// <summary>Gets whether a delivery made now on the calling thread is handed to its receiving sink at once: no pump
    /// has ever registered, the calling thread is the pump inside its window, or the pump has unregistered and the
    /// calling thread was it. Every other delivery is recorded for the pump's next collect.</summary>
    public bool DeliversHere {
        get {
            var pump = Volatile.Read(location: ref m_pumpThread);

            if (pump != 0) {
                return ReferenceEquals(objA: OpenWindow, objB: this);
            }

            var last = Volatile.Read(location: ref m_lastPump);

            return ((last == 0) || (last == Environment.CurrentManagedThreadId));
        }
    }
    /// <summary>Gets why the calling thread may not read the authorities this boundary's host steps, or
    /// <see cref="WorldBoundaryRefusal.None"/> when it may: it handed a tick over and has not collected it, or it is the
    /// registered pump outside its window. A thread that is neither (the step thread, a socket task) is never refused
    /// here; the authority's gate is what orders it.</summary>
    public WorldBoundaryRefusal Refusal {
        get {
            var current = Environment.CurrentManagedThreadId;

            if (Volatile.Read(location: ref m_lentBy) == current) {
                return WorldBoundaryRefusal.TickInFlight;
            }

            return (((Volatile.Read(location: ref m_pumpThread) == current) && !ReferenceEquals(objA: OpenWindow, objB: this))
                ? WorldBoundaryRefusal.OutsideWindow
                : WorldBoundaryRefusal.None);
        }
    }

    internal void Enqueue(Action submission) => m_waiting.Enqueue(submission);

    /// <summary>Marks a tick as handed to the host's step thread by the calling thread, which reads no authority of
    /// the host until <see cref="Collected"/>.</summary>
    public void HandedOver() => Volatile.Write(location: ref m_lentBy, value: Environment.CurrentManagedThreadId);
    /// <summary>Marks the tick <see cref="HandedOver"/> marked as collected.</summary>
    public void Collected() => Volatile.Write(location: ref m_lentBy, value: 0);

    /// <summary>Registers what the pump publishes of the authority for its readers outside the window: it runs on the
    /// pump each time the window closes with no tick in flight, while the authority may still be read, and never
    /// otherwise, so what it publishes is always a settled boundary's.</summary>
    /// <param name="publish">The publisher. It must not throw.</param>
    /// <exception cref="ArgumentNullException"><paramref name="publish"/> is <see langword="null"/>.</exception>
    public void PublishOnClose(Action publish) {
        ArgumentNullException.ThrowIfNull(argument: publish);

        m_closing += publish;
    }

    /// <inheritdoc/>
    public void RegisterPump() {
        if (Interlocked.CompareExchange(ref m_pumpThread, Environment.CurrentManagedThreadId, 0) != 0) {
            throw new InvalidOperationException("The link already has a registered pump.");
        }
    }
    /// <inheritdoc/>
    public void UnregisterPump() {
        RequirePump();
        Volatile.Write(ref m_lastPump, Environment.CurrentManagedThreadId);
        Volatile.Write(ref m_pumpThread, 0);
    }
    /// <inheritdoc/>
    public void Open(bool drain) {
        RequirePump();
        if (OpenWindow is not null) {
            throw new InvalidOperationException("A link boundary window is already open on this thread.");
        }

        OpenWindow = this;
        if (!drain) { return; }

        try {
            while (m_waiting.TryDequeue(out var submission)) { submission(); }
        } catch {
            Close();
            throw;
        }
    }
    /// <inheritdoc/>
    public void Close() {
        if (!ReferenceEquals(objA: OpenWindow, objB: this)) {
            OpenWindow = null;

            return;
        }

        try {
            if (Volatile.Read(location: ref m_lentBy) == 0) { m_closing?.Invoke(); }
        } finally {
            OpenWindow = null;
        }
    }

    private void RequirePump() {
        if (Volatile.Read(ref m_pumpThread) != Environment.CurrentManagedThreadId) {
            throw new InvalidOperationException("Only the registered pump can open the link boundary window.");
        }
    }
}
