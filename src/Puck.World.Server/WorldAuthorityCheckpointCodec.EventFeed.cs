using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteEventEdge(WireWriter writer, WorldEventEdge edge) {
        writer.WriteByte(value: ((byte)edge.Family));
        WriteSubject(
            writer: writer,
            subject: edge.GateA
        );
        WriteOptional(
            writer: writer,
            value: edge.GateB,
            writeValue: WriteSubject
        );
        writer.WriteInt64(value: edge.A);
        writer.WriteInt64(value: edge.B);
    }
    private static WorldEventEdge ReadEventEdge(ref WireReader reader) {
        var family = ((WorldEventFamily)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: family)
        ) {
            reader.Fail(
                detail: $"{nameof(WorldEventFamily)} wire value {((byte)family)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var gateA = ReadSubject(reader: ref reader);
        var gateB = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadSubject(reader: ref r)
        );
        var a = reader.ReadInt64();
        var b = reader.ReadInt64();

        return new WorldEventEdge(
            A: a,
            B: b,
            Family: family,
            GateA: gateA,
            GateB: gateB
        );
    }
    private static byte[] EncodeEventFeed(WorldEventFeed.WorldEventFeedCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Edges,
            writeItem: WriteEventEdge
        );
        WriteArray(
            writer: writer,
            items: section.PendingRoutes,
            writeItem: WriteEventEdge
        );
        WriteBoolArray(
            writer: writer,
            values: section.SeatOccupied
        );
        WriteArray(
            writer: writer,
            items: section.Overlapping,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.A);
                w.WriteInt32(value: row.B);
            }
        );
        WriteArray(
            writer: writer,
            items: section.RegionOccupancy,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Region);
                WriteBoolArray(
                    values: row.Occupancy,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.Links,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Adjacency);
                w.WriteUInt64(value: row.DeliveredTick);
                w.WriteInt64(value: row.StaleTicks);
                w.WriteBoolean(value: row.PendingRefresh);
                w.WriteBoolean(value: row.Dropped);
            }
        );

        return writer.ToArray();
    }
    private static bool TryDecodeEventFeed(byte[] bytes, out string reason, out WorldEventFeed.WorldEventFeedCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var edges = ReadArray(
            reader: ref reader,
            field: "event feed edges",
            readItem: static (ref WireReader r) => ReadEventEdge(reader: ref r)
        );
        var pendingRoutes = ReadArray(
            reader: ref reader,
            field: "event feed pending routes",
            readItem: static (ref WireReader r) => ReadEventEdge(reader: ref r)
        );
        var seatOccupied = ReadBoolArray(
            field: "event feed seat occupied",
            maximum: WorldBodiesLimits.LocalSeatCount,
            reader: ref reader
        );
        var overlapping = ReadArray(
            reader: ref reader,
            field: "event feed overlapping",
            maximum: WorldEventFeed.MaximumTrackedPairsForCapacity(WorldBodiesLimits.CapacityCeiling),
            readItem: static (ref WireReader r) => {
                var a = r.ReadInt32();
                var b = r.ReadInt32();

                return (a, b);
            }
        );
        var regionOccupancy = ReadArray(
            reader: ref reader,
            field: "event feed region occupancy",
            readItem: static (ref WireReader r) => {
                var region = r.ReadString(
                    field: "event feed region name",
                    maxBytes: MaxStringBytes
                );
                var occupancy = ReadBoolArray(
                    field: "event feed region occupancy cells",
                    maximum: WorldBodiesLimits.CapacityCeiling,
                    reader: ref r
                );

                return (region, occupancy);
            }
        );

        var links = ReadArray(
            reader: ref reader,
            field: "event feed links",
            readItem: static (ref WireReader r) => {
                var adjacency = r.ReadString(
                    field: "event feed link adjacency name",
                    maxBytes: MaxStringBytes
                );
                var deliveredTick = r.ReadUInt64();
                var staleTicks = r.ReadInt64();
                var pendingRefresh = r.ReadBoolean();
                var dropped = r.ReadBoolean();

                return new WorldEventFeed.WorldEventLinkState(
                    Adjacency: adjacency,
                    DeliveredTick: deliveredTick,
                    Dropped: dropped,
                    PendingRefresh: pendingRefresh,
                    StaleTicks: staleTicks
                );
            }
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"event feed section: {failure}";

            return false;
        }

        section = new WorldEventFeed.WorldEventFeedCheckpoint(
            Edges: edges,
            Links: links,
            Overlapping: overlapping,
            PendingRoutes: pendingRoutes,
            RegionOccupancy: regionOccupancy,
            SeatOccupied: seatOccupied
        );
        reason = string.Empty;

        return true;
    }
    // ---- owned worlds section ----

}
