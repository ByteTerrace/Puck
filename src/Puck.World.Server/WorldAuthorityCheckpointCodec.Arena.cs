using System.Runtime.InteropServices;
using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteArena(WireWriter writer, ArenaSnapshot arena) {
        WriteIntArray(writer, arena.Capacities);
        writer.WriteArray(items: arena.Keys, writeItem: WriteArenaKey);
        writer.WriteArray(items: arena.Numbers, writeItem: static (w, n) => { w.WriteByte((byte)n.Column); w.WriteInt32(n.Index); w.WriteInt64(n.Value); });
        writer.WriteArray(items: arena.Text, writeItem: static (w, t) => { w.WriteInt32(t.Index); WriteUndoText(w, t.Value); });
        writer.WriteArray(items: arena.Visibility, writeItem: static (w, v) => {
            w.WriteInt32(v.Index); w.WriteByte((byte)v.Value.Hidden); WriteUndoText(w, v.Value.ReadersFrom);
            w.WriteBoolean(v.Value.Readers is not null);
            if (v.Value.Readers is { } readers) { w.WriteArray(items: readers, writeItem: WriteUndoText); }
        });
        writer.WriteArray(items: arena.Observations, writeItem: static (w, o) => { w.WriteInt32(o.Index); w.WriteInt64(o.Value.Tick); w.WriteBoolean(o.Value.Visible); });
        writer.WriteBlock(value: MemoryMarshal.Cast<sbyte, byte>(arena.Vectors.AsSpan()));
    }

    private static ArenaSnapshot ReadArena(ref WireReader reader) {
        var capacities = ReadIntArray(reader: ref reader, field: "arena capacities");
        var keys = reader.ReadArray(field: "arena keys", maximum: StateCapacity.MaxCellKeys, readItem: ReadArenaKey);
        var uniqueKeys = new HashSet<CellName>();
        foreach (var key in keys) { if (!uniqueKeys.Add(key)) { reader.Fail(detail: $"arena key '{key}' is duplicated", refusal: WireRefusal.PayloadMalformed); } }
        var numbers = reader.ReadArray(field: "arena numbers", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => new ArenaNumberImage(Column: (ArenaColumn)r.ReadByte(), Index: r.ReadInt32(), Value: r.ReadInt64()));
        var text = reader.ReadArray(field: "arena text", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => new ArenaTextImage(Index: r.ReadInt32(), Value: ReadUndoText(reader: ref r)!));
        var visibility = reader.ReadArray(field: "arena visibility", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => {
                var index = r.ReadInt32(); var hidden = (HiddenCells)r.ReadByte(); var from = ReadUndoText(reader: ref r);
                var readers = r.ReadBoolean() ? r.ReadArray(field: "arena readers", maximum: StateCapacity.MaxVisibilityReaders,
                    readItem: static (ref WireReader v) => ReadUndoText(reader: ref v)!) : null;
                return new ArenaVisibilityImage(Index: index, Value: new StateVisibility(Hidden: hidden, Readers: readers, ReadersFrom: from));
            });
        var observations = reader.ReadArray(field: "arena observations", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => new ArenaObservationImage(Index: r.ReadInt32(), Value: new StateObservation(Tick: r.ReadInt64(), Visible: r.ReadBoolean())));
        var vectors = MemoryMarshal.Cast<byte, sbyte>(reader.ReadBlock(field: "arena vectors", maxBytes: checked((int)ArenaCapacity.MaxBytes))).ToArray();
        return new ArenaSnapshot(Capacities: capacities, Keys: keys, Numbers: numbers, Text: text, Visibility: visibility, Observations: observations, Vectors: vectors);
    }
}
