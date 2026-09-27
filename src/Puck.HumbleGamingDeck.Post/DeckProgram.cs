namespace Puck.HumbleGamingDeck.Post;

/// <summary>A small 6502 assembler for the battery's original fixture cartridges: it emits instructions at $8000 onward,
/// resolves labels for branches and jumps, and packs the program with CHR data into an NROM-256 image whose reset vector
/// is $8000.</summary>
internal sealed class DeckProgram {
    private readonly List<byte> m_code = [];
    private readonly Dictionary<string, int> m_labels = new(comparer: StringComparer.Ordinal);
    private readonly List<(int Offset, string Label, bool Relative)> m_fixups = [];

    /// <summary>Gets the CPU address the next emitted byte lands at.</summary>
    public ushort Here => ((ushort)(0x8000 + m_code.Count));

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
        ((ushort)(0x8000 + m_labels[name]));
    /// <summary>Packs the program into an NROM-256 image with 32 KiB of PRG ROM and 8 KiB of CHR ROM, vertical mirroring,
    /// and every vector pointing at $8000 unless an NMI label exists.</summary>
    /// <param name="chr">The pattern data, at most 8 KiB; any remaining pattern bytes are zero.</param>
    /// <returns>The iNES image.</returns>
    /// <exception cref="InvalidOperationException">A branch target is out of range.</exception>
    /// <exception cref="KeyNotFoundException">A referenced label is missing.</exception>
    /// <exception cref="ArgumentException"><paramref name="chr"/> is larger than 8 KiB, or the emitted program does not
    /// fit in the image.</exception>
    /// <exception cref="ArgumentNullException">A referenced label is <see langword="null"/>.</exception>
    public byte[] Build(ReadOnlySpan<byte> chr) {
        foreach (var (offset, label, relative) in m_fixups) {
            var target = m_labels[label];

            if (relative) {
                var displacement = (target - (offset + 1));

                if ((displacement < -128) || (displacement > 127)) {
                    throw new InvalidOperationException(message: $"Branch to '{label}' is out of range.");
                }
                m_code[offset] = ((byte)displacement);
            } else {
                var address = (0x8000 + target);

                m_code[offset] = ((byte)address);
                m_code[(offset + 1)] = ((byte)(address >> 8));
            }
        }

        var image = new byte[((16 + 32768) + 8192)];

        "NES\u001a"u8.CopyTo(destination: image);
        image[4] = 2;
        image[5] = 1;
        image[6] = 1;
        m_code.ToArray().CopyTo(array: image, index: 16);

        var nmi = (m_labels.ContainsKey(key: "nmi") ? AddressOf(name: "nmi") : ((ushort)0x8000));

        image[(16 + 0x7FFA)] = ((byte)nmi);
        image[(16 + 0x7FFB)] = ((byte)(nmi >> 8));
        image[(16 + 0x7FFC)] = 0x00;
        image[(16 + 0x7FFD)] = 0x80;
        image[(16 + 0x7FFE)] = 0x00;
        image[(16 + 0x7FFF)] = 0x80;
        chr.CopyTo(destination: image.AsSpan(start: (16 + 32768)));

        return image;
    }
}
