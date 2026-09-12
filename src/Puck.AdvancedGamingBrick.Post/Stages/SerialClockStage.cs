namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks normal-mode clock ownership independently of firmware and cartridge timing.</summary>
internal sealed class SerialClockStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "serial-clock";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.C;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        try {
            foreach (var word in new[] { false, true }) {
                foreach (var fast in new[] { false, true }) {
                    CheckClock(word: word, fast: fast);
                    CheckClockSelection(word: word, fast: fast);
                }
            }
            CheckSupersededEvent();
            return PostStageOutcome.Pass(detail: "Normal8/32 at both clock rates: armed slaves wait for a delayed master, exact master-edge completion and one IRQ each, held-start clock selection, no late duplicate completion; Normal32 payloads exchange both ways");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckClock(bool word, bool fast) {
        var masterClock = new AgbScheduler();
        var slaveClock = new AgbScheduler();
        var masterIrq = new AgbInterruptController();
        var slaveIrq = new AgbInterruptController();
        var master = new AgbSerialController(scheduler: masterClock, interrupts: masterIrq);
        var slave = new AgbSerialController(scheduler: slaveClock, interrupts: slaveIrq);
        var cable = new AgbLinkCable();
        master.Connect(link: cable);
        slave.Connect(link: cable);
        var mode = (ushort)(word ? 0x1000 : 0);
        var rate = (ushort)(fast ? 2 : 0);
        master.WriteRegister(offset: 0x128, value: (ushort)(mode | rate | 1));
        slave.WriteRegister(offset: 0x128, value: mode);
        if (word) {
            master.WriteRegister(offset: 0x120, value: 0x2468);
            master.WriteRegister(offset: 0x122, value: 0x1357);
            slave.WriteRegister(offset: 0x120, value: 0xBEEF);
            slave.WriteRegister(offset: 0x122, value: 0xABCD);
        }
        slave.WriteRegister(offset: 0x128, value: (ushort)(mode | rate | 0x4080));
        slaveClock.Advance(cycles: 8192);
        Require(condition: slave.IsTransferActive && slaveClock.NextWhen == long.MaxValue && Flags(irq: slaveIrq) == 0,
            detail: $"external Normal{(word ? 32 : 8)} completed without master clocks");
        masterClock.Advance(cycles: 8192);
        master.WriteRegister(offset: 0x128, value: (ushort)(mode | rate | 0x4081));
        var duration = (fast ? 8 : 64) * (word ? 32 : 8) + 7;
        masterClock.Advance(cycles: duration - 1);
        Require(condition: master.IsTransferActive && slave.IsTransferActive, detail: "normal transfer completed before its last clock");
        masterClock.Advance(cycles: 1);
        Require(condition: !master.IsTransferActive && !slave.IsTransferActive && Flags(irq: masterIrq) == 128 && Flags(irq: slaveIrq) == 128,
            detail: "master completion did not deliver one serial IRQ and release both busy flags");
        if (word) {
            Require(condition: master.ReadRegister(offset: 0x120) == 0xBEEF && master.ReadRegister(offset: 0x122) == 0xABCD
                && slave.ReadRegister(offset: 0x120) == 0x2468 && slave.ReadRegister(offset: 0x122) == 0x1357,
                detail: "Normal32 exchanged payload mismatch");
        }
        masterIrq.WriteRegister(offset: 0x202, value: 128);
        slaveIrq.WriteRegister(offset: 0x202, value: 128);
        masterClock.Advance(cycles: 16384);
        slaveClock.Advance(cycles: 16384);
        Require(condition: Flags(irq: masterIrq) == 0 && Flags(irq: slaveIrq) == 0, detail: "normal transfer delivered a late duplicate IRQ");
    }

    private static void CheckClockSelection(bool word, bool fast) {
        var clock = new AgbScheduler();
        var irq = new AgbInterruptController();
        var serial = new AgbSerialController(scheduler: clock, interrupts: irq);
        var control = (ushort)((word ? 0x1000 : 0) | (fast ? 2 : 0) | 0x4080);
        serial.WriteRegister(offset: 0x128, value: control);
        clock.Advance(cycles: 8192);
        Require(condition: serial.IsTransferActive && Flags(irq: irq) == 0, detail: "lone external transfer fabricated a clock");
        serial.WriteRegister(offset: 0x128, value: (ushort)(control | 1));
        clock.Advance(cycles: 4096);
        Require(condition: !serial.IsTransferActive && Flags(irq: irq) == 128, detail: "selecting the internal clock with start held did not complete");
    }

    private static void CheckSupersededEvent() {
        var clock = new AgbScheduler();
        var irq = new AgbInterruptController();
        var serial = new AgbSerialController(scheduler: clock, interrupts: irq);
        serial.WriteRegister(offset: 0x128, value: 0x5081);
        serial.WriteRegister(offset: 0x128, value: 0x5080);
        Require(condition: ((IAgbLinkClient)serial).TryCompleteNormalSlave(incoming: 0x12345678, word: true, outgoing: out _),
            detail: "external selection did not accept the peer's completion");
        Require(condition: Flags(irq: irq) == 128 && clock.NextWhen == long.MaxValue, detail: "peer completion retained an abandoned local event");
        irq.WriteRegister(offset: 0x202, value: 128);
        clock.Advance(cycles: 8192);
        Require(condition: Flags(irq: irq) == 0 && serial.ReadRegister(offset: 0x120) == 0x5678
            && serial.ReadRegister(offset: 0x122) == 0x1234, detail: "abandoned local event rewrote data or raised another IRQ");
    }

    private static ushort Flags(AgbInterruptController irq) {
        irq.StepSync(stallingCpu: false);
        return irq.ReadRegister(offset: 0x202);
    }

    private static void Require(bool condition, string detail) {
        if (!condition) { throw new InvalidOperationException(message: detail); }
    }
}
