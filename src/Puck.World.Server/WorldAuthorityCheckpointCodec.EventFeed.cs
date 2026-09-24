using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteEventEdge(WireWriter writer, WorldEventEdge edge) {
        writer.WriteByte(value: ((byte)edge.Family));
        WriteSubject(
            writer: writer,
            subject: edge.GateA
        );
        writer.WriteOptional(
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

        var gateA = WorldWireCodec.ReadSubject(reader: ref reader);
        var gateB = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireCodec.ReadSubject(reader: ref r)
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
    private static byte[] EncodeEventFeed(WorldEventFeedCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteArray(
            items: section.Edges,
            writeItem: WriteEventEdge
        );
        writer.WriteArray(
            items: section.PendingRoutes,
            writeItem: WriteEventEdge
        );
        WriteBoolArray(
            writer: writer,
            values: section.SeatOccupied
        );
        writer.WriteArray(
            items: section.Overlapping,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.A);
                w.WriteInt32(value: row.B);
            }
        );
        writer.WriteArray(
            items: section.RegionOccupancy,
            writeItem: static (w, row) => {
                w.WriteString(value: row.Region);
                WriteBoolArray(
                    values: row.Occupancy,
                    writer: w
                );
            }
        );
        writer.WriteArray(
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
    private static bool TryDecodeEventFeed(byte[] bytes, out string reason, out WorldEventFeedCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var edges = reader.ReadArray(
            field: "event feed edges",
            readItem: static (ref WireReader r) => ReadEventEdge(reader: ref r),
            maximum: MaxCollectionCount
        );
        var pendingRoutes = reader.ReadArray(
            field: "event feed pending routes",
            readItem: static (ref WireReader r) => ReadEventEdge(reader: ref r),
            maximum: MaxCollectionCount
        );
        var seatOccupied = ReadBoolArray(
            field: "event feed seat occupied",
            maximum: WorldBodiesLimits.LocalSeatCount,
            reader: ref reader
        );
        var overlapping = reader.ReadArray(
            field: "event feed overlapping",
            maximum: WorldEventFeed.MaximumTrackedPairsForCapacity(capacity: WorldBodiesLimits.CapacityCeiling),
            readItem: static (ref WireReader r) => {
                var a = r.ReadInt32();
                var b = r.ReadInt32();

                return (a, b);
            }
        );
        var regionOccupancy = reader.ReadArray(
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
            },
            maximum: MaxCollectionCount
        );

        var links = reader.ReadArray(
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

                return new WorldEventLinkState(
                    Adjacency: adjacency,
                    DeliveredTick: deliveredTick,
                    Dropped: dropped,
                    PendingRefresh: pendingRefresh,
                    StaleTicks: staleTicks
                );
            },
            maximum: MaxCollectionCount
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"event feed section: {failure}";

            return false;
        }

        section = new WorldEventFeedCheckpoint(
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
