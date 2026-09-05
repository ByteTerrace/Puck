namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83-family serial cable as an <see cref="IMachineGroupCore"/>: two lent <see cref="HumbleGamingBrickCore"/>s
/// wired peer-to-peer and advanced through one <see cref="SerialLinkSession"/>, so a
/// <see cref="LinkedMachineGroup"/> drives the pair's instruction-atomic interleave from its single execution thread.
/// The two cores stay owned by their own hosts; this type owns only the cable and the pacing that rides it.
/// </summary>
/// <remarks>The group state image is <c>[first length][first state][second length][second state][first credit]
/// [second credit]</c>, all little-endian — the two machines plus the pair-stepper's own overshoot credits, which the
/// machines' snapshots do not carry.</remarks>
internal sealed class SerialLinkGroupCore : IMachineGroupCore {
    private const ulong FnvOffsetBasis = 14_695_981_039_346_656_037UL;
    private const ulong FnvPrime = 1_099_511_628_211UL;

    private readonly HumbleGamingBrickCore m_first;
    private readonly SerialComponent m_firstPort;
    private readonly Action<byte>? m_firstPreviousObserver;
    private readonly HumbleGamingBrickCore m_second;
    private readonly SerialComponent m_secondPort;
    private readonly Action<byte>? m_secondPreviousObserver;
    private readonly SerialLinkSession m_session;
    private readonly StateWriter m_writer = new(capacity: 65_536);

    private bool m_disposed;
    private long m_completedTransfers;
    private byte[] m_firstScratch = [];
    private byte[] m_secondScratch = [];
    private ulong m_trafficFingerprint = FnvOffsetBasis;

    /// <summary>Wires two lent cores' serial ports as peers and anchors the pair-stepper at their current
    /// instants.</summary>
    /// <param name="first">The first core (the interleave's tie-break winner).</param>
    /// <param name="second">The second core.</param>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Both arguments are the same core.</exception>
    /// <exception cref="InvalidOperationException">Either core's serial port is already linked.</exception>
    public SerialLinkGroupCore(HumbleGamingBrickCore first, HumbleGamingBrickCore second) {
        ArgumentNullException.ThrowIfNull(argument: first);
        ArgumentNullException.ThrowIfNull(argument: second);

        m_first = first;
        m_second = second;
        m_firstPort = first.Instance.GetRequiredService<SerialComponent>();
        m_secondPort = second.Instance.GetRequiredService<SerialComponent>();
        m_session = new SerialLinkSession(
            first: first.Instance,
            second: second.Instance
        );
        m_firstPreviousObserver = m_firstPort.TransferCompleted;
        m_secondPreviousObserver = m_secondPort.TransferCompleted;
        m_firstPort.TransferCompleted = OnFirstTransferCompleted;
        m_secondPort.TransferCompleted = OnSecondTransferCompleted;
    }

    /// <inheritdoc/>
    public long CompletedTransfers =>
        Volatile.Read(location: ref m_completedTransfers);
    /// <inheritdoc/>
    public long CycleCount =>
        m_first.CycleCount;
    /// <inheritdoc/>
    public ulong CyclesPerSecond =>
        m_first.CyclesPerSecond;
    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer =>
        m_first.Framebuffer;
    /// <inheritdoc/>
    public int MemberCount => 2;
    /// <inheritdoc/>
    public long NativeFrameIndex =>
        m_first.NativeFrameIndex;
    /// <inheritdoc/>
    public ulong TrafficFingerprint =>
        Volatile.Read(location: ref m_trafficFingerprint);

    /// <inheritdoc/>
    public void ApplyInput(in MachineLinkPads input) {
        m_first.ApplyInput(input: in input[0]);
        m_second.ApplyInput(input: in input[1]);
    }
    /// <inheritdoc/>
    public int CaptureState(ref byte[] buffer) {
        var firstLength = m_first.CaptureState(buffer: ref m_firstScratch);
        var secondLength = m_second.CaptureState(buffer: ref m_secondScratch);
        var credits = m_session.PacingCredits;

        m_writer.Reset();
        m_writer.WriteInt32(value: firstLength);
        m_writer.WriteBytes(value: m_firstScratch.AsSpan(
            length: firstLength,
            start: 0
        ));
        m_writer.WriteInt32(value: secondLength);
        m_writer.WriteBytes(value: m_secondScratch.AsSpan(
            length: secondLength,
            start: 0
        ));
        m_writer.WriteUInt64(value: credits.FirstCredit);
        m_writer.WriteUInt64(value: credits.SecondCredit);

        return SnapshotBuffer.CopyWrittenState(
            buffer: ref buffer,
            writer: m_writer
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_firstPort.TransferCompleted = m_firstPreviousObserver;
        m_secondPort.TransferCompleted = m_secondPreviousObserver;

        // Severing at once is what unplugging the cable does: an unfinished externally-clocked transfer stays armed on
        // its port, waiting for edges that never arrive.
        m_session.Dispose();
    }
    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) {
        var reader = new StateReader(
            buffer: buffer,
            length: length,
            start: 0
        );
        var firstLength = reader.ReadInt32();

        Grow(
            buffer: ref m_firstScratch,
            length: firstLength
        );
        reader.ReadBytes(destination: m_firstScratch.AsSpan(
            length: firstLength,
            start: 0
        ));

        var secondLength = reader.ReadInt32();

        Grow(
            buffer: ref m_secondScratch,
            length: secondLength
        );
        reader.ReadBytes(destination: m_secondScratch.AsSpan(
            length: secondLength,
            start: 0
        ));

        var credits = new SerialLinkResumeToken(
            FirstCredit: reader.ReadUInt64(),
            SecondCredit: reader.ReadUInt64()
        );

        m_first.RestoreState(
            buffer: m_firstScratch,
            length: firstLength
        );
        m_second.RestoreState(
            buffer: m_secondScratch,
            length: secondLength
        );
        m_session.ReanchorPacing(credits: credits);
    }
    /// <inheritdoc/>
    public void RunCycles(long cycles) =>
        m_session.Run(tCycles: ((ulong)cycles));

    private static void Grow(ref byte[] buffer, int length) {
        if (buffer.Length < length) {
            buffer = new byte[length];
        }
    }
    private void Fold(byte value) {
        ++m_completedTransfers;
        m_trafficFingerprint = ((m_trafficFingerprint ^ value) * FnvPrime);
    }
    // The two sides fold under distinct tags so a fingerprint distinguishes which port received a byte, not merely that
    // one did.
    private void OnFirstTransferCompleted(byte value) {
        Fold(value: value);
        m_trafficFingerprint = ((m_trafficFingerprint ^ 0x01UL) * FnvPrime);
        m_firstPreviousObserver?.Invoke(obj: value);
    }
    private void OnSecondTransferCompleted(byte value) {
        Fold(value: value);
        m_trafficFingerprint = ((m_trafficFingerprint ^ 0x02UL) * FnvPrime);
        m_secondPreviousObserver?.Invoke(obj: value);
    }
}
