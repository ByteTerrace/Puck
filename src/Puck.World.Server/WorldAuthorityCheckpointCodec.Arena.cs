using System.Runtime.InteropServices;
using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteArena(WireWriter writer, ArenaSnapshot arena) {
        WriteIntArray(writer, arena.Capacities);
        writer.WriteArray(items: arena.Keys, writeItem: WriteArenaKey);
        writer.WriteArray(items: arena.Numbers, writeItem: static (w, n) => { w.WriteByte((byte)n.Column); w.WriteInt32(n.Index); w.WriteInt64(n.Value); });
        writer.WriteArray(items: arena.Text, writeItem: static (w, t) => { w.WriteInt32(t.Index); WriteUndoText(w, t.Value); });
        writer.WriteArray(items: arena.Visibility, writeItem: static (w, v) => { w.WriteInt32(v.Index); WriteVisibility(w, v.Value); });
        writer.WriteArray(items: arena.Observations, writeItem: static (w, o) => { w.WriteInt32(o.Index); WriteObservation(w, o.Value); });
        writer.WriteBlock(value: MemoryMarshal.Cast<sbyte, byte>(arena.Vectors.AsSpan()));
    }

    // The one wire spelling of a stored audience and of an observation, which the arena image and the retained undo
    // journal both carry.
    private static void WriteVisibility(WireWriter writer, StateVisibility visibility) {
        writer.WriteByte(value: ((byte)visibility.Hidden));
        WriteUndoText(writer, visibility.ReadersFrom);
        writer.WriteBoolean(value: (visibility.Readers is not null));
        if (visibility.Readers is { } readers) { writer.WriteArray(items: readers, writeItem: WriteUndoText); }
    }
    private static StateVisibility ReadVisibility(ref WireReader reader) {
        var hidden = ((HiddenCells)reader.ReadByte());

        if (!Enum.IsDefined(value: hidden)) { reader.Fail(detail: "state visibility hidden mode is unknown", refusal: WireRefusal.EnumValueUnknown); }
        var from = ReadUndoText(reader: ref reader);
        var readers = (reader.ReadBoolean() ? reader.ReadArray(field: "state visibility readers", maximum: StateCapacity.MaxVisibilityReaders,
            readItem: static (ref WireReader item) => ReadUndoText(reader: ref item)!) : null);

        return new StateVisibility(Hidden: hidden, Readers: readers, ReadersFrom: from);
    }
    private static void WriteObservation(WireWriter writer, StateObservation observation) {
        writer.WriteInt64(value: observation.Tick);
        writer.WriteBoolean(value: observation.Visible);
    }
    private static StateObservation ReadObservation(ref WireReader reader) {
        var tick = reader.ReadInt64();

        return new StateObservation(Tick: tick, Visible: reader.ReadBoolean());
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
            readItem: static (ref WireReader r) => { var index = r.ReadInt32(); return new ArenaVisibilityImage(Index: index, Value: ReadVisibility(reader: ref r)); });
        var observations = reader.ReadArray(field: "arena observations", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => { var index = r.ReadInt32(); return new ArenaObservationImage(Index: index, Value: ReadObservation(reader: ref r)); });
        var vectors = MemoryMarshal.Cast<byte, sbyte>(reader.ReadBlock(field: "arena vectors", maxBytes: checked((int)ArenaCapacity.MaxBytes))).ToArray();
        return new ArenaSnapshot(Capacities: capacities, Keys: keys, Numbers: numbers, Text: text, Visibility: visibility, Observations: observations, Vectors: vectors);
    }
}
