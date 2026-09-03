using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSocialKey(WireWriter writer, WorldSocialImpressionKey key) {
        WorldWireLeaves.WriteEntityAddress(writer, key.Observer);
        WorldWireLeaves.WriteEntityAddress(writer, key.Subject);
        writer.WriteInt32(key.Dimension);
    }
    private static WorldSocialImpressionKey ReadSocialKey(ref WireReader reader) => new(
        WorldWireLeaves.ReadEntityAddress(ref reader), WorldWireLeaves.ReadEntityAddress(ref reader), reader.ReadInt32());

    internal static void WriteSocialMemory(WireWriter writer, WorldSocialMemoryCheckpoint state) {
        writer.WriteString(state.PolicyIdentity);
        writer.WriteUInt64(state.EngineTick); writer.WriteInt32(state.EvidenceAttempts); writer.WriteInt32(state.ReclaimedReceipts); writer.WriteUInt64(state.NextOrdinal);
        WriteArray(writer, state.Impressions, static (w, row) => {
            WriteSocialKey(w, row.Key); w.WriteInt64(row.Value); w.WriteInt64(row.Weight); w.WriteInt64(row.Uncertainty);
            WriteUInt128(w, unchecked((UInt128)row.UpdatedAt)); w.WriteUInt64(row.IndependentEvents); w.WriteUInt64(row.FirstReceiptOrdinal);
        });
        WriteArray(writer, state.Receipts, static (w, row) => {
            WriteSocialKey(w, row.Impression); WorldWireLeaves.WriteEntityAddress(w, row.Event.Origin);
            w.WriteString(row.Event.Aspect); w.WriteUInt64(row.Event.Sequence); w.WriteUInt64(row.OccurredAt);
            WriteUInt128(w, unchecked((UInt128)row.LocalOccurredAt)); w.WriteUInt64(row.Ordinal);
            w.WriteInt64(row.Value); w.WriteInt64(row.Weight); w.WriteBoolean(row.Direct); w.WriteBoolean(row.ConflictSeen);
            WriteOptional(w, row.OriginalSource, WorldWireLeaves.WriteEntityAddress); w.WriteInt64(row.OriginalValue);
        });
        WriteArray(writer, state.ImportReservations ?? [], static (w, row) => {
            w.WriteString(row.Key.SourceAuthority); w.WriteUInt64(row.Key.TransferId);
            WriteArray(w, row.Members, static (nested, member) => {
                WorldWireLeaves.WriteEntityAddress(nested, member.Observer);
                nested.WriteInt32(member.Impressions); nested.WriteInt32(member.Receipts);
            });
        });
        WriteArray(writer, state.FrozenObservers ?? [], static (w, row) => {
            WorldWireLeaves.WriteEntityAddress(w, row.Observer); w.WriteString(row.Transfer.SourceAuthority);
            w.WriteUInt64(row.Transfer.TransferId); w.WriteUInt64(row.FrozenAt);
        });
    }

    internal static WorldSocialMemoryCheckpoint ReadSocialMemory(ref WireReader reader) {
        var identity = reader.ReadString("social policy", MaxSectionBytes);
        var clock = reader.ReadUInt64(); var attempts = reader.ReadInt32(); var reclaimed = reader.ReadInt32(); var ordinal = reader.ReadUInt64();
        var impressions = ReadSocialArray(ref reader, "social impressions", 84, static (ref WireReader r) => new WorldSocialImpressionCheckpoint(
            ReadSocialKey(ref r), r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), unchecked((Int128)ReadUInt128(ref r)), r.ReadUInt64(), r.ReadUInt64()));
        var receipts = ReadSocialArray(ref reader, "social receipts", 111, static (ref WireReader r) => new WorldSocialReceiptCheckpoint(
            ReadSocialKey(ref r), new(WorldWireLeaves.ReadEntityAddress(ref r), r.ReadString("social aspect", 256), r.ReadUInt64()),
            r.ReadUInt64(), unchecked((Int128)ReadUInt128(ref r)), r.ReadUInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadBoolean(), r.ReadBoolean(),
            ReadOptional(ref r, static (ref WireReader nested) => WorldWireLeaves.ReadEntityAddress(ref nested)), r.ReadInt64()));
        var reservations = ReadSocialReservations(ref reader);
        var frozen = ReadSocialArray(ref reader, "social frozen observers", 32, static (ref WireReader r) =>
            new WorldSocialFrozenObserverCheckpoint(WorldWireLeaves.ReadEntityAddress(ref r),
                new(r.ReadString("social freeze authority", 2048), r.ReadUInt64()), r.ReadUInt64()), WorldSocialMemory.MaximumFrozenObservers);
        return new(identity, clock, attempts, reclaimed, ordinal, impressions, receipts, reservations, frozen);
    }

    private static WorldSocialImportReservationCheckpoint[] ReadSocialReservations(ref WireReader reader) {
        var count = reader.ReadCount("social import reservations", 0, WorldSocialMemory.MaximumReservedObservers);
        if (reader.Failed) { return []; }
        if (count > reader.Remaining / 16) {
            reader.Fail(WireRefusal.PayloadMalformed, "social import reservation count cannot fit its remaining bytes"); return [];
        }
        var rows = new WorldSocialImportReservationCheckpoint[count];
        var remaining = WorldSocialMemory.MaximumReservedObservers;
        for (var index = 0; index < count && !reader.Failed; index++) {
            var key = new WorldTransferKey(reader.ReadString("social import authority", 2048), reader.ReadUInt64());
            var members = ReadSocialArray(ref reader, "social import observers", 20, static (ref WireReader r) =>
                new WorldSocialImportAllowance(WorldWireLeaves.ReadEntityAddress(ref r), r.ReadInt32(), r.ReadInt32()), remaining);
            remaining -= members.Length;
            rows[index] = new(key, members);
        }
        return rows;
    }

    // Reject impossible row counts before allocating. Minimum sizes omit variable UTF-8 string contents.
    private static T[] ReadSocialArray<T>(ref WireReader reader, string field, int minimumBytes, ReadItem<T> read,
        int maximum = CompiledWorldSocialPolicy.MaximumEntries) {
        var count = reader.ReadCount(field: field, minimum: 0, maximum: maximum);
        if (reader.Failed) { return []; }
        if (count > reader.Remaining / minimumBytes) {
            reader.Fail(WireRefusal.PayloadMalformed, $"{field} count cannot fit its remaining bytes");
            return [];
        }
        var rows = new T[count];
        for (var index = 0; index < count && !reader.Failed; index++) { rows[index] = read(ref reader); }
        return rows;
    }
}
