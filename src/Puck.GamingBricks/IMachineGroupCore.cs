namespace Puck.GamingBricks;

/// <summary>
/// The slim core seam a <see cref="LinkedMachineGroup"/> drives — the group-level counterpart of
/// <see cref="IQueuedMachineCore"/>. An implementation owns the medium connecting the members (a serial cable, an
/// infrared bridge) and the deterministic interleave that advances them through one shared cycle budget; the group
/// owns the thread, the bounded FIFO, the tick-to-cycle accumulator, and each member's framebuffer/audio publication.
/// <para>
/// The state image <see cref="ITimeTravelMachineCore{TInput}.CaptureState"/> writes is the group's whole determinism
/// surface: every member's state plus the medium's own pacing state, so a coupled rewind lands every member and the
/// interleave itself on the recorded instant. That same image is what a cross-process transport would have to carry.
/// </para>
/// </summary>
/// <remarks>Every member runs on the group's single execution thread except construction and disposal, which the
/// group arranges around lending and returning the member cores.</remarks>
public interface IMachineGroupCore : ITimeTravelMachineCore<MachineLinkPads>, IDisposable {
    /// <summary>Gets the number of bytes exchanged over the medium since the group formed. A completed two-sided
    /// exchange counts once per delivered byte, so an eight-bit round trip between two members counts twice.</summary>
    long CompletedTransfers { get; }
    /// <summary>Gets the group's current cycle rate — the rate the group converts each engine-tick budget against. The
    /// medium defines one shared wall-time budget for every member, so this is one rate for the whole group rather
    /// than a per-member rate.</summary>
    ulong CyclesPerSecond { get; }
    /// <summary>Gets the number of members the medium connects.</summary>
    int MemberCount { get; }
    /// <summary>Gets a fingerprint folding every byte the medium has carried, in order — the pipe-assertable traffic
    /// signal a replay compares. It is part of the group's own state image (<see cref="CompletedTransfers"/> beside
    /// it), so a rewind restores it to the value it held at the landed instant, exactly like a member's own
    /// state.</summary>
    ulong TrafficFingerprint { get; }

    /// <summary>Advancing a group cannot fork a lookahead: a lookahead would have to fork every member and the medium
    /// between them, and a peer's future is not a function of the held input a prediction carries.</summary>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always.</exception>
    ITimeTravelLookahead<MachineLinkPads> ITimeTravelMachineCore<MachineLinkPads>.CreateLookahead() =>
        throw new NotSupportedException(message: "A cable-linked group has no lookahead: predicting a peer's future from held input is not a property the medium has.");
}
