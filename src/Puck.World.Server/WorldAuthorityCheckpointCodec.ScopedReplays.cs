using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteScopeStart(WireWriter writer, ArenaScopeSnapshot image) {
        WriteIntArray(writer, image.Rows);
        WriteIntArray(writer, image.CellStarts);
        WriteIntArray(writer, image.MaskWordStarts);
        WriteArena(writer, new ArenaSnapshot(image.Capacities, [], image.Numbers, image.Text, image.Visibility, image.Observations, []));
    }
    private static ArenaScopeSnapshot ReadScopeStart(ref WireReader reader) {
        var rows = ReadIntArray(field: "scope rows", reader: ref reader);
        var cellStarts = ReadIntArray(field: "scope cell starts", reader: ref reader);
        var maskWordStarts = ReadIntArray(field: "scope mask starts", reader: ref reader);
        var arena = ReadArena(reader: ref reader);

        return new(rows, arena.Capacities, cellStarts, maskWordStarts, arena.Numbers, arena.Text, arena.Visibility, arena.Observations);
    }
    private static void WriteScopedReplay(WireWriter writer, WorldScopedReplayCheckpoint scope) {
        writer.WriteString(value: scope.Name);
        writer.WriteOptionalClass(value: scope.Start, writeValue: WriteScopeStart);
        writer.WriteArray(items: scope.Bodies, writeItem: WritePopulationEntry);
        writer.WriteUInt64(value: scope.StartHash);
        writer.WriteArray(items: scope.Inputs, writeItem: static (w, input) => {
            w.WriteInt32(value: input.Tick);
            w.WriteBlock(value: EncodeLeafBlock<WorldMutation>(tryEncode: WorldSubmissionCodec.TryEncodeCommittedMutation, value: input.Mutation, what: "scope input"));
        });
        writer.WriteArray(items: scope.Hashes, writeItem: static (w, hash) => w.WriteUInt64(value: hash));
        writer.WriteInt32(value: scope.Cursor); writer.WriteInt32(value: scope.InputCursor);
        writer.WriteBoolean(value: scope.Recording); writer.WriteBoolean(value: scope.Active); writer.WriteBoolean(value: scope.Diverged);
        writer.WriteArray(items: scope.Commands, writeItem: static (w, input) => {
            w.WriteInt32(value: input.Tick);
            w.WriteBlock(value: EncodeLeafBlock<WorldCommand>(tryEncode: WorldSubmissionCodec.TryEncodeCommand, value: input.Command, what: "scope command"));
        });
        writer.WriteArray(items: scope.Intents, writeItem: static (w, input) => { w.WriteInt32(value: input.Tick); WriteIntentSubmission(submission: input.Intent, writer: w); });
    }
    private static WorldScopedReplayCheckpoint ReadScopedReplay(ref WireReader reader) {
        var name = reader.ReadString(field: "scope name", maxBytes: MaxStringBytes);
        var start = reader.ReadOptionalClass(readValue: ReadScopeStart);
        var bodies = reader.ReadArray(field: "scope bodies", maximum: MaxCollectionCount, readItem: ReadPopulationEntry);
        var startHash = reader.ReadUInt64();
        var inputs = reader.ReadArray(field: "scope inputs", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => {
            var tick = r.ReadInt32();
            var mutation = ReadLeafBlock<WorldMutation>(field: "scope input", reader: ref r, tryDecode: WorldSubmissionCodec.TryDecodeCommittedMutation);

            return (tick, mutation!);
        });
        var hashes = reader.ReadArray(field: "scope hashes", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => r.ReadUInt64());
        var cursor = reader.ReadInt32(); var inputCursor = reader.ReadInt32();
        var recording = reader.ReadBoolean(); var active = reader.ReadBoolean(); var diverged = reader.ReadBoolean();
        var commands = reader.ReadArray(field: "scope commands", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => {
            var tick = r.ReadInt32();
            var command = ReadLeafBlock<WorldCommand>(field: "scope command", reader: ref r, tryDecode: WorldSubmissionCodec.TryDecodeCommand);

            return (tick, command!);
        });
        var intents = reader.ReadArray(field: "scope intents", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => {
            var tick = r.ReadInt32(); return (tick, WorldWireCodec.ReadIntentSubmission(reader: ref r));
        });

        return new(Active: active, Bodies: bodies, Commands: commands, Cursor: cursor, Diverged: diverged, Hashes: hashes, InputCursor: inputCursor, Inputs: inputs, Intents: intents, Name: name, Recording: recording, Start: start, StartHash: startHash);
    }
}
