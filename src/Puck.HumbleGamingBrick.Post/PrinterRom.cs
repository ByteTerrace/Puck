namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Builds the self-contained ROM the Tier-A printer stage boots to drive the machine's serial printer peripheral through a full print job,
/// plus the reference band data the stage checks the emitted print against. The ROM carries a tiny generic "serial
/// blaster" driver and a table of pre-built printer packets: the driver walks the table and, for each packet, shifts
/// every byte out over the internal serial clock (reading and discarding the printer's replies), with a wide idle delay
/// between packets so a snapshot/restore churn can land on a transfer-idle instant. The packets themselves — INIT, one
/// uncompressed DATA band, one RLE-compressed DATA band encoding the SAME image, PRINT, then a run of STATUS polls — carry
/// correct 16-bit additive checksums computed here, so the SM83 side stays a fixed loop and the protocol lives in data.
/// </summary>
internal static class PrinterRom {
    private const int EntryPoint = 0x0100;
    private const int RomSize = 0x8000;
    private const int PacketStreamBase = 0x0150;
    // A full DATA band is 0x280 (640) decompressed bytes = two 8-pixel tile rows.
    private const int BandByteCount = 0x280;
    // Printer command bytes.
    private const byte CommandInit = 0x01;
    private const byte CommandPrint = 0x02;
    private const byte CommandData = 0x04;
    private const byte CommandNul = 0x0F;

    /// <summary>The number of image pixel rows the print job produces (two 16-row DATA bands).</summary>
    public const int PrintedRowCount = 32;
    /// <summary>The number of STATUS-poll packets the driver sends after PRINT (enough to span the busy → ready window).</summary>
    public const int StatusPollCount = 20;
    /// <summary>The work-RAM address the driver writes its completion marker to when every packet has been sent.</summary>
    public const ushort CompletionMarkerAddress = 0xC000;
    /// <summary>The completion-marker value.</summary>
    public const byte CompletionMarker = 0xA5;

    // The serial-blaster driver at the post-boot entry point 0x0100. Registers: HL = packet-stream cursor, BC = bytes left
    // in the current packet, E = idle-delay counter.
    //   0x0100  31 FE FF   ld   sp, 0xFFFE
    //   0x0103  21 50 01   ld   hl, 0x0150      ; packet stream base
    //   next_packet (0x0106):
    //   0x0106  2A         ld   a, (hl+)        ; packet length low
    //   0x0107  4F         ld   c, a
    //   0x0108  2A         ld   a, (hl+)        ; packet length high
    //   0x0109  47         ld   b, a
    //   0x010A  B1         or   c               ; BC == 0 terminates the table
    //   0x010B  28 19      jr   z, 0x0126       ; -> done
    //   send_loop (0x010D):
    //   0x010D  2A         ld   a, (hl+)        ; next packet byte
    //   0x010E  E0 01      ldh  (0xFF01), a     ; SB = byte to send
    //   0x0110  3E 81      ld   a, 0x81
    //   0x0112  E0 02      ldh  (0xFF02), a     ; SC = start (internal clock)
    //   wait (0x0114):
    //   0x0114  F0 02      ldh  a, (0xFF02)     ; poll the transfer bit
    //   0x0116  CB 7F      bit  7, a
    //   0x0118  20 FA      jr   nz, 0x0114
    //   0x011A  0B         dec  bc
    //   0x011B  78         ld   a, b
    //   0x011C  B1         or   c
    //   0x011D  20 EE      jr   nz, 0x010D      ; more bytes in this packet
    //   0x011F  1E C8      ld   e, 200          ; idle delay: a wide transfer-idle window between packets
    //   delay_loop (0x0121):
    //   0x0121  1D         dec  e
    //   0x0122  20 FD      jr   nz, 0x0121
    //   0x0124  18 E0      jr   0x0106          ; -> next_packet
    //   done (0x0126):
    //   0x0126  3E A5      ld   a, 0xA5
    //   0x0128  EA 00 C0   ld   (0xC000), a     ; completion marker
    //   spin (0x012B):
    //   0x012B  18 FE      jr   0x012B
    private static readonly byte[] Driver = [
        0x31, 0xFE, 0xFF,
        0x21, (byte)(PacketStreamBase & 0xFF), (byte)(PacketStreamBase >> 8),
        0x2A,
        0x4F,
        0x2A,
        0x47,
        0xB1,
        0x28, 0x19,
        0x2A,
        0xE0, 0x01,
        0x3E, 0x81,
        0xE0, 0x02,
        0xF0, 0x02,
        0xCB, 0x7F,
        0x20, 0xFA,
        0x0B,
        0x78,
        0xB1,
        0x20, 0xEE,
        0x1E, 0xC8,
        0x1D,
        0x20, 0xFD,
        0x18, 0xE0,
        0x3E, CompletionMarker,
        0xEA, (byte)(CompletionMarkerAddress & 0xFF), (byte)(CompletionMarkerAddress >> 8),
        0x18, 0xFE,
    ];

    /// <summary>Creates the printer test ROM image.</summary>
    /// <returns>A 32&#160;KiB ROM-only cartridge image whose entry point drives the full print job.</returns>
    public static byte[] Create() {
        var band = BuildBand();
        var stream = new List<byte>();

        // INIT clears the printer and image.
        AppendPacket(stream: stream, command: CommandInit, compressed: false, payload: []);
        // Two DATA bands carrying the IDENTICAL image content — one raw, one RLE-compressed — so the emitted print's two
        // halves must match byte-for-byte, proving the decompressor against the raw path.
        AppendPacket(stream: stream, command: CommandData, compressed: false, payload: band);
        AppendPacket(stream: stream, command: CommandData, compressed: true, payload: RleEncode(data: band));
        // PRINT: one sheet, no margins, identity palette (0xE4 maps each 2-bit dot to itself), mid exposure.
        AppendPacket(stream: stream, command: CommandPrint, compressed: false, payload: [0x01, 0x00, 0xE4, 0x40]);

        // STATUS polls (NUL command) so the driver keeps the link live across the busy → ready transition.
        for (var poll = 0; (poll < StatusPollCount); ++poll) {
            AppendPacket(stream: stream, command: CommandNul, compressed: false, payload: []);
        }

        // The zero terminator the driver stops on.
        stream.Add(item: 0x00);
        stream.Add(item: 0x00);

        // A zero-filled image already carries a valid ROM-only header (type 0x00, 32 KiB, no RAM); the driver sits below
        // the header fields and the packet stream begins past the header (0x0150), so neither disturbs the parsed header.
        var rom = new byte[RomSize];

        Driver.CopyTo(array: rom, index: EntryPoint);
        stream.CopyTo(array: rom, arrayIndex: PacketStreamBase);

        return rom;
    }

    /// <summary>Builds the reference 640-byte band content the ROM prints — the deterministic source the stage decodes to
    /// its expected image (both DATA bands carry it). Mixed on purpose: the first half is 8-byte runs (compressible) and
    /// the second half is a non-repeating ramp (raw), so the RLE-compressed band exercises both run kinds.</summary>
    /// <returns>The 640-byte band.</returns>
    public static byte[] BuildBand() {
        var band = new byte[BandByteCount];

        for (var index = 0; (index < band.Length); ++index) {
            band[index] = ((index < (BandByteCount / 2)) ? (byte)(index >> 3) : (byte)index);
        }

        return band;
    }

    /// <summary>Creates a synthetic ROM (the H-05 overflow probe) that drives <paramref name="bandCount"/> consecutive
    /// raw DATA bands — built from <see cref="BuildOverflowBand"/>, so every band decodes to the same two-value pattern —
    /// into the printer with no intervening PRINT, then a PRINT and a run of STATUS polls. Used to drive the image
    /// buffer's 12/13-band capacity boundary and beyond without a crash, per <c>GamePrinterDevice.UnpackBand</c>'s
    /// wrap policy.</summary>
    /// <param name="bandCount">The number of full DATA bands to send before PRINT.</param>
    /// <returns>A 32&#160;KiB ROM-only cartridge image whose entry point drives the overflow scenario.</returns>
    public static byte[] CreateOverflow(int bandCount) {
        var band = BuildOverflowBand();
        var stream = new List<byte>();

        AppendPacket(stream: stream, command: CommandInit, compressed: false, payload: []);

        for (var index = 0; (index < bandCount); ++index) {
            AppendPacket(stream: stream, command: CommandData, compressed: false, payload: band);
        }

        AppendPacket(stream: stream, command: CommandPrint, compressed: false, payload: [0x01, 0x00, 0xE4, 0x40]);

        for (var poll = 0; (poll < StatusPollCount); ++poll) {
            AppendPacket(stream: stream, command: CommandNul, compressed: false, payload: []);
        }

        stream.Add(item: 0x00);
        stream.Add(item: 0x00);

        var rom = new byte[RomSize];

        Driver.CopyTo(array: rom, index: EntryPoint);
        stream.CopyTo(array: rom, arrayIndex: PacketStreamBase);

        return rom;
    }

    /// <summary>Builds the 640-byte band the overflow probe repeats: the identity palette maps a decoded 2bpp dot to
    /// itself, so setting the first tile-row half's low plane to 0xFF/high plane to 0x00 decodes every dot in that half
    /// to shade 1, and the second half's low/high planes swapped decodes every dot to shade 2. Every overflow band
    /// carries the SAME content, so the printer's circular-buffer wrap is checkable purely from the final image's
    /// segment count and alternating 1/2 parity — an independent model of <c>GamePrinterDevice.UnpackBand</c>'s
    /// policy, not a re-test of the tile decoder (already covered by <see cref="BuildBand"/>'s RLE round-trip check).</summary>
    /// <returns>The 640-byte band.</returns>
    public static byte[] BuildOverflowBand() {
        var band = new byte[BandByteCount];

        for (var index = 0; (index < (BandByteCount / 2)); index += 2) {
            band[index] = 0xFF;
            band[index + 1] = 0x00;
        }

        for (var index = (BandByteCount / 2); (index < BandByteCount); index += 2) {
            band[index] = 0x00;
            band[index + 1] = 0xFF;
        }

        return band;
    }

    // Appends one length-prefixed printer packet (2-byte little-endian on-wire length, then the bytes) to the stream. The
    // on-wire bytes are the two magic bytes, command, compression flag, 16-bit payload length, the payload, the 16-bit
    // additive checksum, and two trailing 0x00 bytes the machine shifts to read the printer's alive+status reply.
    private static void AppendPacket(List<byte> stream, byte command, bool compressed, ReadOnlySpan<byte> payload) {
        var length = payload.Length;
        var checksum = (command + (compressed ? 1 : 0) + (length & 0xFF) + ((length >> 8) & 0xFF));

        foreach (var value in payload) {
            checksum += value;
        }

        checksum &= 0xFFFF;

        var packet = new List<byte>(capacity: (payload.Length + 10)) {
            0x88,
            0x33,
            command,
            (byte)(compressed ? 1 : 0),
            (byte)(length & 0xFF),
            (byte)((length >> 8) & 0xFF),
        };

        foreach (var value in payload) {
            packet.Add(item: value);
        }

        packet.Add(item: (byte)(checksum & 0xFF));
        packet.Add(item: (byte)((checksum >> 8) & 0xFF));
        packet.Add(item: 0x00);
        packet.Add(item: 0x00);

        stream.Add(item: (byte)(packet.Count & 0xFF));
        stream.Add(item: (byte)((packet.Count >> 8) & 0xFF));
        stream.AddRange(collection: packet);
    }

    // Encodes a byte run with the printer RLE the compressed decoder expects: a run of 2..129 identical bytes
    // becomes a control byte 0x80|(run-2) plus the value; otherwise 1..128 literal bytes become a control byte (count-1)
    // plus the literals. Decodes back to the exact input.
    private static byte[] RleEncode(ReadOnlySpan<byte> data) {
        var output = new List<byte>();
        var index = 0;

        while (index < data.Length) {
            var run = 1;

            while (((index + run) < data.Length) && (data[index + run] == data[index]) && (run < 129)) {
                ++run;
            }

            if (run >= 2) {
                output.Add(item: (byte)(0x80 | (run - 2)));
                output.Add(item: data[index]);
                index += run;
            } else {
                var start = index;

                ++index;

                while ((index < data.Length) && ((index - start) < 128) && !(((index + 1) < data.Length) && (data[index + 1] == data[index]))) {
                    ++index;
                }

                var count = (index - start);

                output.Add(item: (byte)((count - 1) & 0x7F));

                for (var offset = 0; (offset < count); ++offset) {
                    output.Add(item: data[start + offset]);
                }
            }
        }

        return [.. output];
    }
}
