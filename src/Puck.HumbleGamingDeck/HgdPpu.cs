namespace Puck.HumbleGamingDeck;

/// <summary>
/// The RP2C02G picture processing unit: one dot per call, 341 dots on each of 262 lines, with the pre-render line one dot
/// shorter on odd frames while rendering is enabled. It owns the eight CPU-visible registers and their internal scroll
/// latches (<c>v</c>, <c>t</c>, fine X and the write toggle), the read buffer and I/O latch, palette RAM, primary and
/// secondary object memory, and the background and sprite pipelines, and it writes one nine-bit pixel code per visible
/// dot: six palette bits and the three emphasis bits. Every memory access below $3F00 goes through the cartridge board,
/// which also observes each address the PPU drives.
/// <para>
/// The register timing is dot-exact but not yet sub-dot exact: a CPU access lands between two dots, the second write to
/// $2006 reaches <c>v</c> at once, and the I/O latch holds its value without decaying. Those refinements belong to the NTSC
/// accuracy work that pins them against the PPU test suites.
/// </para>
/// </summary>
public sealed partial class HgdPpu : ISnapshotable {
    /// <summary>The visible picture's width in pixels.</summary>
    public const int Width = 256;
    /// <summary>The visible picture's height in lines.</summary>
    public const int Height = 240;

    private const int DotsPerLine = 341;
    private const int PreRenderLine = 261;
    private const int VblankLine = 241;

    private readonly IHgdMapper m_mapper;
    private readonly HgdNametableRam m_nametables;

    private readonly byte[] m_palette = new byte[32];
    private readonly byte[] m_oam = new byte[256];
    private readonly byte[] m_secondaryOam = new byte[32];
    private readonly ushort[] m_frame = new ushort[(Width * Height)];
    private readonly ushort[] m_completedFrame = new ushort[(Width * Height)];

    private int m_dot;
    private int m_line;
    private bool m_oddFrame;
    private long m_frameIndex;
    private ulong m_masterTick;

    private bool m_warmingUp = true;

    private byte m_control;
    private byte m_mask;
    private bool m_vblank;
    private bool m_spriteZeroHit;
    private bool m_spriteOverflow;
    private bool m_suppressVblank;
    private byte m_oamAddress;
    private ushort m_v;
    private ushort m_t;
    private byte m_fineX;
    private bool m_writeToggle;
    private byte m_readBuffer;
    private byte m_ioLatch;

    /// <summary>Initializes a new instance of the <see cref="HgdPpu"/> class at power-on: line 0, dot 0, an even frame,
    /// every register clear, and register writes that set scroll and rendering ignored until the first pre-render
    /// line.</summary>
    /// <param name="mapper">The cartridge board every PPU memory access below $3F00 reaches.</param>
    /// <param name="nametables">The console's nametable RAM, whose pages the board selects.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mapper"/> or <paramref name="nametables"/> is
    /// <see langword="null"/>.</exception>
    public HgdPpu(IHgdMapper mapper, HgdNametableRam nametables) {
        ArgumentNullException.ThrowIfNull(argument: mapper);
        ArgumentNullException.ThrowIfNull(argument: nametables);

        m_mapper = mapper;
        m_nametables = nametables;
    }

    /// <summary>Gets the current dot within the line, from 0 through 340.</summary>
    public int Dot => m_dot;
    /// <summary>Gets the current line, from 0 through 261; 261 is the pre-render line.</summary>
    public int Line => m_line;
    /// <summary>Gets the number of frames whose vertical blank has begun since power-on.</summary>
    public long FrameIndex => m_frameIndex;
    /// <summary>Gets the PPU's NMI output: the vertical-blank flag while $2000 bit 7 enables it.</summary>
    public bool NmiOutput => (m_vblank && ((m_control & 0x80) != 0));
    /// <summary>Gets the 256 bytes of primary object memory, 64 four-byte sprite entries, for inspection.</summary>
    public ReadOnlySpan<byte> ObjectMemory => m_oam;
    /// <summary>Gets the last completed picture: <see cref="Width"/> by <see cref="Height"/> row-major pixel codes, each
    /// six palette bits with the three emphasis bits of $2001 above them, copied as vertical blank begins so a reader never
    /// sees a frame the PPU is still drawing.</summary>
    public ReadOnlySpan<ushort> Frame => m_completedFrame;

    private bool RenderingEnabled => ((m_mask & 0x18) != 0);
    private bool RenderingLine => ((m_line < Height) || (m_line == PreRenderLine));

    /// <summary>Reads a CPU-visible register with every side effect: $2002 clears the vertical-blank flag and the write
    /// toggle, $2004 reads object memory, $2007 reads through the read buffer and increments <c>v</c>.</summary>
    /// <param name="register">The register index, the CPU address's low three bits.</param>
    /// <returns>The byte the PPU drives; bits it does not drive come from its I/O latch.</returns>
    public byte ReadRegister(int register) {
        switch (register & 7) {
            case 2: {
                    // A read in the dot before the flag sets returns it clear and keeps it from setting this frame.
                    // https://www.nesdev.org/wiki/PPU_frame_timing#VBL_Flag_Timing
                    if ((m_line == VblankLine) && (m_dot == 1)) {
                        m_suppressVblank = true;
                    }

                    var status = ((byte)((m_vblank ? 0x80 : 0) | (m_spriteZeroHit ? 0x40 : 0) | (m_spriteOverflow ? 0x20 : 0)));

                    m_ioLatch = ((byte)(status | (m_ioLatch & 0x1F)));
                    m_vblank = false;
                    m_writeToggle = false;

                    return m_ioLatch;
                }
            case 4:
                m_ioLatch = ReadObjectMemory();

                return m_ioLatch;
            case 7: {
                    var address = ((ushort)(m_v & 0x3FFF));

                    if (address >= 0x3F00) {
                        m_ioLatch = ((byte)((ReadPalette(address: address) & PaletteMask) | (m_ioLatch & 0xC0)));
                        m_readBuffer = ReadMemory(address: ((ushort)(address - 0x1000)));
                    } else {
                        m_ioLatch = m_readBuffer;
                        m_readBuffer = ReadMemory(address: address);
                    }
                    IncrementAfterDataAccess();

                    return m_ioLatch;
                }
            default:
                return m_ioLatch;
        }
    }
    /// <summary>Returns what a register read would drive, changing nothing.</summary>
    /// <param name="register">The register index, the CPU address's low three bits.</param>
    /// <returns>The byte a read would drive now; $2007 reports the read buffer below the palette.</returns>
    public byte PeekRegister(int register) =>
        (register & 7) switch {
            2 => ((byte)((m_vblank ? 0x80 : 0) | (m_spriteZeroHit ? 0x40 : 0) | (m_spriteOverflow ? 0x20 : 0) | (m_ioLatch & 0x1F))),
            4 => m_oam[m_oamAddress],
            7 => (((m_v & 0x3FFF) >= 0x3F00)
                ? ((byte)((ReadPalette(address: ((ushort)(m_v & 0x3FFF))) & PaletteMask) | (m_ioLatch & 0xC0)))
                : m_readBuffer),
            _ => m_ioLatch,
        };
    /// <summary>Writes a CPU-visible register. Until the first pre-render line after power-on the PPU ignores writes to
    /// $2000, $2001, $2005 and $2006; every write still loads the I/O latch.</summary>
    /// <param name="register">The register index, the CPU address's low three bits.</param>
    /// <param name="value">The data byte.</param>
    public void WriteRegister(int register, byte value) {
        m_ioLatch = value;
        switch (register & 7) {
            case 0:
                if (!m_warmingUp) {
                    m_control = value;
                    m_t = ((ushort)((m_t & 0x73FF) | ((value & 3) << 10)));
                }

                break;
            case 1:
                if (!m_warmingUp) {
                    m_mask = value;
                }

                break;
            case 3:
                m_oamAddress = value;

                break;
            case 4:
                WriteObjectMemory(value: value);

                break;
            case 5:
                if (!m_warmingUp) {
                    if (!m_writeToggle) {
                        m_t = ((ushort)((m_t & 0x7FE0) | (value >> 3)));
                        m_fineX = ((byte)(value & 7));
                    } else {
                        m_t = ((ushort)((m_t & 0x0C1F) | ((value & 7) << 12) | ((value & 0xF8) << 2)));
                    }
                    m_writeToggle = !m_writeToggle;
                }

                break;
            case 6:
                if (!m_warmingUp) {
                    if (!m_writeToggle) {
                        m_t = ((ushort)((m_t & 0x00FF) | ((value & 0x3F) << 8)));
                    } else {
                        m_t = ((ushort)((m_t & 0x7F00) | value));
                        m_v = m_t;
                        m_mapper.ObservePpuAddress(
                            address: ((ushort)(m_v & 0x3FFF)),
                            masterTick: m_masterTick
                        );
                    }
                    m_writeToggle = !m_writeToggle;
                }

                break;
            case 7: {
                    var address = ((ushort)(m_v & 0x3FFF));

                    if (address >= 0x3F00) {
                        m_palette[PaletteIndex(address: address)] = ((byte)(value & 0x3F));
                    } else {
                        m_mapper.PpuWrite(
                            address: address,
                            nametables: m_nametables,
                            value: value
                        );
                    }
                    IncrementAfterDataAccess();

                    break;
                }
        }
    }
    /// <summary>Writes one byte of object memory at $2003's address and increments it, the transfer an OAM DMA put cycle
    /// makes through $2004.</summary>
    /// <param name="value">The data byte.</param>
    public void WriteObjectMemoryPort(byte value) =>
        WriteRegister(
            register: 4,
            value: value
        );
    /// <summary>Advances one dot.</summary>
    /// <param name="masterTick">The master tick the dot ends on, forwarded with every address the board observes.</param>
    public void StepDot(ulong masterTick) {
        m_masterTick = masterTick;
        if (RenderingLine) {
            if (RenderingEnabled) {
                StepRenderingDot();
            }
            if ((m_line < Height) && (m_dot >= 1) && (m_dot <= Width)) {
                EmitPixel(x: (m_dot - 1));
            }
        }
        if (m_dot == 1) {
            if (m_line == VblankLine) {
                if (!m_suppressVblank) {
                    m_vblank = true;
                }
                m_suppressVblank = false;
                m_frame.CopyTo(array: m_completedFrame, index: 0);
                ++m_frameIndex;
            } else if (m_line == PreRenderLine) {
                m_vblank = false;
                m_spriteZeroHit = false;
                m_spriteOverflow = false;
                m_warmingUp = false;
            }
        }
        AdvancePosition();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) =>
        TransferState(transfer: new StateSaveTransfer(writer: writer));
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The reader does not contain a complete PPU state.</exception>
    /// <exception cref="InvalidDataException">The serialized dot, line, or sprite-pipeline position is out of
    /// range.</exception>
    public void LoadState(StateReader reader) {
        TransferState(transfer: new StateLoadTransfer(reader: reader));
        if (
            (((uint)m_dot) >= DotsPerLine) ||
            (((uint)m_line) > PreRenderLine) ||
            (((uint)m_spriteCount) > 8) ||
            (((uint)m_nextSpriteCount) > 8) ||
            (((uint)m_secondaryIndex) > 32) ||
            (((uint)m_copyRemaining) > 3)
        ) {
            throw new InvalidDataException(message: "Snapshot PPU position or sprite pipeline is out of range.");
        }
    }

    private byte PaletteMask => (((m_mask & 1) != 0) ? ((byte)0x30) : ((byte)0x3F));

    private static int PaletteIndex(ushort address) {
        var index = address & 0x1F;

        // $3F10, $3F14, $3F18 and $3F1C are the same cells as $3F00, $3F04, $3F08 and $3F0C.
        return (((index & 0x13) == 0x10) ? index & 0x0F : index);
    }
    private byte ReadPalette(ushort address) =>
        m_palette[PaletteIndex(address: address)];
    private byte ReadMemory(ushort address) {
        m_mapper.ObservePpuAddress(
            address: address,
            masterTick: m_masterTick
        );

        return m_mapper.PpuRead(
            address: address,
            nametables: m_nametables
        );
    }
    // A $2007 access during rendering clocks both scroll counters instead of adding the $2000 increment.
    // https://www.nesdev.org/wiki/PPU_scrolling#$2007_reads_and_writes
    private void IncrementAfterDataAccess() {
        if (RenderingEnabled && RenderingLine) {
            IncrementCoarseX();
            IncrementY();
        } else {
            m_v = ((ushort)((m_v + (((m_control & 4) != 0) ? 32 : 1)) & 0x7FFF));
        }
    }
    private void AdvancePosition() {
        // The pre-render line's last dot is skipped on odd frames while rendering is enabled.
        if ((m_line == PreRenderLine) && (m_dot == 339) && m_oddFrame && RenderingEnabled) {
            m_dot = 340;
        }
        ++m_dot;
        if (m_dot < DotsPerLine) {
            return;
        }

        m_dot = 0;
        BeginLine();
        ++m_line;
        if (m_line > PreRenderLine) {
            m_line = 0;
            m_oddFrame = !m_oddFrame;
        }
    }
    private void TransferState<TTransfer>(TTransfer transfer) where TTransfer : struct, IStateTransfer {
        transfer.Int32(value: ref m_dot);
        transfer.Int32(value: ref m_line);
        transfer.Boolean(value: ref m_oddFrame);
        transfer.Int64(value: ref m_frameIndex);
        transfer.UInt64(value: ref m_masterTick);
        transfer.Boolean(value: ref m_warmingUp);
        transfer.Byte(value: ref m_control);
        transfer.Byte(value: ref m_mask);
        transfer.Boolean(value: ref m_vblank);
        transfer.Boolean(value: ref m_spriteZeroHit);
        transfer.Boolean(value: ref m_spriteOverflow);
        transfer.Boolean(value: ref m_suppressVblank);
        transfer.Byte(value: ref m_oamAddress);
        transfer.UInt16(value: ref m_v);
        transfer.UInt16(value: ref m_t);
        transfer.Byte(value: ref m_fineX);
        transfer.Boolean(value: ref m_writeToggle);
        transfer.Byte(value: ref m_readBuffer);
        transfer.Byte(value: ref m_ioLatch);
        transfer.Block(values: m_palette.AsSpan());
        transfer.Block(values: m_oam.AsSpan());
        transfer.Block(values: m_secondaryOam.AsSpan());
        transfer.Block(values: m_frame.AsSpan());
        transfer.Block(values: m_completedFrame.AsSpan());
        TransferBackgroundState(transfer: transfer);
        TransferSpriteState(transfer: transfer);
    }
}
