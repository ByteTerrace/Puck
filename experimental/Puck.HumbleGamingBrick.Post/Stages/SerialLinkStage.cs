using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage: the serial link cable — two machines, one deterministic exchange. A DMG master and a Color slave each
/// boot the <see cref="SerialLinkRom"/> exchange protocol and are advanced together through a
/// <see cref="SerialLinkSession"/> in per-frame budgets (the same shape a host engine drives). The master's eight
/// internal-clock transfers must deliver both counting sequences intact — every sent byte lands in the peer's receive
/// buffer, in order — with the correct register/interrupt outcomes on BOTH sides: each completed transfer raises the
/// serial interrupt request (observed, counted, and acknowledged by the ROM itself), SC's transfer bit reads back
/// clear, and both completion markers land. The whole linked scenario then runs a SECOND time from fresh machines
/// with the identical budget schedule and must reproduce byte-identical final snapshots on both machines — the
/// replay-identical proof that the link (and its pair-stepping interleave) adds no nondeterminism.
/// </summary>
internal sealed class SerialLinkStage : IPostStage {
    private const int Frames = 8;
    private const byte MasterSendBase = 0x10;
    private const byte SlaveSendBase = 0xA0;
    private const ushort SerialControlAddress = 0xFF02;

    /// <inheritdoc/>
    public string Name =>
        "serial-link";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var first = RunLinkedScenario();

        if (Verify(result: first) is { } failure) {
            return PostStageOutcome.Fail(detail: failure);
        }

        var second = RunLinkedScenario();

        if (!first.MasterState.ContentEquals(other: second.MasterState)) {
            return PostStageOutcome.Fail(detail: "the master machine's final state differed between two identical linked runs");
        }

        if (!first.SlaveState.ContentEquals(other: second.SlaveState)) {
            return PostStageOutcome.Fail(detail: "the slave machine's final state differed between two identical linked runs");
        }

        return PostStageOutcome.Pass(
            detail: $"{SerialLinkRom.TransferCount} bytes exchanged each way (dmg master ↔ cgb slave), {SerialLinkRom.TransferCount} serial interrupts observed per side, replay-identical across two runs ({first.MasterState.Size}+{first.SlaveState.Size} state bytes)"
        );
    }

    // One complete linked scenario from freshly built machines: connect, run the per-frame budget schedule, read the
    // protocol's work-RAM verdicts, snapshot. Fully self-contained so the determinism leg can repeat it identically.
    private static LinkScenarioResult RunLinkedScenario() {
        using var master = PostMachine.Build(model: ConsoleModel.Dmg, rom: SerialLinkRom.Create(internalClock: true, sendBase: MasterSendBase));
        using var slave = PostMachine.Build(model: ConsoleModel.Cgb, rom: SerialLinkRom.Create(internalClock: false, sendBase: SlaveSendBase));
        using var session = new SerialLinkSession(first: master, second: slave);

        for (var frame = 0; (frame < Frames); ++frame) {
            session.Run(tCycles: (ulong)PostMachine.TCyclesPerFrame);
        }

        return new LinkScenarioResult(
            MasterVerdict: ReadVerdict(instance: master),
            SlaveVerdict: ReadVerdict(instance: slave),
            MasterState: master.Machine.Snapshot(),
            SlaveState: slave.Machine.Snapshot()
        );
    }

    private static LinkSideVerdict ReadVerdict(MachineInstance instance) {
        var bus = instance.GetRequiredService<ISystemBus>();
        var received = new byte[SerialLinkRom.TransferCount];

        for (var index = 0; (index < received.Length); ++index) {
            received[index] = bus.ReadByte(address: (ushort)(SerialLinkRom.ReceiveBufferAddress + index));
        }

        return new LinkSideVerdict(
            CompletionMarker: bus.ReadByte(address: SerialLinkRom.CompletionMarkerAddress),
            InterruptCount: bus.ReadByte(address: SerialLinkRom.InterruptCountAddress),
            Received: received,
            SerialControl: bus.ReadByte(address: SerialControlAddress)
        );
    }

    // Judges the first run's protocol outcomes; null means every expectation held.
    private static string? Verify(LinkScenarioResult result) =>
        (VerifySide(verdict: result.MasterVerdict, side: "master", expectedBase: SlaveSendBase)
            ?? VerifySide(verdict: result.SlaveVerdict, side: "slave", expectedBase: MasterSendBase));

    private static string? VerifySide(LinkSideVerdict verdict, string side, byte expectedBase) {
        if (verdict.CompletionMarker != SerialLinkRom.CompletionMarker) {
            return $"the {side} never completed its {SerialLinkRom.TransferCount} transfers (marker 0x{verdict.CompletionMarker:X2})";
        }

        if (verdict.InterruptCount != SerialLinkRom.TransferCount) {
            return $"the {side} observed {verdict.InterruptCount} serial interrupt requests; expected {SerialLinkRom.TransferCount}";
        }

        if ((verdict.SerialControl & 0x80) != 0) {
            return $"the {side}'s SC transfer bit is still set after the exchange (SC 0x{verdict.SerialControl:X2})";
        }

        for (var index = 0; (index < verdict.Received.Length); ++index) {
            var expected = (byte)(expectedBase + index);

            if (verdict.Received[index] != expected) {
                return $"the {side}'s received byte {index} is 0x{verdict.Received[index]:X2}; expected 0x{expected:X2}";
            }
        }

        return null;
    }

    private readonly record struct LinkScenarioResult(
        LinkSideVerdict MasterVerdict,
        LinkSideVerdict SlaveVerdict,
        MachineSnapshot MasterState,
        MachineSnapshot SlaveState
    );

    private readonly record struct LinkSideVerdict(
        byte CompletionMarker,
        byte InterruptCount,
        byte[] Received,
        byte SerialControl
    );
}
