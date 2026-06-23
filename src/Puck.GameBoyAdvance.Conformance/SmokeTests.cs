using Microsoft.Extensions.DependencyInjection;

namespace Puck.GameBoyAdvance.Conformance;

// Hand-assembled ARM/Thumb vectors that exercise the core with no external ROMs: enough to prove the harness
// works and to guard the highest-risk behaviours (flag derivation, the barrel shifter, the MSR field mask fix,
// mode switching into Thumb). Each vector runs against a fresh FlatTestBus and asserts the resulting state.
internal static class SmokeTests {
    private const uint FlagN = 1u << 31;
    private const uint FlagZ = 1u << 30;
    private const uint FlagC = 1u << 29;
    private const uint FlagV = 1u << 28;
    private const uint FlagT = 1u << 5;

    // ARM "branch to self" — a safe landing pad so over-stepping a vector just re-executes the loop.
    private const uint ArmSelfLoop = 0xEAFFFFFEu;

    // Thumb "branch to self".
    private const ushort ThumbSelfLoop = 0xE7FE;

    private static int s_passed;
    private static int s_failed;

    public static int Run() {
        s_passed = 0;
        s_failed = 0;

        Console.WriteLine("== Puck.GameBoyAdvance CPU smoke tests ==");

        DataProcessingAndFlags();
        BarrelShifterCarry();
        MsrFieldMaskFix();
        ThumbModeAndExecution();
        InterruptsTimersAndDma();
        PpuTiming();
        SpriteRendering();
        AffineBackgroundRendering();
        BrightnessBlend();
        BiosIrqDispatch();
        DependencyInjectionScope();

        Console.WriteLine($"== smoke: {s_passed} passed, {s_failed} failed ==");

        return s_failed;
    }

    private static void DataProcessingAndFlags() {
        // MOV r0,#10 ; MOV r1,#3 ; SUB r2,r0,r1 ; ADD r3,r0,r1 ; B .
        var cpu = RunArm(steps: 8,
            0xE3A0000Au,
            0xE3A01003u,
            0xE0402001u,
            0xE0803001u,
            ArmSelfLoop);

        Check(name: "SUB r2,r0,r1 = 7", ok: cpu.GetRegister(2) == 7u, detail: $"got {cpu.GetRegister(2)}");
        Check(name: "ADD r3,r0,r1 = 13", ok: cpu.GetRegister(3) == 13u, detail: $"got {cpu.GetRegister(3)}");

        // MVN r0,#0 (0xFFFFFFFF) ; ADDS r1,r0,#1 -> r1=0, carry+zero set ; B .
        cpu = RunArm(steps: 6,
            0xE3E00000u,
            0xE2901001u,
            ArmSelfLoop);

        Check(name: "ADDS wraps to 0", ok: cpu.GetRegister(1) == 0u, detail: $"got {cpu.GetRegister(1)}");
        Check(name: "ADDS sets carry", ok: (cpu.Cpsr & FlagC) != 0u);
        Check(name: "ADDS sets zero", ok: (cpu.Cpsr & FlagZ) != 0u);
        Check(name: "ADDS clears negative", ok: (cpu.Cpsr & FlagN) == 0u);
        Check(name: "ADDS clears overflow", ok: (cpu.Cpsr & FlagV) == 0u);
    }

    private static void BarrelShifterCarry() {
        // MOV r0,#1 ; MOV r1,r0,LSL#31 (0x80000000) ; MOVS r2,r1,LSL#1 -> r2=0, carry from bit31 ; B .
        var cpu = RunArm(steps: 8,
            0xE3A00001u,
            0xE1A01F80u,
            0xE1B02081u,
            ArmSelfLoop);

        Check(name: "LSL#31 produces 0x80000000", ok: cpu.GetRegister(1) == 0x80000000u, detail: $"got 0x{cpu.GetRegister(1):X8}");
        Check(name: "MOVS LSL#1 result 0", ok: cpu.GetRegister(2) == 0u, detail: $"got 0x{cpu.GetRegister(2):X8}");
        Check(name: "MOVS LSL#1 carry from bit31", ok: (cpu.Cpsr & FlagC) != 0u);
        Check(name: "MOVS LSL#1 zero set", ok: (cpu.Cpsr & FlagZ) != 0u);
    }

    private static void MsrFieldMaskFix() {
        // MSR CPSR_f,#0xFF000000 ; MRS r0,CPSR ; B .  — the flag byte must only land NZCV (0xF0000000);
        // reserved bits 24–27 must NOT stick (the field-mask fix verified against mGBA).
        var cpu = RunArm(steps: 6,
            0xE328F4FFu,
            0xE10F0000u,
            ArmSelfLoop);

        Check(name: "MSR sets NZCV", ok: (cpu.GetRegister(0) & 0xF0000000u) == 0xF0000000u, detail: $"got 0x{cpu.GetRegister(0):X8}");
        Check(name: "MSR drops reserved bits 24-27", ok: (cpu.GetRegister(0) & 0x0F000000u) == 0u, detail: $"got 0x{cpu.GetRegister(0):X8}");
    }

    private static void ThumbModeAndExecution() {
        // ARM: MOV r0,#0x81 ; BX r0  (enter Thumb at 0x80)
        // Thumb @0x80: MOV r1,#5 ; ADD r1,#3 ; B .
        var bus = new FlatTestBus();

        bus.LoadArm(byteOffset: 0u, 0xE3A00081u, 0xE12FFF10u);
        bus.LoadThumb(byteOffset: 0x80u, 0x2105, 0x3103, ThumbSelfLoop);

        var cpu = new Arm7Tdmi(bus: bus);

        for (var i = 0; i < 8; ++i) {
            cpu.Step();
        }

        Check(name: "BX entered Thumb state", ok: (cpu.Cpsr & FlagT) != 0u);
        Check(name: "Thumb MOV/ADD r1 = 8", ok: cpu.GetRegister(1) == 8u, detail: $"got {cpu.GetRegister(1)}");
    }

    private static void InterruptsTimersAndDma() {
        // Interrupt controller: line asserts only with IME + IE + IF; IF is write-one-to-clear.
        var controller = new GbaInterruptController();
        var timer0Bit = (ushort)(1u << (int)InterruptSource.Timer0);

        Check(name: "IRQ line low at rest", ok: !controller.LineAsserted);

        controller.WriteRegister(offset: 0x208u, value: 1);          // IME = 1
        controller.WriteRegister(offset: 0x200u, value: timer0Bit);  // IE: timer 0
        controller.Request(source: InterruptSource.Timer0);

        Check(name: "IRQ asserts with IME+IE+IF", ok: controller.LineAsserted);

        controller.WriteRegister(offset: 0x202u, value: timer0Bit);  // acknowledge

        Check(name: "IRQ IF write-one-to-clear", ok: !controller.LineAsserted);

        // Timer overflow raises its interrupt: reload 0xFFFE, prescaler 1, two ticks to overflow.
        var timerInterrupts = new GbaInterruptController();

        timerInterrupts.WriteRegister(offset: 0x208u, value: 1);
        timerInterrupts.WriteRegister(offset: 0x200u, value: timer0Bit);

        var timers = new GbaTimerController(interrupts: timerInterrupts);

        timers.WriteRegister(offset: 0x100u, value: 0xFFFE);          // reload
        timers.WriteRegister(offset: 0x102u, value: 0x00C0);          // enable + IRQ, prescaler 1
        timers.Step(cycles: 2);

        Check(name: "timer overflow raises IRQ", ok: timerInterrupts.LineAsserted);

        // Immediate DMA copies a word through the full bus I/O path.
        var dmaInterrupts = new GbaInterruptController();
        var bus = new GbaBus(
            bios: new ReplacementBios(image: new byte[ReplacementBios.ImageSize]),
            cartridge: new GbaCartridge(rom: new byte[256]),
            interrupts: dmaInterrupts,
            timers: new GbaTimerController(interrupts: dmaInterrupts),
            dma: new GbaDmaController(interrupts: dmaInterrupts),
            ppu: new GbaPpu(interrupts: dmaInterrupts));

        bus.Write32(address: 0x03000000u, value: 0xCAFEBABEu, access: BusAccessType.NonSequential);
        bus.Write16(address: 0x040000B0u, value: 0x0000, access: BusAccessType.NonSequential); // SAD lo
        bus.Write16(address: 0x040000B2u, value: 0x0300, access: BusAccessType.NonSequential); // SAD hi → 0x03000000
        bus.Write16(address: 0x040000B4u, value: 0x0100, access: BusAccessType.NonSequential); // DAD lo
        bus.Write16(address: 0x040000B6u, value: 0x0300, access: BusAccessType.NonSequential); // DAD hi → 0x03000100
        bus.Write16(address: 0x040000B8u, value: 0x0001, access: BusAccessType.NonSequential); // count = 1
        bus.Write16(address: 0x040000BAu, value: 0x8400, access: BusAccessType.NonSequential); // enable + 32-bit + immediate

        var copied = bus.Read32(address: 0x03000100u, access: BusAccessType.NonSequential);

        Check(name: "immediate DMA copies a word", ok: copied == 0xCAFEBABEu, detail: $"got 0x{copied:X8}");
    }

    private static void PpuTiming() {
        var interrupts = new GbaInterruptController();
        var ppu = new GbaPpu(interrupts: interrupts);

        interrupts.WriteRegister(offset: 0x208u, value: 1);
        interrupts.WriteRegister(offset: 0x200u, value: (ushort)(1u << (int)InterruptSource.VBlank));
        ppu.WriteRegister(offset: 0x04u, value: 0x08); // enable V-blank interrupt

        Check(name: "PPU starts at scanline 0", ok: ppu.ReadRegister(offset: 0x06u) == 0);

        // Advance to the first visible scanline's end and confirm an H-blank was flagged.
        ppu.Step(cycles: 960);

        Check(name: "PPU H-blank flagged on visible line", ok: ppu.ConsumeHBlankStarted());

        // Advance to scanline 160 (V-blank). 160 lines * 1232 cycles, less the 960 already stepped.
        ppu.Step(cycles: (160 * 1232) - 960);

        Check(name: "PPU reaches V-blank at line 160", ok: ppu.ReadRegister(offset: 0x06u) == 160);
        Check(name: "PPU V-blank flag set", ok: (ppu.ReadRegister(offset: 0x04u) & 0x1) != 0);
        Check(name: "PPU V-blank started flag", ok: ppu.ConsumeVBlankStarted());
        Check(name: "PPU V-blank raised IRQ", ok: interrupts.LineAsserted);

        // Run out the rest of the frame back to the top.
        ppu.Step(cycles: (TotalLinesForTest - 160) * 1232);

        Check(name: "PPU wraps to scanline 0", ok: ppu.ReadRegister(offset: 0x06u) == 0);
    }

    private const int TotalLinesForTest = 228;

    private static void SpriteRendering() {
        var ppu = new GbaPpu(interrupts: new GbaInterruptController());

        // Mode 0, OBJ enabled (bit 12), 1-D tile mapping (bit 6).
        ppu.WriteRegister(offset: 0x00u, value: 0x1040);

        // Object tile 0 in OBJ VRAM (0x06010000): an 8×8 4bpp tile of colour index 1. Written as halfwords
        // because 8-bit writes to the object region are dropped by hardware (the quirk above).
        for (uint i = 0; i < 32u; i += 2u) {
            ppu.WriteVideo(address: 0x06010000u + i, width: 2, value: 0x1111);
        }

        // OBJ palette colour index 1 = red (BGR555 0x001F); OBJ palette starts at palette index 256.
        ppu.WriteVideo(address: 0x05000000u + ((256u + 1u) * 2u), width: 2, value: 0x001F);

        // OAM sprite 0 at (0,0), 8×8 square, tile 0, priority 0.
        ppu.WriteVideo(address: 0x07000000u, width: 2, value: 0x0000); // attr0: y=0, normal
        ppu.WriteVideo(address: 0x07000002u, width: 2, value: 0x0000); // attr1: x=0, 8×8
        ppu.WriteVideo(address: 0x07000004u, width: 2, value: 0x0000); // attr2: tile 0

        ppu.Step(cycles: 1232); // render scanline 0

        var pixel = ppu.Framebuffer[0];
        var backdrop = ppu.Framebuffer[100]; // x=100 is beyond the 8-pixel sprite → backdrop

        Check(name: "sprite pixel is red", ok: pixel == 0xFF0000FFu, detail: $"got 0x{pixel:X8}");
        Check(name: "non-sprite pixel is backdrop", ok: (backdrop & 0x00FFFFFFu) == 0u, detail: $"got 0x{backdrop:X8}");
    }

    private static void AffineBackgroundRendering() {
        var ppu = new GbaPpu(interrupts: new GbaInterruptController());

        // Mode 2 with BG2 (affine) enabled.
        ppu.WriteRegister(offset: 0x00u, value: 0x0402);
        // BG2CNT: char base 0, screen base block 8 (map at 0x4000), size 0 (128×128).
        ppu.WriteRegister(offset: 0x0Cu, value: 0x0800);
        // Identity affine matrix (PA = PD = 1.0 in 8.8), reference point 0.
        ppu.WriteRegister(offset: 0x20u, value: 0x0100);
        ppu.WriteRegister(offset: 0x22u, value: 0x0000);
        ppu.WriteRegister(offset: 0x24u, value: 0x0000);
        ppu.WriteRegister(offset: 0x26u, value: 0x0100);

        // Tile 0 (char base 0) is an 8bpp tile of colour index 1; map entry (0,0) = tile 0 (default).
        for (uint i = 0; i < 64u; i += 2u) {
            ppu.WriteVideo(address: 0x06000000u + i, width: 2, value: 0x0101);
        }

        // BG palette colour index 1 = red.
        ppu.WriteVideo(address: 0x05000002u, width: 2, value: 0x001F);

        ppu.Step(cycles: 1232); // render scanline 0

        var pixel = ppu.Framebuffer[0];

        Check(name: "affine BG renders (identity transform)", ok: pixel == 0xFF0000FFu, detail: $"got 0x{pixel:X8}");
    }

    private static void BrightnessBlend() {
        var ppu = new GbaPpu(interrupts: new GbaInterruptController());

        // Mode 0, BG0 enabled.
        ppu.WriteRegister(offset: 0x00u, value: 0x0100);
        // BG0CNT: char base 0, screen base block 8 (map at 0x4000), 4bpp.
        ppu.WriteRegister(offset: 0x08u, value: 0x0800);
        // BLDCNT: brightness-increase effect (bits 6-7 = 10), BG0 as 1st target.
        ppu.WriteRegister(offset: 0x50u, value: 0x0081);
        // BLDY: EVY = 16 (maximum) — brightens any colour to full white.
        ppu.WriteRegister(offset: 0x54u, value: 0x0010);

        // Tile 0 (char base 0): a 4bpp tile of colour index 1.
        for (uint i = 0; i < 32u; i += 2u) {
            ppu.WriteVideo(address: 0x06000000u + i, width: 2, value: 0x1111);
        }

        // BG palette index 1 = red.
        ppu.WriteVideo(address: 0x05000002u, width: 2, value: 0x001F);

        ppu.Step(cycles: 1232);

        var pixel = ppu.Framebuffer[0];

        Check(name: "brightness blend → white at EVY=16", ok: pixel == 0xFFFFFFFFu, detail: $"got 0x{pixel:X8}");
    }

    private static void BiosIrqDispatch() {
        // Needs the real replacement BIOS; skip cleanly when only the zero stub is present.
        if (RomRunner.BiosImage.Span.IndexOfAnyExcept((byte)0) < 0) {
            Console.WriteLine("  [SKIP] BIOS IRQ dispatch (no replacement BIOS loaded)");

            return;
        }

        // A cartridge that just spins, so the CPU has something to be interrupted out of.
        var rom = new byte[4];

        BitConverter.TryWriteBytes(destination: rom, value: 0xEAFFFFFEu); // b .

        var services = new ServiceCollection();

        _ = services.AddGameBoyAdvance();
        _ = services.AddReplacementBios(image: RomRunner.BiosImage);
        services.AddScoped<GbaCartridge>(implementationFactory: _ => new GbaCartridge(rom: rom));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var machine = scope.ServiceProvider.GetRequiredService<GameBoyAdvanceMachine>();

        machine.DirectBoot();

        // A user IRQ handler in IWRAM: write 0xAA to 0x03001000, then return. (r2 = 0x03000000 + 0x1000.)
        uint[] handler = { 0xE3A010AAu, 0xE3A02403u, 0xE2822A01u, 0xE5821000u, 0xE12FFF1Eu };

        for (var i = 0; i < handler.Length; ++i) {
            machine.Bus.Write32(address: 0x03000000u + ((uint)i * 4u), value: handler[i], access: BusAccessType.NonSequential);
        }

        machine.Bus.Write32(address: 0x03007FFCu, value: 0x03000000u, access: BusAccessType.NonSequential); // BIOS user-IRQ-handler pointer
        machine.Bus.Write16(address: 0x04000004u, value: 0x0008, access: BusAccessType.NonSequential);       // DISPSTAT: V-blank IRQ enable
        machine.Bus.Write16(address: 0x04000200u, value: 0x0001, access: BusAccessType.NonSequential);       // IE: V-blank
        machine.Bus.Write16(address: 0x04000208u, value: 0x0001, access: BusAccessType.NonSequential);       // IME

        for (long i = 0; i < 1_000_000; ++i) {
            machine.Step();
        }

        var marker = machine.Bus.Read32(address: 0x03001000u, access: BusAccessType.NonSequential);

        Check(name: "BIOS dispatches V-blank IRQ to user handler", ok: marker == 0xAAu, detail: $"got 0x{marker:X8}");
    }

    private static void DependencyInjectionScope() {
        // Prove the composition root + scope-per-machine: register a preloaded bus, resolve IArmCpu from a scope.
        var bus = new FlatTestBus();

        bus.LoadArm(byteOffset: 0u, 0xE3A0002Au, ArmSelfLoop); // MOV r0,#42 ; B .

        var services = new ServiceCollection();

        _ = services.AddGameBoyAdvance();
        services.AddScoped<IGbaBus>(implementationFactory: _ => bus);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var cpu = scope.ServiceProvider.GetRequiredService<IArmCpu>();

        for (var i = 0; i < 4; ++i) {
            cpu.Step();
        }

        Check(name: "DI-resolved IArmCpu executes (r0 = 42)", ok: cpu.GetRegister(0) == 42u, detail: $"got {cpu.GetRegister(0)}");
    }

    private static Arm7Tdmi RunArm(int steps, params uint[] program) {
        var bus = new FlatTestBus();

        bus.LoadArm(byteOffset: 0u, program);

        var cpu = new Arm7Tdmi(bus: bus);

        for (var i = 0; i < steps; ++i) {
            cpu.Step();
        }

        return cpu;
    }

    private static void Check(string name, bool ok, string detail = "") {
        if (ok) {
            ++s_passed;

            Console.WriteLine($"  [PASS] {name}");
        }
        else {
            ++s_failed;

            Console.WriteLine($"  [FAIL] {name}{(string.IsNullOrEmpty(detail) ? "" : $" — {detail}")}");
        }
    }
}
