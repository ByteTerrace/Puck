using Puck.Networking;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    internal static WorldScopedReplayCheckpoint CopyScopedReplay(WorldScopedReplayCheckpoint scope) {
        var writer = new WireWriter();

        WriteScopedReplay(scope: scope, writer: writer);
        var reader = new WireReader(bytes: writer.WrittenSpan);
        var copy = ReadScopedReplay(reader: ref reader);

        if (!reader.TryFinish(failure: out var failure)) { throw new InvalidOperationException(message: failure.ToString()); }
        return copy;
    }
    // Computed only when a starting image is captured or admitted. The runtime owns the copied image,
    // so its immutable digest covers the actual stored continuation without serializing it every tick.
    internal static ulong HashScopedReplayStart(WorldScopedReplayCheckpoint scope) {
        var writer = new WireWriter();

        writer.WriteOptionalClass(value: scope.Start, writeValue: WriteScopeStart);
        writer.WriteArray(items: scope.Bodies, writeItem: WritePopulationEntry);
        WriteScopeRules(scope: scope, writer: writer);
        return Fnv1aHash.Compute(values: writer.WrittenSpan);
    }

    private static void WriteScopeRules(WireWriter writer, WorldScopedReplayCheckpoint scope) {
        writer.WriteArray(items: scope.RuleGateHeld, writeItem: WriteRuleLatchEntry);
        writer.WriteArray(items: scope.InteractionGateHeld, writeItem: WriteRuleLatchEntry);
        writer.WriteArray(items: scope.RuleGroups, writeItem: static (w, group) => {
            w.WriteString(value: group.Group); w.WriteInt32(value: group.Step); w.WriteBoolean(value: group.Running); w.WriteBoolean(value: group.Breached);
        });
    }
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
        WriteScopeRules(scope: scope, writer: writer);
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
        var ruleLatch = reader.ReadArray(field: "scope rule latches", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => ReadRuleLatchEntry(field: "scope rule latch", reader: ref r));
        var interactionLatch = reader.ReadArray(field: "scope interaction latches", maximum: MaxCollectionCount, readItem: static (ref WireReader r) => ReadRuleLatchEntry(field: "scope interaction latch", reader: ref r));
        var groups = reader.ReadArray(field: "scope rule groups", maximum: MaxCollectionCount, readItem: static (ref WireReader r) =>
            new WorldRuleGroupEntry(r.ReadString(field: "scope rule group", maxBytes: MaxStringBytes), r.ReadInt32(), r.ReadBoolean(), r.ReadBoolean()));
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

        return new(Active: active, Bodies: bodies, Commands: commands, Cursor: cursor, Diverged: diverged, Hashes: hashes, InputCursor: inputCursor, Inputs: inputs, Intents: intents, InteractionGateHeld: interactionLatch, Name: name, Recording: recording, RuleGateHeld: ruleLatch,
            RuleGroups: groups, Start: start, StartHash: startHash);
    }
}
