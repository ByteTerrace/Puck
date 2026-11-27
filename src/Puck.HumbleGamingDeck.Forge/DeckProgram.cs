namespace Puck.HumbleGamingDeck.Forge;

/// <summary>A small 6502 assembler for original Deck cartridges, the Post battery's fixtures, the shipped test
/// cartridges and the cartridge compiler alike: it emits instructions from its origin onward, resolves labels for
/// branches, jumps, absolute operands and address bytes, and packs the program with CHR data into an NROM-256 image, or
/// into a larger MMC1 or MMC3 image whose last 16 KiB holds it. The reset vector is the origin; the NMI and IRQ vectors
/// are the <c>nmi</c> and <c>irq</c> labels when they exist.</summary>
/// <remarks>A far branch (<see cref="BranchFar"/>) packs as the two-byte relative branch while its target is in reach,
/// and otherwise as the inverted branch over a three-byte jump. Packing settles every far branch's form before any
/// address is resolved: a branch that grows can push another out of reach, never back in, so forms only grow and the
/// settling ends.</remarks>
public sealed class DeckProgram {
    private enum Fixup {
        Relative,
        Absolute,
        Low,
        High,
    }

    private readonly ushort m_origin;
    private readonly List<byte> m_code = [];
    private readonly Dictionary<string, int> m_labels = new(comparer: StringComparer.Ordinal);
    private readonly List<(int Offset, string Label, Fixup Kind)> m_fixups = [];
    // Each far branch's opcode offset and target, in emission order (and so in offset order).
    private readonly List<(int Offset, string Label)> m_far = [];
    private int m_nextLabel;

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

    /// <summary>Gets the CPU address the next emitted byte lands at, counting every far branch at its short form.</summary>
    public ushort Here => ((ushort)(m_origin + m_code.Count));
    /// <summary>Gets the bytes emitted so far, counting every far branch at its short form.</summary>
    public int Length => m_code.Count;

    /// <summary>Returns a label name no other call returns and no caller-chosen name can collide with.</summary>
    /// <returns>The fresh, unmarked label.</returns>
    public string NewLabel() =>
        $"@{m_nextLabel++}";
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
    /// <summary>Emits a relative branch to a label, which must be in reach.</summary>
    /// <param name="opcode">The branch opcode.</param>
    /// <param name="label">The target label.</param>
    /// <returns>This program.</returns>
    public DeckProgram Branch(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, Fixup.Relative));
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Emits a conditional branch to a label at any distance: the relative branch when the target is in reach,
    /// otherwise the opposite branch over an absolute jump.</summary>
    /// <param name="opcode">The branch opcode; its opposite differs in bit 5, as every 6502 branch pair does.</param>
    /// <param name="label">The target label.</param>
    /// <returns>This program.</returns>
    public DeckProgram BranchFar(byte opcode, string label) {
        m_far.Add(item: (m_code.Count, label));
        m_code.Add(item: opcode);
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Emits an instruction whose absolute operand is a label's address: a jump or call, or any absolute or
    /// indexed load or store of a table the program carries.</summary>
    /// <param name="opcode">The opcode.</param>
    /// <param name="label">The target label.</param>
    /// <returns>This program.</returns>
    public DeckProgram Jump(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, Fixup.Absolute));
        m_code.Add(item: 0);
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Emits an instruction whose one-byte operand is the low byte of a label's address.</summary>
    /// <param name="opcode">The opcode, normally an immediate load.</param>
    /// <param name="label">The label.</param>
    /// <returns>This program.</returns>
    public DeckProgram Low(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, Fixup.Low));
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Emits an instruction whose one-byte operand is the high byte of a label's address.</summary>
    /// <param name="opcode">The opcode, normally an immediate load.</param>
    /// <param name="label">The label.</param>
    /// <returns>This program.</returns>
    public DeckProgram High(byte opcode, string label) {
        m_code.Add(item: opcode);
        m_fixups.Add(item: (m_code.Count, label, Fixup.High));
        m_code.Add(item: 0);

        return this;
    }
    /// <summary>Returns the address a label marks.</summary>
    /// <param name="name">The label.</param>
    /// <returns>The CPU address, with every far branch before it at its settled form.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException"><paramref name="name"/> does not mark an address.</exception>
    public ushort AddressOf(string name) =>
        Final(offset: m_labels[name], grown: Settle());
    /// <summary>Packs the program into an NROM-256 image with 32 KiB of PRG ROM and 8 KiB of CHR ROM.</summary>
    /// <param name="chr">The pattern data, at most 8 KiB; any remaining pattern bytes are zero.</param>
    /// <param name="horizontal">Whether the nametables mirror horizontally rather than vertically.</param>
    /// <returns>The iNES image. With origin $C000 the 16 KiB at $8000 are zero for the caller to fill.</returns>
    /// <exception cref="InvalidOperationException">A branch target is out of range.</exception>
    /// <exception cref="KeyNotFoundException">A referenced label is missing.</exception>
    /// <exception cref="ArgumentException"><paramref name="chr"/> is larger than 8 KiB, or the emitted program overlaps
    /// the interrupt vectors.</exception>
    /// <exception cref="ArgumentNullException">A referenced label is <see langword="null"/>.</exception>
    public byte[] Build(ReadOnlySpan<byte> chr, bool horizontal = false) {
        var image = new byte[((16 + 32768) + 8192)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 2;
        image[5] = 1;
        image[6] = (horizontal ? (byte)0 : (byte)1);
        Pack(window: image.AsSpan(start: 16, length: 32768));
        chr.CopyTo(destination: image.AsSpan(start: (16 + 32768)));

        return image;
    }
    /// <summary>Packs the program into an NES 2.0 image for a banked board, with 8 KiB of PRG-RAM.
    /// The program and its vectors fill the last 32 KiB of PRG ROM; MMC1 (PRG mode 3) and MMC3 (PRG mode 0) keep its
    /// last 16 KiB at $C000 from power-on, so a program that switches banks is built with origin $C000. Every other PRG
    /// byte is zero for the caller to fill.</summary>
    /// <param name="chr">The CHR ROM, a whole number of 8 KiB banks, or empty for 8 KiB of CHR-RAM.</param>
    /// <param name="mapper">The iNES mapper number: 1 for MMC1, 4 for MMC3.</param>
    /// <param name="prgRomSize">The PRG ROM size in bytes, a multiple of 16 KiB from 32 KiB.</param>
    /// <param name="submapper">The NES 2.0 submapper number.</param>
    /// <param name="battery">Whether the PRG-RAM is battery-backed NVRAM rather than volatile RAM.</param>
    /// <param name="horizontal">Whether the header's nametable wiring is horizontal rather than vertical; a board that
    /// switches its wiring overrides it.</param>
    /// <returns>The NES 2.0 image.</returns>
    /// <exception cref="InvalidOperationException">A branch target is out of range.</exception>
    /// <exception cref="KeyNotFoundException">A referenced label is missing.</exception>
    /// <exception cref="ArgumentException">A size is not a whole number of banks, or the emitted program overlaps the
    /// interrupt vectors.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mapper"/> or <paramref name="submapper"/> does not fit
    /// its header field.</exception>
    /// <exception cref="ArgumentNullException">A referenced label is <see langword="null"/>.</exception>
    public byte[] Build(ReadOnlySpan<byte> chr, int mapper, int prgRomSize, int submapper = 0, bool battery = false, bool horizontal = false) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: mapper);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: mapper, other: 255);
        ArgumentOutOfRangeException.ThrowIfNegative(value: submapper);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: submapper, other: 15);
        if ((prgRomSize < 32768) || ((prgRomSize % 16384) != 0) || ((prgRomSize >> 14) > 255) || ((chr.Length % 8192) != 0) || ((chr.Length >> 13) > 255)) {
            throw new ArgumentException(message: "PRG ROM is a multiple of 16 KiB from 32 KiB and CHR ROM a multiple of 8 KiB, each at most 255 banks.");
        }

        var image = new byte[((16 + prgRomSize) + chr.Length)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = ((byte)(prgRomSize >> 14));
        image[5] = ((byte)(chr.Length >> 13));
        image[6] = ((byte)(((mapper & 15) << 4) | (battery ? 2 : 0) | (horizontal ? 0 : 1)));
        image[7] = ((byte)((mapper & 0xF0) | 8));
        image[8] = ((byte)(submapper << 4));
        image[10] = (battery ? (byte)0x70 : (byte)0x07);
        image[11] = (chr.IsEmpty ? (byte)0x07 : (byte)0);
        Pack(window: image.AsSpan(start: ((16 + prgRomSize) - 32768), length: 32768));
        chr.CopyTo(destination: image.AsSpan(start: (16 + prgRomSize)));

        return image;
    }

    // Settles every far branch's form, returning each branch's growth before it: entry i is the bytes the long forms
    // among the first i far branches add. Every branch starts short; a pass measures reach against the previous pass's
    // layout, and one whose target is out of reach grows. Growing only moves code apart, so a pass that grows nothing
    // has settled the layout it measured.
    private int[] Settle() {
        var far = new bool[m_far.Count];
        var grown = new int[(m_far.Count + 1)];

        for (var changed = true; changed;) {
            changed = false;
            for (var index = 0; (index < m_far.Count); ++index) {
                if (far[index]) {
                    continue;
                }

                var (offset, label) = m_far[index];
                var displacement = (Final(offset: m_labels[label], grown: grown) - (Final(offset: offset, grown: grown) + 2));

                if ((displacement < -128) || (displacement > 127)) {
                    far[index] = true;
                    changed = true;
                }
            }
            for (var index = 0; (index < m_far.Count); ++index) {
                grown[(index + 1)] = (grown[index] + (far[index] ? 3 : 0));
            }
        }

        return grown;
    }
    // The address an emitted offset lands at once every long far branch before it has grown by its three-byte jump.
    private ushort Final(int offset, int[] grown) {
        var low = 0;
        var high = m_far.Count;

        // The count of far branches emitted before the offset; their offsets ascend in emission order.
        while (low < high) {
            var middle = ((low + high) >>> 1);

            if (m_far[middle].Offset < offset) {
                low = (middle + 1);
            } else {
                high = middle;
            }
        }

        return ((ushort)((m_origin + offset) + grown[low]));
    }
    // Resolves every label reference and writes the code and the three vectors into the 32 KiB window $8000-$FFFF maps.
    private void Pack(Span<byte> window) {
        var grown = Settle();
        var code = new List<byte>(capacity: (m_code.Count + (3 * m_far.Count)));
        var next = 0;

        for (var index = 0; (index <= m_far.Count); ++index) {
            var end = ((index < m_far.Count) ? m_far[index].Offset : m_code.Count);

            for (; (next < end); ++next) {
                code.Add(item: m_code[next]);
            }
            if (index == m_far.Count) {
                break;
            }

            var (offset, label) = m_far[index];
            var target = Final(offset: m_labels[label], grown: grown);
            var opcode = m_code[offset];

            if (grown[(index + 1)] != grown[index]) {
                code.AddRange(collection: [((byte)(opcode ^ 0x20)), 3, DeckOpcodes.Jmp, ((byte)target), ((byte)(target >> 8))]);
            } else {
                code.AddRange(collection: [opcode, ((byte)(target - (Final(offset: offset, grown: grown) + 2)))]);
            }
            next = (offset + 2);
        }
        foreach (var (offset, label, kind) in m_fixups) {
            var site = (Final(offset: offset, grown: grown) - m_origin);
            var target = Final(offset: m_labels[label], grown: grown);

            switch (kind) {
                case Fixup.Relative: {
                        var displacement = (target - ((m_origin + site) + 1));

                        if ((displacement < -128) || (displacement > 127)) {
                            throw new InvalidOperationException(message: $"Branch to '{label}' is out of range.");
                        }
                        code[site] = ((byte)displacement);

                        break;
                    }
                case Fixup.Absolute:
                    code[site] = ((byte)target);
                    code[(site + 1)] = ((byte)(target >> 8));

                    break;
                case Fixup.Low:
                    code[site] = ((byte)target);

                    break;
                default:
                    code[site] = ((byte)(target >> 8));

                    break;
            }
        }

        var start = (m_origin - 0x8000);

        if ((start + code.Count) > 0x7FFA) {
            throw new ArgumentException(message: $"The program's {code.Count} bytes overlap the interrupt vectors; {(0x7FFA - start)} bytes fit below them.");
        }
        code.ToArray().CopyTo(destination: window[start..]);
        Vector(address: (m_labels.ContainsKey(key: "nmi") ? AddressOf(name: "nmi") : m_origin), offset: 0x7FFA, window: window);
        Vector(address: m_origin, offset: 0x7FFC, window: window);
        Vector(address: (m_labels.ContainsKey(key: "irq") ? AddressOf(name: "irq") : m_origin), offset: 0x7FFE, window: window);
    }
    private static void Vector(Span<byte> window, int offset, ushort address) {
        window[offset] = ((byte)address);
        window[(offset + 1)] = ((byte)(address >> 8));
    }
}
