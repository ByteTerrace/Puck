namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Checks bounded sleeping CPU steps, DMA count widths, and audio output reconfiguration without ROM assets.</summary>
internal sealed class LifecycleStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "lifecycle";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        try {
            CheckAudio();
            CheckHalt(stop: false);
            CheckHalt(stop: true);
            CheckHaltedDma();
            for (var channel = 0; channel < 4; ++channel) {
                foreach (var count in new ushort[] { 0, 1, 0x3FFF, 0x4000, 0x4001, 0x8001, 0xFFFF }) {
                    CheckDma(channel: channel, count: count);
                }
            }
            return PostStageOutcome.Pass(detail: "HALT/STOP yield, snapshot/replay and keypad wake; DMA during HALT; 28 DMA count-width vectors; audio disable/drain/re-enable");
        } catch (Exception exception) when (exception is InvalidOperationException or IndexOutOfRangeException) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckAudio() {
        var apu = new AgbApu();
        var samples = new short[32];
        for (var iteration = 0; iteration < 3; ++iteration) {
            apu.ConfigureOutput(sampleRate: 32_000);
            apu.Step(cycles: 4096);
            // Leave unread samples even after moving the read cursor.
            Require(condition: apu.DrainSamples(destination: samples.AsSpan(start: 0, length: 2)) == 2, detail: "audio did not produce samples");
            apu.ConfigureOutput(sampleRate: 0);
            Require(condition: apu.DrainSamples(destination: samples) == 0, detail: "disabled audio retained queued samples");
        }
        apu.ConfigureOutput(sampleRate: 48_000);
        Require(condition: apu.DrainSamples(destination: samples) == 0, detail: "re-enabled audio retained old samples");
        apu.Step(cycles: 4096);
        Require(condition: apu.DrainSamples(destination: samples) > 0, detail: "re-enabled audio did not produce samples");
    }

    private static void CheckHalt(bool stop) {
        using var instance = PostMachine.BuildInstance(bios: new byte[ReplacementBios.ImageSize], rom: SyntheticRom.Create());
        var machine = instance.Machine;
        var bus = machine.Bus;
        bus.Write16(address: 0x04000200, value: 0x1000, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x04000132, value: 0x4001, access: BusAccessType.NonSequential);
        bus.Halt(stop: stop);
        var pc = machine.Cpu.GetRegister(index: 15);
        var scheduler = instance.GetRequiredService<AgbScheduler>();
        var cutoff = new AgbScheduler.Event { Callback = _ => throw new InvalidOperationException(message: "halt failed to return within a bounded cycle budget") };
        scheduler.Schedule(e: cutoff, cyclesFromNow: 2 * AdvancedGamingBrickMachine.CyclesPerFrame);
        foreach (var budget in new[] { 1, 17, AdvancedGamingBrickMachine.CyclesPerFrame }) {
            var before = machine.Cycles;
            _ = machine.RunCycles(cycles: budget);
            Require(condition: machine.Cycles == before + budget && bus.Halted && machine.Cpu.GetRegister(index: 15) == pc,
                detail: $"{(stop ? "STOP" : "HALT")} exceeded budget or executed an instruction while asleep");
        }
        scheduler.Deschedule(e: cutoff);
        var snapshot = machine.Snapshot();
        machine.SetKeyInput(keys: 0x03FE);
        _ = machine.RunCycles(cycles: 100);
        Require(condition: !bus.Halted, detail: "keypad input did not wake sleeping CPU");
        var future = machine.Snapshot();
        machine.Restore(snapshot: snapshot);
        machine.SetKeyInput(keys: 0x03FE);
        _ = machine.RunCycles(cycles: 100);
        Require(condition: future.Data.SequenceEqual(other: machine.Snapshot().Data), detail: "sleep/wake snapshot replay differed");
    }

    private static void CheckHaltedDma() {
        using var instance = PostMachine.BuildInstance(bios: new byte[ReplacementBios.ImageSize], rom: SyntheticRom.Create());
        var bus = instance.Machine.Bus;
        var dma = instance.GetRequiredService<IAgbDmaController>();
        ConfigureDma(bus: bus, dma: dma, channel: 0, count: 1, control: 0x9100);
        bus.Halt(stop: false);
        dma.OnVBlank(bus: bus);
        instance.Machine.Step();
        Require(condition: bus.Halted && bus.Read16(address: 0x02000000, access: BusAccessType.NonSequential) == 0xBEEF,
            detail: "timed DMA did not run while the CPU was halted");
    }

    private static void CheckDma(int channel, ushort count) {
        using var instance = PostMachine.BuildInstance(bios: new byte[ReplacementBios.ImageSize], rom: SyntheticRom.Create());
        var bus = instance.Machine.Bus;
        var dma = instance.GetRequiredService<IAgbDmaController>();
        ConfigureDma(bus: bus, dma: dma, channel: channel, count: count, control: 0x8100);
        dma.RunPending(bus: bus);
        // Expected length comes from the register's hardware width, independent of the controller's latches.
        var expected = channel == 3 ? (int)count : count % 16384;
        if (expected == 0) {
            expected = channel == 3 ? 65536 : 16384;
        }
        Require(condition: bus.Read16(address: (uint)(0x02000000 + 2 * (expected - 1)), access: BusAccessType.NonSequential) == 0xBEEF
            && bus.Read16(address: (uint)(0x02000000 + 2 * expected), access: BusAccessType.NonSequential) == 0,
            detail: $"DMA{channel} count {count:X4} copied the wrong number of halfwords");
    }

    private static void ConfigureDma(IAgbBus bus, IAgbDmaController dma, int channel, ushort count, ushort control) {
        bus.Write16(address: 0x03000000, value: 0xBEEF, access: BusAccessType.NonSequential);
        var offset = (uint)(0xB0 + 12 * channel);
        dma.WriteRegister(offset: offset, value: 0, bus: bus);
        dma.WriteRegister(offset: offset + 2, value: 0x0300, bus: bus);
        dma.WriteRegister(offset: offset + 4, value: 0, bus: bus);
        dma.WriteRegister(offset: offset + 6, value: 0x0200, bus: bus);
        dma.WriteRegister(offset: offset + 8, value: count, bus: bus);
        dma.WriteRegister(offset: offset + 10, value: control, bus: bus);
    }

    private static void Require(bool condition, string detail) {
        if (!condition) {
            throw new InvalidOperationException(message: detail);
        }
    }
}
