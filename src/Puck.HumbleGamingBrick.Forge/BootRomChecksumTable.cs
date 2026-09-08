namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Re-encodes <see cref="BootDivPrediction.ChecksumContributions"/> for the Color boot image. The prediction indexes a
/// 256-entry table by title checksum, which is 512 bytes of a 1792-byte window; most entries carry the same
/// contribution, so the image carries that one value plus a three-byte row per checksum that differs. The rows are
/// derived from the prediction's own table, so there is still one copy of the data.
/// </summary>
internal static class BootRomChecksumTable {
    private const int RowLength = 3;

    /// <summary>Gets the contribution every checksum without a row takes.</summary>
    public static ushort Common =>
        s_common;
    /// <summary>Gets the rows, three bytes each: the title checksum, then the contribution low and high bytes.</summary>
    public static ReadOnlySpan<byte> Rows =>
        s_rows;
    /// <summary>Gets the number of rows, which the emitted scan counts down from.</summary>
    public static int RowCount =>
        (s_rows.Length / RowLength);

    private static readonly ushort s_common = MostCommon(contributions: BootDivPrediction.ChecksumContributions);
    private static readonly byte[] s_rows = BuildRows(contributions: BootDivPrediction.ChecksumContributions);

    private static ushort MostCommon(ReadOnlySpan<ushort> contributions) {
        var counts = new Dictionary<ushort, int>();
        ushort common = 0;
        var best = 0;

        foreach (var contribution in contributions) {
            counts[contribution] = (counts.GetValueOrDefault(key: contribution) + 1);
        }

        // Ties break toward the lower value so the encoding is a function of the table alone.
        foreach (var (value, count) in counts) {
            if (
                (count > best) ||
                ((count == best) && (value < common))
            ) {
                best = count;
                common = value;
            }
        }

        return common;
    }
    private static byte[] BuildRows(ReadOnlySpan<ushort> contributions) {
        var common = MostCommon(contributions: contributions);
        var rows = new List<byte>();

        for (var checksum = 0; (checksum < contributions.Length); ++checksum) {
            var contribution = contributions[checksum];

            if (contribution == common) {
                continue;
            }

            rows.Add(item: ((byte)checksum));
            rows.Add(item: ((byte)(contribution & 0xFF)));
            rows.Add(item: ((byte)(contribution >> 8)));
        }

        if (rows.Count > (byte.MaxValue * RowLength)) {
            throw new InvalidOperationException(message: "The checksum-contribution rows outgrew the single-byte counter the emitted scan runs on.");
        }

        return [.. rows];
    }
}
