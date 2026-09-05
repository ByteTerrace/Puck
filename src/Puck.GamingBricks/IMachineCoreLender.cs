namespace Puck.GamingBricks;

/// <summary>
/// The seam a <see cref="QueuedMachineWorker"/> reaches back through while its core is lent to a link. A lent core has
/// no worker thread of its own — the link steps it — so the host-facing operations that must observe a coherent
/// inter-instruction boundary (a debug peek or poke, a live reconfigure, a save flush) marshal onto the link's thread
/// instead of running on the caller's.
/// </summary>
public interface IMachineCoreLender {
    /// <summary>Runs one unit of work on the link's execution thread, between steps, and blocks until it
    /// completes.</summary>
    /// <param name="work">The work to run.</param>
    /// <returns><see langword="true"/> when the work ran; <see langword="false"/> when the link could not accept it
    /// (it is stopping or already severed), in which case the work did not run.</returns>
    bool RunOnLinkThread(Action work);
    /// <summary>Drops the link's captured rewind history because a member took an advance or a mutation the group's
    /// frame-oriented replay log cannot reproduce. Call from inside work already running on the link's thread.</summary>
    void InvalidateLinkHistory();
    /// <summary>Severs the link because a member is going away — every member's core returns to its own worker before
    /// the departing member tears its core down.</summary>
    void SeverLink();
}
