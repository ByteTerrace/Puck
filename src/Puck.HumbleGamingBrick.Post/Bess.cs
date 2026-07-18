using System.Buffers.Binary;
using System.Text;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Shared constants and small binary helpers for the BESS ("Best Effort Save State") interchange format
/// (spec: <c>BESS.md</c>; a savestate footer any BESS-compliant emulator can read a
/// portable subset of another's state from). This project writes and reads exactly the block set a flat-RAM SM83
/// core can populate faithfully: <c>NAME</c>, <c>INFO</c>, <c>CORE</c>, an optional <c>MBC </c> block, and the
/// required <c>END </c> block. <c>XOAM</c> is legitimately omitted (this core does not model the extra
/// 0xFEA0-0xFEFF OAM range the spec allows skipping); <c>RTC</c>/<c>HUC3</c>/<c>TPP1</c>/<c>MBC7</c>/<c>SGB</c> are
/// out of scope for this evidence tool's first pass.
/// </summary>
internal static class Bess {
    /// <summary>The length of the required <c>CORE</c> block's defined prefix. BESS spec (CORE block): "The length
    /// of the CORE block is 0xD0 bytes, but implementations are expected to ignore any excess bytes." — a CORE
    /// payload at least this long is legal; only the first <see cref="CoreBlockLength"/> bytes are ever read.</summary>
    public const int CoreBlockLength = 0xD0;
    /// <summary>The only BESS major version this importer accepts. BESS spec (CORE block): "Both major and minor
    /// versions should be 1. Implementations are expected to reject incompatible majors, but still attempt to read
    /// newer minor versions." — so only the major is gated here; the minor is never compared.</summary>
    public const ushort SupportedCoreMajorVersion = 1;
    /// <summary>The number of memory-mapped registers the <c>CORE</c> block embeds (0xFF00-0xFF7F).</summary>
    public const int RegisterPageLength = 0x80;
    /// <summary>The byte offset of the register page within the <c>CORE</c> block.</summary>
    public const int RegisterPageOffset = 0x18;
    /// <summary>The byte offset of the size/offset buffer table within the <c>CORE</c> block.</summary>
    public const int BufferTableOffset = 0x98;
    /// <summary>The footer's fixed trailing length (a 4-byte offset plus the 4-byte <c>"BESS"</c> tag).</summary>
    public const int FooterLength = 8;

    /// <summary>Appends one BESS block (4-byte tag, little-endian 32-bit length, payload).</summary>
    /// <param name="destination">The list to append to.</param>
    /// <param name="tag">The exact 4-character ASCII tag.</param>
    /// <param name="payload">The block payload.</param>
    public static void WriteBlock(List<byte> destination, string tag, ReadOnlySpan<byte> payload) {
        WriteTag(destination: destination, tag: tag);

        var length = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: length, value: (uint)payload.Length);
        destination.AddRange(collection: length);
        destination.AddRange(collection: payload.ToArray());
    }
    /// <summary>Appends the 8-byte trailing footer.</summary>
    /// <param name="destination">The list to append to.</param>
    /// <param name="firstBlockOffset">The absolute file offset of the first BESS block (<c>NAME</c> or <c>CORE</c>).</param>
    public static void WriteFooter(List<byte> destination, uint firstBlockOffset) {
        var offset = new byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: offset, value: firstBlockOffset);
        destination.AddRange(collection: offset);
        WriteTag(destination: destination, tag: "BESS");
    }
    /// <summary>Locates the first BESS block via the trailing footer.</summary>
    /// <param name="file">The whole file's bytes.</param>
    /// <param name="firstBlockOffset">Receives the absolute offset of the first block.</param>
    /// <returns><see langword="true"/> when the footer is present and well-formed.</returns>
    public static bool TryReadFooter(ReadOnlySpan<byte> file, out int firstBlockOffset) {
        firstBlockOffset = 0;

        if (file.Length < FooterLength) {
            return false;
        }

        var footer = file[^FooterLength..];

        if (!footer[4..].SequenceEqual(other: "BESS"u8)) {
            return false;
        }

        firstBlockOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: footer[..4]);

        return ((firstBlockOffset >= 0) && (firstBlockOffset < (file.Length - FooterLength)));
    }
    /// <summary>Reads one block header at a position, bounds-checked against the file so a malformed length or a
    /// truncated file is reported rather than throwing mid-parse — the block-graph half of the "validate the complete
    /// graph before applying anything" contract <see cref="BessImporter"/> relies on.</summary>
    /// <param name="file">The whole file's bytes.</param>
    /// <param name="offset">The block's starting offset.</param>
    /// <param name="end">The offset of the trailing footer; a block may not start, or its payload extend, past this.</param>
    /// <param name="tag">Receives the 4-character tag, or an empty string when the block is malformed.</param>
    /// <param name="payload">Receives the payload span, or an empty span when the block is malformed.</param>
    /// <param name="next">Receives the offset of the byte immediately after this block, or <paramref name="end"/> when
    /// the block is malformed.</param>
    /// <returns><see langword="true"/> when the block's header and payload both fit within <paramref name="end"/>.</returns>
    public static bool TryReadBlock(ReadOnlySpan<byte> file, int offset, int end, out string tag, out ReadOnlySpan<byte> payload, out int next) {
        tag = string.Empty;
        payload = default;
        next = end;

        if ((offset < 0) || ((offset + 8) > end)) {
            return false;
        }

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(source: file.Slice(start: (offset + 4), length: 4));
        var payloadEnd = (offset + 8 + length);

        if ((length < 0) || (payloadEnd < 0) || (payloadEnd > end)) {
            return false;
        }

        tag = Encoding.ASCII.GetString(bytes: file.Slice(start: offset, length: 4));
        payload = file.Slice(start: (offset + 8), length: length);
        next = payloadEnd;

        return true;
    }
    /// <summary>Maps a <see cref="ConsoleModel"/> to the spec's 4-character model identifier.</summary>
    /// <param name="model">The model to encode.</param>
    /// <returns>The 4 ASCII bytes (family, model, revision, padding).</returns>
    public static byte[] ModelTag(ConsoleModel model) =>
        model switch {
            ConsoleModel.Dmg => "GD  "u8.ToArray(),
            ConsoleModel.Cgb => "CCE "u8.ToArray(),
            // The Advance's Dmg/Cgb-compatibility mode: family 'C' (the Cgb/Agb family), model 'A' (the Agb line).
            _ => "CA  "u8.ToArray(),
        };
    private static void WriteTag(List<byte> destination, string tag) {
        var bytes = new byte[4];

        Encoding.ASCII.GetBytes(chars: tag, bytes: bytes);
        destination.AddRange(collection: bytes);
    }
}
