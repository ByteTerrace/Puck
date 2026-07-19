namespace Puck.Forge.Framework;

/// <summary>A named block of baked cartridge data: its absolute ROM address and byte length. Game code references the
/// address as an immediate; helpers take the whole table so address and length always travel together.</summary>
/// <param name="Address">The absolute ROM address of the block's first byte.</param>
/// <param name="Length">The block's byte length.</param>
internal readonly record struct RomTable(ushort Address, int Length);

/// <summary>
/// Lays out the cartridge's data window (0x4000..0x7FFF — the second 16 KiB bank, mapped by default on a 32 KiB
/// MBC1 image with zero bank-switch code): blocks are appended sequentially from 0x4000 and returned as
/// <see cref="RomTable"/>s, and an overrun throws at build time instead of corrupting the image.
/// </summary>
internal sealed class RomDataBuilder {
    /// <summary>The first data address (the start of ROM bank 1).</summary>
    public const ushort BaseAddress = 0x4000;

    private const int Capacity = 0x4000;

    private readonly List<byte> m_bytes = [];
    private readonly HashSet<string> m_names = [];
    private readonly TextModule m_text;

    /// <summary>Creates the builder. <paramref name="text"/> encodes <see cref="AddText"/> strings to font tile ids.</summary>
    /// <param name="text">The framework text module (supplies the character→tile encoding).</param>
    public RomDataBuilder(TextModule text) {
        ArgumentNullException.ThrowIfNull(text);

        m_text = text;
    }

    /// <summary>The number of data bytes laid out so far (of the 16 KiB window).</summary>
    public int BytesUsed => m_bytes.Count;

    /// <summary>Appends a named block and returns its table.</summary>
    /// <param name="name">A unique diagnostic name (duplicate names throw — they are almost always a copy/paste slip).</param>
    /// <param name="bytes">The block's bytes.</param>
    /// <returns>The block's address and length.</returns>
    public RomTable Add(string name, byte[] bytes) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(bytes);

        if (!m_names.Add(item: name)) {
            throw new ArgumentException(message: $"A data block named '{name}' was already added.", paramName: nameof(name));
        }

        if ((m_bytes.Count + bytes.Length) > Capacity) {
            throw new InvalidOperationException(message: $"Adding '{name}' ({bytes.Length} bytes) overruns the {Capacity}-byte data window ({m_bytes.Count} bytes already used).");
        }

        var table = new RomTable(Address: (ushort)(BaseAddress + m_bytes.Count), Length: bytes.Length);

        m_bytes.AddRange(collection: bytes);

        return table;
    }

    /// <summary>Appends a text string encoded to font tile ids, with the print routines' <c>0xFF</c> terminator.</summary>
    /// <param name="name">A unique diagnostic name.</param>
    /// <param name="text">The text (the framework font's character set: space, 0-9, A-Z, '&gt;', '-', '.').</param>
    /// <returns>The block's address and length (including the terminator byte).</returns>
    public RomTable AddText(string name, string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Add(name: name, bytes: [.. m_text.EncodeString(text: text), 0xFF]);
    }

    /// <summary>Returns the finished data blob (copied at <see cref="BaseAddress"/> by the cartridge assembler).</summary>
    /// <returns>The concatenated data bytes.</returns>
    public byte[] ToArray() => [.. m_bytes];
}
