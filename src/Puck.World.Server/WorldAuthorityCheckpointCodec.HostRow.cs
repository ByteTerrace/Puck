using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeHostRow(WorldAuthorityHostRowCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteUInt64(value: section.ScheduleAccumulatorTicks);
        writer.WriteUInt64(value: section.ElapsedEngineTicks);
        writer.WriteBoolean(value: section.IsPaused);
        WriteArray(
            writer: writer,
            items: section.PortalOccupancy,
            writeItem: static (w, row) => {
                w.WriteString(value: row.PlacementId);
                w.WriteString(value: row.FaceName);
                w.WriteInt32(value: row.Seat);
            }
        );
        writer.WriteUInt64(value: section.NextTransferId);
        WriteArray(
            writer: writer,
            items: section.InDoubtTransfers,
            writeItem: WriteInDoubtTransfer
        );
        WriteArray(
            writer: writer,
            items: section.ForwardedBodies,
            writeItem: WriteForwardedBody
        );
        WriteArray(
            writer: writer,
            items: section.AppliedTransferIds,
            writeItem: static (w, v) => w.WriteUInt64(value: v)
        );
        WriteOptional(
            writer: writer,
            value: section.AppliedTransferHighWater,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteInt32(value: section.FreshCounter);
        writer.WriteBoolean(value: section.Retained);
        WriteArray(
            writer: writer,
            items: section.AnnouncedCrossingHolds,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Seat);
                w.WriteUInt64(value: row.TransferId);
            }
        );
        WriteArray(
            writer: writer,
            items: section.SeededArrivals,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Seat);
                w.WriteString(value: row.Border);
            }
        );

        return writer.ToArray();
    }
    private static bool TryDecodeHostRow(byte[] bytes, WorldPlayerDefaults defaults, out string reason, out WorldAuthorityHostRowCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var scheduleAccumulatorTicks = reader.ReadUInt64();
        var elapsedEngineTicks = reader.ReadUInt64();
        var isPaused = reader.ReadBoolean();
        var portalOccupancy = ReadArray(
            reader: ref reader,
            field: "host row portal occupancy",
            readItem: static (ref WireReader r) => {
                var placementId = r.ReadString(
                    field: "portal occupancy placement id",
                    maxBytes: MaxStringBytes
                );
                var faceName = r.ReadString(
                    field: "portal occupancy face name",
                    maxBytes: MaxStringBytes
                );
                var seat = r.ReadInt32();

                return (placementId, faceName, seat);
            }
        );
        var nextTransferId = reader.ReadUInt64();
        var inDoubtTransfers = ReadArray(
            reader: ref reader,
            field: "host row in-doubt transfers",
            readItem: (ref WireReader r) => ReadInDoubtTransfer(
                defaults: defaults,
                reader: ref r
            )
        );
        var forwardedBodies = ReadArray(
            reader: ref reader,
            field: "host row forwarded bodies",
            readItem: static (ref WireReader r) => ReadForwardedBody(reader: ref r)
        );
        var appliedTransferIds = ReadArray(
            reader: ref reader,
            field: "host row applied transfer ids",
            readItem: static (ref WireReader r) => r.ReadUInt64()
        );
        var appliedTransferHighWater = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var freshCounter = reader.ReadInt32();
        var retained = reader.ReadBoolean();
        var announcedCrossingHolds = ReadArray(
            reader: ref reader,
            field: "host row announced crossing holds",
            readItem: static (ref WireReader r) => {
                var seat = r.ReadInt32();
                var transferId = r.ReadUInt64();

                return (seat, transferId);
            }
        );
        var seededArrivals = ReadArray(
            reader: ref reader,
            field: "host row seeded arrivals",
            readItem: static (ref WireReader r) => {
                var seat = r.ReadInt32();
                var border = r.ReadString(
                    field: "seeded arrival border",
                    maxBytes: MaxStringBytes
                );

                return (seat, border);
            }
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"host row section: {failure}";

            return false;
        }

        section = new WorldAuthorityHostRowCheckpoint(
            AnnouncedCrossingHolds: announcedCrossingHolds,
            AppliedTransferHighWater: appliedTransferHighWater,
            AppliedTransferIds: appliedTransferIds,
            ElapsedEngineTicks: elapsedEngineTicks,
            ForwardedBodies: forwardedBodies,
            FreshCounter: freshCounter,
            InDoubtTransfers: inDoubtTransfers,
            IsPaused: isPaused,
            NextTransferId: nextTransferId,
            PortalOccupancy: portalOccupancy,
            Retained: retained,
            ScheduleAccumulatorTicks: scheduleAccumulatorTicks,
            SeededArrivals: seededArrivals
        );
        reason = string.Empty;

        return true;
    }
}
