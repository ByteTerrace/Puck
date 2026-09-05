namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Emits the Color boot program's header-driven half: the title checksum and licensee gate, the register file the
/// handoff needs (which differs between a color cartridge and one the Color hardware runs in compatibility mode), the
/// compatibility-mode selector, and the <see cref="BootDivPrediction"/> table walk that resolves the handoff counter
/// this cartridge's boot is supposed to take. All of it runs before the divider is reset, so none of its cost reaches
/// the handoff.
/// </summary>
internal static class BootRomColorTiming {
    private const byte BackgroundPaletteData = 0x69;
    private const byte BackgroundPaletteIndex = 0x68;
    private const byte ObjectPaletteData = 0x6B;
    private const byte ObjectPaletteIndex = 0x6A;
    // Bit 7 of an index register advances it after every data write.
    private const byte PaletteAutoIncrement = 0x80;
    private const byte PaletteByteCount = 8;
    private const byte CompatibilityModePort = 0x4C;
    private const int TitleLength = 16;
    // High-page scratch the compatibility-mode whitening writes through, so both header classes cost the same.
    private const byte ScratchPaletteDecoyData = 0x8F;
    private const byte ScratchPaletteDecoyIndex = 0x8E;

    /// <summary>Emits the Color program's header-driven half.</summary>
    /// <param name="emitter">The emitter to append to.</param>
    /// <param name="layout">The revision's layout, whose probes carry the handoff line of each header class.</param>
    /// <param name="calibration">The solved straight-line machine-cycle counts.</param>
    /// <param name="scratch">The high-page slots to stage computed values in.</param>
    /// <param name="header">The cartridge header offsets to read.</param>
    /// <param name="machineCyclesPerLine">The machine cycles one scanline spans.</param>
    /// <param name="labels">The labels the emitted tables and the palette-copy routine sit at.</param>
    public static void Emit(
        Sm83Emitter emitter,
        BootRomLayout layout,
        BootRomCalibration calibration,
        BootRomScratch scratch,
        BootRomHeaderPorts header,
        int machineCyclesPerLine,
        BootRomColorLabels labels
    ) {
        EmitTitleChecksum(
            emitter: emitter,
            header: header,
            scratch: scratch
        );
        EmitHandoffStaging(
            calibration: calibration,
            emitter: emitter,
            header: header,
            labels: labels,
            layout: layout,
            machineCyclesPerLine: machineCyclesPerLine,
            scratch: scratch
        );
        EmitCounterLookup(
            emitter: emitter,
            header: header,
            labels: labels,
            scratch: scratch
        );
    }
    /// <summary>Emits the routine that loads one four-colour palette through a high-page data port: the accumulator
    /// carries the palette's byte offset into the pool and C the port, and the eight bytes go out through the port's
    /// auto-increment, which is what leaves the index registers where the handoff expects them.</summary>
    /// <param name="emitter">The emitter to append to.</param>
    /// <param name="labels">The labels the emitted tables sit at.</param>
    public static void EmitCopyPalette(Sm83Emitter emitter, BootRomColorLabels labels) {
        var copy = emitter.NewLabel();

        emitter.MarkLabel(label: labels.CopyPalette);
        emitter.Push(pair: StackPair.Bc);
        emitter.Push(pair: StackPair.De);
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );
        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: labels.PaletteColors
        );
        emitter.AddToHl(pair: Reg16.De);
        emitter.LoadImmediate(
            destination: Reg8.B,
            value: PaletteByteCount
        );
        emitter.MarkLabel(label: copy);
        emitter.LoadAFromHlIncrement();
        emitter.StoreAToHighPageC();
        emitter.Decrement(register: Reg8.B);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: copy
        );
        emitter.Pop(pair: StackPair.De);
        emitter.Pop(pair: StackPair.Bc);
        emitter.Return();
    }

    // The eight-bit sum of the sixteen title bytes, plus the fourth title letter the tie-break tables key on.
    private static void EmitTitleChecksum(Sm83Emitter emitter, BootRomHeaderPorts header, BootRomScratch scratch) {
        var accumulate = emitter.NewLabel();

        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: header.ChecksumStart
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: TitleLength
        );
        emitter.XorA();
        emitter.MarkLabel(label: accumulate);
        emitter.Arithmetic(
            op: AluOp.Add,
            source: Reg8.Memory
        );
        emitter.Increment(pair: Reg16.Hl);
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: accumulate
        );
        emitter.StoreAToHighPage(port: scratch.TitleChecksum);
        emitter.Load(
            destination: Reg8.B,
            source: Reg8.A
        );
        emitter.LoadAFromAddress(address: ((ushort)(header.ChecksumStart + 3)));
        emitter.StoreAToHighPage(port: scratch.FourthLetter);
    }
    // Stages the handoff register bytes, the compatibility-mode selector, the color cartridge's white background
    // palettes, and the enable distance for the cartridge's header class. B holds the title checksum on entry.
    private static void EmitHandoffStaging(Sm83Emitter emitter, BootRomLayout layout, BootRomCalibration calibration, BootRomScratch scratch, BootRomHeaderPorts header, int machineCyclesPerLine, BootRomColorLabels labels) {
        var firstParty = emitter.NewLabel();
        var notFirstParty = emitter.NewLabel();
        var afterParty = emitter.NewLabel();

        emitter.LoadAFromAddress(address: header.OldLicensee);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x01
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: firstParty
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x33
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: notFirstParty
        );
        emitter.LoadAFromAddress(address: header.NewLicensee);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)'0')
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: notFirstParty
        );
        emitter.LoadAFromAddress(address: ((ushort)(header.NewLicensee + 1)));
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)'1')
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: firstParty
        );
        emitter.MarkLabel(label: notFirstParty);
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: 0x00
        );
        emitter.JumpRelative(label: afterParty);
        emitter.MarkLabel(label: firstParty);
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.B
        );
        emitter.MarkLabel(label: afterParty);

        var colorCartridge = emitter.NewLabel();
        var monochromeCartridge = emitter.NewLabel();
        var stageEnableDistance = emitter.NewLabel();

        EmitColorFlagTest(
            emitter: emitter,
            header: header,
            taken: colorCartridge
        );
        emitter.JumpRelative(label: monochromeCartridge);

        emitter.MarkLabel(label: colorCartridge);
        StageHandoffByte(
            emitter: emitter,
            index: 0,
            scratch: scratch,
            value: 0x00
        );
        StageHandoffByte(
            emitter: emitter,
            index: 1,
            scratch: scratch,
            value: 0xFF
        );
        StageHandoffByte(
            emitter: emitter,
            index: 2,
            scratch: scratch,
            value: 0x56
        );
        StageHandoffByte(
            emitter: emitter,
            index: 3,
            scratch: scratch,
            value: 0x00
        );
        StageHandoffByte(
            emitter: emitter,
            index: 4,
            scratch: scratch,
            value: 0x0D
        );
        EmitBootPalette(emitter: emitter);
        StagePaletteReadPorts(
            data: BackgroundPaletteData,
            emitter: emitter,
            index: BackgroundPaletteIndex,
            scratch: scratch
        );
        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: ((ushort)((machineCyclesPerLine * layout.Probes[0].HandoffLine) - calibration.EnableToHandoffColor))
        );
        // The compatibility branch below is far past a relative jump's reach.
        emitter.JumpAbsolute(label: stageEnableDistance);

        emitter.MarkLabel(label: monochromeCartridge);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.C
        );
        emitter.StoreAToHighPage(port: scratch.RegisterB);
        StageHandoffByte(
            emitter: emitter,
            index: 1,
            scratch: scratch,
            value: 0x00
        );
        StageHandoffByte(
            emitter: emitter,
            index: 2,
            scratch: scratch,
            value: 0x08
        );

        var copiesLogo = emitter.NewLabel();
        var afterCopy = emitter.NewLabel();

        emitter.Load(
            destination: Reg8.A,
            source: Reg8.C
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x43
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: copiesLogo
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x58
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: copiesLogo
        );
        StageHandoffByte(
            emitter: emitter,
            index: 3,
            scratch: scratch,
            value: 0x00
        );
        StageHandoffByte(
            emitter: emitter,
            index: 4,
            scratch: scratch,
            value: 0x7C
        );
        emitter.JumpRelative(label: afterCopy);
        emitter.MarkLabel(label: copiesLogo);
        StageHandoffByte(
            emitter: emitter,
            index: 3,
            scratch: scratch,
            value: 0x99
        );
        StageHandoffByte(
            emitter: emitter,
            index: 4,
            scratch: scratch,
            value: 0x1A
        );
        emitter.MarkLabel(label: afterCopy);
        emitter.LoadAImmediate(value: DmgCompatibilityState.Key0CompatibilityBit);
        emitter.StoreAToHighPage(port: CompatibilityModePort);
        // Compatibility mode renders through the palettes the picture processor resolves from the cartridge title, and
        // the seeded handoff leaves palette RAM clear, so the whitening is aimed at high-page scratch instead.
        StagePaletteReadPorts(
            data: ScratchPaletteDecoyData,
            emitter: emitter,
            index: ScratchPaletteDecoyIndex,
            scratch: scratch
        );
        EmitCompatibilityPaletteLoad(
            emitter: emitter,
            labels: labels,
            scratch: scratch
        );
        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: ((ushort)((machineCyclesPerLine * layout.Probes[1].HandoffLine) - calibration.EnableToHandoffMonochrome))
        );

        emitter.MarkLabel(label: stageEnableDistance);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.L
        );
        emitter.StoreAToHighPage(port: scratch.EnableDistance);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.H
        );
        emitter.StoreAToHighPage(port: ((byte)(scratch.EnableDistance + 1)));
    }
    private static void StageHandoffByte(Sm83Emitter emitter, BootRomScratch scratch, int index, byte value) {
        if (value == 0x00) {
            emitter.XorA();
        } else {
            emitter.LoadAImmediate(value: value);
        }

        emitter.StoreAToHighPage(port: ((byte)(scratch.RegisterB + index)));
    }
    // Loads the compatibility palettes the boot ROM assigns a cartridge without the color flag. C carries the title
    // checksum gated on the first-party licensee, which is exactly the key the selection scans on: a cartridge that is
    // not first-party carries zero, and zero is the table's own default row. The eight background bytes and then the
    // two eight-byte object palettes go out through the auto-increment ports, which leaves the index registers at the
    // values the handoff reads back.
    private static void EmitCompatibilityPaletteLoad(Sm83Emitter emitter, BootRomScratch scratch, BootRomColorLabels labels) {
        var scan = emitter.NewLabel();
        var next = emitter.NewLabel();
        var candidate = emitter.NewLabel();
        var resolved = emitter.NewLabel();

        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: BootRomLowWindow.TitleChecksumRows
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );
        emitter.MarkLabel(label: scan);
        emitter.LoadAFromHlIncrement();
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.C
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: candidate
        );
        emitter.MarkLabel(label: next);
        emitter.Increment(register: Reg8.D);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.D
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)CompatibilityPalette.TitleChecksumRows.Length)
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: scan
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );
        emitter.JumpRelative(label: resolved);

        emitter.MarkLabel(label: candidate);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.D
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)CompatibilityPalette.FirstDuplicateIndex)
        );
        emitter.JumpRelative(
            condition: Condition.Carry,
            label: resolved
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Subtract,
            value: ((byte)CompatibilityPalette.FirstDuplicateIndex)
        );
        emitter.Push(pair: StackPair.Hl);
        EmitIndexIntoLowWindow(
            emitter: emitter,
            table: BootRomLowWindow.DuplicateLetters
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Pop(pair: StackPair.Hl);
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.LoadAFromHighPage(port: scratch.FourthLetter);
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.E
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: next
        );

        emitter.MarkLabel(label: resolved);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.D
        );
        EmitIndexIntoLowWindow(
            emitter: emitter,
            table: BootRomLowWindow.CombinationPerRow
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.ArithmeticImmediate(
            op: AluOp.And,
            value: 0x7F
        );
        // Three byte offsets per combination.
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.Arithmetic(
            op: AluOp.Add,
            source: Reg8.A
        );
        emitter.Arithmetic(
            op: AluOp.Add,
            source: Reg8.E
        );
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );
        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: labels.PaletteCombinations
        );
        emitter.AddToHl(pair: Reg16.De);
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.B,
            source: Reg8.A
        );
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.D,
            source: Reg8.A
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );

        emitter.LoadAImmediate(value: PaletteAutoIncrement);
        emitter.StoreAToHighPage(port: BackgroundPaletteIndex);
        emitter.LoadAImmediate(value: PaletteAutoIncrement);
        emitter.StoreAToHighPage(port: ObjectPaletteIndex);

        EmitCopyPaletteCall(
            copyPalette: labels.CopyPalette,
            emitter: emitter,
            offset: Reg8.E,
            port: BackgroundPaletteData
        );
        EmitCopyPaletteCall(
            copyPalette: labels.CopyPalette,
            emitter: emitter,
            offset: Reg8.B,
            port: ObjectPaletteData
        );
        EmitCopyPaletteCall(
            copyPalette: labels.CopyPalette,
            emitter: emitter,
            offset: Reg8.D,
            port: ObjectPaletteData
        );
    }
    private static void EmitCopyPaletteCall(Sm83Emitter emitter, Reg8 offset, byte port, int copyPalette) {
        emitter.Load(
            destination: Reg8.A,
            source: offset
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: port
        );
        emitter.Call(label: copyPalette);
    }
    // hl = table + a, with a cleared afterwards. The tables live at fixed low-window addresses, so the base is a
    // literal rather than a label.
    private static void EmitIndexIntoLowWindow(Sm83Emitter emitter, ushort table) {
        emitter.LoadImmediate(
            pair: Reg16.Hl,
            value: table
        );
        emitter.Arithmetic(
            op: AluOp.Add,
            source: Reg8.L
        );
        emitter.Load(
            destination: Reg8.L,
            source: Reg8.A
        );
        emitter.LoadAImmediate(value: 0x00);
        emitter.Arithmetic(
            op: AluOp.AddWithCarry,
            source: Reg8.H
        );
        emitter.Load(
            destination: Reg8.H,
            source: Reg8.A
        );
    }
    private static void StagePaletteReadPorts(Sm83Emitter emitter, BootRomScratch scratch, byte index, byte data) {
        emitter.LoadAImmediate(value: index);
        emitter.StoreAToHighPage(port: scratch.PaletteIndexPort);
        emitter.LoadAImmediate(value: data);
        emitter.StoreAToHighPage(port: scratch.PaletteDataPort);
    }
    // The shades the mark is drawn against while the boot runs. The handoff whitens all of it again, which is what the
    // seeded state carries, so this palette lives only for the length of the scroll.
    private static void EmitBootPalette(Sm83Emitter emitter) {
        ReadOnlySpan<ushort> shades = [0x7FFF, 0x56B5, 0x294A, 0x0000];
        var fill = emitter.NewLabel();

        emitter.LoadAImmediate(value: 0x80);
        emitter.StoreAToHighPage(port: BackgroundPaletteIndex);

        foreach (var shade in shades) {
            emitter.LoadAImmediate(value: ((byte)(shade & 0xFF)));
            emitter.StoreAToHighPage(port: BackgroundPaletteData);
            emitter.LoadAImmediate(value: ((byte)(shade >> 8)));
            emitter.StoreAToHighPage(port: BackgroundPaletteData);
        }

        emitter.LoadImmediate(
            destination: Reg8.C,
            value: 28
        );
        emitter.MarkLabel(label: fill);
        emitter.LoadAImmediate(value: 0xFF);
        emitter.StoreAToHighPage(port: BackgroundPaletteData);
        emitter.LoadAImmediate(value: 0x7F);
        emitter.StoreAToHighPage(port: BackgroundPaletteData);
        emitter.Decrement(register: Reg8.C);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: fill
        );
        emitter.XorA();
        emitter.StoreAToHighPage(port: BackgroundPaletteIndex);
    }
    // Jumps to taken when the cartridge advertises Color enhancements.
    private static void EmitColorFlagTest(Sm83Emitter emitter, BootRomHeaderPorts header, int taken) {
        emitter.LoadAFromAddress(address: header.ColorFlag);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x80
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: taken
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0xC0
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: taken
        );
    }
    // Walks the prediction tables into the staged handoff counter: the licensee-and-color-flag row, then the
    // checksum contribution, then the fourth-letter tie-break for the checksums that share a row.
    private static void EmitCounterLookup(Sm83Emitter emitter, BootRomScratch scratch, BootRomHeaderPorts header, BootRomColorLabels labels) {
        var bucketZero = emitter.NewLabel();
        var bucketOne = emitter.NewLabel();
        var bucketDone = emitter.NewLabel();
        var colorRow = emitter.NewLabel();
        var rowDone = emitter.NewLabel();
        var counterDone = emitter.NewLabel();
        var needsChecksum = emitter.NewLabel();
        var addContribution = emitter.NewLabel();
        var ambiguous = emitter.NewLabel();

        emitter.LoadAFromAddress(address: header.OldLicensee);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x01
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: bucketZero
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: 0x33
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: bucketOne
        );
        emitter.LoadImmediate(
            destination: Reg8.E,
            value: 2
        );
        emitter.JumpRelative(label: bucketDone);
        emitter.MarkLabel(label: bucketZero);
        emitter.LoadImmediate(
            destination: Reg8.E,
            value: 0
        );
        emitter.JumpRelative(label: bucketDone);
        emitter.MarkLabel(label: bucketOne);
        emitter.LoadImmediate(
            destination: Reg8.E,
            value: 1
        );
        emitter.MarkLabel(label: bucketDone);

        EmitColorFlagTest(
            emitter: emitter,
            header: header,
            taken: colorRow
        );
        emitter.JumpRelative(label: rowDone);
        emitter.MarkLabel(label: colorRow);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.E
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Add,
            value: 3
        );
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.MarkLabel(label: rowDone);
        emitter.Shift(
            op: ShiftOp.ShiftLeftArithmetic,
            register: Reg8.E
        );
        emitter.Shift(
            op: ShiftOp.ShiftLeftArithmetic,
            register: Reg8.E
        );

        var skipNewZero = emitter.NewLabel();
        var skipNewOne = emitter.NewLabel();

        emitter.LoadAFromAddress(address: header.NewLicensee);
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)'0')
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: skipNewZero
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.E
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Add,
            value: 2
        );
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.MarkLabel(label: skipNewZero);
        emitter.LoadAFromAddress(address: ((ushort)(header.NewLicensee + 1)));
        emitter.ArithmeticImmediate(
            op: AluOp.Compare,
            value: ((byte)'1')
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: skipNewOne
        );
        emitter.Increment(register: Reg8.E);
        emitter.MarkLabel(label: skipNewOne);

        emitter.Shift(
            op: ShiftOp.ShiftLeftArithmetic,
            register: Reg8.E
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: 0x00
        );
        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: labels.HeaderBases
        );
        emitter.AddToHl(pair: Reg16.De);
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.StoreAToHighPage(port: scratch.Counter);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Load(
            destination: Reg8.D,
            source: Reg8.A
        );
        emitter.StoreAToHighPage(port: ((byte)(scratch.Counter + 1)));

        EmitSentinelTest(
            emitter: emitter,
            above: counterDone,
            below: needsChecksum,
            high: Reg8.D,
            low: Reg8.E
        );

        // The contribution table is carried as the one value most checksums take plus a row per checksum that differs,
        // so the walk is a scan rather than an index.
        var scanRow = emitter.NewLabel();
        var rowHit = emitter.NewLabel();
        var contributionReady = emitter.NewLabel();

        emitter.MarkLabel(label: needsChecksum);
        emitter.LoadAFromHighPage(port: scratch.TitleChecksum);
        emitter.Load(
            destination: Reg8.E,
            source: Reg8.A
        );
        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: labels.ChecksumExceptions
        );
        emitter.LoadImmediate(
            destination: Reg8.D,
            value: ((byte)BootRomChecksumTable.RowCount)
        );
        emitter.MarkLabel(label: scanRow);
        emitter.LoadAFromHlIncrement();
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.E
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: rowHit
        );
        emitter.Increment(pair: Reg16.Hl);
        emitter.Increment(pair: Reg16.Hl);
        emitter.Decrement(register: Reg8.D);
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: scanRow
        );
        emitter.LoadImmediate(
            destination: Reg8.C,
            value: ((byte)(BootRomChecksumTable.Common & 0xFF))
        );
        emitter.LoadImmediate(
            destination: Reg8.B,
            value: ((byte)(BootRomChecksumTable.Common >> 8))
        );
        emitter.JumpRelative(label: contributionReady);
        emitter.MarkLabel(label: rowHit);
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Load(
            destination: Reg8.B,
            source: Reg8.A
        );
        emitter.MarkLabel(label: contributionReady);

        EmitSentinelTest(
            emitter: emitter,
            above: addContribution,
            below: ambiguous,
            high: Reg8.B,
            low: Reg8.C
        );

        emitter.MarkLabel(label: ambiguous);
        EmitAmbiguousScan(
            ambiguousRows: labels.AmbiguousRows,
            counterDone: counterDone,
            emitter: emitter,
            scratch: scratch
        );

        emitter.MarkLabel(label: addContribution);
        emitter.LoadAFromHighPage(port: scratch.Counter);
        emitter.Arithmetic(
            op: AluOp.Add,
            source: Reg8.C
        );
        emitter.StoreAToHighPage(port: scratch.Counter);
        emitter.LoadAFromHighPage(port: ((byte)(scratch.Counter + 1)));
        emitter.Arithmetic(
            op: AluOp.AddWithCarry,
            source: Reg8.B
        );
        emitter.StoreAToHighPage(port: ((byte)(scratch.Counter + 1)));
        emitter.MarkLabel(label: counterDone);
    }
    // Branches to above when the 16-bit value in high:low exceeds the table sentinel, and to below otherwise. The
    // comparison is a borrow-producing subtraction of the sentinel plus one, so it is exact for any pair of bytes.
    private static void EmitSentinelTest(Sm83Emitter emitter, Reg8 high, Reg8 low, int above, int below) {
        emitter.Load(
            destination: Reg8.A,
            source: low
        );
        emitter.ArithmeticImmediate(
            op: AluOp.Subtract,
            value: ((byte)((BootDivPrediction.Sentinel + 1) & 0xFF))
        );
        emitter.Load(
            destination: Reg8.A,
            source: high
        );
        emitter.ArithmeticImmediate(
            op: AluOp.SubtractWithCarry,
            value: ((byte)((BootDivPrediction.Sentinel + 1) >> 8))
        );
        emitter.JumpRelative(
            condition: Condition.Carry,
            label: below
        );
        emitter.JumpRelative(label: above);
    }
    // Walks the four-byte tie-break rows for the cartridge's checksum, taking the first whose letter matches or is the
    // row that closes the checksum. A zero checksum closes the table; nothing matched leaves the base counter alone.
    private static void EmitAmbiguousScan(Sm83Emitter emitter, BootRomScratch scratch, int ambiguousRows, int counterDone) {
        var scan = emitter.NewLabel();
        var next = emitter.NewLabel();
        var hit = emitter.NewLabel();

        emitter.LoadImmediateAddressOf(
            pair: Reg16.Hl,
            label: ambiguousRows
        );
        emitter.MarkLabel(label: scan);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Arithmetic(
            op: AluOp.Or,
            source: Reg8.A
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: counterDone
        );
        emitter.Load(
            destination: Reg8.D,
            source: Reg8.A
        );
        emitter.LoadAFromHighPage(port: scratch.TitleChecksum);
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.D
        );
        emitter.JumpRelative(
            condition: Condition.NotZero,
            label: next
        );
        emitter.Increment(pair: Reg16.Hl);
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Decrement(pair: Reg16.Hl);
        emitter.Arithmetic(
            op: AluOp.Or,
            source: Reg8.A
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: hit
        );
        emitter.Load(
            destination: Reg8.D,
            source: Reg8.A
        );
        emitter.LoadAFromHighPage(port: scratch.FourthLetter);
        emitter.Arithmetic(
            op: AluOp.Compare,
            source: Reg8.D
        );
        emitter.JumpRelative(
            condition: Condition.Zero,
            label: hit
        );
        emitter.MarkLabel(label: next);

        for (var step = 0; (step < 4); ++step) {
            emitter.Increment(pair: Reg16.Hl);
        }

        emitter.JumpRelative(label: scan);
        emitter.MarkLabel(label: hit);
        emitter.Increment(pair: Reg16.Hl);
        emitter.Increment(pair: Reg16.Hl);
        emitter.LoadAFromHlIncrement();
        emitter.Load(
            destination: Reg8.C,
            source: Reg8.A
        );
        emitter.Load(
            destination: Reg8.A,
            source: Reg8.Memory
        );
        emitter.Load(
            destination: Reg8.B,
            source: Reg8.A
        );
    }
}
