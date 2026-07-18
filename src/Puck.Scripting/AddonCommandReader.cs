using System.Buffers.Binary;

using Puck.Commands;

namespace Puck.Scripting;

/// <summary>The desync-proof, fixed-stride command reader: validates and decodes exactly <c>count</c>
/// contiguous 24-byte records into a caller-owned buffer, applying the A.8 decode guards in order. Every
/// guard failure is reported by record index; <see cref="DescribeError"/> renders the specific reason for a
/// diagnostic line.</summary>
public static class AddonCommandReader {
    /// <summary>Validates and decodes <paramref name="count"/> command records from <paramref name="source"/>.</summary>
    /// <param name="source">The packed record bytes (at least <paramref name="count"/> × 24 bytes).</param>
    /// <param name="count">The number of records the guest returned.</param>
    /// <param name="destination">The caller-owned buffer decoded records are written into.</param>
    /// <param name="errorIndex">When this returns <see langword="false"/>, the index of the offending record; otherwise <c>-1</c>.</param>
    /// <returns><see langword="true"/> if every record decoded cleanly; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public static bool TryDecode(ReadOnlySpan<byte> source, int count, AddonCommand[] destination, out int errorIndex) {
        ArgumentNullException.ThrowIfNull(destination);

        errorIndex = -1;

        if ((count < 0) || (count > destination.Length) || ((count * AddonAbi.CommandRecordBytes) > source.Length)) {
            errorIndex = 0;
            return false;
        }

        for (var index = 0; (index < count); ++index) {
            var record = source.Slice(
                length: AddonAbi.CommandRecordBytes,
                start: (index * AddonAbi.CommandRecordBytes)
            );

            if (!TryDecodeRecord(
                command: out var command,
                record: record
            )) {
                errorIndex = index;
                return false;
            }

            destination[index] = command;
        }

        return true;
    }

    /// <summary>Renders the specific reason a single 24-byte record fails the decode guards, in guard order.</summary>
    /// <param name="record">The offending 24-byte record.</param>
    /// <returns>A terse reason such as <c>"padId 42 unknown"</c> or <c>"reserved1 must be zero"</c>.</returns>
    public static string DescribeError(ReadOnlySpan<byte> record) {
        if (record.Length < AddonAbi.CommandRecordBytes) {
            return "record truncated";
        }

        var padId = BinaryPrimitives.ReadUInt16LittleEndian(source: record[AddonAbi.RecordOffsets.PadId..]);

        if (!PadCommandId.IsKnown(padId: padId)) {
            return $"padId {padId} unknown";
        }

        var phaseByte = record[AddonAbi.RecordOffsets.Phase];

        if (!TryMapPhase(
            mapped: out _,
            phase: phaseByte
        )) {
            return $"phase {phaseByte} out of range";
        }

        if (record[AddonAbi.RecordOffsets.Reserved0] != 0) {
            return "reserved0 must be zero";
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source: record[AddonAbi.RecordOffsets.Reserved1..]) != 0u) {
            return "reserved1 must be zero";
        }

        var kind = PadCommandId.KindOf(padId: padId);
        var valueY = BinaryPrimitives.ReadInt64LittleEndian(source: record[AddonAbi.RecordOffsets.ValueY..]);

        if ((kind != AddonValueKind.Axis2D) && (valueY != 0L)) {
            return $"valueY must be zero for {kind}";
        }

        return "malformed";
    }

    private static bool TryDecodeRecord(ReadOnlySpan<byte> record, out AddonCommand command) {
        command = default;

        var padId = BinaryPrimitives.ReadUInt16LittleEndian(source: record[AddonAbi.RecordOffsets.PadId..]);

        if (!PadCommandId.IsKnown(padId: padId)) {
            return false;
        }

        if (!TryMapPhase(
            mapped: out var phase,
            phase: record[AddonAbi.RecordOffsets.Phase]
        )) {
            return false;
        }

        if (record[AddonAbi.RecordOffsets.Reserved0] != 0) {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source: record[AddonAbi.RecordOffsets.Reserved1..]) != 0u) {
            return false;
        }

        var kind = PadCommandId.KindOf(padId: padId);
        var valueX = BinaryPrimitives.ReadInt64LittleEndian(source: record[AddonAbi.RecordOffsets.ValueX..]);
        var valueY = BinaryPrimitives.ReadInt64LittleEndian(source: record[AddonAbi.RecordOffsets.ValueY..]);

        if ((kind != AddonValueKind.Axis2D) && (valueY != 0L)) {
            return false;
        }

        command = new AddonCommand(
            Kind: kind,
            PadId: padId,
            Phase: phase,
            ValueX: valueX,
            ValueY: valueY
        );
        return true;
    }
    private static bool TryMapPhase(byte phase, out CommandPhase mapped) {
        switch (phase) {
            case 0:
                mapped = CommandPhase.Started;
                return true;
            case 1:
                mapped = CommandPhase.Active;
                return true;
            case 2:
                mapped = CommandPhase.Completed;
                return true;
            case 3:
                mapped = CommandPhase.Canceled;
                return true;
            default:
                mapped = default;
                return false;
        }
    }
}
