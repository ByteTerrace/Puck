using Puck.HumbleGamingDeck.Forge;
using Puck.Machines;
using Puck.Testing;

namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Hardware-behaviour laws of the MMC1 and MMC3 boards, from https://www.nesdev.org/wiki/MMC1 and
/// https://www.nesdev.org/wiki/MMC3, over synthetic images built here: every PRG byte holds its bank number and every CHR
/// byte its bank number, so a read names the bank a window maps.</summary>
public sealed class BoardLawTests {
    private const byte Cld = 0xD8;
    private const byte Cli = 0x58;
    private const byte IncAbsolute = 0xEE;
    private const byte IncZeroPage = 0xE6;
    private const byte Jmp = 0x4C;
    private const byte LdaImmediate = 0xA9;
    private const byte Pha = 0x48;
    private const byte Pla = 0x68;
    private const byte Rti = 0x40;
    private const byte Sei = 0x78;
    private const byte StaAbsolute = 0x8D;
    private const byte BitAbsolute = 0x2C;
    private const byte Bpl = 0x10;

    /// <summary>MMC1 powers on in PRG mode 3 and its serial port reaches all four registers: 32 KiB, fixed-first and
    /// fixed-last PRG modes, and 8 KiB and 4 KiB CHR modes.</summary>
    [Fact]
    public void Mmc1BankModesFollowTheSerialRegisters() {
        var mapper = Mmc1(prgBanks: 8, chrBanks: 8);
        var nametables = new HgdNametableRam();

        Assert.Equal(expected: 0x0C, actual: mapper.Control);
        Assert.Equal(expected: 0, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 7, actual: mapper.CpuRead(address: 0xC000, openBus: 0));
        Serial(address: 0xE000, mapper: mapper, value: 5);
        Assert.Equal(expected: 5, actual: mapper.CpuRead(address: 0xBFFF, openBus: 0));
        Assert.Equal(expected: 7, actual: mapper.CpuRead(address: 0xFFFF, openBus: 0));
        Serial(address: 0x8000, mapper: mapper, value: 0x08);
        Assert.Equal(expected: 0, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 5, actual: mapper.CpuRead(address: 0xC000, openBus: 0));
        Serial(address: 0x8000, mapper: mapper, value: 0x00);
        Assert.Equal(expected: 4, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 5, actual: mapper.CpuRead(address: 0xC000, openBus: 0));

        // 8 KiB CHR mode ignores CHR bank 0's low bit and CHR bank 1; 4 KiB mode switches each half.
        Serial(address: 0xA000, mapper: mapper, value: 3);
        Serial(address: 0xC000, mapper: mapper, value: 6);
        Assert.Equal(expected: 2, actual: mapper.PpuRead(address: 0x0000, nametables: nametables));
        Assert.Equal(expected: 3, actual: mapper.PpuRead(address: 0x1FFF, nametables: nametables));
        Serial(address: 0x8000, mapper: mapper, value: 0x1C);
        Assert.Equal(expected: 3, actual: mapper.PpuRead(address: 0x0FFF, nametables: nametables));
        Assert.Equal(expected: 6, actual: mapper.PpuRead(address: 0x1000, nametables: nametables));
    }
    /// <summary>MMC1's control bits 0-1 select one-screen lower, one-screen upper, vertical and horizontal mirroring.</summary>
    /// <param name="control">The control register's mirroring bits.</param>
    /// <param name="pages">The CIRAM page each of the four nametables reaches.</param>
    [InlineData(0, new[] { 0, 0, 0, 0 })]
    [InlineData(1, new[] { 1, 1, 1, 1 })]
    [InlineData(2, new[] { 0, 1, 0, 1 })]
    [InlineData(3, new[] { 0, 0, 1, 1 })]
    [Theory]
    public void Mmc1MirroringFollowsControl(int control, int[] pages) {
        var mapper = Mmc1(prgBanks: 2, chrBanks: 2);

        Serial(address: 0x8000, mapper: mapper, value: ((byte)(0x0C | control)));
        Assert.Equal(expected: pages, actual: Pages(mapper: mapper));
    }
    /// <summary>A write with bit 7 set empties the shift register and sets PRG mode 3, whatever was shifted in.</summary>
    [Fact]
    public void Mmc1ResetWriteEmptiesTheShiftRegister() {
        var mapper = Mmc1(prgBanks: 8, chrBanks: 8);

        Serial(address: 0x8000, mapper: mapper, value: 0x00);
        Assert.Equal(expected: 0x00, actual: mapper.Control);
        Write(address: 0xE000, mapper: mapper, value: 1);
        Write(address: 0xE000, mapper: mapper, value: 1);
        Write(address: 0xE000, mapper: mapper, value: 0x80);
        Assert.Equal(expected: 0x10, actual: mapper.Shift);
        Assert.Equal(expected: 0x0C, actual: mapper.Control);
        Serial(address: 0xE000, mapper: mapper, value: 2);
        Assert.Equal(expected: 2, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 7, actual: mapper.CpuRead(address: 0xC000, openBus: 0));
    }
    /// <summary>A write on the cycle after another write is ignored, a reset included; one cycle between them is
    /// enough.</summary>
    [Fact]
    public void Mmc1IgnoresTheSecondOfConsecutiveCycleWrites() {
        var mapper = Mmc1(prgBanks: 8, chrBanks: 8);

        mapper.CpuWrite(address: 0x8000, value: 1);
        Cycle(mapper: mapper);
        mapper.CpuWrite(address: 0x8000, value: 0x80);
        Assert.Equal(expected: 0x18, actual: mapper.Shift);
        Cycle(mapper: mapper);
        Cycle(mapper: mapper);
        mapper.CpuWrite(address: 0x8000, value: 0x80);
        Assert.Equal(expected: 0x10, actual: mapper.Shift);
    }
    /// <summary>Through the real CPU, after one bit is shifted in, a read-modify-write instruction reaches the serial port
    /// only with the value it writes first: INC of a $FF byte resets and its $00 is ignored, and INC of a $7F byte shifts
    /// a one in and its $80 reset is ignored.</summary>
    /// <param name="data">The ROM byte the INC targets.</param>
    /// <param name="shift">The shift register afterwards.</param>
    [InlineData(0xFF, 0x10)]
    [InlineData(0x7F, 0x1C)]
    [Theory]
    public void Mmc1SeesOnlyTheFirstWriteOfAReadModifyWrite(byte data, byte shift) {
        var program = new DeckProgram(origin: 0xC000)
            .Bytes(Sei, Cld)
            .Op(opcode: LdaImmediate, operand: ((byte)1)).Op(address: 0x8000, opcode: StaAbsolute)
            .Jump(label: "data", opcode: IncAbsolute)
            .Label(name: "idle").Jump(label: "idle", opcode: Jmp)
            .Label(name: "data").Bytes(data);
        var machine = new HgdMachine(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: program.Build(chr: new byte[8192], mapper: 1, prgRomSize: 32768))
        ));

        machine.RunCycles(masterTicks: 2000);
        Assert.Equal(expected: shift, actual: ((HgdMmc1)machine.Bus.Mapper).Shift);
    }
    /// <summary>PRG bank bit 4 disables PRG-RAM: reads return open bus and writes are lost.</summary>
    [Fact]
    public void Mmc1PrgRamFollowsItsEnableBit() {
        var mapper = Mmc1(prgBanks: 2, chrBanks: 2);

        mapper.CpuWrite(address: 0x6123, value: 0x5A);
        Assert.Equal(expected: 0x5A, actual: mapper.CpuRead(address: 0x6123, openBus: 0xEE));
        Serial(address: 0xE000, mapper: mapper, value: 0x10);
        Assert.Equal(expected: 0xEE, actual: mapper.CpuRead(address: 0x6123, openBus: 0xEE));
        mapper.CpuWrite(address: 0x6123, value: 0x77);
        Serial(address: 0xE000, mapper: mapper, value: 0x00);
        Assert.Equal(expected: 0x5A, actual: mapper.CpuRead(address: 0x6123, openBus: 0xEE));
    }
    /// <summary>On 512 KiB of PRG, CHR bank bit 4 selects the 256 KiB half both PRG windows read, the fixed bank
    /// included.</summary>
    [Fact]
    public void Mmc1OuterBankSelectsThePrgHalf() {
        var mapper = Mmc1(prgBanks: 32, chrBanks: 0);

        Serial(address: 0xE000, mapper: mapper, value: 3);
        Assert.Equal(expected: 3, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 15, actual: mapper.CpuRead(address: 0xC000, openBus: 0));
        Serial(address: 0xA000, mapper: mapper, value: 0x10);
        Assert.Equal(expected: 19, actual: mapper.CpuRead(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 31, actual: mapper.CpuRead(address: 0xC000, openBus: 0));
    }
    /// <summary>On 16 KiB of PRG-RAM (SOROM) CHR bank bit 3 selects the 8 KiB bank, and on 32 KiB (SXROM) bits 3 and 2
    /// do.</summary>
    /// <param name="ramShift">The PRG-RAM size as NES 2.0 encodes it: 8 for 16 KiB, 9 for 32 KiB.</param>
    /// <param name="chrBank">The CHR bank 0 value selecting the second bank checked.</param>
    [InlineData(8, 0x08)]
    [InlineData(9, 0x0C)]
    [Theory]
    public void Mmc1ChrBankSelectsThePrgRamBank(int ramShift, byte chrBank) {
        var image = new DeckProgram(origin: 0xC000).Label(name: "idle").Jump(label: "idle", opcode: Jmp).Build(chr: [], mapper: 1, prgRomSize: 32768);

        image[10] = ((byte)ramShift);
        var mapper = ((HgdMmc1)HgdCartridge.Load(image: image).CreateMapper());

        mapper.CpuWrite(address: 0x6000, value: 0x11);
        Serial(address: 0xA000, mapper: mapper, value: chrBank);
        Assert.Equal(expected: 0, actual: mapper.CpuRead(address: 0x6000, openBus: 0xEE));
        mapper.CpuWrite(address: 0x6000, value: 0x22);
        Serial(address: 0xA000, mapper: mapper, value: 0);
        Assert.Equal(expected: 0x11, actual: mapper.CpuRead(address: 0x6000, openBus: 0));
        Serial(address: 0xA000, mapper: mapper, value: chrBank);
        Assert.Equal(expected: 0x22, actual: mapper.CpuRead(address: 0x6000, openBus: 0));
    }
    /// <summary>CHR-RAM is banked like CHR-ROM on both boards: a byte written through one bank reads back through the same
    /// bank wherever it is mapped and is absent from the other.</summary>
    [Fact]
    public void ChrRamFollowsTheBankRegisters() {
        var nametables = new HgdNametableRam();
        var mmc1 = Mmc1(prgBanks: 2, chrBanks: 0);

        Serial(address: 0x8000, mapper: mmc1, value: 0x1C);
        Serial(address: 0xA000, mapper: mmc1, value: 1);
        mmc1.PpuWrite(address: 0x0010, nametables: nametables, value: 0x5A);
        Serial(address: 0xC000, mapper: mmc1, value: 1);
        Assert.Equal(expected: 0x5A, actual: mmc1.PpuRead(address: 0x1010, nametables: nametables));
        Serial(address: 0xA000, mapper: mmc1, value: 0);
        Assert.Equal(expected: 0, actual: mmc1.PpuRead(address: 0x0010, nametables: nametables));
        Assert.Equal(expected: 1, actual: mmc1.Header.Mapper);

        var mmc3 = Mmc3(prgBanks: 4, chrBanks: 0);

        BankData(mapper: mmc3, register: 2, value: 3);
        mmc3.PpuWrite(address: 0x1010, nametables: nametables, value: 0xA5);
        BankData(mapper: mmc3, register: 5, value: 3);
        Assert.Equal(expected: 0xA5, actual: mmc3.PpuRead(address: 0x1C10, nametables: nametables));
        Assert.Equal(expected: 0, actual: mmc3.PpuRead(address: 0x1410, nametables: nametables));
    }
    /// <summary>A four-screen MMC3 image answers the upper two nametables from its own RAM, wires the lower two
    /// vertically, and ignores $A000.</summary>
    [Fact]
    public void Mmc3FourScreenKeepsItsOwnNametables() {
        var image = new DeckProgram(origin: 0xC000).Label(name: "idle").Jump(label: "idle", opcode: Jmp).Build(chr: new byte[8192], mapper: 4, prgRomSize: 32768);

        image[6] |= 0x08;
        var mapper = HgdCartridge.Load(image: image).CreateMapper();
        var nametables = new HgdNametableRam();

        mapper.CpuWrite(address: 0xA000, value: 1);
        for (var quadrant = 0; (quadrant < 4); ++quadrant) {
            mapper.PpuWrite(address: ((ushort)(0x2000 + (quadrant * 0x400))), nametables: nametables, value: ((byte)(0x10 + quadrant)));
        }
        Assert.Equal(expected: 0x10, actual: nametables.Read(offset: 0, page: 0));
        Assert.Equal(expected: 0x11, actual: nametables.Read(offset: 0, page: 1));
        Assert.Equal(expected: 0x12, actual: mapper.PpuRead(address: 0x2800, nametables: nametables));
        Assert.Equal(expected: 0x13, actual: mapper.PpuRead(address: 0x2C00, nametables: nametables));
    }
    /// <summary>A board refuses a snapshot whose shift register or A12 filter count no board could hold.</summary>
    [Fact]
    public void BoardsRefuseOutOfRangeSnapshots() {
        IHgdMapper[] mappers = [Mmc1(prgBanks: 2, chrBanks: 2), Mmc3(prgBanks: 4, chrBanks: 8)];
        int[] fromEnd = [7, 1];

        for (var index = 0; (index < mappers.Length); ++index) {
            var writer = new StateWriter();

            mappers[index].SaveState(writer: writer);
            var bytes = writer.ToArray();

            bytes[(bytes.Length - fromEnd[index])] = 0x40;
            Assert.Throws<InvalidDataException>(testCode: () => mappers[index].LoadState(reader: new StateReader(buffer: bytes)));
        }
    }
    /// <summary>The assembler places code from its origin and refuses an origin other than $8000 or $C000, a branch out
    /// of range, a banked size that is not whole banks, and a program that runs into the vectors.</summary>
    [Fact]
    public void DeckProgramRefusesWhatItCannotPack() {
        Assert.Equal(expected: 0xC002, actual: new DeckProgram(origin: 0xC000).Bytes(Sei, Cld).Here);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new DeckProgram(origin: 0xA000));
        Assert.Throws<InvalidOperationException>(testCode: () => new DeckProgram().Label(name: "far").Bytes(new byte[200]).Branch(label: "far", opcode: Bpl).Build(chr: []));
        Assert.Throws<ArgumentException>(testCode: () => new DeckProgram(origin: 0xC000).Build(chr: [], mapper: 1, prgRomSize: 40960));
        Assert.Throws<ArgumentException>(testCode: () => new DeckProgram(origin: 0xC000).Bytes(new byte[0x3FFB]).Build(chr: [], mapper: 4, prgRomSize: 32768));
    }
    /// <summary>MMC3's R6 and R7 switch two 8 KiB windows, the last bank stays at $E000, and bank select bit 6 swaps $8000
    /// with $C000.</summary>
    [Fact]
    public void Mmc3PrgModesSwapTheFixedWindow() {
        var mapper = Mmc3(prgBanks: 16, chrBanks: 32);

        BankData(mapper: mapper, register: 6, value: 2);
        BankData(mapper: mapper, register: 7, value: 3);
        Assert.Equal(expected: new[] { 2, 3, 14, 15 }, actual: PrgWindows(mapper: mapper));
        mapper.CpuWrite(address: 0x8000, value: 0x46);
        Assert.Equal(expected: new[] { 14, 3, 2, 15 }, actual: PrgWindows(mapper: mapper));
    }
    /// <summary>R0 and R1 select 2 KiB CHR windows with their low bit ignored and R2-R5 1 KiB windows; bank select bit 7
    /// swaps the two halves of pattern space.</summary>
    [Fact]
    public void Mmc3ChrWindowsFollowInversion() {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 32);

        for (var register = 0; (register < 6); ++register) {
            BankData(mapper: mapper, register: register, value: ((byte)((register < 2) ? ((register * 4) + 9) : (register + 14))));
        }
        Assert.Equal(expected: new[] { 8, 9, 12, 13, 16, 17, 18, 19 }, actual: ChrWindows(mapper: mapper));
        mapper.CpuWrite(address: 0x8000, value: 0x80);
        Assert.Equal(expected: new[] { 16, 17, 18, 19, 8, 9, 12, 13 }, actual: ChrWindows(mapper: mapper));
    }
    /// <summary>$A000 bit 0 selects vertical or horizontal mirroring.</summary>
    [Fact]
    public void Mmc3MirroringFollowsA000() {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 8);

        mapper.CpuWrite(address: 0xA000, value: 0);
        Assert.Equal(expected: new[] { 0, 1, 0, 1 }, actual: Pages(mapper: mapper));
        mapper.CpuWrite(address: 0xA000, value: 1);
        Assert.Equal(expected: new[] { 0, 0, 1, 1 }, actual: Pages(mapper: mapper));
    }
    /// <summary>$A001 bit 7 enables PRG-RAM and bit 6 denies writes to it.</summary>
    [Fact]
    public void Mmc3PrgRamProtect() {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 8);

        mapper.CpuWrite(address: 0x7001, value: 0x42);
        Assert.Equal(expected: 0x42, actual: mapper.CpuRead(address: 0x7001, openBus: 0xEE));
        mapper.CpuWrite(address: 0xA001, value: 0xC0);
        mapper.CpuWrite(address: 0x7001, value: 0x43);
        Assert.Equal(expected: 0x42, actual: mapper.CpuRead(address: 0x7001, openBus: 0xEE));
        mapper.CpuWrite(address: 0xA001, value: 0x00);
        Assert.Equal(expected: 0xEE, actual: mapper.CpuRead(address: 0x7001, openBus: 0xEE));
        mapper.CpuWrite(address: 0x7001, value: 0x44);
        mapper.CpuWrite(address: 0xA001, value: 0x80);
        Assert.Equal(expected: 0x42, actual: mapper.CpuRead(address: 0x7001, openBus: 0xEE));
    }
    /// <summary>The counter clocks on a PPU A12 rise only after A12 stayed low for three falling M2 edges.</summary>
    /// <param name="lowFalls">The falling M2 edges seen while A12 was low.</param>
    /// <param name="clocked">Whether the rise clocks the counter.</param>
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(9, true)]
    [Theory]
    public void Mmc3A12RiseIsFilteredByM2(int lowFalls, bool clocked) {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 8);

        mapper.CpuWrite(address: 0xC000, value: 5);
        mapper.CpuWrite(address: 0xC001, value: 0);
        Rise(lowFalls: 3, mapper: mapper);
        Assert.Equal(expected: 5, actual: mapper.IrqCounter);
        Rise(lowFalls: lowFalls, mapper: mapper);
        Assert.Equal(expected: (clocked ? 4 : 5), actual: mapper.IrqCounter);
    }
    /// <summary>A clock reloads a zero counter or a requested reload from the latch and decrements otherwise; reaching
    /// zero with IRQs enabled raises the IRQ until $E000 acknowledges and disables it.</summary>
    [Fact]
    public void Mmc3CounterReloadsDecrementsAndInterrupts() {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 8);
        var counts = new List<int>();
        var raised = new List<bool>();

        mapper.CpuWrite(address: 0xC000, value: 2);
        mapper.CpuWrite(address: 0xC001, value: 0);
        mapper.CpuWrite(address: 0xE001, value: 0);
        for (var clock = 0; (clock < 5); ++clock) {
            Rise(lowFalls: 3, mapper: mapper);
            counts.Add(item: mapper.IrqCounter);
            raised.Add(item: mapper.Irq);
        }
        Assert.Equal(expected: new[] { 2, 1, 0, 2, 1 }, actual: counts);
        Assert.Equal(expected: new[] { false, false, true, true, true }, actual: raised);
        mapper.CpuWrite(address: 0xE000, value: 0);
        Assert.False(condition: mapper.Irq);
        Rise(lowFalls: 3, mapper: mapper);
        Assert.Equal(expected: 0, actual: mapper.IrqCounter);
        Assert.False(condition: mapper.Irq);

        // A $C001 write mid-count reloads at the next clock instead of decrementing.
        mapper.CpuWrite(address: 0xC000, value: 7);
        Rise(lowFalls: 3, mapper: mapper);
        Assert.Equal(expected: 7, actual: mapper.IrqCounter);
        mapper.CpuWrite(address: 0xC000, value: 3);
        mapper.CpuWrite(address: 0xC001, value: 0);
        Rise(lowFalls: 3, mapper: mapper);
        Assert.Equal(expected: 3, actual: mapper.IrqCounter);
    }
    /// <summary>With a latch of zero the MMC3 raises the IRQ on every clock, while the MMC3A (submapper 4) raises it only
    /// on the clock that consumes a requested reload.</summary>
    /// <param name="submapper">The NES 2.0 submapper.</param>
    /// <param name="expected">Whether each of three clocks raises the IRQ.</param>
    [InlineData(0, new[] { true, true, true })]
    [InlineData(4, new[] { true, false, false })]
    [Theory]
    public void Mmc3LatchZeroDependsOnRevision(int submapper, bool[] expected) {
        var mapper = Mmc3(prgBanks: 4, chrBanks: 8, submapper: submapper);
        var raised = new List<bool>();

        mapper.CpuWrite(address: 0xC000, value: 0);
        mapper.CpuWrite(address: 0xC001, value: 0);
        mapper.CpuWrite(address: 0xE001, value: 0);
        for (var clock = 0; (clock < 3); ++clock) {
            Rise(lowFalls: 3, mapper: mapper);
            raised.Add(item: mapper.Irq);
            mapper.CpuWrite(address: 0xE000, value: 0);
            mapper.CpuWrite(address: 0xE001, value: 0);
        }
        Assert.Equal(expected: expected, actual: raised);
    }
    /// <summary>On the running console, with background patterns at $0000 and sprites at $1000, the counter clocks once a
    /// line at the sprite fetches: reloaded to N on the pre-render line, it raises the IRQ on line N - 1, at the same dot
    /// every frame, and the CPU takes it.</summary>
    [Fact]
    public void Mmc3IrqLandsOnTheLatchedLine() {
        const byte Latch = 20;
        var program = new DeckProgram(origin: 0xC000)
            .Bytes(Sei, Cld)
            .Label(name: "warm-up-1").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-1", opcode: Bpl)
            .Label(name: "warm-up-2").Op(address: 0x2002, opcode: BitAbsolute).Branch(label: "warm-up-2", opcode: Bpl)
            // The APU's frame-counter IRQ shares the CPU's IRQ line; inhibit it.
            .Op(opcode: LdaImmediate, operand: ((byte)0x40)).Op(address: 0x4017, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: Latch).Op(address: 0xC000, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x88)).Op(address: 0x2000, opcode: StaAbsolute)
            .Op(opcode: LdaImmediate, operand: ((byte)0x18)).Op(address: 0x2001, opcode: StaAbsolute)
            .Bytes(Cli)
            .Label(name: "idle").Jump(label: "idle", opcode: Jmp)
            .Label(name: "nmi").Bytes(Pha).Op(address: 0xC001, opcode: StaAbsolute).Op(address: 0xE001, opcode: StaAbsolute).Bytes(Pla, Rti)
            .Label(name: "irq").Bytes(Pha).Op(address: 0xE000, opcode: StaAbsolute).Op(opcode: IncZeroPage, operand: ((byte)0x10)).Bytes(Pla, Rti);
        var machine = new HgdMachine(configuration: new HgdMachineConfiguration(
            cartridge: HgdCartridge.Load(image: program.Build(chr: new byte[8192], mapper: 4, prgRomSize: 32768))
        ));
        var mapper = machine.Bus.Mapper;
        var rises = new List<(int Line, int Dot)>();

        machine.RunCycles(masterTicks: (4 * 357_366UL));

        var handled = machine.Bus.Peek(address: 0x10);
        var previous = mapper.Irq;

        for (var tick = 0; (tick < (3 * 357_366)); ++tick) {
            machine.StepMasterTick();
            if (mapper.Irq && !previous) {
                rises.Add(item: (machine.Ppu.Line, machine.Ppu.Dot));
            }
            previous = mapper.Irq;
        }
        Assert.Equal(expected: 3, actual: rises.Count);
        Assert.All(collection: rises, action: rise => Assert.Equal(expected: (Latch - 1), actual: rise.Line));
        Assert.All(collection: rises, action: rise => Assert.Equal(expected: rises[0].Dot, actual: rise.Dot));
        Assert.InRange(actual: rises[0].Dot, low: 257, high: 265);
        Assert.Equal(expected: ((byte)(handled + 3)), actual: machine.Bus.Peek(address: 0x10));
    }
    /// <summary>Battery-backed MMC1 PRG-RAM survives the core's save file, and synthetic MMC1 and MMC3 images mount on the
    /// engine's core and run.</summary>
    [Fact]
    public void BankedBoardsMountRunAndPersistBatteryRam() {
        using var directory = new TemporaryDirectory(prefix: "puck-hgd-boards-");
        var path = directory.PathOf(name: "mmc1.sav");
        var image = new DeckProgram(origin: 0xC000)
            .Label(name: "idle").Jump(label: "idle", opcode: Jmp)
            .Build(battery: true, chr: [], mapper: 1, prgRomSize: 131072);

        using (var core = new HumbleGamingDeckCore(cartridgeImage: image, savePath: path)) {
            core.RunCycles(cycles: 357_366);
            core.Machine.Bus.Write(address: 0x6000, value: 0x3C);
            core.Machine.Bus.Write(address: 0x7FFF, value: 0xC3);
        }
        var saved = File.ReadAllBytes(path: path);

        Assert.Equal(expected: 8192, actual: saved.Length);
        Assert.Equal(expected: 0x3C, actual: saved[0]);
        Assert.Equal(expected: 0xC3, actual: saved[8191]);
        using (var restored = new HumbleGamingDeckCore(cartridgeImage: image, savePath: path)) {
            Assert.Equal(expected: 0x3C, actual: restored.PeekByte(address: 0x6000));
            Assert.Equal(expected: 0xC3, actual: restored.PeekByte(address: 0x7FFF));
        }

        using var mmc3 = new HumbleGamingDeckCore(cartridgeImage: new DeckProgram(origin: 0xC000)
            .Label(name: "idle").Jump(label: "idle", opcode: Jmp)
            .Build(chr: new byte[131072], mapper: 4, prgRomSize: 262144));

        mmc3.RunCycles(cycles: (10 * 357_366));
        Assert.Equal(expected: 10, actual: mmc3.NativeFrameIndex);
    }

    private static HgdMmc1 Mmc1(int prgBanks, int chrBanks) =>
        ((HgdMmc1)Load(chrBanks: chrBanks, chrUnit: 4096, mapper: 1, prgBanks: prgBanks, prgUnit: 16384, submapper: 0).CreateMapper());
    private static HgdMmc3 Mmc3(int prgBanks, int chrBanks, int submapper = 0) =>
        ((HgdMmc3)Load(chrBanks: chrBanks, chrUnit: 1024, mapper: 4, prgBanks: prgBanks, prgUnit: 8192, submapper: submapper).CreateMapper());
    // An NES 2.0 image with 8 KiB of PRG-RAM, CHR-RAM when chrBanks is zero, and each ROM byte holding its bank number.
    private static HgdCartridge Load(int mapper, int submapper, int prgBanks, int prgUnit, int chrBanks, int chrUnit) {
        var prg = (prgBanks * prgUnit);
        var chr = (chrBanks * chrUnit);
        var image = new byte[((16 + prg) + chr)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = ((byte)(prg >> 14));
        image[5] = ((byte)(chr >> 13));
        image[6] = ((byte)((mapper & 15) << 4));
        image[7] = ((byte)((mapper & 0xF0) | 8));
        image[8] = ((byte)(submapper << 4));
        image[10] = 0x07;
        image[11] = ((chr == 0) ? (byte)0x07 : (byte)0);
        for (var offset = 0; (offset < prg); ++offset) {
            image[(16 + offset)] = ((byte)(offset / prgUnit));
        }
        for (var offset = 0; (offset < chr); ++offset) {
            image[((16 + prg) + offset)] = ((byte)(offset / chrUnit));
        }

        return HgdCartridge.Load(image: image);
    }
    // One CPU cycle's falling M2 edge.
    private static void Cycle(IHgdMapper mapper) {
        mapper.ObserveM2(high: true, masterHalfTick: 0);
        mapper.ObserveM2(high: false, masterHalfTick: 0);
    }
    // A CPU write followed by the cycle after it, so the next write is not consecutive.
    private static void Write(IHgdMapper mapper, ushort address, byte value) {
        mapper.CpuWrite(address: address, value: value);
        Cycle(mapper: mapper);
        Cycle(mapper: mapper);
    }
    // Five serial writes, lowest bit first, to the register an address selects.
    private static void Serial(HgdMmc1 mapper, ushort address, byte value) {
        for (var bit = 0; (bit < 5); ++bit) {
            Write(address: address, mapper: mapper, value: ((byte)((value >> bit) & 1)));
        }
    }
    private static void BankData(HgdMmc3 mapper, int register, byte value) {
        mapper.CpuWrite(address: 0x8000, value: ((byte)register));
        mapper.CpuWrite(address: 0x8001, value: value);
    }
    // PPU A12 low for some falling M2 edges, then high.
    private static void Rise(HgdMmc3 mapper, int lowFalls) {
        mapper.ObservePpuAddress(address: 0x0FF0, masterTick: 0);
        for (var fall = 0; (fall < lowFalls); ++fall) {
            Cycle(mapper: mapper);
        }
        mapper.ObservePpuAddress(address: 0x1000, masterTick: 0);
    }
    private static int[] PrgWindows(IHgdMapper mapper) =>
        [.. Enumerable.Range(start: 0, count: 4).Select(selector: window => (int)mapper.CpuRead(address: ((ushort)(0x8000 + (window * 0x2000))), openBus: 0))];
    private static int[] ChrWindows(IHgdMapper mapper) {
        var nametables = new HgdNametableRam();

        return [.. Enumerable.Range(start: 0, count: 8).Select(selector: window => (int)mapper.PpuRead(address: ((ushort)(window * 0x400)), nametables: nametables))];
    }
    // The CIRAM page each nametable reaches: write a distinct byte through each, then find it in the pages.
    private static int[] Pages(IHgdMapper mapper) {
        var pages = new int[4];

        for (var quadrant = 0; (quadrant < 4); ++quadrant) {
            var nametables = new HgdNametableRam();

            mapper.PpuWrite(address: ((ushort)(0x2000 + (quadrant * 0x400))), nametables: nametables, value: 0x99);
            pages[quadrant] = ((nametables.Read(offset: 0, page: 1) == 0x99) ? 1 : 0);
        }

        return pages;
    }
}
