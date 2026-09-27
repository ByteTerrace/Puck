namespace Puck.HumbleGamingDeck.Tests;

/// <summary>Verifies header decoding, board refusal diagnostics, and NROM memory selection.</summary>
public sealed class LoaderHeaderTests {
    private static byte[] Header() {
        var bytes = new byte[16];

        "NES\u001a"u8.CopyTo(destination: bytes);
        bytes[4] = 1;
        bytes[7] = 8;
        bytes[11] = 7;

        return bytes;
    }

    /// <summary>Checks the NES 2.0 exponent/multiplier representation without narrowing its byte count.</summary>
    /// <param name="exponent">The exponent encoded in the size byte.</param>
    /// <param name="multiplier">The encoded multiplier index, from zero through three.</param>
    /// <param name="expected">The exact ROM size in bytes.</param>
    [InlineData(14, 0, 16384UL)]
    [InlineData(13, 1, 24576UL)]
    [InlineData(12, 2, 20480UL)]
    [InlineData(11, 3, 14336UL)]
    [InlineData(63, 0, 9223372036854775808UL)]
    [Theory]
    public void Nes20ExponentMultiplierSizesAreExact(int exponent, int multiplier, ulong expected) {
        var bytes = Header();

        bytes[5] = ((byte)((exponent << 2) | multiplier));
        bytes[4] = bytes[5];
        bytes[9] = 0xFF;
        var header = HgdCartridgeHeader.Parse(header: bytes);

        Assert.Equal(expected: ((UInt128)expected), actual: header.PrgRomSize);
        Assert.Equal(expected: ((UInt128)expected), actual: header.ChrRomSize);
    }
    /// <summary>Checks mapper, submapper, memory, timing, console, and expansion fields.</summary>
    [Fact]
    public void Nes20ParsesAllExtendedFields() {
        var bytes = Header();

        bytes[4] = 0x23;
        bytes[5] = 0x45;
        bytes[6] = 0xCD;
        bytes[7] = 0xB9;
        bytes[8] = 0x7A;
        bytes[9] = 0x12;
        bytes[10] = 0x87;
        bytes[11] = 0x65;
        bytes[12] = 3;
        bytes[13] = 0x42;
        bytes[14] = 2;
        bytes[15] = 0x21;
        var header = HgdCartridgeHeader.Parse(header: bytes);

        Assert.Equal(expected: 0xABC, actual: header.Mapper);
        Assert.Equal(expected: 7, actual: header.Submapper);
        Assert.Equal(expected: (((UInt128)0x223) * 16384), actual: header.PrgRomSize);
        Assert.Equal(expected: (((UInt128)0x145) * 8192), actual: header.ChrRomSize);
        Assert.Equal(expected: 8192, actual: header.PrgRamSize);
        Assert.Equal(expected: 16384, actual: header.PrgNvRamSize);
        Assert.Equal(expected: 2048, actual: header.ChrRamSize);
        Assert.Equal(expected: 4096, actual: header.ChrNvRamSize);
        Assert.True(condition: header.HasTrainer);
        Assert.Equal(expected: HgdMirroring.FourScreen, actual: header.Mirroring);
        Assert.Equal(expected: HgdTiming.Dendy, actual: header.Timing);
        Assert.Equal(expected: 2, actual: header.VsPpuType);
        Assert.Equal(expected: 4, actual: header.VsHardwareType);
        Assert.Equal(expected: 2, actual: header.MiscellaneousRomCount);
        Assert.Equal(expected: 0x21, actual: header.DefaultExpansionDevice);
    }
    /// <summary>Rejects reserved legacy header bits with an ambiguity diagnostic.</summary>
    /// <param name="offset">The header byte offset to damage.</param>
    /// <param name="value">The byte containing reserved bits.</param>
    [InlineData(7, 4)]
    [InlineData(9, 128)]
    [InlineData(11, 1)]
    [InlineData(12, 68)]
    [InlineData(15, 33)]
    [Theory]
    public void DamagedInesGetsAnExplicitDiagnostic(int offset, byte value) {
        var bytes = Header();

        bytes[7] = 0;
        bytes[11] = 0;
        bytes[offset] = value;
        var failure = Assert.Throws<InvalidDataException>(testCode: () => HgdCartridgeHeader.Parse(header: bytes));

        Assert.Contains(expectedSubstring: "Ambiguous or damaged iNES", actualString: failure.Message);
    }
    /// <summary>Distinguishes legacy unspecified memory from an explicit NES 2.0 zero size.</summary>
    [Fact]
    public void LegacyUnspecifiedRamIsDiagnosedAndNes20ZeroMeansAbsent() {
        var bytes = Header();

        bytes[7] = 0;
        bytes[11] = 0;
        var legacy = HgdCartridgeHeader.Parse(header: bytes);

        Assert.Equal(expected: 8192, actual: legacy.PrgRamSize);
        Assert.NotNull(@object: legacy.Diagnostic);
        bytes[7] = 8;
        var modern = HgdCartridgeHeader.Parse(header: bytes);

        Assert.Equal(expected: 0, actual: modern.PrgRamSize);
        Assert.Equal(expected: 0, actual: modern.ChrRamSize);
    }
    /// <summary>Checks that unsupported board diagnostics identify the mapper and submapper.</summary>
    /// <param name="mapper">The twelve-bit mapper number.</param>
    /// <param name="submapper">The four-bit submapper number.</param>
    /// <param name="name">The board name or number expected in the diagnostic.</param>
    [InlineData(1, 0, "MMC1")]
    [InlineData(4, 1, "MMC3")]
    [InlineData(0, 1, "NROM")]
    [InlineData(4095, 15, "4095")]
    [Theory]
    public void UnimplementedMapperAndSubmapperAreNamed(int mapper, int submapper, string name) {
        var bytes = Header();

        bytes[6] = ((byte)((mapper & 15) << 4));
        bytes[7] |= ((byte)(mapper & 0xF0));
        bytes[8] = ((byte)((mapper >> 8) | (submapper << 4)));
        var failure = Assert.Throws<NotSupportedException>(testCode: () => HgdCartridge.Load(image: bytes));

        Assert.Contains(expectedSubstring: name, actualString: failure.Message);
        Assert.Contains(expectedSubstring: $"submapper {submapper}", actualString: failure.Message);
    }
    /// <summary>Checks truncated payload refusal and the largest exponent/multiplier byte count.</summary>
    [Fact]
    public void TruncatedImageIsRefusedAndTheLargestHeaderSizeIsStillParsedExactly() {
        Assert.Throws<InvalidDataException>(testCode: () => HgdCartridge.Load(image: Header()));
        var bytes = Header();

        bytes[4] = 255;
        bytes[9] = 15;
        Assert.Equal(expected: (((UInt128)7) << 63), actual: HgdCartridgeHeader.Parse(header: bytes).PrgRomSize);
        Assert.Throws<NotSupportedException>(testCode: () => HgdCartridge.Load(image: bytes));
    }
    /// <summary>Checks trainer placement, fixed PRG windows, mirroring, and CHR-ROM write protection.</summary>
    [Fact]
    public void NromTrainerAndOptionalRamFollowHeader() {
        var image = new byte[(((16 + 512) + 32768) + 8192)];

        Header().CopyTo(array: image, index: 0);
        image[4] = 2;
        image[5] = 1;
        image[6] = 5;
        image[10] = 7;
        image[11] = 0;
        image[16] = 0x6A;
        image[528] = 0x12;
        image[(528 + 16384)] = 0x34;
        var mapper = HgdCartridge.Load(image: image).CreateMapper();

        Assert.Equal(expected: 0x6A, actual: mapper.CpuRead(address: 0x7000, openBus: 0));
        Assert.Equal(expected: 0x12, actual: mapper.CpuPeek(address: 0x8000, openBus: 0));
        Assert.Equal(expected: 0x34, actual: mapper.CpuPeek(address: 0xC000, openBus: 0));
        Assert.Equal(expected: HgdMirroring.Vertical, actual: mapper.Header.Mirroring);
        mapper.PpuWrite(address: 0, value: 0x77);
        Assert.Equal(expected: 0, actual: mapper.PpuRead(address: 0));
    }
}
