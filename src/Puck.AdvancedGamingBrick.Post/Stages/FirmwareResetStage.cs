using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Native nonreturning reset, selective RAM/register reset, and IRQ-woken sleep contracts.</summary>
/// <remarks>Behavioral reference: https://retrointernals.github.io/gba-bios-reference/ (SWIs 00–03, 26–27).
/// Fixtures contain only original instructions; no retail timing or undocumented register-clobber parity is claimed.</remarks>
internal sealed class FirmwareResetStage : IPostStage<PostContext> {
    private static readonly (uint Address, uint Size, uint Flag)[] Regions = [
        (0x02000000, 0x40000, 1), (0x03000000, 0x7E00, 2),
        (0x05000000, 0x400, 4), (0x06000000, 0x18000, 8), (0x07000000, 0x400, 16),
    ];

    /// <inheritdoc/>
    public string Name => "firmware-reset";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var failures = new List<string>();
        var cases = 0;
        RunImage(name: "Puck", bios: null);
        var hasRetail = AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.RealVerified;
        if (hasRetail) {
            RunImage(name: "retail", bios: context.BiosImage.ToArray());
        }
        return failures.Count == 0
            ? PostStageOutcome.Pass(detail: $"44 asset-free native ARM/Thumb cases: ROM/EWRAM SoftReset entry markers, register/stack hygiene and RAM boundaries; cold HardReset relaunch; selective RAM/I/O groups, forced blank and Direct Sound FIFO queued-silence/playing/partial-word reset; Halt/Stop/CustomHalt timer/keypad wake and snapshot replay. {(hasRetail ? "42 verified-retail black-box controls also passed; HardReset is Puck-only (original header)." : "Verified-retail controls not run: no verified external BIOS." )} Functional contracts, not retail timing or STOP clock-freeze parity.")
            : PostStageOutcome.Fail(detail: $"{failures.Count}/{cases} cases failed: {string.Join(separator: "; ", values: failures)}");

        void RunImage(string name, byte[]? bios) {
            foreach (var thumb in new[] { false, true }) {
                foreach (var flag in new byte[] { 0, 1, 0xFF }) {
                    RunCase(name: $"{name} SoftReset Thumb={thumb} flag={flag:X2}", test: () => CheckReset(thumb: thumb, flag: flag, hard: false, bios: bios));
                }
                if (bios is null) {
                    RunCase(name: $"{name} HardReset Thumb={thumb}", test: () => CheckReset(thumb: thumb, flag: 1, hard: true, bios: bios));
                }
                foreach (var flags in new uint[] { 0, 1, 2, 4, 8, 16, 32, 64, 128, 255 }) {
                    RunCase(name: $"{name} RegisterRamReset Thumb={thumb} flags={flags:X2}", test: () => CheckRegisters(thumb: thumb, flags: flags, bios: bios));
                }
                foreach (var flags in new uint[] { 0, 0x40, 0x80, 0xFF }) {
                    RunCase(name: $"{name} FIFO reset Thumb={thumb} flags={flags:X2}", test: () => CheckFifos(thumb: thumb, flags: flags, bios: bios));
                }
                foreach (var (number, value) in new (byte, uint)[] { (2, 0x80), (3, 0), (0x27, 0), (0x27, 0x80) }) {
                    RunCase(name: $"{name} sleep SWI={number:X2} Thumb={thumb} r2={value:X2}", test: () => CheckSleep(thumb: thumb, number: number, value: value, bios: bios));
                }
            }
        }

        void RunCase(string name, Action test) {
            ++cases;
            try {
                test();
            } catch (InvalidOperationException exception) {
                failures.Add(item: $"{name}: {exception.Message}");
            }
        }
    }

    private static void CheckReset(bool thumb, byte flag, bool hard, byte[]? bios) {
        using var core = new AdvancedGamingBrickCore(configuration: AgbFirmware.CreateConfiguration(cartridgeRom: FirmwareResetCartridge.Create(number: hard ? (byte)0x26 : (byte)0, thumb: thumb), bootMode: MachineBootMode.Fast, bios: bios));
        var machine = core.Instance.Machine;
        var bus = machine.Bus;
        machine.Cpu.SetupDirectBoot(entryPoint: FirmwareResetCartridge.Caller);
        for (var index = 0; index <= 12; ++index) {
            machine.Cpu.SetRegister(index: index, value: 0xABCD1000u + (uint)index);
        }
        SeedRegions(bus: bus);
        FirmwareSwiProbe.WriteBytes(bus: bus, address: 0x02000000, bytes: FirmwareResetCartridge.Entry(marker: 0xA5));
        // Preserve probes lie below every deliberate caller stack. The reserved
        // top window is checked before the entry marker executes any instructions.
        Write(bus: bus, address: 0x03007DFC, value: 0xC35A96F0);
        for (uint address = 0x03007E00; address < 0x03008000; address += 4) {
            Write(bus: bus, address: address, value: 0xC35A96F0);
        }
        bus.Write8(address: 0x03007FFA, value: flag, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x04000208, value: 0, access: BusAccessType.NonSequential);
        var target = hard || flag == 0 ? 0x08000000u : 0x02000000u;
        var before = machine.Cycles;
        var limit = hard ? 120L * AdvancedGamingBrickMachine.CyclesPerFrame : 2_000_000L;
        var artwork = hard ? FirmwarePresentationArtwork.Create() : [];
        var nextFrame = before + AdvancedGamingBrickMachine.CyclesPerFrame;
        var sawArtwork = false;
        while (machine.Cycles - before < limit && machine.Cpu.GetRegister(index: 15) != target) {
            machine.Step();
            if (hard && machine.Cycles >= nextFrame) {
                sawArtwork |= core.Framebuffer.SequenceEqual(other: artwork);
                nextFrame += AdvancedGamingBrickMachine.CyclesPerFrame;
            }
        }
        Require(condition: machine.Cpu.GetRegister(index: 15) == target, detail: $"never branched to entry {target:X8}; PC={machine.Cpu.GetRegister(index: 15):X8}, CPSR={machine.Cpu.Cpsr:X8}");
        Require(condition: machine.Cpu.Cpsr == 0x1F, detail: $"entry CPSR={machine.Cpu.Cpsr:X8}, expected ARM System with clean flags and IRQ/FIQ enabled");
        for (var index = 0; index <= 12; ++index) {
            Require(condition: machine.Cpu.GetRegister(index: index) == 0, detail: $"entry r{index} was not cleared");
        }
        Require(condition: machine.Cpu.GetRegister(index: 14) == target, detail: "entry LR did not identify reset target");
        for (uint address = 0x03007E00; address < 0x03008000; address += 4) {
            Expect(bus: bus, address: address, expected: 0, detail: "reserved IWRAM clear");
        }
        if (hard) {
            Require(condition: sawArtwork, detail: "HardReset never replayed the native PUCK/BYTETERRACE wordmark");
            Require(condition: machine.Cycles - before >= 72L * AdvancedGamingBrickMachine.CyclesPerFrame, detail: "HardReset skipped the native cold presentation interval");
            CheckRegionSentinels(bus: bus, flags: 255, skipFirstEwram: false);
            Expect(bus: bus, address: 0x03007DFC, expected: 0, detail: "HardReset IWRAM reset");
        } else {
            CheckRegionSentinels(bus: bus, flags: 0, skipFirstEwram: true);
            Expect(bus: bus, address: 0x03007DFC, expected: 0xC35A96F0, detail: "SoftReset lower boundary preservation");
        }
        var marker = hard || flag == 0 ? 0x5Au : 0xA5u;
        for (var instruction = 0; instruction < 64 && Read(bus: bus, address: FirmwareResetCartridge.Marker) != marker; ++instruction) {
            machine.Step();
        }
        Expect(bus: bus, address: FirmwareResetCartridge.Marker, expected: marker, detail: "native reset destination marker");
        foreach (var (offset, expected) in new (uint, uint)[] { (0, 0x03007F00), (4, 0x03007FA0), (8, 0), (12, 0x03007FE0), (16, 0), (24, 0), (28, 0) }) {
            Expect(bus: bus, address: FirmwareResetCartridge.Result + offset, expected: expected, detail: "native banked stack/SPSR capture");
        }
    }

    private static void CheckRegisters(bool thumb, uint flags, byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(number: 1, thumb: thumb, r0: flags, bios: bios, setup: static machine => {
            var bus = machine.Bus;
            SeedRegions(bus: bus);
            Write(bus: bus, address: 0x03007E00, value: 0xC35A96F0);
            Write(bus: bus, address: 0x03007FF8, value: 0xC35A96F0);
            bus.Write16(address: 0x04000000, value: 0x0403, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000008, value: 0x1234, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000132, value: 0x4001, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000134, value: 0x8001, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000200, value: 0x1000, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000204, value: 0x4000, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000084, value: 0x80, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000080, value: 0x1177, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000082, value: 0x030E, access: BusAccessType.NonSequential);
        });
        var bus = core.Instance.Machine.Bus;
        CheckRegionSentinels(bus: bus, flags: flags, skipFirstEwram: false);
        Expect(bus: bus, address: 0x03007E00, expected: 0xC35A96F0, detail: "RegisterRamReset reserved IWRAM start preservation");
        Expect(bus: bus, address: 0x03007FF8, expected: 0xC35A96F0, detail: "RegisterRamReset reserved IWRAM end preservation");
        ExpectHalf(bus: bus, address: 0x04000000, expected: 0x80, detail: "unconditional forced blank");
        var other = (flags & 128) != 0;
        ExpectHalf(bus: bus, address: 0x04000008, expected: other ? (ushort)0 : (ushort)0x1234, detail: "BG0CNT selective reset");
        ExpectHalf(bus: bus, address: 0x04000200, expected: other ? (ushort)0 : (ushort)0x1000, detail: "IE selective reset");
        ExpectHalf(bus: bus, address: 0x04000204, expected: other ? (ushort)0 : (ushort)0x4000, detail: "WAITCNT selective reset");
        ExpectHalf(bus: bus, address: 0x04000132, expected: other ? (ushort)0 : (ushort)0x4001, detail: "KEYCNT selective reset");
        ExpectHalf(bus: bus, address: 0x04000134, expected: (flags & 32) != 0 ? (ushort)0x8000 : (ushort)0x8001, detail: "RCNT selective serial reset");
        ExpectHalf(bus: bus, address: 0x04000080, expected: (flags & 64) != 0 ? (ushort)0 : (ushort)0x1177, detail: "SOUNDCNT_L selective reset");
        ExpectHalf(bus: bus, address: 0x04000082, expected: (flags & 64) != 0 ? (ushort)0x000E : (ushort)0x030E, detail: "SOUNDCNT_H selective reset");
    }

    private static void CheckSleep(bool thumb, byte number, uint value, byte[]? bios) {
        using var core = new AdvancedGamingBrickCore(configuration: AgbFirmware.CreateConfiguration(cartridgeRom: FirmwareResetCartridge.Create(number: number, thumb: thumb), bootMode: MachineBootMode.Fast, bios: bios));
        var machine = core.Instance.Machine;
        var bus = machine.Bus;
        machine.Cpu.SetupDirectBoot(entryPoint: FirmwareResetCartridge.Caller);
        machine.Cpu.SetRegister(index: 0, value: value ^ 0x80); // CustomHalt must use r2, not r0.
        machine.Cpu.SetRegister(index: 2, value: value);
        bus.Write16(address: 0x04000208, value: 0, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x04000200, value: 0x1008, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x04000132, value: 0x4001, access: BusAccessType.NonSequential);
        for (var instruction = 0; instruction < 4096 && !bus.Halted; ++instruction) {
            machine.Step();
        }
        Require(condition: bus.Halted, detail: "SWI never entered low-power state");
        var pc = machine.Cpu.GetRegister(index: 15);
        _ = machine.RunCycles(cycles: 1234);
        Require(condition: bus.Halted && machine.Cpu.GetRegister(index: 15) == pc, detail: "sleep executed instructions without a wake source");
        var sleeping = machine.Snapshot();
        // A pending enabled timer IRQ distinguishes HALT from STOP without
        // claiming the emulator currently freezes all peripheral clocks in STOP.
        core.Instance.GetRequiredService<IAgbInterruptController>().Request(source: InterruptSource.Timer0);
        _ = machine.RunCycles(cycles: 4096);
        var stop = number == 3 || (number == 0x27 && (value & 0x80) != 0);
        Require(condition: bus.Halted == stop, detail: "timer IRQ wake selected the wrong HALT/STOP mode");
        machine.Restore(snapshot: sleeping);
        Wake();
        var future = machine.Snapshot();
        machine.Restore(snapshot: sleeping);
        Wake();
        Require(condition: future.Data.SequenceEqual(other: machine.Snapshot().Data), detail: "native sleep/wake snapshot replay diverged");

        void Wake() {
            machine.SetKeyInput(keys: 0x03FE);
            for (var instruction = 0; instruction < 4096 && machine.Cpu.GetRegister(index: 7) != 0xEE; ++instruction) {
                machine.Step();
            }
            Require(condition: !bus.Halted && machine.Cpu.GetRegister(index: 7) == 0xEE, detail: "keypad IRQ did not resume the native SWI caller with IME=0");
            Require(condition: (machine.Cpu.Cpsr & 0x3F) == (thumb ? 0x3Fu : 0x1Fu), detail: "sleep return lost caller ARM/Thumb state");
            ExpectHalf(bus: bus, address: 0x04000208, expected: 0, detail: "sleep changed IME");
            ExpectHalf(bus: bus, address: 0x04000202, expected: 0x1000, detail: "sleep unexpectedly acknowledged keypad IF");
        }
    }

    private static void CheckFifos(bool thumb, uint flags, byte[]? bios) {
        using var core = FirmwareSwiProbe.Run(number: 1, thumb: thumb, r0: flags, bios: bios, setup: static machine => {
            var bus = machine.Bus;
            bus.Write16(address: 0x04000084, value: 0x80, access: BusAccessType.NonSequential);
            bus.Write16(address: 0x04000082, value: 0x880E, access: BusAccessType.NonSequential);
            for (var timer = 0u; timer < 4; ++timer) {
                bus.Write16(address: 0x04000102 + timer * 4, value: 0, access: BusAccessType.NonSequential);
            }
            for (var fifo = 0u; fifo < 2; ++fifo) {
                for (var word = 0u; word < 3; ++word) {
                    Write(bus: bus, address: 0x040000A0 + fifo * 4, value: 0x31231507 + fifo * 0x08080808 + word);
                }
            }
            // One explicit peripheral clock seeds a partly consumed playing
            // word. Timers remain disabled throughout the native SWI call.
            machine.Apu.OnTimerOverflow(timer: 0);
            var apu = (AgbApu)machine.Apu;
            for (var fifo = 0; fifo < 2; ++fifo) {
                Require(condition: apu.DebugFifoWordCount(fifo: fifo) == 2 && apu.DebugFifoPlayingBytes(fifo: fifo) == 3,
                    detail: "FIFO fixture failed to seed ring and playing buffers");
                bus.Write16(address: 0x040000A0 + (uint)fifo * 4, value: 0x7967, access: BusAccessType.NonSequential);
            }
        });
        var apu = (AgbApu)core.Instance.Machine.Apu;
        var reset = (flags & 0x40) != 0;
        for (var fifo = 0; fifo < 2; ++fifo) {
            var words = apu.DebugFifoWordCount(fifo: fifo);
            var playing = apu.DebugFifoPlayingBytes(fifo: fifo);
            // The verified retail control refills two zero words after reset;
            // it does not leave an empty ring. Observable silence matters here,
            // not the misleading identical readable SOUNDCNT_H value alone.
            Require(condition: words == 2 && playing == (reset ? 0 : 3),
                detail: $"FIFO {fifo}: ring={words}, playing={playing}; expected {(reset ? "2/0 after sound reset" : "2/3 preserved")}");
            // Two new bytes must not complete a word using pre-reset partial
            // data. Without sound reset they do complete the preserved word.
            core.Instance.Machine.Bus.Write16(address: 0x04000084, value: 0x80, access: BusAccessType.NonSequential);
            core.Instance.Machine.Bus.Write16(address: 0x040000A0 + (uint)fifo * 4, value: 0x5B49, access: BusAccessType.NonSequential);
            Require(condition: apu.DebugFifoWordCount(fifo: fifo) == (reset ? 2 : 3), detail: $"FIFO {fifo} partial-word reset/preservation mismatch");
        }
        if (reset) {
            for (var sample = 0; sample < 8; ++sample) {
                apu.OnTimerOverflow(timer: 0);
                for (var fifo = 0; fifo < 2; ++fifo) {
                    Require(condition: apu.DebugDirectSound(fifo: fifo) == 0, detail: $"FIFO {fifo} reset left stale queued sample {sample}");
                }
            }
            for (var fifo = 0; fifo < 2; ++fifo) {
                Require(condition: apu.DebugFifoWordCount(fifo: fifo) == 0 && apu.DebugFifoPlayingBytes(fifo: fifo) == 0,
                    detail: $"FIFO {fifo} reset left more than two queued zero words");
            }
        }
    }

    private static void SeedRegions(IAgbBus bus) {
        foreach (var (address, size, _) in Regions) {
            foreach (var offset in new uint[] { 0, size / 2, size - 4 }) {
                Write(bus: bus, address: address + offset, value: 0xC35A96F0);
            }
        }
    }

    private static void CheckRegionSentinels(IAgbBus bus, uint flags, bool skipFirstEwram) {
        foreach (var (address, size, flag) in Regions) {
            foreach (var offset in new uint[] { 0, size / 2, size - 4 }) {
                if (skipFirstEwram && address == 0x02000000 && offset == 0) {
                    continue; // The independent EWRAM entry fixture intentionally occupies this word.
                }
                Expect(bus: bus, address: address + offset, expected: (flags & flag) != 0 ? 0 : 0xC35A96F0, detail: "selective RAM region sentinel");
            }
        }
    }

    private static uint Read(IAgbBus bus, uint address) => bus.Read32(address: address, access: BusAccessType.NonSequential);
    private static void Write(IAgbBus bus, uint address, uint value) => bus.Write32(address: address, value: value, access: BusAccessType.NonSequential);
    private static void Expect(IAgbBus bus, uint address, uint expected, string detail) {
        var actual = Read(bus: bus, address: address);
        Require(condition: actual == expected, detail: $"{detail} at {address:X8}: {actual:X8}, expected {expected:X8}");
    }
    private static void ExpectHalf(IAgbBus bus, uint address, ushort expected, string detail) {
        var actual = bus.Read16(address: address, access: BusAccessType.NonSequential);
        Require(condition: actual == expected, detail: $"{detail} at {address:X8}: {actual:X4}, expected {expected:X4}");
    }
    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
}
