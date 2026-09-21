using System.Runtime.InteropServices;
using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteUndoHistory(WireWriter writer, ArenaUndoSnapshot history) => WriteArray(writer, history.Groups, static (w, group) => {
        w.WriteString(group.Name);
        w.WriteInt32(group.Depth);
        WriteIntArray(w, group.Rows);
        WriteArray(w, group.Segments, WriteUndoSegment);
        WriteOptionalClass(w, group.Pending, WriteUndoSegment);
    });
    private static ArenaUndoSnapshot ReadUndoHistory(ref WireReader reader) => new(ReadArray(ref reader, "undo groups", static (ref WireReader r) => {
        var name = r.ReadString(field: "undo group", maxBytes: MaxStringBytes);
        var depth = r.ReadCount(field: "undo depth", maximum: (ArenaCapacity.MaxJournalBytes / ArenaJournal.EntryBytes), minimum: 1);
        var rows = ReadIntArray(field: "undo rows", reader: ref r);
        var segments = ReadArray(ref r, "undo turns", ReadUndoSegment, maximum: depth);
        var pending = ReadOptionalClass(readValue: ReadUndoSegment, reader: ref r);

        return new ArenaUndoGroupSnapshot(name, depth, rows, segments, pending);
    }));
    private static void WriteUndoSegment(WireWriter writer, ArenaUndoSegmentSnapshot segment) {
        writer.WriteBoolean(segment.Rewindable);
        WriteArray(writer, segment.Entries, static (w, entry) => {
            w.WriteByte(((byte)entry.Column));
            w.WriteInt32(entry.Index);
            w.WriteInt64(entry.Number);
            WriteUndoText(w, entry.Text);
            WriteOptionalClass(w, entry.Visibility, static (v, visibility) => {
                v.WriteByte(((byte)visibility.Hidden));
                WriteUndoText(v, visibility.ReadersFrom);
                v.WriteBoolean((visibility.Readers is not null));
                if (visibility.Readers is { } readers) { WriteArray(v, readers, WriteUndoText); }
            });
            WriteOptionalClass(w, entry.Observation, static (v, observation) => {
                v.WriteInt64(observation.Tick);
                v.WriteBoolean(observation.Visible);
            });
            WriteUndoText(w, entry.MemberKey);
            w.WriteBoolean((entry.Components is not null));
            if (entry.Components is { } components) { w.WriteBlock(MemoryMarshal.Cast<sbyte, byte>(components)); }
        });
    }
    private static ArenaUndoSegmentSnapshot ReadUndoSegment(ref WireReader reader) {
        var rewindable = reader.ReadBoolean();
        var entries = ReadArray(ref reader, "undo entries", static (ref WireReader r) => {
            var column = ((ArenaColumn)r.ReadByte());

            if (!Enum.IsDefined(value: column)) { r.Fail(detail: "undo column is unknown", refusal: WireRefusal.EnumValueUnknown); }
            var index = r.ReadInt32();
            var number = r.ReadInt64();
            var text = ReadUndoText(reader: ref r);
            var visibility = ReadOptionalClass(ref r, static (ref WireReader v) => {
                var hidden = ((HiddenCells)v.ReadByte());

                if (!Enum.IsDefined(value: hidden)) { v.Fail(detail: "undo visibility hidden mode is unknown", refusal: WireRefusal.EnumValueUnknown); }
                var from = ReadUndoText(reader: ref v);
                var readers = (v.ReadBoolean() ? ReadArray(ref v, "undo readers", static (ref WireReader item) => ReadUndoText(reader: ref item)!, maximum: StateCapacity.MaxVisibilityReaders) : null);

                return new StateVisibility(Hidden: hidden, Readers: readers, ReadersFrom: from);
            });
            var observation = ReadOptionalClass(ref r, static (ref WireReader v) => new StateObservation(Tick: v.ReadInt64(), Visible: v.ReadBoolean()));
            var key = ReadUndoText(reader: ref r);
            var components = (r.ReadBoolean() ? MemoryMarshal.Cast<byte, sbyte>(span: r.ReadBlock(field: "undo vector", maxBytes: ArenaCapacity.MaxJournalBytes)).ToArray() : null);

            return new ArenaUndoEntrySnapshot(column, index, number, text, visibility, observation, key, components);
        }, maximum: (ArenaCapacity.MaxJournalBytes / ArenaJournal.EntryBytes));

        return new ArenaUndoSegmentSnapshot(rewindable, entries);
    }
    // State text is a sequence of UTF-16 code units, including isolated surrogates. UTF-8 replacement would change
    // a retained value and its hash, so this uses explicit little-endian code units on every host.
    private static void WriteUndoText(WireWriter writer, string? text) {
        writer.WriteBoolean(value: (text is not null));
        if (text is null) { return; }
        var bytes = new byte[checked((text.Length * 2))];

        for (var index = 0; (index < text.Length); index++) {
            bytes[(index * 2)] = unchecked((byte)text[index]);
            bytes[((index * 2) + 1)] = unchecked((byte)(text[index] >> 8));
        }
        writer.WriteBlock(value: bytes);
    }
    private static string? ReadUndoText(ref WireReader reader) {
        if (!reader.ReadBoolean()) { return null; }
        var bytes = reader.ReadBlock(field: "undo text", maxBytes: ArenaCapacity.MaxJournalBytes);

        if ((bytes.Length & 1) != 0) { reader.Fail(detail: "undo text has an incomplete UTF-16 code unit", refusal: WireRefusal.PayloadMalformed); }
        if (reader.Failed) { return string.Empty; }
        var characters = new char[(bytes.Length / 2)];

        for (var index = 0; (index < characters.Length); index++) { characters[index] = ((char)(bytes[(index * 2)] | (bytes[((index * 2) + 1)] << 8))); }
        return new string(value: characters);
    }
}
