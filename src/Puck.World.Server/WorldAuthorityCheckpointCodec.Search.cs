using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSearchLevel(WireWriter writer, WorldSearchLevelCheckpoint level) {
        writer.WriteInt32(value: level.Shape);
        writer.WriteInt32(value: level.Token);
        writer.WriteInt32(value: level.Target);
        writer.WriteInt64(value: level.Alpha);
        writer.WriteInt64(value: level.Beta);
        writer.WriteInt64(value: level.Best);
        writer.WriteInt32(value: level.BestToken);
        writer.WriteInt32(value: level.BestTarget);
        writer.WriteInt64(value: level.BaseTurn);
        WriteLongArray(
            writer: writer,
            values: level.Values
        );
    }
    private static WorldSearchLevelCheckpoint ReadSearchLevel(ref WireReader reader) {
        var shape = reader.ReadInt32();
        var token = reader.ReadInt32();
        var target = reader.ReadInt32();
        var alpha = reader.ReadInt64();
        var beta = reader.ReadInt64();
        var best = reader.ReadInt64();
        var bestToken = reader.ReadInt32();
        var bestTarget = reader.ReadInt32();
        var baseTurn = reader.ReadInt64();
        var values = ReadLongArray(
            reader: ref reader,
            field: "search level values"
        );

        return new WorldSearchLevelCheckpoint(
            Shape: shape,
            Token: token,
            Target: target,
            Alpha: alpha,
            Beta: beta,
            Best: best,
            BestToken: bestToken,
            BestTarget: bestTarget,
            BaseTurn: baseTurn,
            Values: values
        );
    }
    private static void WriteSearchJob(WireWriter writer, WorldSearchJobCheckpoint job) {
        writer.WriteString(value: job.Name);
        writer.WriteUInt64(value: job.Stamp);
        writer.WriteBoolean(value: job.Running);
        writer.WriteBoolean(value: job.Done);
        writer.WriteInt32(value: job.Shape);
        writer.WriteInt32(value: job.Token);
        writer.WriteInt32(value: job.Target);
        writer.WriteInt64(value: job.Count);
        WriteLongArray(
            writer: writer,
            values: job.Legal
        );
        WriteLongArray(
            writer: writer,
            values: job.Counts
        );
        WriteLongArray(
            writer: writer,
            values: job.Wide
        );
        writer.WriteInt64(value: job.Nodes);
        writer.WriteInt64(value: job.BaseTurn);
        writer.WriteInt32(value: job.PassDepth);
        writer.WriteInt32(value: job.Active);
        writer.WriteInt64(value: job.Best);
        writer.WriteInt32(value: job.BestToken);
        writer.WriteInt32(value: job.BestTarget);
        writer.WriteInt64(value: job.Alpha);
        writer.WriteInt64(value: job.Beta);
        WriteArray(
            writer: writer,
            items: job.Levels,
            writeItem: WriteSearchLevel
        );
    }
    private static WorldSearchJobCheckpoint ReadSearchJob(ref WireReader reader) {
        var name = reader.ReadRequiredString(
            field: "search job name",
            maxBytes: MaxHashChars
        );
        var stamp = reader.ReadUInt64();
        var running = reader.ReadBoolean();
        var done = reader.ReadBoolean();
        var shape = reader.ReadInt32();
        var token = reader.ReadInt32();
        var target = reader.ReadInt32();
        var count = reader.ReadInt64();
        var legal = ReadLongArray(
            reader: ref reader,
            field: "search job legal"
        );
        var counts = ReadLongArray(
            reader: ref reader,
            field: "search job counts"
        );
        var wide = ReadLongArray(
            reader: ref reader,
            field: "search job wide"
        );
        var nodes = reader.ReadInt64();
        var baseTurn = reader.ReadInt64();
        var passDepth = reader.ReadInt32();
        var active = reader.ReadInt32();
        var best = reader.ReadInt64();
        var bestToken = reader.ReadInt32();
        var bestTarget = reader.ReadInt32();
        var alpha = reader.ReadInt64();
        var beta = reader.ReadInt64();
        var levels = ReadArray(
            reader: ref reader,
            field: "search job levels",
            readItem: static (ref WireReader r) => ReadSearchLevel(reader: ref r)
        );

        return new WorldSearchJobCheckpoint(
            Name: name,
            Stamp: stamp,
            Running: running,
            Done: done,
            Shape: shape,
            Token: token,
            Target: target,
            Count: count,
            Legal: legal,
            Counts: counts,
            Wide: wide,
            Nodes: nodes,
            BaseTurn: baseTurn,
            PassDepth: passDepth,
            Active: active,
            Best: best,
            BestToken: bestToken,
            BestTarget: bestTarget,
            Alpha: alpha,
            Beta: beta,
            Levels: levels
        );
    }
    private static byte[] EncodeSearch(WorldSearchCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Jobs,
            writeItem: WriteSearchJob
        );

        return writer.ToArray();
    }
    private static bool TryDecodeSearch(byte[] bytes, out string reason, out WorldSearchCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var jobs = ReadArray(
            reader: ref reader,
            field: "search jobs",
            readItem: static (ref WireReader r) => ReadSearchJob(reader: ref r)
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"search section: {failure}";

            return false;
        }

        section = new WorldSearchCheckpoint(Jobs: jobs);
        reason = string.Empty;

        return true;
    }
}
