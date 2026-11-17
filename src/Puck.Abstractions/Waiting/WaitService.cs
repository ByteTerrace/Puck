namespace Puck.Abstractions.Waiting;

/// <summary>
/// Work a thread services while it sleeps out the pacer's wait (<see cref="HostWaits.Pace"/>): what would otherwise
/// advance only once an iteration, such as background GPU work that takes its next job when one completes.
/// <see cref="HostWaits.Serve"/> registers it for the calling thread, which is the only thread that ever runs it.
/// <para>
/// The thread sleeps on the pacer's deadline and on <see cref="Signal"/> together, an operating-system wait on both, so
/// it wakes for whichever comes first and never polls. Setting the signal (<see cref="Wake"/>, or anything handed the
/// event, such as a GPU fence's completion) runs every service of the thread; so does the time a service asked to run
/// again by. A service that is never served loses nothing but latency: its owner still advances it at its own points
/// in the iteration.
/// </para>
/// </summary>
public sealed class WaitService : IDisposable {
    private readonly HostWaits.ServedThread m_thread;
    private readonly Func<TimeSpan, TimeSpan?> m_service;

    internal WaitService(HostWaits.ServedThread thread, in WaitReason reason, Func<TimeSpan, TimeSpan?> service) {
        m_thread = thread;
        m_service = service;
        Reason = reason;
    }

    /// <summary>Gets what the service does while its thread waits.</summary>
    public WaitReason Reason { get; }
    /// <summary>Gets the event that wakes the serving thread from its pacer wait. It is the thread's one wake, shared by
    /// every service registered on it and kept for the life of the process, so a native completion that holds its handle
    /// (a fence's) never signals a closed one.</summary>
    public EventWaitHandle Signal => m_thread.Wake;

    /// <summary>Wakes the serving thread from its pacer wait to run its services. Callable on any thread.</summary>
    public void Wake() => _ = m_thread.Wake.Set();
    /// <summary>Ends the registration; the thread serves it no more. Callable on any thread.</summary>
    public void Dispose() => m_thread.Remove(service: this);

    internal TimeSpan? Run(TimeSpan remaining) => m_service(arg: remaining);
}
