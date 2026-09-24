namespace Puck.HumbleGamingBrick;

/// <summary>
/// A serial link cable between two machines: the <see cref="LinkSession{TPort}"/> over <see cref="SerialComponent"/>.
/// Constructing the session wires the two machines' serial ports as peers; bits are exchanged synchronously inside the
/// internally-clocked port's tick (see <see cref="SerialComponent"/>), and the pair advances through the shared
/// furthest-behind interleave.
/// <para>
/// When one machine's serial shifter exchanges a bit, the peer's state is its last instruction boundary — at most one
/// instruction stale, well inside a normal-rate bit period (512 T-cycles). That is the finest an instruction-atomic
/// core can offer; byte-level link protocols (the handshake style real link software uses) are exact under it.
/// Disposing the session leaves an unfinished external-clock transfer pending, as on unplugged hardware.
/// </para>
/// <para>
/// <see cref="LinkSession{TPort}.Suspend"/> requires both ports to be transfer-idle (SC bit 7 clear) and throws
/// <see cref="InvalidOperationException"/> rather than severing a cable mid-transfer, since a round no console can
/// recover would leave the resumed session unable to reconstruct hardware state. The live
/// <see cref="LinkSession{TPort}.ReanchorPacing"/> path has no such requirement.
/// </para>
/// </summary>
public sealed class SerialLinkSession : LinkSession<SerialComponent> {
    /// <summary>Initializes a new instance of the <see cref="SerialLinkSession"/> class, connecting two machines' serial
    /// ports and anchoring the pair-stepper at their current instants.</summary>
    /// <param name="first">The first machine (the tie-break winner when both are equally behind).</param>
    /// <param name="second">The second machine.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both arguments are the same instance.</exception>
    /// <exception cref="InvalidOperationException">Either machine's serial port is already linked.</exception>
    public SerialLinkSession(MachineInstance first, MachineInstance second)
        : base(
        connect: SerialComponent.Connect,
        disconnect: SerialComponent.Disconnect,
        first: first,
        second: second
    ) { }
    /// <summary>Initializes a new instance of the <see cref="SerialLinkSession"/> class for a suspended pair,
    /// re-anchoring each machine's pacing target at <c>CycleCount − credit</c> so the run continues the exact pacing the
    /// matching <see cref="LinkSession{TPort}.Suspend"/> severed at.</summary>
    /// <param name="first">The first machine, restored from its across-suspend snapshot (the tie-break winner).</param>
    /// <param name="second">The second machine, restored from its across-suspend snapshot.</param>
    /// <param name="resumeToken">The token the matching suspend returned.</param>
    /// <exception cref="ArgumentNullException">Either machine is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both machines are the same instance, or a credit exceeds its machine's own
    /// cycle count.</exception>
    /// <exception cref="InvalidOperationException">Either machine's serial port is already linked.</exception>
    public SerialLinkSession(MachineInstance first, MachineInstance second, LinkResumeToken resumeToken)
        : base(
        connect: SerialComponent.Connect,
        disconnect: SerialComponent.Disconnect,
        first: first,
        resumeToken: resumeToken,
        second: second
    ) { }

    /// <inheritdoc/>
    /// <remarks>Refuses while either port has a transfer armed or in flight (SC bit 7 set).</remarks>
    private protected override void RequireSeverable(SerialComponent firstPort, SerialComponent secondPort) {
        if (firstPort.IsTransferActive) {
            throw new InvalidOperationException(message: "the first port has a transfer armed or in flight (SC bit 7 set); suspend only at a transfer-idle instant, never mid-transfer.");
        }

        if (secondPort.IsTransferActive) {
            throw new InvalidOperationException(message: "the second port has a transfer armed or in flight (SC bit 7 set); suspend only at a transfer-idle instant, never mid-transfer.");
        }
    }
}
