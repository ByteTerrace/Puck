namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Emits one revision's boot program. The program runs in two halves. Everything before the divider reset — the video
/// setup, the logo and header-checksum verification, the mark's scroll, and the table lookups that resolve the
/// revision's handoff counter — costs whatever it costs. Everything after it is straight-line, so the handoff counter
/// is exactly the delay the program computed plus a constant the builder solves by booting the image.
/// </summary>
/// <remarks>
/// The high-page scratch the program stages its computed values in:
/// <list type="table">
/// <item><term>0xFF80</term><description>the predicted handoff counter, low then high byte (Color only).</description></item>
/// <item><term>0xFF82</term><description>the enable-distance constant for the cartridge's class (Color only).</description></item>
/// <item><term>0xFF84</term><description>the handoff B, D, E, H, and L bytes, in that order (Color only).</description></item>
/// <item><term>0xFF89</term><description>the title checksum, then the fourth title letter (Color only).</description></item>
/// <item><term>0xFF8B</term><description>the high-page ports the palette whitening writes through, index then data:
/// the real palette registers for a color cartridge, and two scratch bytes for a cartridge the Color hardware runs in
/// compatibility mode, whose palette RAM the seeded handoff leaves clear (Color only).</description></item>
/// </list>
/// </remarks>
internal static class BootRomProgram {
    private const byte ScratchCounter = 0x80;
    private const byte ScratchEnableDistance = 0x82;
    private const byte ScratchRegisterB = 0x84;
    private const byte ScratchTitleChecksum = 0x89;
    private const byte ScratchFourthLetter = 0x8A;
    private const byte ScratchPaletteIndexPort = 0x8B;
    private const byte ScratchPaletteDataPort = 0x8C;

    private const ushort HeaderColorFlag = 0x0143;
    private const ushort HeaderChecksumStart = 0x0134;
    private const ushort HeaderNewLicensee = 0x0144;
    private const ushort HeaderOldLicensee = 0x014B;
    private const ushort MarkTileData = 0x8010;
    private const ushort MarkTileMapEntry = 0x9909;
    private const ushort UnmapAddress = 0x00FE;
    private const ushort WaveRamStart = 0xFF30;
    private const byte WaveRamLength = 16;
    // Both button-group selection bits high.
    private const byte JoypadDeselected = 0x30;
    // The staged scratch runs from this port through 0xFF8F; the epilogue clears everything above it first, then these
    // ports once the handoff registers have been read out of them.
    private const byte ScratchStagedEnd = 0x89;

    // The scroll the mark falls in over, one step per frame.
    private const byte ScrollSteps = 32;
    // A whole scanline is 456 dots, and a machine cycle is four of them.
    private const int MachineCyclesPerLine = 114;

    // A one-bit-per-row mark the program doubles into both bit planes, so it draws in the darkest shade.
    private static ReadOnlySpan<byte> MarkBitmap =>
        [0x7C, 0x66, 0x66, 0x7C, 0x60, 0x60, 0x60, 0x00];

    /// <summary>Emits the program bytes for a layout at a solved calibration.</summary>
    /// <param name="layout">The revision's layout.</param>
    /// <param name="calibration">The straight-line machine-cycle counts to subtract.</param>
    /// <returns>The assembled program, to be placed at the layout's code base.</returns>
    public static byte[] Emit(BootRomLayout layout, BootRomCalibration calibration) {
        var emitter = new Sm83Emitter();
        var logo = emitter.NewLabel();
        var mark = emitter.NewLabel();
        var registerTable = emitter.NewLabel();
        var delay = emitter.NewLabel();
        var colorLabels = new BootRomColorLabels(
            AmbiguousRows: emitter.NewLabel(),
            ChecksumExceptions: emitter.NewLabel(),
            CopyPalette: emitter.NewLabel(),
            HeaderBases: emitter.NewLabel(),
            PaletteColors: emitter.NewLabel(),
            PaletteCombinations: emitter.NewLabel()
        );

        EmitVideoSetup(
            emitter: emitter,
            layout: layout,
            mark: mark
        );

        if (layout.VerifiesHeader) {
            EmitHeaderVerification(
                emitter: emitter,
                logo: logo
            );
        }

        if (layout.SupportsColor) {
            EmitColorTiming(
                calibration: calibration,
                emitter: emitter,
                labels: colorLabels,
                layout: layout
            );
        }

        // The LCD comes on only once every video-memory and palette write has landed: the picture processor locks both
        // out while it draws.
        emitter.LoadAImmediate(value: 0x91);
        emitter.StoreAToHighPage(port: 0x40);

        EmitScroll(emitter: emitter);

        if (layout.Model.IsSuperGameBoy()) {
            EmitSuperTiming(
                calibration: calibration,
                emitter: emitter
            );
        }

        EmitTimedTail(
            calibration: calibration,
            delay: delay,
            emitter: emitter,
            layout: layout
        );
        EmitHandoff(
            emitter: emitter,
            layout: layout,
            registerTable: registerTable
        );

        if (layout.TimesFromHeader) {
            EmitDelayRoutine(
                delay: delay,
                emitter: emitter
            );
        }

        if (layout.SupportsColor) {
            BootRomColorTiming.EmitCopyPalette(
                emitter: emitter,
                labels: colorLabels
            );
        }

        EmitTables(
            emitter: emitter,
            labels: colorLabels,
            layout: layout,
            logo: logo,
            mark: mark,
            registerTable: registerTable
        );

        return emitter.ToArray(baseAddress: (layout.SupportsColor
            ? ((ushort)0x0200)
            : ((ushort)0x0000)));
    }

    // Parks the coincidence comparison off-screen, seeds the audio and joypad registers the revision hands off, draws
    // the mark, whitens the color palettes a color cartridge gets, and lights the LCD. Every video-memory write happens
    // before the LCD comes on, so none of it can be locked out, and every write here is prologue: the divider is reset
    // afterwards, so none of it reaches the handoff counter.
    private static void EmitVideoSetup(Sm83Emitter emitter, BootRomLayout layout, int mark) {
        // A call before the stack pointer is set would push over the interrupt-enable register; the handoff sets the
        // same value again, so this is the boot program's own stack rather than part of the handoff.
        emitter.LoadStackPointer(value: 0xFFFE);

        // The comparison register is parked off-screen for the boot; the handoff writes the value the cartridge reads.
        emitter.LoadAImmediate(value: 0xFF);
        emitter.StoreAToHighPage(port: 0x45);
        emitter.LoadAImmediate(value: 0xFC);
        emitter.StoreAToHighPage(port: 0x47);

        if (layout.Model.SeedsWaveRamOnBoot()) {
            EmitWaveRamFill(emitter: emitter);
        }

        if (layout.Model.DeselectsJoypadOnBoot()) {
            EmitJoypadDeselect(emitter: emitter);
        }

        emitter.LoadImmediate(pair: Reg16.Hl, value: MarkTileData);
        emitter.LoadImmediateAddressOf(pair: Reg16.De, label: mark);
        emitter.LoadImmediate(destination: Reg8.C, value: ((byte)MarkBitmap.Length));

        var row = emitter.NewLabel();

        emitter.MarkLabel(label: row);
        emitter.LoadAFromDe();
        emitter.Increment(pair: Reg16.De);
        emitter.StoreAToHlIncrement();
        emitter.StoreAToHlIncrement();
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: row
        );

        emitter.LoadImmediate(pair: Reg16.Hl, value: MarkTileMapEntry);
        emitter.LoadAImmediate(value: 0x01);
        emitter.Load(
            destination: Reg8.Memory,
            source: Reg8.A
        );
    }
    // Writes the alternating 0x00/0xFF pattern the Color boot ROMs leave in wave RAM. The wave channel is silent for
    // the whole boot, so every byte lands at its own address rather than following a live sample position.
    private static void EmitWaveRamFill(Sm83Emitter emitter) {
        var pair = emitter.NewLabel();

        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: WaveRamStart
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: (WaveRamLength / 2)
        );
        emitter.MarkLabel(label: pair);
        emitter.XorA();
        emitter.StoreAToHlIncrement();
        emitter.LoadAImmediate(value: 0xFF);
        emitter.StoreAToHlIncrement();
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: pair
        );
    }
    // Deselects both button groups, which is what the joypad register reads back as on the revisions whose boot ROM
    // leaves it that way.
    private static void EmitJoypadDeselect(Sm83Emitter emitter) {
        emitter.LoadAImmediate(value: JoypadDeselected);
        emitter.StoreAToHighPage(port: 0x00);
    }
    // Clears high RAM from a port to the last byte below the interrupt-enable register, so the cartridge wakes to the
    // cleared page the seeded handoff carries rather than to the boot program's stack residue and staged scratch.
    private static void EmitClearHighRam(Sm83Emitter emitter, byte start) {
        var clear = emitter.NewLabel();

        emitter.XorA();
        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: ((ushort)(0xFF00 | start))
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: ((byte)(0xFF - start))
        );
        emitter.MarkLabel(label: clear);
        emitter.StoreAToHlIncrement();
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: clear
        );
    }
    // Refuses a cartridge whose logo or header checksum does not check out, the way the hardware does: the machine
    // wedges rather than handing off.
    private static void EmitHeaderVerification(Sm83Emitter emitter, int logo) {
        var compare = emitter.NewLabel();
        var lockUp = emitter.NewLabel();
        var start = emitter.NewLabel();

        // The wedge sits inside the checks so both branches to it stay within relative-jump reach.
        emitter.JumpRelative(label: start);
        emitter.MarkLabel(label: lockUp);
        emitter.JumpRelative(label: lockUp);
        emitter.MarkLabel(label: start);

        emitter.LoadImmediate(pair: Reg16.Hl, value: CartridgeHeader.LogoOffset);
        emitter.LoadImmediateAddressOf(pair: Reg16.De, label: logo);
        emitter.LoadImmediate(destination: Reg8.C, value: ((byte)CartridgeHeader.Logo.Length));
        emitter.MarkLabel(label: compare);
        emitter.LoadAFromDe();
        emitter.Increment(pair: Reg16.De);
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.Memory
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: lockUp
        );
        emitter.Increment(pair: Reg16.Hl);
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: compare
        );

        var accumulate = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: HeaderChecksumStart);
        emitter.LoadImmediate(destination: Reg8.C, value: 25);
        emitter.XorA();
        emitter.MarkLabel(label: accumulate);
        emitter.Arithmetic(
            op: AluOp.Subtract,
            source: Reg8.Memory
        );
        emitter.Decrement(register: Reg8.A);
        emitter.Increment(pair: Reg16.Hl);
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: accumulate
        );
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.Memory
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: lockUp
        );
    }
    // Falls the mark in from above, one scroll step per frame. Pure prologue: it runs before the divider is reset, so
    // its cost never reaches the handoff.
    private static void EmitScroll(Sm83Emitter emitter) {
        var step = emitter.NewLabel();
        var enterVBlank = emitter.NewLabel();
        var leaveVBlank = emitter.NewLabel();

        emitter.LoadImmediate(destination: Reg8.B, value: ScrollSteps);
        emitter.MarkLabel(label: step);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.B
        );
        emitter.StoreAToHighPage(port: 0x42);
        emitter.MarkLabel(label: enterVBlank);
        emitter.LoadAFromHighPage(port: 0x44);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x90
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: enterVBlank
        );
        emitter.MarkLabel(label: leaveVBlank);
        emitter.LoadAFromHighPage(port: 0x44);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x90
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: leaveVBlank
        );
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: step
        );
    }
    // The companion console's boot time is the all-clear forwarding duration less one machine cycle per set bit in
    // 0x0104-0x014F, so the program counts those bits and subtracts.
    private static void EmitSuperTiming(Sm83Emitter emitter, BootRomCalibration calibration) {
        var perByte = emitter.NewLabel();
        var perBit = emitter.NewLabel();
        var clear = emitter.NewLabel();

        emitter.LoadImmediate(pair: Reg16.Hl, value: CartridgeHeader.LogoOffset);
        emitter.LoadImmediate(pair: Reg16.De, value: 0x0000);
        emitter.LoadImmediate(destination: Reg8.C, value: 76);
        emitter.MarkLabel(label: perByte);
        emitter.LoadAFromHlIncrement();
        emitter.LoadImmediate(destination: Reg8.B, value: 8);
        emitter.MarkLabel(label: perBit);
        emitter.RotateRightCircularA();
        emitter.JumpRelative(
            condition: Condition.NoCarry,
            label: clear
        );
        emitter.Increment(pair: Reg16.De);
        emitter.MarkLabel(label: clear);
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: perBit
        );
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: perByte
        );

        EmitSubtractDeFromImmediate(
            emitter: emitter,
            value: ((ushort)((BootDivPrediction.SgbBaseCounter / 4) - calibration.DividerTail))
        );
    }
    // hl = value - de.
    private static void EmitSubtractDeFromImmediate(Sm83Emitter emitter, ushort value) {
        emitter.LoadAImmediate(value: ((byte)(value & 0xFF)));
        emitter.Arithmetic(
            op: AluOp.Subtract,
            source: Reg8.E
        );
        emitter.Load(
            destination: Reg8.L,
            source: Reg8.A
        );
        emitter.LoadAImmediate(value: ((byte)(value >> 8)));
        emitter.Arithmetic(
            op: AluOp.SubtractWithCarry,
            source: Reg8.D
        );
        emitter.Load(
            destination: Reg8.H,
            source: Reg8.A
        );
    }
    // The straight-line half: the events whose distance from the handoff is fixed, separated by the delays the program
    // computed. A layout that hands off mid vertical blank restarts the LCD here so the handoff line is timed; one that
    // hands off on the first line restarts it in the epilogue instead.
    private static void EmitTimedTail(Sm83Emitter emitter, BootRomLayout layout, BootRomCalibration calibration, int delay) {
        if (layout.TimesLcdEnable) {
            emitter.LoadAImmediate(value: 0x11);
            emitter.StoreAToHighPage(port: 0x40);

            if (layout.SupportsColor) {
                // Palette RAM is locked out while the processor draws, so the whitening rides in the window the LCD is
                // off. It writes through staged ports: the real palette registers for a color cartridge, and high-page
                // scratch for one the hardware runs in compatibility mode, so both header classes cost the same.
                EmitStagedPaletteWhitening(emitter: emitter);
            }

            emitter.LoadAImmediate(value: 0x91);
            emitter.StoreAToHighPage(port: 0x40);

            if (layout.SupportsColor) {
                EmitLoadCounterQuarter(emitter: emitter);
                emitter.LoadAFromHighPage(port: ScratchEnableDistance);
                emitter.Arithmetic(
                    op: AluOp.Subtract,
                    source: Reg8.L
                );
                emitter.Load(
                    destination: Reg8.E,
                    source: Reg8.A
                );
                emitter.LoadAFromHighPage(port: ((byte)(ScratchEnableDistance + 1)));
                emitter.Arithmetic(
                    op: AluOp.SubtractWithCarry,
                    source: Reg8.H
                );
                emitter.Load(
                    destination: Reg8.D,
                    source: Reg8.A
                );
                emitter.Load(
                    destination: Reg8.H,
                    source: Reg8.D
                );
                emitter.Load(
                    destination: Reg8.L,
                    source: Reg8.E
                );
                emitter.Call(label: delay);
            } else {
                EmitConstantDelay(
                    emitter: emitter,
                    machineCycles: (((MachineCyclesPerLine * layout.Probes[0].HandoffLine) - (layout.ConstantCounter / 4)) - calibration.EnableToHandoffMonochrome)
                );
            }
        }

        emitter.XorA();
        emitter.StoreAToHighPage(port: 0x04);

        if (layout.TimesFromHeader) {
            if (layout.SupportsColor) {
                EmitLoadCounterQuarter(emitter: emitter);
                emitter.Load(
                    destination: Reg8.A,
                    source: Reg8.L
                );
                emitter.ArithmeticImmediate(
                    op: AluOp.Subtract,
                    value: ((byte)(calibration.DividerTail & 0xFF))
                );
                emitter.Load(
                    destination: Reg8.L,
                    source: Reg8.A
                );
                emitter.Load(
                    destination: Reg8.A,
                    source: Reg8.H
                );
                emitter.ArithmeticImmediate(
                    op: AluOp.SubtractWithCarry,
                    value: ((byte)((calibration.DividerTail >> 8) & 0xFF))
                );
                emitter.Load(
                    destination: Reg8.H,
                    source: Reg8.A
                );
            }

            emitter.Call(label: delay);

            return;
        }

        EmitConstantDelay(
            emitter: emitter,
            machineCycles: ((layout.ConstantCounter / 4) - calibration.DividerTail)
        );
    }
    // hl = the staged handoff counter, divided by four: the counter runs four steps per machine cycle.
    private static void EmitLoadCounterQuarter(Sm83Emitter emitter) {
        emitter.LoadAFromHighPage(port: ScratchCounter);
        emitter.Load(
            destination: Reg8.L,
            source: Reg8.A
        );
        emitter.LoadAFromHighPage(port: ((byte)(ScratchCounter + 1)));
        emitter.Load(
            destination: Reg8.H,
            source: Reg8.A
        );
        emitter.Shift(
            op: ShiftOp.ShiftRightLogical,
            register: Reg8.H
        );
        emitter.Shift(
            op: ShiftOp.RotateRight,
            register: Reg8.L
        );
        emitter.Shift(
            op: ShiftOp.ShiftRightLogical,
            register: Reg8.H
        );
        emitter.Shift(
            op: ShiftOp.RotateRight,
            register: Reg8.L
        );
    }
    // Whitens the background palettes through the staged ports, then parks the index register. Every step costs the
    // same whichever ports were staged.
    private static void EmitStagedPaletteWhitening(Sm83Emitter emitter) {
        var fill = emitter.NewLabel();

        emitter.LoadAFromHighPage(port: ScratchPaletteIndexPort);
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.LoadAImmediate(value: 0x80);
        emitter.StoreAToHighPageC();
        emitter.LoadAFromHighPage(port: ScratchPaletteDataPort);
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.LoadImmediate(
            destination: Reg8.B,
            value: 32
        );
        emitter.MarkLabel(label: fill);
        emitter.LoadAImmediate(value: 0xFF);
        emitter.StoreAToHighPageC();
        emitter.LoadAImmediate(value: 0x7F);
        emitter.StoreAToHighPageC();
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: fill
        );
        emitter.LoadAFromHighPage(port: ScratchPaletteIndexPort);
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.XorA();
        emitter.StoreAToHighPageC();
    }
    private static void EmitLcdRestart(Sm83Emitter emitter) {
        emitter.LoadAImmediate(value: 0x11);
        emitter.StoreAToHighPage(port: 0x40);
        emitter.LoadAImmediate(value: 0x91);
        emitter.StoreAToHighPage(port: 0x40);
    }
    // A delay of exactly the requested machine cycles: a 7-cycle countdown for the bulk, then whole cycles of padding
    // for the remainder. Emitted only where the count is a per-revision constant.
    private static void EmitConstantDelay(Sm83Emitter emitter, int machineCycles) {
        if (machineCycles < 9) {
            throw new InvalidOperationException(message: $"A constant boot delay of {machineCycles} machine cycles is shorter than the countdown it is emitted as.");
        }

        var padding = ((machineCycles - 2) % 7);
        var iterations = (((machineCycles - 2) - padding) / 7);
        var loop = emitter.NewLabel();

        emitter.LoadImmediate(
            pair: Reg16.Bc,
            value: ((ushort)iterations)
        );
        emitter.MarkLabel(label: loop);
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.B
        );
        emitter.Arithmetic(
            op: AluOp.Or,
            source: Reg8.C
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: loop
        );

        for (var index = 0; (index < padding); ++index) {
            emitter.Nop();
        }
    }
    // A delay of exactly (38 + hl) machine cycles measured from the call to the instruction after the return: the
    // low three bits index a run of padding cycles, the rest drives an 8-cycle countdown.
    private static void EmitDelayRoutine(Sm83Emitter emitter, int delay) {
        var sled = emitter.NewLabel();
        var loop = emitter.NewLabel();

        emitter.MarkLabel(label: delay);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.L
        );
        emitter.ArithmeticImmediate(
            op: AluOp.And,
            value: 0x07
        );
        emitter.ComplementA();
        emitter.ArithmeticImmediate(
            op: AluOp.And,
            value: 0x07
        );
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );

        for (var shift = 0; (shift < 3); ++shift) {
            emitter.Shift(
                op: ShiftOp.ShiftRightLogical,
                register: Reg8.H
            );
            emitter.Shift(
                op: ShiftOp.RotateRight,
                register: Reg8.L
            );
        }

        emitter.Load(
            destination: Reg8.B,
            source: Reg8.H
        );
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.L
        );
        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: sled
        );
        emitter.AddToHl(pair: Reg16.De);
        emitter.JumpToHl();
        emitter.MarkLabel(label: sled);

        for (var index = 0; (index < 7); ++index) {
            emitter.Nop();
        }

        emitter.MarkLabel(label: loop);
        emitter.Nop();
        emitter.Decrement(pair: Reg16.Bc);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.B
        );
        emitter.Arithmetic(
            op: AluOp.Or,
            source: Reg8.C
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: loop
        );
        emitter.Return();
    }
    // The register file the cartridge wakes up to, then the unmap.
    private static void EmitHandoff(Sm83Emitter emitter, BootRomLayout layout, int registerTable) {
        var write = emitter.NewLabel();

        // The cartridge wakes to a cleared high page. The Color program stages its computed bytes at the bottom of that
        // page and calls, so it clears everything above the staged bytes here and the staged bytes themselves once the
        // register file has read them. A monochrome program stages nothing and only pushes when it carries the delay
        // routine, so its whole residue is one return address below the stack pointer.
        if (layout.SupportsColor) {
            EmitClearHighRam(
                emitter: emitter,
                start: ScratchStagedEnd
            );
        } else if (layout.TimesFromHeader) {
            emitter.XorA();
            emitter.StoreAToHighPage(port: 0xFC);
            emitter.StoreAToHighPage(port: 0xFD);
        }

        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: registerTable
        );
        emitter.LoadImmediate(
            destination: Reg8.B,
            value: ((byte)(RegisterWrites(layout: layout).Length / 2))
        );
        emitter.MarkLabel(label: write);
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPageC();
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: write
        );

        EmitHandoffRegisters(
            emitter: emitter,
            layout: layout
        );

        emitter.LoadStackPointer(value: 0xFFFE);
        EmitHandoffFlags(
            emitter: emitter,
            layout: layout
        );

        // Nothing past this point may disturb the flags, so only immediate loads and high-page stores are left. A
        // layout that hands off on the first line restarts the LCD here, as late as it can, and then parks the
        // comparison register on zero, which the picture processor re-latches against the first line every dot.
        if (!layout.TimesLcdEnable) {
            EmitLcdRestart(emitter: emitter);
        }

        emitter.LoadAImmediate(value: 0x00);
        emitter.StoreAToHighPage(port: 0x45);

        if (layout.SupportsColor) {
            for (var port = ScratchCounter; (port < ScratchStagedEnd); ++port) {
                emitter.StoreAToHighPage(port: port);
            }
        }

        emitter.LoadAImmediate(value: HandoffAccumulator(layout: layout));
        emitter.JumpAbsolute(address: UnmapAddress);
    }
    private static void EmitHandoffRegisters(Sm83Emitter emitter, BootRomLayout layout) {
        if (layout.SupportsColor) {
            // The Color handoff's B, D, E, H and L depend on the cartridge header, so the prologue staged them.
            ReadOnlySpan<Reg8> staged = [Reg8.B, Reg8.D, Reg8.E, Reg8.H, Reg8.L];

            for (var index = 0; (index < staged.Length); ++index) {
                emitter.LoadAFromHighPage(port: ((byte)(ScratchRegisterB + index)));
                emitter.Load(
                    destination: staged[index],
                    source: Reg8.A
                );
            }

            emitter.LoadImmediate(
                destination: Reg8.C,
                value: 0x00
            );

            return;
        }

        var handoff = MonochromeHandoff(model: layout.Model);

        emitter.LoadImmediate(
            destination: Reg8.B,
            value: handoff.B
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: handoff.C
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: handoff.D
        );
        emitter.LoadImmediate(
            destination: Reg8.E,
            value: handoff.E
        );
        emitter.LoadImmediate(
            destination: Reg8.H,
            value: handoff.H
        );
        emitter.LoadImmediate(
            destination: Reg8.L,
            value: handoff.L
        );
    }
    // Leaves the flags the revision hands off with. Every instruction after this one is an immediate load or a
    // high-page store, neither of which disturbs them.
    private static void EmitHandoffFlags(Sm83Emitter emitter, BootRomLayout layout) {
        if (layout.SupportsColor) {
            // xor a leaves the zero flag set and everything else clear, which is the Color handoff's flag byte; the
            // Advanced revisions then run the extra increment a cartridge probes for, whose result is their flag byte.
            emitter.XorA();

            if (layout.Model.HasAgbBootHandoff()) {
                emitter.Increment(register: Reg8.B);
            }

            return;
        }

        var handoff = MonochromeHandoff(model: layout.Model);

        if (handoff.F == 0xB0) {
            // 0xFF + 1 carries out of bit 3 and bit 7 and lands on zero: the zero, half-carry and carry flags set.
            emitter.LoadAImmediate(value: 0xFF);
            emitter.ArithmeticImmediate(
                op: AluOp.Add,
                value: 0x01
            );

            return;
        }

        emitter.LoadAImmediate(value: handoff.A);
        emitter.Arithmetic(
            op: AluOp.Or,
            source: Reg8.A
        );
    }
    // The accumulator the cartridge wakes up to, which is also the byte written to the unmap latch.
    private static byte HandoffAccumulator(BootRomLayout layout) =>
        (layout.SupportsColor
        ? ((byte)0x11)
        : MonochromeHandoff(model: layout.Model).A);
    private static void EmitTables(Sm83Emitter emitter, BootRomLayout layout, int logo, int mark, int registerTable, BootRomColorLabels labels) {
        emitter.MarkLabel(label: mark);
        emitter.EmitData(value: MarkBitmap);

        emitter.MarkLabel(label: registerTable);
        emitter.EmitData(value: RegisterWrites(layout: layout));

        if (layout.VerifiesHeader) {
            emitter.MarkLabel(label: logo);
            emitter.EmitData(value: CartridgeHeader.Logo);
        }

        if (!layout.SupportsColor) {
            return;
        }

        emitter.MarkLabel(label: labels.HeaderBases);
        emitter.EmitData(value: BootDivPrediction.HeaderBases);
        emitter.MarkLabel(label: labels.ChecksumExceptions);
        emitter.EmitData(value: BootRomChecksumTable.Rows);
        emitter.MarkLabel(label: labels.AmbiguousRows);
        emitter.EmitData(value: BootDivPrediction.AmbiguousRows);
        // The scan walks four-byte rows until a zero checksum closes the table.
        emitter.EmitData(value: [0x00, 0x00, 0x00, 0x00]);
        emitter.MarkLabel(label: labels.PaletteCombinations);
        emitter.EmitData(value: CompatibilityPalette.Combinations);
        emitter.MarkLabel(label: labels.PaletteColors);
        emitter.EmitData(value: CompatibilityPalette.Colors);
    }
    // The high-page registers the handoff writes, as port/value pairs. The chime leaves square channel 1 sounding on
    // every revision but the companion console's, whose boot ROM plays nothing and hands off with the audio unit
    // powered and silent — so its trigger bit stays clear.
    private static byte[] RegisterWrites(BootRomLayout layout) {
        var trigger = (layout.Model.LeavesBootChimeSounding()
            ? ((byte)0x87)
            : ((byte)0x07));

        return [
            0x26, 0x80,
            0x11, 0x80,
            0x12, 0xF3,
            0x13, 0xC1,
            0x14, trigger,
            0x24, 0x77,
            0x25, 0xF3,
            0x42, 0x00,
            0x43, 0x00,
            0x4A, 0x00,
            0x4B, 0x00,
            0x47, 0xFC,
            0x48, 0xFF,
            0x49, 0xFF,
            0x0F, 0x01,
        ];
    }
    private static (byte A, byte F, byte B, byte C, byte D, byte E, byte H, byte L) MonochromeHandoff(ConsoleModel model) =>
        model switch {
            ConsoleModel.Dmg0 => (0x01, 0x00, 0xFF, 0x13, 0x00, 0xC1, 0x84, 0x03),
            ConsoleModel.Mgb => (0xFF, 0xB0, 0x00, 0x13, 0x00, 0xD8, 0x01, 0x4D),
            ConsoleModel.Sgb => (0x01, 0x00, 0x00, 0x14, 0x00, 0x00, 0xC0, 0x60),
            ConsoleModel.Sgb2 => (0xFF, 0x00, 0x00, 0x14, 0x00, 0x00, 0xC0, 0x60),
            _ => (0x01, 0xB0, 0x00, 0x13, 0x00, 0xD8, 0x01, 0x4D),
        };

    private static void EmitColorTiming(Sm83Emitter emitter, BootRomLayout layout, BootRomCalibration calibration, BootRomColorLabels labels) =>
        BootRomColorTiming.Emit(
            calibration: calibration,
            emitter: emitter,
            labels: labels,
            layout: layout,
            machineCyclesPerLine: MachineCyclesPerLine,
            scratch: new BootRomScratch(
                Counter: ScratchCounter,
                EnableDistance: ScratchEnableDistance,
                FourthLetter: ScratchFourthLetter,
                PaletteDataPort: ScratchPaletteDataPort,
                PaletteIndexPort: ScratchPaletteIndexPort,
                RegisterB: ScratchRegisterB,
                TitleChecksum: ScratchTitleChecksum
            ),
            header: new BootRomHeaderPorts(
                ChecksumStart: HeaderChecksumStart,
                ColorFlag: HeaderColorFlag,
                NewLicensee: HeaderNewLicensee,
                OldLicensee: HeaderOldLicensee
            )
        );
}
/// <summary>The high-page scratch slots the Color program stages its computed values in.</summary>
/// <param name="Counter">The low byte of the predicted handoff counter.</param>
/// <param name="EnableDistance">The low byte of the enable-distance constant for the cartridge's class.</param>
/// <param name="RegisterB">The first of the five staged handoff register bytes.</param>
/// <param name="TitleChecksum">The title checksum.</param>
/// <param name="FourthLetter">The fourth title letter.</param>
/// <param name="PaletteIndexPort">The high-page port the palette whitening writes its index through.</param>
/// <param name="PaletteDataPort">The high-page port the palette whitening writes its data through.</param>
internal readonly record struct BootRomScratch(byte Counter, byte EnableDistance, byte RegisterB, byte TitleChecksum, byte FourthLetter, byte PaletteIndexPort, byte PaletteDataPort);
/// <summary>The cartridge header offsets the Color program reads.</summary>
/// <param name="ColorFlag">The color flag.</param>
/// <param name="ChecksumStart">The first title byte, where the title checksum starts.</param>
/// <param name="NewLicensee">The first character of the new licensee code.</param>
/// <param name="OldLicensee">The legacy licensee code.</param>
internal readonly record struct BootRomHeaderPorts(ushort ColorFlag, ushort ChecksumStart, ushort NewLicensee, ushort OldLicensee);
/// <summary>The labels the Color program's tables and its palette-copy routine are emitted at.</summary>
/// <param name="HeaderBases">The header-steered base table.</param>
/// <param name="ChecksumExceptions">The checksum contributions that differ from the common one.</param>
/// <param name="AmbiguousRows">The fourth-letter tie-breaks for the checksums that share a contribution row.</param>
/// <param name="PaletteCombinations">The compatibility palette combinations.</param>
/// <param name="PaletteColors">The compatibility palette pool.</param>
/// <param name="CopyPalette">The routine that loads one palette through a high-page data port.</param>
internal readonly record struct BootRomColorLabels(int HeaderBases, int ChecksumExceptions, int AmbiguousRows, int PaletteCombinations, int PaletteColors, int CopyPalette);
