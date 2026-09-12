using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Exercises a real native MultiBoot sender and cold receiver against independent wire and handoff expectations.</summary>
internal sealed class FirmwareMultiBootStage : IPostStage<PostContext> {
    private const int ChunkCycles = 64;
    private const int TransferBudget = 60 * AdvancedGamingBrickMachine.CyclesPerFrame / ChunkCycles;

    /// <inheritdoc/>
    public string Name => "firmware-multiboot";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        try {
            var download = FirmwareMultiBootCartridge.Download();
            var negotiation = FirmwareMultiBootCartridge.Negotiation(download: download);
            var expected = FirmwareMultiBootProtocol.Expected(download: download, negotiation: negotiation);
            using var sender = new AdvancedGamingBrickCore(cartridgeRom: FirmwareMultiBootCartridge.Sender(download: download, negotiation: negotiation), bootMode: MachineBootMode.Fast);
            using var receiver = PrepareReceiver(emptyCartridge: false);
            var session = new AgbLinkSession(sender.Instance, receiver.Instance);
            try {
                var trace = new List<FirmwareMultiBootProtocol.Exchange>();
                var checkpointTransfers = negotiation.Length + 1 + 32;
                Pump(session: ref session, sender: sender, receiver: receiver, trace: trace, expected: expected,
                    untilTransfers: checkpointTransfers, churn: false, corruptChecksum: false);
                var token = session.Suspend();
                var senderCheckpoint = sender.Instance.Machine.Snapshot();
                var receiverCheckpoint = receiver.Instance.Machine.Snapshot();
                session = new AgbLinkSession(resumeToken: token, sender.Instance, receiver.Instance);
                Finish(session: ref session, sender: sender, receiver: receiver, trace: trace, expected: expected, churn: false, corruptChecksum: false);
                CheckDownloadedProgram(receiver: receiver, download: download);
                CheckSender(sender: sender);
                _ = session.Suspend();
                var senderFinal = sender.Instance.Machine.Snapshot();
                var receiverFinal = receiver.Instance.Machine.Snapshot();

                sender.Instance.Machine.Restore(snapshot: senderCheckpoint);
                receiver.Instance.Machine.Restore(snapshot: receiverCheckpoint);
                session = new AgbLinkSession(resumeToken: token, sender.Instance, receiver.Instance);
                var replay = trace.GetRange(index: 0, count: checkpointTransfers);
                Finish(session: ref session, sender: sender, receiver: receiver, trace: replay, expected: expected, churn: true, corruptChecksum: false);
                CheckDownloadedProgram(receiver: receiver, download: download);
                CheckSender(sender: sender);
                _ = session.Suspend();
                Require(condition: trace.SequenceEqual(second: replay), detail: "MultiBoot wire transcript changed across snapshot/reconnect replay");
                Require(condition: senderFinal.ContentEquals(other: sender.Instance.Machine.Snapshot()), detail: "MultiBoot sender snapshot diverged across credit-preserving reconnect churn");
                Require(condition: receiverFinal.ContentEquals(other: receiver.Instance.Machine.Snapshot()), detail: "MultiBoot receiver snapshot diverged across credit-preserving reconnect churn");

                sender.Instance.Machine.Restore(snapshot: senderCheckpoint);
                receiver.Instance.Machine.Restore(snapshot: receiverCheckpoint);
                session = new AgbLinkSession(resumeToken: token, sender.Instance, receiver.Instance);
                var corrupted = trace.GetRange(index: 0, count: checkpointTransfers);
                Finish(session: ref session, sender: sender, receiver: receiver, trace: corrupted, expected: expected, churn: false, corruptChecksum: true);
                Require(condition: Read(core: receiver, offset: 0) == 0, detail: "receiver executed a downloaded program after a bad wire checksum");
                Require(condition: receiver.Instance.Machine.Cpu.GetRegister(index: 15) < ReplacementBios.ImageSize, detail: "checksum refusal did not retain native BIOS control");
            } finally {
                session.Dispose();
            }
            using var empty = PrepareReceiver(emptyCartridge: true);
            using var erased = PrepareReceiver(emptyCartridge: true, erasedCartridge: true);
            FirmwareMultiBootVariants.Check(download: download);
            return PostStageOutcome.Pass(detail: $"native SWI25 sender -> cold receivers; {expected.Length} independently checked single-child multiplayer exchanges, normal256kHz/2MHz and three-child multiplayer, 256-byte EWRAM program and mode/id/register handoff; mid-payload snapshot replay/reconnect churn; corrupted checksum refused; zero-length and erased-cartridge receiver entry; no Joybus or retail timing claim");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    internal static AdvancedGamingBrickCore PrepareReceiver(bool emptyCartridge, bool erasedCartridge = false, int transferMode = 1) {
        var rom = emptyCartridge ? erasedCartridge ? new byte[512] : [] : FirmwarePresentationCartridge.Create();
        if (erasedCartridge) { Array.Fill(array: rom, value: (byte)0xFF); }
        var core = new AdvancedGamingBrickCore(cartridgeRom: rom);
        try {
            if (!emptyCartridge) { core.Instance.Machine.SetKeyInput(keys: 0x03F3); }
            var serial = core.Instance.GetRequiredService<IAgbSerialController>();
            var expectedMode = transferMode == 1 ? 0x6000 : 0x5000;
            while (core.CycleCount < 300L * AdvancedGamingBrickMachine.CyclesPerFrame) {
                core.RunCycles(cycles: 1024);
                if ((serial.ReadRegister(offset: 0x128) & 0x7000) == expectedMode) {
                    Require(condition: core.Instance.Machine.Cpu.GetRegister(index: 15) < ReplacementBios.ImageSize, detail: "download receiver readiness came from cartridge code instead of the native BIOS");
                    return core;
                }
            }
            throw new InvalidOperationException(message: $"cold firmware did not enter download mode {transferMode}: empty cartridge={emptyCartridge}, erased={erasedCartridge}");
        } catch {
            core.Dispose();
            throw;
        }
    }

    private static void Finish(ref AgbLinkSession session, AdvancedGamingBrickCore sender, AdvancedGamingBrickCore receiver,
        List<FirmwareMultiBootProtocol.Exchange> trace, FirmwareMultiBootProtocol.Exchange[] expected, bool churn, bool corruptChecksum) {
        Pump(session: ref session, sender: sender, receiver: receiver, trace: trace, expected: expected,
            untilTransfers: expected.Length, churn: churn, corruptChecksum: corruptChecksum);
        session.Run(cycles: 12L * AdvancedGamingBrickMachine.CyclesPerFrame);
    }

    private static void Pump(ref AgbLinkSession session, AdvancedGamingBrickCore sender, AdvancedGamingBrickCore receiver,
        List<FirmwareMultiBootProtocol.Exchange> trace, FirmwareMultiBootProtocol.Exchange[] expected, int untilTransfers, bool churn, bool corruptChecksum) {
        var parentSio = sender.Instance.GetRequiredService<IAgbSerialController>();
        var childSio = receiver.Instance.GetRequiredService<IAgbSerialController>();
        var wasActive = parentSio.IsTransferActive;
        for (var chunk = 0; chunk < TransferBudget && trace.Count < untilTransfers; ++chunk) {
            session.Run(cycles: ChunkCycles);
            var active = parentSio.IsTransferActive;
            if (!wasActive && active && corruptChecksum && trace.Count == expected.Length - 1) {
                // Controlled corruption of the final outgoing wire word, after native code queued it and before
                // the cable latches it. No downloaded code or receiver memory is patched to manufacture refusal.
                parentSio.WriteRegister(offset: 0x12A, value: (ushort)(expected[^1].Sent ^ 1));
            }
            if (wasActive && !active) {
                var actual = new FirmwareMultiBootProtocol.Exchange(Sent: parentSio.ReadRegister(offset: 0x120), Reply: parentSio.ReadRegister(offset: 0x122));
                var wanted = expected[trace.Count];
                if (corruptChecksum && trace.Count == expected.Length - 1) { wanted = wanted with { Sent = (ushort)(wanted.Sent ^ 1) }; }
                Require(condition: actual == wanted, detail: $"MultiBoot transfer {trace.Count}: sent/reply {actual.Sent:X4}/{actual.Reply:X4}, expected {wanted.Sent:X4}/{wanted.Reply:X4}");
                Require(condition: parentSio.ReadRegister(offset: 0x124) == 0xFFFF && parentSio.ReadRegister(offset: 0x126) == 0xFFFF, detail: "absent MultiBoot client slots did not stay idle-high");
                trace.Add(item: actual);
                if (churn && trace.Count % 17 == 0 && !childSio.IsTransferActive) {
                    var token = session.Suspend();
                    session = new AgbLinkSession(resumeToken: token, sender.Instance, receiver.Instance);
                }
            }
            wasActive = active;
        }
        Require(condition: trace.Count == untilTransfers, detail: $"MultiBoot stalled after {trace.Count}/{untilTransfers} expected exchanges within a 60-frame wire budget");
    }

    internal static void CheckDownloadedProgram(AdvancedGamingBrickCore receiver, byte[] download, int mode = 1, int client = 1) {
        Require(condition: Read(core: receiver, offset: 0) == 0xA5, detail: "native receiver did not execute the downloaded EWRAM program");
        Require(condition: Read(core: receiver, offset: 4) == 0x1F, detail: "downloaded program observed an incorrect ARM/System CPSR at handoff");
        Require(condition: Read(core: receiver, offset: 8) == 0x03007F00, detail: "downloaded program observed an incorrect system stack at handoff");
        for (var index = 0; index < download.Length; ++index) {
            var expected = index == 0xC4 ? (byte)(mode == 1 ? 3 : 2) : index == 0xC5 ? (byte)client : download[index];
            var actual = receiver.Instance.Machine.Bus.Read8(address: 0x02000000 + (uint)index, access: BusAccessType.NonSequential);
            Require(condition: actual == expected, detail: $"downloaded EWRAM/header byte {index:X3} differs: {actual:X2} != {expected:X2}");
        }
    }

    internal static void CheckSender(AdvancedGamingBrickCore sender) {
        Require(condition: Read(core: sender, offset: 0) == 0x5A, detail: "native sender SWI25 did not return to its cartridge");
        Require(condition: Read(core: sender, offset: 4) == 0, detail: "native sender SWI25 reported failure after a valid transfer");
    }

    private static uint Read(AdvancedGamingBrickCore core, uint offset) => core.Instance.Machine.Bus.Read32(
        address: FirmwareMultiBootCartridge.Completion + offset, access: BusAccessType.NonSequential);
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
}
