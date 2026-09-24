using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeHostRow(WorldAuthorityHostRowCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteUInt64(value: section.ScheduleAccumulatorTicks);
        writer.WriteUInt64(value: section.ElapsedEngineTicks);
        writer.WriteBoolean(value: section.IsPaused);
        writer.WriteArray(
            items: section.PortalOccupancy,
            writeItem: static (w, row) => {
                w.WriteString(value: row.PlacementId);
                w.WriteString(value: row.FaceName);
                w.WriteInt32(value: row.Seat);
            }
        );
        writer.WriteUInt64(value: section.NextTransferId);
        writer.WriteArray(
            items: section.InDoubtTransfers,
            writeItem: WriteInDoubtTransfer
        );
        writer.WriteArray(
            items: section.ForwardedBodies,
            writeItem: WriteForwardedBody
        );
        writer.WriteArray(
            items: section.AppliedTransferIds,
            writeItem: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteOptional(
            value: section.AppliedTransferHighWater,
            writeValue: static (w, v) => w.WriteUInt64(value: v)
        );
        writer.WriteInt32(value: section.FreshCounter);
        writer.WriteBoolean(value: section.Retained);
        writer.WriteArray(
            items: section.AnnouncedCrossingHolds,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Seat);
                w.WriteUInt64(value: row.TransferId);
            }
        );
        writer.WriteArray(
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
        var portalOccupancy = reader.ReadArray(
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
            },
            maximum: MaxCollectionCount
        );
        var nextTransferId = reader.ReadUInt64();
        var inDoubtTransfers = reader.ReadArray(
            field: "host row in-doubt transfers",
            readItem: (ref WireReader r) => ReadInDoubtTransfer(
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var forwardedBodies = reader.ReadArray(
            field: "host row forwarded bodies",
            readItem: static (ref WireReader r) => ReadForwardedBody(reader: ref r),
            maximum: MaxCollectionCount
        );
        var appliedTransferIds = reader.ReadArray(
            field: "host row applied transfer ids",
            readItem: static (ref WireReader r) => r.ReadUInt64(),
            maximum: MaxCollectionCount
        );
        var appliedTransferHighWater = reader.ReadOptional(
            readValue: static (ref WireReader r) => r.ReadUInt64()
        );
        var freshCounter = reader.ReadInt32();
        var retained = reader.ReadBoolean();
        var announcedCrossingHolds = reader.ReadArray(
            field: "host row announced crossing holds",
            readItem: static (ref WireReader r) => {
                var seat = r.ReadInt32();
                var transferId = r.ReadUInt64();

                return (seat, transferId);
            },
            maximum: MaxCollectionCount
        );
        var seededArrivals = reader.ReadArray(
            field: "host row seeded arrivals",
            readItem: static (ref WireReader r) => {
                var seat = r.ReadInt32();
                var border = r.ReadString(
                    field: "seeded arrival border",
                    maxBytes: MaxStringBytes
                );

                return (seat, border);
            },
            maximum: MaxCollectionCount
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
