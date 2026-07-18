namespace Puck.HumbleGamingBrick;

/// <summary>
/// The link-cable printer peripheral, modeled as a deterministic serial-cable peer — a device that sits where a second
/// machine would on the <see cref="SerialLinkSession"/> seam without being one. It answers the bytes the console shifts
/// out over its INTERNAL clock (<see cref="ISerialPeer.ShiftBit"/>): parse the packet protocol, accumulate DATA bands
/// into an image, and on a PRINT render the assembled band buffer and raise it to the host as a
/// <see cref="GamePrintout"/> event. All of the printer's parsing state, the accumulated image, and the
/// print-in-progress countdown are plain fields captured in a fixed order (<see cref="ISnapshotable"/>), so a snapshot
/// of a printing session round-trips exactly; only the host-side <see cref="PrintEmitted"/> observation seam is not
/// serialized. The image buffer is circular: a run of valid DATA bands beyond its 12.5-band capacity wraps the write
/// cursor rather than overrunning it (see <see cref="UnpackBand"/>).
/// <para>
/// Protocol: each packet is two magic bytes <c>0x88 0x33</c>, a command byte (INIT <c>0x01</c>, PRINT <c>0x02</c>, DATA
/// <c>0x04</c>, NUL/STATUS <c>0x0F</c>), a compression flag, a 16-bit payload length, the payload (RLE-compressible when
/// the flag is set), and a 16-bit additive checksum. The console then shifts two trailing bytes to read the printer's
/// two-byte reply: the device-alive byte <c>0x81</c> followed by the status byte (bit 0 checksum error, bit 1 printing
/// in progress, bit 2 print requested/done, bit 3 unprocessed data present). The reply is pipelined one byte behind
/// reception exactly as on the wire — the byte the printer sends during transfer N is the response it computed at the
/// end of transfer N-1.
/// </para>
/// </summary>
public sealed class GamePrinterDevice : ISerialPeer, ISnapshotable {
    /// <summary>The image width in pixels the printer prints (160, the console's screen width).</summary>
    public const int ImageWidth = 160;
    /// <summary>The maximum accumulated image height in pixels (200); a DATA band adds 16 rows, so the buffer holds up
    /// to 12 full bands before the write cursor wraps (see <see cref="UnpackBand"/>). Sized once at construction, so it
    /// never allocates on the emulation path.</summary>
    public const int MaxImageHeight = 200;
    // A full DATA band is exactly 0x280 (640) decompressed bytes = two 8-pixel tile rows of twenty 2bpp tiles.
    private const int BandByteCount = 0x280;
    // The two magic bytes that frame every printer packet.
    private const byte Magic1 = 0x88;
    private const byte Magic2 = 0x33;
    // The device-alive byte the printer replies once a packet's checksum verifies — the "a printer is connected" signal.
    private const byte AliveByte = 0x81;
    // Command bytes (low nibble of the command byte).
    private const byte CommandInit = 0x01;
    private const byte CommandPrint = 0x02;
    private const byte CommandData = 0x04;
    // Status byte values. Idle=0; checksum-error is bit 0; a stored band raises bit 3; PRINT raises print-requested +
    // printing-in-progress (6); when the countdown elapses the printer reports done (print-requested only, 4).
    private const byte StatusChecksumError = 0x01;
    private const byte StatusPrinting = 0x06;
    private const byte StatusDone = 0x04;
    private const byte StatusDataFull = 0x08;
    // The print-in-progress duration, derived from the deterministic tick clock: master T-cycles per printed pixel row.
    // Roughly one second per 8-pixel row; this integer analogue keeps the busy window a pure function of the image
    // height and elapsed emulated cycles, never wall time. The exact value is presentation timing, not a gate.
    private const ulong CyclesPerPrintedRow = 8_192;

    // The packet parser's position.
    private enum ParseState : byte {
        Magic1,
        Magic2,
        CommandId,
        Compression,
        LengthLow,
        LengthHigh,
        Data,
        ChecksumLow,
        ChecksumHigh,
        Active,
        Status,
    }

    private readonly byte[] m_commandData = new byte[BandByteCount];
    private readonly byte[] m_image = new byte[ImageWidth * MaxImageHeight];
    private ParseState m_state;
    private byte m_bitsReceived;
    private byte m_byteBeingReceived;
    private byte m_commandId;
    private int m_commandLength;
    private bool m_compression;
    private byte m_compressionRunLength;
    private bool m_compressionRunIsCompressed;
    private ushort m_checksum;
    private int m_imageOffset;
    private ushort m_lengthLeft;
    private ulong m_remainingBusyCycles;
    private byte m_sendByte;
    private byte m_status;

    /// <summary>An optional observer invoked once with the rendered <see cref="GamePrintout"/> the instant a PRINT command
    /// completes — the machine-to-host print event. Like <see cref="SerialComponent.TransferCompleted"/> it is a pure
    /// host-side observation seam: it is never serialized, so setting it cannot perturb determinism, and it is
    /// <see langword="null"/> in a headless run.</summary>
    public Action<GamePrintout>? PrintEmitted { get; set; }

    /// <summary>Gets the printer's current status byte — the value a STATUS poll reads back (bit 0 checksum error, bit 1
    /// printing in progress, bit 2 print requested/done, bit 3 unprocessed data). Snapshot state.</summary>
    public byte Status =>
        m_status;

    /// <summary>Gets the number of pixels accumulated into the image since the last INIT/PRINT — a host- and
    /// gate-facing progress read (0 immediately after a print flushes the buffer). Snapshot state.</summary>
    public int ImageOffset =>
        m_imageOffset;

    /// <inheritdoc/>
    bool ISerialPeer.ShiftBit(bool incoming) {
        // Byte-pipelined exchange: shift this transfer's reply bit out (MSB first, from the byte computed last transfer),
        // shift the console's bit in, and on the eighth bit process the received byte and latch the next reply byte.
        var outgoing = ((m_sendByte & 0x80) != 0);

        m_sendByte = (byte)(m_sendByte << 1);
        m_byteBeingReceived = (byte)((m_byteBeingReceived << 1) | (incoming ? 0x01 : 0x00));

        if (++m_bitsReceived == 8) {
            var received = m_byteBeingReceived;

            m_bitsReceived = 0;
            m_byteBeingReceived = 0;

            ProcessByte(received: received);
        }

        return outgoing;
    }

    /// <summary>Advances the print-in-progress countdown by a budget of master T-cycles — the deterministic tick clock a
    /// <see cref="GamePrinterLinkSession"/> hands in as it advances the linked machine. When the countdown elapses a
    /// printing job reports done, so the busy → ready transition a STATUS poll observes is a pure function of emulated
    /// cycles, never wall time.</summary>
    /// <param name="tCycles">The master T-cycles elapsed this budget.</param>
    public void AdvanceBusy(ulong tCycles) {
        if (m_remainingBusyCycles == 0) {
            return;
        }

        if (m_remainingBusyCycles <= tCycles) {
            m_remainingBusyCycles = 0;

            if (m_status == StatusPrinting) {
                m_status = StatusDone;
            }
        } else {
            m_remainingBusyCycles -= tCycles;
        }
    }

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: (byte)m_state);
        writer.WriteByte(value: m_bitsReceived);
        writer.WriteByte(value: m_byteBeingReceived);
        writer.WriteByte(value: m_commandId);
        writer.WriteInt32(value: m_commandLength);
        writer.WriteBoolean(value: m_compression);
        writer.WriteByte(value: m_compressionRunLength);
        writer.WriteBoolean(value: m_compressionRunIsCompressed);
        writer.WriteUInt16(value: m_checksum);
        writer.WriteInt32(value: m_imageOffset);
        writer.WriteUInt16(value: m_lengthLeft);
        writer.WriteUInt64(value: m_remainingBusyCycles);
        writer.WriteByte(value: m_sendByte);
        writer.WriteByte(value: m_status);
        writer.WriteBlock<byte>(values: m_commandData);
        writer.WriteBlock<byte>(values: m_image);
    }

    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_state = (ParseState)reader.ReadByte();
        m_bitsReceived = reader.ReadByte();
        m_byteBeingReceived = reader.ReadByte();
        m_commandId = reader.ReadByte();
        m_commandLength = reader.ReadInt32();
        m_compression = reader.ReadBoolean();
        m_compressionRunLength = reader.ReadByte();
        m_compressionRunIsCompressed = reader.ReadBoolean();
        m_checksum = reader.ReadUInt16();
        m_imageOffset = reader.ReadInt32();
        m_lengthLeft = reader.ReadUInt16();
        m_remainingBusyCycles = reader.ReadUInt64();
        m_sendByte = reader.ReadByte();
        m_status = reader.ReadByte();
        reader.ReadBlock<byte>(destination: m_commandData);
        reader.ReadBlock<byte>(destination: m_image);
    }

    // One received serial byte drives the packet parser. The reply byte defaults to 0 and is overridden by the states
    // that answer (the alive byte after a good checksum, the status byte at the end of a packet); the additive checksum
    // folds in the command..data bytes; the state advances unless it is consuming the payload.
    private void ProcessByte(byte received) {
        m_sendByte = 0;

        switch (m_state) {
            case ParseState.Magic1:
                if (received != Magic1) {
                    return;
                }

                m_status &= unchecked((byte)~StatusChecksumError);
                m_commandLength = 0;
                m_checksum = 0;

                break;
            case ParseState.Magic2:
                if (received != Magic2) {
                    // Re-sync: a second 0x88 keeps us hunting for the 0x33; anything else restarts the magic scan.
                    if (received != Magic1) {
                        m_state = ParseState.Magic1;
                    }

                    return;
                }

                break;
            case ParseState.CommandId:
                m_commandId = (byte)(received & 0x0F);

                break;
            case ParseState.Compression:
                m_compression = ((received & 0x01) != 0);

                break;
            case ParseState.LengthLow:
                m_lengthLeft = received;

                break;
            case ParseState.LengthHigh:
                m_lengthLeft |= (ushort)((received & 0x03) << 8);

                break;
            case ParseState.Data:
                AppendDataByte(received: received);
                --m_lengthLeft;

                break;
            case ParseState.ChecksumLow:
                m_checksum ^= received;

                break;
            case ParseState.ChecksumHigh:
                m_checksum ^= (ushort)(received << 8);

                // The console's transmitted checksum equals the printer's additive sum on a clean packet, so the two
                // XORs cancel to zero; any non-zero residue is a corrupted packet — raise the error bit and re-sync.
                if (m_checksum != 0) {
                    m_status |= StatusChecksumError;
                    m_state = ParseState.Magic1;

                    return;
                }

                m_sendByte = AliveByte;

                break;
            case ParseState.Active:
                // The alive byte has been sent; this transfer's reply is the status byte (INIT is special-cased to 0, the
                // value games expect). A printing job that has run out its countdown flips to done as it is polled.
                if (m_commandId == CommandInit) {
                    m_sendByte = 0;
                } else {
                    if ((m_status == StatusPrinting) && (m_remainingBusyCycles == 0)) {
                        m_status = StatusDone;
                    }

                    m_sendByte = m_status;
                }

                break;
            case ParseState.Status:
                m_state = ParseState.Magic1;
                HandleCommand();

                return;
            default:
                break;
        }

        // The additive checksum covers the command through the payload — the parser states from CommandId up to (but
        // excluding) ChecksumLow.
        if ((m_state >= ParseState.CommandId) && (m_state < ParseState.ChecksumLow)) {
            m_checksum += received;
        }

        // The Data state lingers until the whole payload is consumed; every other state advances by one.
        if (m_state != ParseState.Data) {
            ++m_state;
        }

        if ((m_state == ParseState.Data) && (m_lengthLeft == 0)) {
            ++m_state;
        }
    }

    // Stores one payload byte into the command buffer, decompressing an RLE run when the packet's compression flag is
    // set: a control byte with bit 7 set introduces a compressed run of (len&0x7F)+2 copies of the next byte; bit 7
    // clear introduces (len&0x7F)+1 raw bytes. The buffer never overruns a full band.
    private void AppendDataByte(byte received) {
        if (m_commandLength == BandByteCount) {
            return;
        }

        if (!m_compression) {
            m_commandData[m_commandLength++] = received;

            return;
        }

        if (m_compressionRunLength == 0) {
            m_compressionRunIsCompressed = ((received & 0x80) != 0);
            m_compressionRunLength = (byte)((received & 0x7F) + 1 + (m_compressionRunIsCompressed ? 1 : 0));

            return;
        }

        if (m_compressionRunIsCompressed) {
            while (m_compressionRunLength != 0) {
                m_commandData[m_commandLength++] = received;
                --m_compressionRunLength;

                if (m_commandLength == BandByteCount) {
                    m_compressionRunLength = 0;
                }
            }

            return;
        }

        m_commandData[m_commandLength++] = received;
        --m_compressionRunLength;
    }

    // Acts on a completed packet: INIT clears the status and the image, DATA unpacks a full band into the image, PRINT
    // renders the assembled image with the command's palette and raises it to the host, arming the print countdown.
    private void HandleCommand() {
        switch (m_commandId) {
            case CommandInit:
                m_status = 0;
                m_imageOffset = 0;

                break;
            case CommandPrint:
                if (m_commandLength == 4) {
                    EmitPrint();
                }

                break;
            case CommandData:
                if (m_commandLength == BandByteCount) {
                    m_status = StatusDataFull;
                    UnpackBand();
                }

                break;
            default:
                break;
        }
    }

    // Renders the accumulated image with the PRINT command's palette (each 2-bit source dot indexes the palette byte for
    // its 0-3 shade), raises the printout to the host, arms the tick-clock print countdown, and flushes the buffer.
    // UnpackBand keeps m_imageOffset inside the buffer by construction, so this clamp is never live on the emulation
    // path; it stays as an explicit guarantee that a print can never index past the image source regardless of how the
    // cursor got here (e.g. a foreign snapshot).
    private void EmitPrint() {
        m_status = StatusPrinting;

        var margins = m_commandData[1];
        var palette = m_commandData[2];
        var exposure = (byte)(m_commandData[3] & 0x7F);
        var length = Math.Min(m_imageOffset, m_image.Length);
        var pixels = new byte[length];

        for (var index = 0; (index < length); ++index) {
            pixels[index] = (byte)((palette >> (m_image[index] * 2)) & 0x03);
        }

        var height = (length / ImageWidth);

        m_remainingBusyCycles = ((ulong)height * CyclesPerPrintedRow);

        PrintEmitted?.Invoke(obj: new GamePrintout(
            width: ImageWidth,
            height: height,
            topMargin: (byte)(margins >> 4),
            bottomMargin: (byte)(margins & 0x0F),
            palette: palette,
            exposure: exposure,
            pixels: pixels
        ));

        m_imageOffset = 0;
    }

    // Unpacks a full 0x280-byte DATA band (two 8-pixel tile rows of twenty 2bpp tiles) into the image at the running
    // offset, MSB-first per the console's 2bpp tile format (byte pair = low then high bitplane). Reads the command
    // buffer without mutating it.
    //
    // The buffer is circular: the write cursor wraps modulo the buffer length after each 8-row half, mirroring
    // SameBoy's overflow policy ("gb->printer.image_offset %= sizeof(gb->printer.image);", Core/printer.c:49) but
    // applied per half-band rather than once per full band — SameBoy's single top-of-band modulo does not stop a
    // band's second half from writing past the array once the cursor is already within one half-band of the end, so a
    // 13th full band silently overruns SameBoy's own fixed-size buffer too (its documented caveat: "incorrect usage is
    // not correctly emulated"). Wrapping every 8*ImageWidth-byte half instead keeps the cursor (always a multiple of
    // 8*ImageWidth, which divides the buffer length evenly: MaxImageHeight/8 is exact) strictly inside the buffer by
    // construction, so 13+ valid bands overwrite the oldest rows in place rather than fault.
    private void UnpackBand() {
        var position = 0;

        for (var row = 0; (row < 2); ++row) {
            for (var tileX = 0; (tileX < (ImageWidth / 8)); ++tileX) {
                for (var y = 0; (y < 8); ++y) {
                    var low = m_commandData[position];
                    var high = m_commandData[position + 1];

                    position += 2;

                    for (var x = 0; (x < 8); ++x) {
                        var bit = (7 - x);
                        var value = (byte)(((low >> bit) & 0x01) | (((high >> bit) & 0x01) << 1));
                        var pixel = (m_imageOffset + (tileX * 8) + x + (y * ImageWidth));

                        m_image[pixel] = value;
                    }
                }
            }

            m_imageOffset = ((m_imageOffset + (8 * ImageWidth)) % m_image.Length);
        }
    }
}
