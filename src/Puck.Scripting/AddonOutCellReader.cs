using System.Buffers.Binary;

namespace Puck.Scripting;

/// <summary>The STRUCTURAL batch decoder for the guest→host output ring: validates and decodes exactly
/// <c>count</c> contiguous 32-byte cells, checking only what the wire shape itself pins — the <c>Kind</c>
/// discriminant and the <c>Channel</c> index bound. Vocabulary (verb ranges, payload domains, <c>Ask</c> rules)
/// is the Simulation adapter's sealed writer, not this core's concern; every other field decodes unconditionally.
/// A malformed cell refuses the whole batch.</summary>
public static class AddonOutCellReader {
    /// <summary>Validates and decodes <paramref name="count"/> output cells from <paramref name="source"/>.</summary>
    /// <param name="source">The packed cell bytes (at least <paramref name="count"/> × <see cref="AddonAbi.OutCellBytes"/> bytes).</param>
    /// <param name="count">The number of cells the guest returned.</param>
    /// <param name="channelCount">The addon's declared channel count, bounding a cell's <c>Channel</c> byte.</param>
    /// <param name="destination">The caller-owned buffer decoded cells are written into.</param>
    /// <param name="errorIndex">When this returns <see langword="false"/>, the offending cell index; otherwise <c>-1</c>.</param>
    /// <param name="error">When this returns <see langword="false"/>, the specific rejection reason; otherwise empty.</param>
    /// <returns><see langword="true"/> if every cell decoded cleanly; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, int count, int channelCount, AddonOutCell[] destination, out int errorIndex, out string error) {
        ArgumentNullException.ThrowIfNull(destination);

        errorIndex = -1;
        error = "";

        if ((count < 0) || (count > destination.Length) || ((count * AddonAbi.OutCellBytes) > source.Length)) {
            errorIndex = 0;
            error = "output batch truncated";
            return false;
        }

        for (var index = 0; (index < count); ++index) {
            var cell = source.Slice(start: (index * AddonAbi.OutCellBytes), length: AddonAbi.OutCellBytes);

            if (!TryDecodeCell(channelCount: channelCount, cell: cell, decoded: out var decoded, error: out var cellError)) {
                errorIndex = index;
                error = $"cell {index}: {cellError}";
                return false;
            }

            destination[index] = decoded;
        }

        return true;
    }

    private static bool TryDecodeCell(ReadOnlySpan<byte> cell, int channelCount, out AddonOutCell decoded, out string error) {
        decoded = default;
        error = "";

        var kindByte = cell[AddonAbi.OutCellOffsets.Kind];
        var kind = (AddonOutCellKind)kindByte;

        if (!Enum.IsDefined(value: kind)) {
            error = $"kind {kindByte} is not defined";
            return false;
        }

        var channel = cell[AddonAbi.OutCellOffsets.Channel];

        if (channel >= channelCount) {
            error = $"channel {channel} out of range [0, {channelCount})";
            return false;
        }

        decoded = new AddonOutCell(
            A: BinaryPrimitives.ReadInt64LittleEndian(source: cell[AddonAbi.OutCellOffsets.A..]),
            B: BinaryPrimitives.ReadInt64LittleEndian(source: cell[AddonAbi.OutCellOffsets.B..]),
            C: BinaryPrimitives.ReadInt64LittleEndian(source: cell[AddonAbi.OutCellOffsets.C..]),
            Channel: channel,
            HandleGeneration: BinaryPrimitives.ReadUInt16LittleEndian(source: cell[AddonAbi.OutCellOffsets.HandleGeneration..]),
            HandleIndex: BinaryPrimitives.ReadUInt16LittleEndian(source: cell[AddonAbi.OutCellOffsets.HandleIndex..]),
            Kind: kind,
            Verb: BinaryPrimitives.ReadUInt16LittleEndian(source: cell[AddonAbi.OutCellOffsets.Verb..])
        );
        return true;
    }
}
