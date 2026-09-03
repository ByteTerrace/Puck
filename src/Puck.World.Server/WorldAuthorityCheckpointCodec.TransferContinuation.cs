using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteTransferContinuation(WireWriter writer, WorldTransferContinuationCheckpoint row) {
        WriteArray(writer, row.CohortSlots, static (w, slot) => w.WriteInt32(slot));
        writer.WriteInt32(row.SourceSlot);
        writer.WriteString(row.Border);
        writer.WriteNullableString(row.AdjacencyCounterpart);
        writer.WriteFixedVector(row.SourceCrossingPoint);
        writer.WriteBoolean(row.SourceFrame.HasValue);
        if (row.SourceFrame is { } frame) {
            writer.WriteFixedVector(frame.Origin);
            writer.WriteFixedVector(frame.Right);
            writer.WriteFixedVector(frame.Up);
            writer.WriteFixedVector(frame.Normal);
            writer.WriteFixed(frame.HalfWidth);
            writer.WriteFixed(frame.HalfHeight);
            writer.WriteFixed(frame.HalfDepth);
        }
        writer.WriteNullableString(row.DestinationName);
        writer.WriteNullableString(row.ScopeKey);
        writer.WriteBoolean(row.GenerationId.HasValue);
        if (row.GenerationId is { } generation) { writer.WriteUInt64(generation); }
    }

    private static WorldTransferContinuationCheckpoint ReadTransferContinuation(ref WireReader reader) {
        var slots = ReadArray(ref reader, "transfer continuation slots", static (ref WireReader r) => r.ReadInt32(), WorldBodiesLimits.CapacityCeiling);
        var sourceSlot = reader.ReadInt32();
        var border = reader.ReadString("transfer continuation border", MaxStringBytes);
        var counterpart = reader.ReadNullableString("transfer continuation counterpart", MaxStringBytes);
        var point = reader.ReadFixedVector();
        WorldFaceFrame? frame = reader.ReadBoolean()
            ? new(reader.ReadFixedVector(), reader.ReadFixedVector(), reader.ReadFixedVector(), reader.ReadFixedVector(),
                reader.ReadFixed(), reader.ReadFixed(), reader.ReadFixed())
            : null;
        var destination = reader.ReadNullableString("transfer continuation destination", MaxStringBytes);
        var scope = reader.ReadNullableString("transfer continuation scope", MaxStringBytes);
        ulong? generation = reader.ReadBoolean() ? reader.ReadUInt64() : null;
        return new(slots, sourceSlot, border, counterpart, point, frame, destination, scope, generation);
    }
}
