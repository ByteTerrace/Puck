namespace Puck.HumbleGamingDeck.Forge;

/// <summary>A small 6502 assembler for original Deck cartridges, the Post battery's fixtures and the shipped test
/// cartridges alike: it emits instructions from its origin onward, resolves labels for branches and jumps, and packs the
/// program with CHR data into an NROM-256 image, or into a larger MMC1 or MMC3 image whose last 16 KiB holds it. The
/// reset vector is the origin; the NMI and IRQ vectors are the <c>nmi</c> and <c>irq</c> labels when they exist.</summary>
public sealed class DeckProgram {
    private readonly ushort m_origin;
    private readonly List<byte> m_code = [];
    private readonly Dictionary<string, int> m_labels = new(comparer: StringComparer.Ordinal);
    private readonly List<(int Offset, string Label, bool Relative)> m_fixups = [];

    /// <summary>Initializes a new instance of the <see cref="DeckProgram"/> class with no code.</summary>
    /// <param name="origin">The CPU address of the first emitted byte: $8000, or $C000 for a program packed into a banked
    /// image, where only $C000-$FFFF stays mapped while the board switches banks.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> is neither $8000 nor $C000.</exception>
    public DeckProgram(ushort origin = 0x8000) {
        if ((origin != 0x8000) && (origin != 0xC000)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(origin), actualValue: origin, message: "The origin is $8000 or $C000.");
        }
        m_origin = origin;
    }

    /// <summary>Gets the CPU address the next emitted byte lands at.</summary>
    public ushort Here => ((ushort)(m_origin + m_code.Count));

    /// <summary>Marks the current address with a label.</summary>
    /// <param name="name">The label.</param>
    /// <returns>This program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> already marks an address.</exception>
    public DeckProgram Label(string name) {
        m_labels.Add(key: name, value: m_code.Count);

        return this;
    }
    /// <summary>Emits raw bytes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>This program.</returns>
    public DeckProgram Bytes(params ReadOnlySpan<byte> bytes) {
        foreach (var value in bytes) {
            m_code.Add(item: value);
        }

        return this;
    }
    /// <summary>Emits an instruction with an immediate or zero-page operand.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <param name="operand">The one-byte operand.</param>
    /// <returns>This program.</returns>
    public DeckProgram Op(byte opcode, byte operand) =>
        Bytes(opcode, operand);
    /// <summary>Emits an instruction with an absolute operand.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <param name="address">The two-byte operand.</param>
    /// <returns>This program.</returns>
    public DeckProgram Op(byte opcode, ushort address) =>
        Bytes(opcode, ((byte)address), ((byte)(address >> 8)));
    /// <summary>Emits a relative branch to a label.</summary>
    /// <param name="opcode">The branch opcode.</param>
    /// <param name="label">The target label.</param>
    /// <returns>This program.</returns>
    public DeckProgram Branch(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, true));
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Emits an absolute jump or call to a label.</summary>
    /// <param name="opcode">The JMP or JSR opcode.</param>
    /// <param name="label">The target label.</param>
    /// <returns>This program.</returns>
    public DeckProgram Jump(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, false));
        m_code.Add(item: 0);
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Returns the address a label marks.</summary>
    /// <param name="name">The label.</param>
    /// <returns>The CPU address.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="name"/> does not mark an address.</exception>
    public ushort AddressOf(string name) =>
        ((ushort)(m_origin + m_labels[name]));
    /// <summary>Packs the program into an NROM-256 image with 32 KiB of PRG ROM and 8 KiB of CHR ROM and vertical
    /// mirroring.</summary>
    /// <param name="chr">The pattern data, at most 8 KiB; any remaining pattern bytes are zero.</param>
    /// <returns>The iNES image.</returns>
    /// <exception cref="InvalidOperationException">A branch target is out of range.</exception>
    /// <exception cref="KeyNotFoundException">A referenced label is missing.</exception>
    /// <exception cref="ArgumentException"><paramref name="chr"/> is larger than 8 KiB, or the emitted program does not
    /// fit in the image.</exception>
    /// <exception cref="ArgumentNullException">A referenced label is <see langword="null"/>.</exception>
    public byte[] Build(ReadOnlySpan<byte> chr) {
        var image = new byte[((16 + 32768) + 8192)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        Pack(window: image.AsSpan(start: 16, length: 32768));
        chr.CopyTo(destination: image.AsSpan(start: (16 + 32768)));

        return image;
    }
    /// <summary>Packs the program into an NES 2.0 image for a banked board, with vertical mirroring and 8 KiB of PRG-RAM.
    /// The program fills the last 16 KiB of PRG ROM, which MMC1 (PRG mode 3) and MMC3 (PRG mode 0) map at $C000 from
    /// power-on, so it is built with origin $C000; every other PRG byte is zero for the caller to fill.</summary>
    /// <param name="chr">The CHR ROM, a whole number of 8 KiB banks, or empty for 8 KiB of CHR-RAM.</param>
    /// <param name="mapper">The iNES mapper number: 1 for MMC1, 4 for MMC3.</param>
    /// <param name="prgRomSize">The PRG ROM size in bytes, a multiple of 16 KiB from 32 KiB.</param>
    /// <param name="submapper">The NES 2.0 submapper number.</param>
    /// <param name="battery">Whether the PRG-RAM is battery-backed NVRAM rather than volatile RAM.</param>
    /// <returns>The NES 2.0 image.</returns>
    /// <exception cref="InvalidOperationException">The program's origin is not $C000, or a branch target is out of
    /// range.</exception>
    /// <exception cref="KeyNotFoundException">A referenced label is missing.</exception>
    /// <exception cref="ArgumentException">A size is not a whole number of banks, or the emitted program does not fit in
    /// its 16 KiB.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mapper"/> or <paramref name="submapper"/> does not fit
    /// its header field.</exception>
    /// <exception cref="ArgumentNullException">A referenced label is <see langword="null"/>.</exception>
    public byte[] Build(ReadOnlySpan<byte> chr, int mapper, int prgRomSize, int submapper = 0, bool battery = false) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: mapper);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: mapper, other: 255);
        ArgumentOutOfRangeException.ThrowIfNegative(value: submapper);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: submapper, other: 15);
        if (m_origin != 0xC000) {
            throw new InvalidOperationException(message: "A banked image holds the program in its last 16 KiB; build it with origin $C000.");
        }
        if ((prgRomSize < 32768) || ((prgRomSize % 16384) != 0) || ((prgRomSize >> 14) > 255) || ((chr.Length % 8192) != 0) || ((chr.Length >> 13) > 255)) {
            throw new ArgumentException(message: "PRG ROM is a multiple of 16 KiB from 32 KiB and CHR ROM a multiple of 8 KiB, each at most 255 banks.");
        }

        var image = new byte[((16 + prgRomSize) + chr.Length)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = ((byte)(prgRomSize >> 14));
        image[5] = ((byte)(chr.Length >> 13));
        image[6] = ((byte)(((mapper & 15) << 4) | (battery ? 2 : 0) | 1));
        image[7] = ((byte)((mapper & 0xF0) | 8));
        image[8] = ((byte)(submapper << 4));
        image[10] = (battery ? (byte)0x70 : (byte)0x07);
        image[11] = (chr.IsEmpty ? (byte)0x07 : (byte)0);
        Pack(window: image.AsSpan(start: ((16 + prgRomSize) - 32768), length: 32768));
        chr.CopyTo(destination: image.AsSpan(start: (16 + prgRomSize)));

        return image;
    }

    // Resolves every label reference and writes the code and the three vectors into the 32 KiB window $8000-$FFFF maps.
    private void Pack(Span<byte> window) {
        foreach (var (offset, label, relative) in m_fixups) {
            var target = m_labels[label];

            if (relative) {
                var displacement = (target - (offset + 1));

                if ((displacement < -128) || (displacement > 127)) {
                    throw new InvalidOperationException(message: $"Branch to '{label}' is out of range.");
                }
                m_code[offset] = ((byte)displacement);
            } else {
                var address = (m_origin + target);

                m_code[offset] = ((byte)address);
                m_code[(offset + 1)] = ((byte)(address >> 8));
            }
        }

        var start = (m_origin - 0x8000);

        if ((start + m_code.Count) > 0x7FFA) {
            throw new ArgumentException(message: "The program overlaps the interrupt vectors.");
        }
        m_code.ToArray().CopyTo(destination: window[start..]);
        Vector(address: (m_labels.ContainsKey(key: "nmi") ? AddressOf(name: "nmi") : m_origin), offset: 0x7FFA, window: window);
        Vector(address: m_origin, offset: 0x7FFC, window: window);
        Vector(address: (m_labels.ContainsKey(key: "irq") ? AddressOf(name: "irq") : m_origin), offset: 0x7FFE, window: window);
    }
    private static void Vector(Span<byte> window, int offset, ushort address) {
        window[offset] = ((byte)address);
        window[(offset + 1)] = ((byte)(address >> 8));
    }
}
