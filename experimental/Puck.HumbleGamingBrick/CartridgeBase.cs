using System.Numerics;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The shared substrate for every mapper: it owns the immutable ROM image and the mutable save RAM, and turns the
/// region-decoded reads and writes into array accesses once a subclass has mapped the address to an offset. ROM reads
/// mirror over the image, and RAM accesses outside the populated size or while disabled read open-bus <c>0xFF</c> and
/// drop writes — the hardware-faithful defaults. Only the save RAM and the subclass's registers are snapshotted; the
/// ROM is reloaded from the same image when a machine is forked.
/// </summary>
public abstract class CartridgeBase : ICartridge {
    private readonly byte[] m_ram;
    private readonly byte[] m_rom;

    /// <summary>Initializes the shared cartridge state from a ROM image and its decoded header.</summary>
    /// <param name="rom">The full ROM image.</param>
    /// <param name="header">The decoded header, whose RAM size sizes the save RAM by default.</param>
    /// <param name="ramByteCount">The size of the save RAM in bytes, or <c>-1</c> to take it from the header. A mapper
    /// with built-in RAM that the header does not describe (MBC2) supplies its own size here.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> or <paramref name="header"/> is
    /// <see langword="null"/>.</exception>
    protected CartridgeBase(byte[] rom, CartridgeHeader header, int ramByteCount = -1) {
        ArgumentNullException.ThrowIfNull(argument: rom);
        ArgumentNullException.ThrowIfNull(argument: header);

        Header = header;
        m_ram = new byte[(ramByteCount < 0) ? header.RamByteCount : ramByteCount];
        m_rom = rom;
    }

    /// <inheritdoc/>
    public CartridgeHeader Header { get; }
    /// <inheritdoc/>
    public int ExternalRamByteCount => m_ram.Length;
    /// <inheritdoc/>
    public bool ExternalRamDirty { get; private set; }

    /// <summary>Gets whether the external RAM window currently responds (RAM exists and the mapper has enabled it).</summary>
    protected abstract bool RamAccessible { get; }

    /// <inheritdoc/>
    public byte[] ExportExternalRam() {
        return [.. m_ram];
    }
    /// <inheritdoc/>
    public void ReadExternalRam(int offset, Span<byte> destination) {
        // Side-effect-free by construction: a straight copy out of the private RAM array by absolute offset, with no
        // dirty-flag touch and no bank-select read — the host win-condition poll must never perturb the game. Missing
        // bytes (out of range, or no RAM at all) stay zero, so a too-small cartridge simply never matches a gate.
        destination.Clear();

        if (offset < 0) {
            return;
        }

        var available = (m_ram.Length - offset);

        if (available <= 0) {
            return;
        }

        m_ram.AsSpan(start: offset, length: Math.Min(available, destination.Length)).CopyTo(destination: destination);
    }
    /// <inheritdoc/>
    public void ImportExternalRam(ReadOnlySpan<byte> source) {
        source[..Math.Min(source.Length, m_ram.Length)].CopyTo(destination: m_ram);
        ExternalRamDirty = false;
    }
    /// <inheritdoc/>
    public void MarkExternalRamClean() {
        ExternalRamDirty = false;
    }
    /// <inheritdoc/>
    public virtual int PersistentClockByteCount => 0;
    /// <inheritdoc/>
    public virtual byte[] ExportPersistentClock(long unixTimestampSeconds) {
        return [];
    }
    /// <inheritdoc/>
    public virtual void ImportPersistentClock(ReadOnlySpan<byte> source) {
    }

    /// <inheritdoc/>
    public byte ReadRom(ushort address) =>
        m_rom[MapRomOffset(address: address) % m_rom.Length];
    /// <inheritdoc/>
    public virtual byte ReadRam(ushort address) {
        if (!RamAccessible) {
            return 0xFF;
        }

        var offset = MapRamOffset(address: address);

        return ((uint)offset < (uint)m_ram.Length) ? m_ram[offset] : (byte)0xFF;
    }
    /// <inheritdoc/>
    public virtual void WriteRam(ushort address, byte value) {
        if (!RamAccessible) {
            return;
        }

        var offset = MapRamOffset(address: address);

        if ((uint)offset < (uint)m_ram.Length) {
            m_ram[offset] = value;
            // Every mapper's RAM store funnels through here (m_ram is private), so this is the ONE dirty site the
            // host's battery-save flush watches.
            ExternalRamDirty = true;
        }
    }
    /// <inheritdoc/>
    public abstract void WriteControl(ushort address, byte value);
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBytes(value: m_ram);
        SaveRegisters(writer: writer);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        reader.ReadBytes(destination: m_ram);
        LoadRegisters(reader: reader);
    }

    /// <summary>Deposits a block straight into save RAM at an absolute byte offset, bypassing the address decode and the
    /// dirty flag. This is the seam a sensor mapper (the Pocket Camera) uses to write its freshly captured image into
    /// bank&#160;0: that image is <b>regenerated hardware output</b>, not a player-authored store, so it must NOT trip the
    /// battery-save flush — <see cref="WriteRam"/> stays the one dirty site. A snapshot still captures the deposited
    /// bytes because the whole RAM array is serialized. An out-of-range span is dropped whole.</summary>
    /// <param name="offset">The absolute byte offset into save RAM (independent of the current bank selection).</param>
    /// <param name="source">The bytes to deposit.</param>
    protected void DepositExternalRam(int offset, ReadOnlySpan<byte> source) {
        if ((offset < 0) || ((long)offset + source.Length > m_ram.Length)) {
            return;
        }

        source.CopyTo(destination: m_ram.AsSpan(start: offset));
    }

    /// <summary>Computes the wrap mask for a bank select whose decoded chip mirrors on a power-of-two bank count: the
    /// mask is <c>bankCount - 1</c> when the count is a power of two greater than one, otherwise zero (every select
    /// resolves to bank zero, the single-bank/absent-chip wiring). Mappers whose bank arithmetic can go out of range —
    /// or negative, like the MMM01's boot mapping of the image's last two banks — wrap through this mask.</summary>
    /// <param name="byteCount">The chip's total size in bytes.</param>
    /// <param name="bankSize">The size of one bank in bytes.</param>
    /// <returns>The wrap mask.</returns>
    protected static int ComputeBankWrapMask(int byteCount, int bankSize) {
        var bankCount = (byteCount / bankSize);

        return ((bankCount > 1) && BitOperations.IsPow2(value: (uint)bankCount)) ? (bankCount - 1) : 0;
    }

    /// <summary>Maps a ROM-region address to an absolute byte offset into the ROM image (mirrored on read).</summary>
    /// <param name="address">An address in <c>[0x0000, 0x7FFF]</c>.</param>
    /// <returns>The absolute ROM offset.</returns>
    protected abstract int MapRomOffset(ushort address);
    /// <summary>Maps a RAM-window address to an absolute byte offset into the save RAM.</summary>
    /// <param name="address">An address in <c>[0xA000, 0xBFFF]</c>.</param>
    /// <returns>The absolute RAM offset; a value outside the populated size reads open-bus and drops writes.</returns>
    protected abstract int MapRamOffset(ushort address);
    /// <summary>Writes the mapper's register state to a snapshot.</summary>
    /// <param name="writer">The snapshot sink.</param>
    protected abstract void SaveRegisters(StateWriter writer);
    /// <summary>Reads the mapper's register state back from a snapshot.</summary>
    /// <param name="reader">The snapshot source.</param>
    protected abstract void LoadRegisters(StateReader reader);
}
