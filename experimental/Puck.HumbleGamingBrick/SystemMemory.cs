using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The console's internal RAM: video RAM, work RAM, object attribute memory, and high RAM, together with the CGB bank
/// selects that page the switchable halves. It owns the raw bytes and the two bank indices and exposes only typed
/// region accessors, so the bus routes an address here without arithmetic leaking out. It is snapshot state, not a
/// timeline component — its bytes are part of every snapshot and fork.
/// </summary>
public sealed class SystemMemory : ISnapshotable {
    private const int HighRamSize = 0x7F;
    private const int ObjectAttributeMemorySize = 0xA0;
    private const int VideoRamBankSize = 0x2000;
    private const int WorkRamBankSize = 0x1000;

    private readonly byte[] m_highRam;
    private readonly byte[] m_objectAttributeMemory;
    private readonly byte[] m_videoRam;
    private readonly byte[] m_workRam;

    private int m_videoRamBank;
    private int m_workRamBank;

    /// <summary>Creates zero-initialized internal RAM with the bank selects at their reset values (VRAM bank 0, work
    /// RAM bank 1).</summary>
    public SystemMemory() {
        m_highRam = new byte[HighRamSize];
        m_objectAttributeMemory = new byte[ObjectAttributeMemorySize];
        m_videoRam = new byte[2 * VideoRamBankSize];
        m_workRam = new byte[8 * WorkRamBankSize];
        m_videoRamBank = 0;
        m_workRamBank = 1;
    }

    /// <summary>Gets or sets the selected video RAM bank (0 or 1); only bit 0 is significant.</summary>
    public int VideoRamBank {
        get => m_videoRamBank;
        set => m_videoRamBank = (value & 0x01);
    }
    /// <summary>Gets or sets the selected work RAM bank for the switchable half (1–7); a written 0 selects bank 1.</summary>
    public int WorkRamBank {
        get => m_workRamBank;
        set => m_workRamBank = Math.Max(val1: 1, val2: (value & 0x07));
    }

    /// <summary>Reads a byte of video RAM at an absolute address in the VRAM region, honoring the selected bank.</summary>
    /// <param name="address">An address in <c>[0x8000, 0x9FFF]</c>.</param>
    /// <returns>The byte at that address in the current VRAM bank.</returns>
    public byte ReadVideoRam(ushort address) =>
        m_videoRam[(m_videoRamBank * VideoRamBankSize) + (address - MemoryMap.VideoRamStart)];
    /// <summary>Reads a byte of video RAM from an explicit bank, independent of the CPU-selected bank. The PPU's fetcher
    /// uses this to read tile maps and tile data from bank 0 and CGB map attributes and banked tiles from bank 1 in the
    /// same step, regardless of which bank the CPU has paged in.</summary>
    /// <param name="bank">The VRAM bank (0 or 1); only bit 0 is significant.</param>
    /// <param name="address">An address in <c>[0x8000, 0x9FFF]</c>.</param>
    /// <returns>The byte at that address in the requested bank.</returns>
    public byte ReadVideoRamBank(int bank, ushort address) =>
        m_videoRam[((bank & 0x01) * VideoRamBankSize) + (address - MemoryMap.VideoRamStart)];
    /// <summary>Writes a byte of video RAM at an absolute address in the VRAM region, honoring the selected bank.</summary>
    /// <param name="address">An address in <c>[0x8000, 0x9FFF]</c>.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteVideoRam(ushort address, byte value) =>
        m_videoRam[(m_videoRamBank * VideoRamBankSize) + (address - MemoryMap.VideoRamStart)] = value;
    /// <summary>Reads a byte of work RAM (or its echo), mapping the fixed and switchable halves to their banks.</summary>
    /// <param name="address">An address in <c>[0xC000, 0xDFFF]</c> (the caller folds echo addresses into this range).</param>
    /// <returns>The byte at that address.</returns>
    public byte ReadWorkRam(ushort address) =>
        m_workRam[WorkRamOffset(address: address)];
    /// <summary>Writes a byte of work RAM (or its echo), mapping the fixed and switchable halves to their banks.</summary>
    /// <param name="address">An address in <c>[0xC000, 0xDFFF]</c>.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteWorkRam(ushort address, byte value) =>
        m_workRam[WorkRamOffset(address: address)] = value;
    /// <summary>Reads a byte of object attribute memory.</summary>
    /// <param name="address">An address in <c>[0xFE00, 0xFE9F]</c>.</param>
    /// <returns>The byte at that address.</returns>
    public byte ReadObjectAttributeMemory(ushort address) =>
        m_objectAttributeMemory[address - MemoryMap.ObjectAttributeMemoryStart];
    /// <summary>Writes a byte of object attribute memory.</summary>
    /// <param name="address">An address in <c>[0xFE00, 0xFE9F]</c>.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteObjectAttributeMemory(ushort address, byte value) =>
        m_objectAttributeMemory[address - MemoryMap.ObjectAttributeMemoryStart] = value;
    /// <summary>Reads a byte of high RAM.</summary>
    /// <param name="address">An address in <c>[0xFF80, 0xFFFE]</c>.</param>
    /// <returns>The byte at that address.</returns>
    public byte ReadHighRam(ushort address) =>
        m_highRam[address - MemoryMap.HighRamStart];
    /// <summary>Writes a byte of high RAM.</summary>
    /// <param name="address">An address in <c>[0xFF80, 0xFFFE]</c>.</param>
    /// <param name="value">The byte to store.</param>
    public void WriteHighRam(ushort address, byte value) =>
        m_highRam[address - MemoryMap.HighRamStart] = value;
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteInt32(value: m_videoRamBank);
        writer.WriteInt32(value: m_workRamBank);
        writer.WriteBytes(value: m_videoRam);
        writer.WriteBytes(value: m_workRam);
        writer.WriteBytes(value: m_objectAttributeMemory);
        writer.WriteBytes(value: m_highRam);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_videoRamBank = reader.ReadInt32();
        m_workRamBank = reader.ReadInt32();
        reader.ReadBytes(destination: m_videoRam);
        reader.ReadBytes(destination: m_workRam);
        reader.ReadBytes(destination: m_objectAttributeMemory);
        reader.ReadBytes(destination: m_highRam);
    }

    private int WorkRamOffset(ushort address) {
        var local = (address & 0x1FFF);

        // The fixed half (0xC000–0xCFFF) is always bank 0; the switchable half (0xD000–0xDFFF) follows the select.
        return (local < WorkRamBankSize)
            ? local
            : ((m_workRamBank * WorkRamBankSize) + (local - WorkRamBankSize));
    }
}
