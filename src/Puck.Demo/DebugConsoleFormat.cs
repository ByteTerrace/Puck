using System.Globalization;
using System.Text;

namespace Puck.Demo;

/// <summary>
/// Shared formatting for the machine-debug console verbs (<c>hgb.*</c> cabinets and the <c>agb.*</c> scene): a canonical
/// hex dump and a forgiving byte-token parser, so the SM83 and ARM sides echo peek/poke in one identical shape rather
/// than forking two ad-hoc formats.
/// </summary>
internal static class DebugConsoleFormat {
    /// <summary>Renders a classic <c>address: bytes | ascii</c> hex dump of <paramref name="length"/> bytes read from
    /// <paramref name="baseAddress"/> through the side-effect-free <paramref name="read"/>, 16 bytes per line.</summary>
    /// <param name="label">The verb label the first line is bracketed with (e.g. <c>hgb.peek</c>).</param>
    /// <param name="baseAddress">The first address dumped.</param>
    /// <param name="length">The byte count.</param>
    /// <param name="read">A side-effect-free byte reader.</param>
    /// <param name="addressDigits">The hex width of the address column (4 for an SM83 bus, 8 for an ARM bus).</param>
    /// <returns>The multi-line dump.</returns>
    public static string HexDump(string label, uint baseAddress, int length, Func<uint, byte> read, int addressDigits = 4) {
        ArgumentNullException.ThrowIfNull(read);

        var builder = new StringBuilder();
        var addressFormat = $"X{addressDigits.ToString(provider: CultureInfo.InvariantCulture)}";

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[{label} 0x{baseAddress.ToString(format: addressFormat, provider: CultureInfo.InvariantCulture)} +{length}]");

        for (var row = 0; (row < length); row += 16) {
            var hex = new StringBuilder();
            var ascii = new StringBuilder();
            var rowCount = Math.Min(val1: 16, val2: (length - row));

            for (var column = 0; (column < rowCount); ++column) {
                var value = read(baseAddress + (uint)(row + column));

                hex.Append(provider: CultureInfo.InvariantCulture, handler: $"{value:X2} ");
                ascii.Append(value: (((value >= 0x20) && (value < 0x7F)) ? (char)value : '.'));
            }

            builder.Append(value: '\n');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  {(baseAddress + (uint)row).ToString(format: addressFormat, provider: CultureInfo.InvariantCulture)}: {hex.ToString().PadRight(totalWidth: 48)} {ascii}");
        }

        return builder.ToString();
    }

    /// <summary>Parses poke byte tokens: either one byte per token (<c>DE AD BE EF</c>, each 0x-optional hex 0..0xFF) or a
    /// single contiguous even-length hex string (<c>DEADBEEF</c>). Returns false on any malformed or out-of-range token.</summary>
    /// <param name="tokens">The byte tokens after the address.</param>
    /// <param name="bytes">The parsed bytes on success.</param>
    /// <returns>Whether every token parsed.</returns>
    public static bool TryParseBytes(string[] tokens, out byte[] bytes) {
        ArgumentNullException.ThrowIfNull(tokens);

        bytes = [];

        if (tokens.Length == 0) {
            return false;
        }

        // A single contiguous hex string (an even number of nibbles beyond one byte) is split into byte pairs.
        if (tokens.Length == 1) {
            var single = Strip(token: tokens[0]);

            if ((single.Length > 2) && ((single.Length % 2) == 0)) {
                var packed = new byte[(single.Length / 2)];

                for (var index = 0; (index < packed.Length); ++index) {
                    if (!byte.TryParse(s: single.AsSpan(start: (index * 2), length: 2), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out packed[index])) {
                        return false;
                    }
                }

                bytes = packed;

                return true;
            }
        }

        var parsed = new byte[tokens.Length];

        for (var index = 0; (index < tokens.Length); ++index) {
            if (!byte.TryParse(s: Strip(token: tokens[index]), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out parsed[index])) {
                return false;
            }
        }

        bytes = parsed;

        return true;
    }

    private static string Strip(string token) =>
        (token.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) ? token[2..] : token);
}
