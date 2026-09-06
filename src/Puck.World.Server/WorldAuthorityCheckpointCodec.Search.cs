using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSearchJob(WireWriter writer, WorldSearchJobCheckpoint job) {
        writer.WriteString(value: job.Name);
        writer.WriteUInt64(value: job.Stamp);
        writer.WriteBoolean(value: job.Running);
        writer.WriteBoolean(value: job.Done);
        writer.WriteInt32(value: job.Token);
        writer.WriteInt32(value: job.Target);
        writer.WriteInt64(value: job.Count);
        WriteLongArray(
            writer: writer,
            values: job.Legal
        );
        writer.WriteInt64(value: job.Nodes);
        writer.WriteInt64(value: job.BaseTurn);
    }
    private static WorldSearchJobCheckpoint ReadSearchJob(ref WireReader reader) {
        var name = reader.ReadRequiredString(
            field: "search job name",
            maxBytes: MaxHashChars
        );
        var stamp = reader.ReadUInt64();
        var running = reader.ReadBoolean();
        var done = reader.ReadBoolean();
        var token = reader.ReadInt32();
        var target = reader.ReadInt32();
        var count = reader.ReadInt64();
        var legal = ReadLongArray(
            reader: ref reader,
            field: "search job legal"
        );
        var nodes = reader.ReadInt64();
        var baseTurn = reader.ReadInt64();

        return new WorldSearchJobCheckpoint(
            Name: name,
            Stamp: stamp,
            Running: running,
            Done: done,
            Token: token,
            Target: target,
            Count: count,
            Legal: legal,
            Nodes: nodes,
            BaseTurn: baseTurn
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
