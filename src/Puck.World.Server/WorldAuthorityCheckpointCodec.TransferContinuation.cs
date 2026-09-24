using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteTransferContinuation(WireWriter writer, WorldTransferContinuationCheckpoint row) {
        writer.WriteArray(
            items: row.CohortSlots,
            writeItem: static (w, slot) => w.WriteInt32(value: slot)
        );
        writer.WriteInt32(value: row.SourceSlot);
        writer.WriteString(value: row.Border);
        writer.WriteNullableString(value: row.AdjacencyCounterpart);
        writer.WriteFixedVector(value: row.SourceCrossingPoint);
        writer.WriteBoolean(value: row.SourceFrame.HasValue);
        if (row.SourceFrame is { } frame) {
            writer.WriteFixedVector(value: frame.Origin);
            writer.WriteFixedVector(value: frame.Right);
            writer.WriteFixedVector(value: frame.Up);
            writer.WriteFixedVector(value: frame.Normal);
            writer.WriteFixed(value: frame.HalfWidth);
            writer.WriteFixed(value: frame.HalfHeight);
            writer.WriteFixed(value: frame.HalfDepth);
        }
        writer.WriteNullableString(value: row.DestinationName);
        writer.WriteNullableString(value: row.ScopeKey);
        writer.WriteBoolean(value: row.GenerationId.HasValue);
        if (row.GenerationId is { } generation) { writer.WriteUInt64(value: generation); }
    }
    private static WorldTransferContinuationCheckpoint ReadTransferContinuation(ref WireReader reader) {
        var slots = reader.ReadArray(
            field: "transfer continuation slots",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var sourceSlot = reader.ReadInt32();
        var border = reader.ReadString(
            field: "transfer continuation border",
            maxBytes: MaxStringBytes
        );
        var counterpart = reader.ReadNullableString(
            field: "transfer continuation counterpart",
            maxBytes: MaxStringBytes
        );
        var point = reader.ReadFixedVector();
        WorldFaceFrame? frame = (reader.ReadBoolean()
            ? new(
                reader.ReadFixedVector(),
                reader.ReadFixedVector(),
                reader.ReadFixedVector(),
                reader.ReadFixedVector(),
                reader.ReadFixed(),
                reader.ReadFixed(),
                reader.ReadFixed()
            )
            : null
        );
        var destination = reader.ReadNullableString(
            field: "transfer continuation destination",
            maxBytes: MaxStringBytes
        );
        var scope = reader.ReadNullableString(
            field: "transfer continuation scope",
            maxBytes: MaxStringBytes
        );
        ulong? generation = (reader.ReadBoolean()
            ? reader.ReadUInt64()
            : null
        );

        return new(
            AdjacencyCounterpart: counterpart,
            Border: border,
            CohortSlots: slots,
            DestinationName: destination,
            GenerationId: generation,
            ScopeKey: scope,
            SourceCrossingPoint: point,
            SourceFrame: frame,
            SourceSlot: sourceSlot
        );
    }
}
