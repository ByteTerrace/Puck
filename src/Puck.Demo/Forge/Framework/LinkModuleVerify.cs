using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The <see cref="LinkModule"/> loopback proof: two minimal framework carts (one internal-clock sender, one
/// external-clock receiver — mirroring the vocabulary and machine-pair construction of the Tier C
/// <c>experimental/Puck.HumbleGamingBrick.Post/Stages/SerialLinkStage.cs</c>) are linked through a real
/// <see cref="SerialLinkSession"/> and driven in per-frame budgets. Every transfer is a single shift-register
/// exchange (SB carries the outgoing byte in, the incoming byte out), so one poll-to-completion proves BOTH
/// directions at once. The battery asserts: several transfers land correctly on both sides, an UNLINKED machine's
/// external-clock receive times out to its fallback label instead of hanging, and the whole two-machine scenario is
/// REPLAY-IDENTICAL across two fresh runs (snapshot equality, the <c>SerialLinkStage</c> idiom). This is a
/// self-verify harness for the low-level link helpers. Game protocols consume the higher-level
/// <see cref="LinkProtocolModule"/>.
/// </summary>
internal static class LinkModuleVerify {
    private const ushort PollBudget = 2048;
    private const byte ReceiverSendBase = 0x90;
    private const byte SenderSendBase = 0x40;
    private const ulong TCyclesPerFrame = 70224UL;
    private const byte TransferCount = 5;

    // Game-owned WRAM (0xC200+): the sender/receiver stage their own send/receive bytes and a transfer index here —
    // LinkModule owns none of it, per the module's no-WRAM contract.
    private const ushort SendByte = FrameworkMemoryMap.GameRam;
    private const ushort RecvBuffer = (ushort)(FrameworkMemoryMap.GameRam + 1); // TransferCount bytes.
    private const ushort DoneFlag = (ushort)(TimeoutFlag + 1);
    private const ushort TimeoutFlag = (ushort)(TransferIndex + 1);
    private const ushort TransferIndex = (ushort)(RecvBuffer + TransferCount);
    // A one-byte scratch landing spot for LinkModule.EmitReadByte (a fixed-address helper); the tick body then
    // copies it into the runtime-indexed RecvBuffer slot.
    private const ushort ReadScratch = (ushort)(DoneFlag + 1);
    private const byte StatePlay = 0;

    /// <summary>Runs the whole loopback battery. Throws loudly on any violation.</summary>
    /// <returns>A one-line narration of what ran (bytes exchanged both ways, the timeout path, replay-identity).</returns>
    public static string Run() {
        AssertDisconnectedReceiverTimesOut();

        var first = RunLinkedScenario();

        AssertExchange(result: first);

        var second = RunLinkedScenario();

        if (!first.SenderState.ContentEquals(other: second.SenderState)) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the sender machine's final state differed between two identical linked runs.");
        }

        if (!first.ReceiverState.ContentEquals(other: second.ReceiverState)) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the receiver machine's final state differed between two identical linked runs.");
        }

        return $"LinkModule verify | {TransferCount} bytes exchanged each way (internal-clock sender <-> external-clock receiver) | disconnected receiver timed out to its fallback label after {PollBudget}-iteration polls (never hung) | replay-identical across two runs ({first.SenderState.Size}+{first.ReceiverState.Size} state bytes)";
    }

    // A receiver with NO linked peer: an external-clock arm never completes on real hardware, so the bounded poll
    // must fall through to the timeout label instead of hanging — the disconnect story LinkModule documents. Eight
    // frames comfortably covers boot (~2 frames to reach the play state) plus one full PollBudget-iteration poll.
    private static void AssertDisconnectedReceiverTimesOut() {
        using var lone = new Driver(rom: BuildReceiverRom());

        lone.RunFrames(buttons: JoypadButtons.None, frames: 8);

        if (lone.Read(address: TimeoutFlag) == 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: an unconnected receiver never reached its timeout fallback label (the bounded poll hung instead).");
        }

        if (lone.Read(address: DoneFlag) != 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: an unconnected receiver reported a completed transfer with no peer attached.");
        }
    }
    private static void AssertExchange(LinkScenarioResult result) {
        if (result.SenderTimeout != 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the linked sender hit its poll timeout despite a live peer.");
        }

        if (result.ReceiverTimeout != 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the linked receiver hit its poll timeout despite a live peer.");
        }

        if (result.SenderDone == 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the sender never finished its transfer run.");
        }

        if (result.ReceiverDone == 0) {
            throw new InvalidOperationException(message: "LinkModule verification failed: the receiver never finished its transfer run.");
        }

        for (var index = 0; (index < TransferCount); index++) {
            var expectedAtReceiver = (byte)(SenderSendBase + index);
            var expectedAtSender = (byte)(ReceiverSendBase + index);

            if (result.ReceiverReceived[index] != expectedAtReceiver) {
                throw new InvalidOperationException(message: $"LinkModule verification failed: the receiver's byte {index} is 0x{result.ReceiverReceived[index]:X2}; expected 0x{expectedAtReceiver:X2} (sender -> receiver).");
            }

            if (result.SenderReceived[index] != expectedAtSender) {
                throw new InvalidOperationException(message: $"LinkModule verification failed: the sender's byte {index} is 0x{result.SenderReceived[index]:X2}; expected 0x{expectedAtSender:X2} (receiver -> sender).");
            }
        }
    }

    // One complete linked scenario from freshly built machines: connect, run the per-frame budget schedule, read
    // both sides' work-RAM verdicts, snapshot. Fully self-contained so the replay leg can repeat it identically.
    private static LinkScenarioResult RunLinkedScenario() {
        using var sender = new Driver(rom: BuildSenderRom());
        using var receiver = new Driver(rom: BuildReceiverRom());
        using var session = new SerialLinkSession(first: sender.Machine, second: receiver.Machine);

        for (var frame = 0; (frame < 16); frame++) {
            session.Run(tCycles: TCyclesPerFrame);
        }

        sender.SettleAfterSessionRun();
        receiver.SettleAfterSessionRun();

        var receiverReceived = new byte[TransferCount];
        var senderReceived = new byte[TransferCount];

        for (var index = 0; (index < TransferCount); index++) {
            receiverReceived[index] = receiver.Read(address: (ushort)(RecvBuffer + index));
            senderReceived[index] = sender.Read(address: (ushort)(RecvBuffer + index));
        }

        return new LinkScenarioResult(
            SenderDone: sender.Read(address: DoneFlag),
            SenderTimeout: sender.Read(address: TimeoutFlag),
            SenderReceived: senderReceived,
            SenderState: sender.Snapshot(),
            ReceiverDone: receiver.Read(address: DoneFlag),
            ReceiverTimeout: receiver.Read(address: TimeoutFlag),
            ReceiverReceived: receiverReceived,
            ReceiverState: receiver.Snapshot()
        );
    }

    // The internal-clock side: each frame while transfers remain, send the next counting byte and poll to
    // completion, storing what came back (the receiver's counting byte, by the shift register's duplex nature).
    private static byte[] BuildSenderRom() =>
        BuildRom(sendBase: SenderSendBase, internalClock: true);

    // The external-clock side: same shape, but arms a receive instead of driving the clock.
    private static byte[] BuildReceiverRom() =>
        BuildRom(sendBase: ReceiverSendBase, internalClock: false);

    // The smallest possible framework cart (one blank tile, the font, one palette, one screen, one state — the
    // TuneGame template) whose single state runs TransferCount link transfers, one per frame, through LinkModule.
    private static byte[] BuildRom(byte sendBase, bool internalClock) {
        var manifest = new GameManifest();

        manifest.DefineTiles(name: "game-tiles", tiles2bpp: BuildBlankTile());
        manifest.DefineFontTiles();
        manifest.DefineBackgroundPalettes(name: "bg", paletteData: BuildPalette());
        manifest.DefineObjectPalettes(name: "obj", paletteData: BuildPalette());
        manifest.DefineScreen(name: "play", cells: new byte[0x400], overlays: []);

        var fw = new GameFramework(fontTileBase: manifest.FontTileBase, saveDefaultPayload: [0x00], saveVersion: 1);
        var linked = manifest.Link(framework: fw);
        var link = new LinkModule(emitter: fw.Emitter);

        fw.States.DefineState(
            id: StatePlay,
            emitEnter: e => EmitPlayEnter(e: e, sendBase: sendBase),
            emitTick: e => EmitPlayTick(e: e, link: link, internalClock: internalClock)
        );

        return fw.BuildRom(
            title: (internalClock ? "LINKSEND" : "LINKRECV"),
            bootSpec: new FrameworkBootSpec(
                BgPalettes: linked.BackgroundPalettes,
                InitialMap: linked.Screen(name: "play").Map,
                InitialState: StatePlay,
                Lcdc: Hw.LcdBackgroundAndObjects,
                ObjPalettes: linked.ObjectPalettes,
                Tiles: linked.TileBank,
                TileByteCount: linked.TileBank.Length
            )
        );
    }
    private static void EmitPlayEnter(Sm83Emitter e, byte sendBase) {
        e.LoadAImmediate(value: sendBase);
        e.StoreAToAddress(address: SendByte);
        e.XorA();
        e.StoreAToAddress(address: TransferIndex);
        e.StoreAToAddress(address: TimeoutFlag);
        e.StoreAToAddress(address: DoneFlag);
    }

    // One transfer per frame while transfers remain: send/arm, poll (bounded), on success store the received byte,
    // advance the send byte and index; on the poll budget expiring, flag the timeout and stop (the disconnect path).
    private static void EmitPlayTick(Sm83Emitter e, LinkModule link, bool internalClock) {
        var timeout = e.NewLabel();
        var done = e.NewLabel();
        var skip = e.NewLabel();

        e.LoadAFromAddress(address: DoneFlag);
        e.Arithmetic(op: AluOp.Or, source: Reg8.A);
        e.JumpRelative(condition: Condition.NotZero, label: skip); // Already finished (or timed out): idle.

        e.LoadAFromAddress(address: TransferIndex);
        e.ArithmeticImmediate(op: AluOp.Compare, value: TransferCount);
        e.JumpRelative(condition: Condition.Zero, label: done);

        if (internalClock) {
            link.EmitStartInternalSend(sourceAddress: SendByte);
        } else {
            link.EmitArmExternalReceive(sourceAddress: SendByte);
        }

        link.EmitPollComplete(iterationBudget: PollBudget, timeoutLabel: timeout);

        // Success: land the received byte via the module's fixed-address helper, then copy it into RecvBuffer[index]
        // (a runtime-indexed address EmitReadByte itself cannot target).
        link.EmitReadByte(destAddress: ReadScratch);
        e.LoadAFromAddress(address: TransferIndex);
        e.Load(destination: Reg8.C, source: Reg8.A);
        e.LoadImmediate(destination: Reg8.B, value: 0);
        e.LoadImmediate(pair: Reg16.Hl, value: RecvBuffer);
        e.AddToHl(pair: Reg16.Bc);
        e.LoadAFromAddress(address: ReadScratch);
        e.Load(destination: Reg8.Memory, source: Reg8.A);

        // Advance the send byte (so the next transfer sends a fresh counting value) and bump the index.
        e.LoadAFromAddress(address: SendByte);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: SendByte);
        e.LoadAFromAddress(address: TransferIndex);
        e.Increment(register: Reg8.A);
        e.StoreAToAddress(address: TransferIndex);
        e.JumpRelative(label: skip);

        e.MarkLabel(label: timeout);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: TimeoutFlag);
        e.JumpRelative(label: skip);

        e.MarkLabel(label: done);
        e.LoadAImmediate(value: 1);
        e.StoreAToAddress(address: DoneFlag);

        e.MarkLabel(label: skip);
    }
    private static byte[] BuildBlankTile() {
        var indices = new byte[64];

        Array.Fill(array: indices, value: (byte)1);

        return HgbImage.EncodeTile2bpp(tileIndices: indices);
    }
    private static byte[] BuildPalette() =>
        HgbImage.EncodePalette(palette: [
            new HgbImage.Rgb(R: 0x10, G: 0x18, B: 0x30),
            new HgbImage.Rgb(R: 0x30, G: 0x48, B: 0x78),
            new HgbImage.Rgb(R: 0x88, G: 0xA8, B: 0xD8),
            new HgbImage.Rgb(R: 0xF0, G: 0xF4, B: 0xFC),
        ]);

    private readonly record struct LinkScenarioResult(
        byte SenderDone,
        byte SenderTimeout,
        byte[] SenderReceived,
        MachineSnapshot SenderState,
        byte ReceiverDone,
        byte ReceiverTimeout,
        byte[] ReceiverReceived,
        MachineSnapshot ReceiverState
    );
    private sealed class Driver : IDisposable {
        private readonly ICpu m_cpu;
        private readonly IJoypad m_joypad;
        private readonly MachineInstance m_machine;
        private readonly ISystemBus m_bus;

        public Driver(byte[] rom) {
            m_machine = MachineFactory.Create(
                configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
                compose: static services => services.AddHumbleGamingBrickComponents()
            );
            m_bus = m_machine.GetRequiredService<ISystemBus>();
            m_cpu = m_machine.GetRequiredService<ICpu>();
            m_joypad = m_machine.GetRequiredService<IJoypad>();
        }

        public MachineInstance Machine => m_machine;

        public byte Read(ushort address) => m_bus.ReadByte(address: address);
        public void RunFrames(JoypadButtons buttons, int frames) {
            for (var frame = 0; (frame < frames); frame++) {
                m_joypad.SetButtons(pressed: buttons);
                m_machine.Machine.Run(tCycles: TCyclesPerFrame);
            }

            VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "link-module");
        }
        public void SettleAfterSessionRun() => VerifyMachineSettle.SettleOutOfOamDma(machine: m_machine.Machine, cpu: m_cpu, label: "link-module");
        public MachineSnapshot Snapshot() => m_machine.Machine.Snapshot();
        public void Dispose() => m_machine.Dispose();
    }
}
