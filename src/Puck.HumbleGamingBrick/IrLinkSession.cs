namespace Puck.HumbleGamingBrick;

/// <summary>
/// An infrared link between two machines: the <see cref="LinkSession{TPort}"/> over <see cref="InfraredPort"/>.
/// Constructing the session wires the two transceivers as peers, and the pair advances through the shared
/// furthest-behind interleave.
/// <para>
/// Unlike the serial cable there is no clock to negotiate: infrared carries a light level, not a clocked bit stream, so
/// the session does not arbitrate a shift edge — it only keeps the two machines' light states coherent by advancing them
/// in the furthest-behind interleave. When a machine samples its received-light line, the peer's emitted light is the
/// peer's state at its last instruction boundary — at most one instruction stale, well inside the many-thousand-cycle
/// windows IR link software times its pulses over. Disposing the session reverts both received lines to dark, as on an
/// unpaired transceiver.
/// </para>
/// <para>
/// No bit is ever mid-shift on the IR medium — the whole transceiver state is a level plus register bits, all
/// snapshotted — so any budget boundary is a clean instant for <see cref="LinkSession{TPort}.Suspend"/>.
/// </para>
/// </summary>
public sealed class IrLinkSession : LinkSession<InfraredPort> {
    /// <summary>Initializes a new instance of the <see cref="IrLinkSession"/> class, connecting two machines' infrared
    /// transceivers and anchoring the pair-stepper at their current instants.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="second">The second machine.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both arguments are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's infrared port is already linked.</exception>
    public IrLinkSession(MachineInstance first, MachineInstance second)
        : base(
        connect: InfraredPort.Connect,
        disconnect: InfraredPort.Disconnect,
        first: first,
        second: second
    ) { }
    /// <summary>Initializes a new instance of the <see cref="IrLinkSession"/> class for a suspended pair, re-anchoring
    /// each machine's pacing target at <c>CycleCount − credit</c> so the run continues the exact pacing the matching
    /// <see cref="LinkSession{TPort}.Suspend"/> severed at.</summary>
    /// <param name="first">The first machine, restored from its across-suspend snapshot (the tie-break winner).</param>
    /// <param name="second">The second machine, restored from its across-suspend snapshot.</param>
    /// <param name="resumeToken">The token the matching suspend returned.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance, or a credit exceeds its machine's own
    /// cycle count.</exception>
    /// <exception cref="InvalidOperationException">Either machine's infrared port is already linked.</exception>
    public IrLinkSession(MachineInstance first, MachineInstance second, LinkResumeToken resumeToken)
        : base(
        connect: InfraredPort.Connect,
        disconnect: InfraredPort.Disconnect,
        first: first,
        resumeToken: resumeToken,
        second: second
    ) { }
}
