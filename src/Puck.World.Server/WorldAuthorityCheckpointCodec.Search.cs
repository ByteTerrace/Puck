using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSearchLevel(WireWriter writer, SearchLevelCheckpoint level) {
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
        writer.WriteUInt64(value: level.Key);
        writer.WriteInt64(value: level.AlphaEntry);
    }
    private static SearchLevelCheckpoint ReadSearchLevel(ref WireReader reader) {
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
            field: "search level values",
            reader: ref reader
        );
        var key = reader.ReadUInt64();
        var alphaEntry = reader.ReadInt64();

        return new SearchLevelCheckpoint(
            Alpha: alpha,
            AlphaEntry: alphaEntry,
            BaseTurn: baseTurn,
            Best: best,
            BestTarget: bestTarget,
            BestToken: bestToken,
            Beta: beta,
            Key: key,
            Shape: shape,
            Target: target,
            Token: token,
            Values: values
        );
    }
    private static void WriteSearchJob(WireWriter writer, SearchJobCheckpoint job) {
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
        WriteULongArray(
            writer: writer,
            values: job.TtKey
        );
        WriteLongArray(
            writer: writer,
            values: job.TtValue
        );
        WriteLongArray(
            writer: writer,
            values: job.TtMeta
        );
        writer.WriteBoolean(value: (job.Tree is not null));

        if (job.Tree is { } tree) {
            writer.WriteBoolean(value: tree.Active);
            writer.WriteInt32(value: tree.Phase);
            writer.WriteInt32(value: tree.Count);
            writer.WriteInt32(value: tree.Iteration);
            writer.WriteUInt64(value: tree.Seed);
            writer.WriteInt32(value: tree.UShape);
            writer.WriteInt32(value: tree.UToken);
            writer.WriteInt32(value: tree.UTarget);
            writer.WriteInt32(value: tree.UScan);
            writer.WriteInt32(value: tree.UStart);
            writer.WriteInt32(value: tree.PlayoutPlies);
            writer.WriteInt32(value: tree.PathLength);

            foreach (var values in new[] { tree.Parent, tree.FirstChild, tree.ChildCount, tree.Visits, tree.Total, tree.Shape, tree.Token, tree.Target, tree.Expanded, tree.Path, tree.UctValues, tree.PlayValues }) {
                WriteLongArray(
                    values: values,
                    writer: writer
                );
            }
        }
    }
    private static SearchJobCheckpoint ReadSearchJob(ref WireReader reader) {
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
            field: "search job legal",
            reader: ref reader
        );
        var counts = ReadLongArray(
            field: "search job counts",
            reader: ref reader
        );
        var wide = ReadLongArray(
            field: "search job wide",
            reader: ref reader
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
        var ttKey = ReadULongArray(
            field: "search job transposition keys",
            reader: ref reader
        );
        var ttValue = ReadLongArray(
            field: "search job transposition values",
            reader: ref reader
        );
        var ttMeta = ReadLongArray(
            field: "search job transposition meta",
            reader: ref reader
        );
        SearchTreeCheckpoint? tree = null;

        if (reader.ReadBoolean()) {
            var treeActive = reader.ReadBoolean();
            var phase = reader.ReadInt32();
            var treeCount = reader.ReadInt32();
            var iteration = reader.ReadInt32();
            var seed = reader.ReadUInt64();
            var uShape = reader.ReadInt32();
            var uToken = reader.ReadInt32();
            var uTarget = reader.ReadInt32();
            var uScan = reader.ReadInt32();
            var uStart = reader.ReadInt32();
            var playoutPlies = reader.ReadInt32();
            var pathLength = reader.ReadInt32();
            var arrays = new long[12][];

            for (var index = 0; (index < arrays.Length); index++) {
                arrays[index] = ReadLongArray(
                    field: "search job tree",
                    reader: ref reader
                );
            }

            tree = new SearchTreeCheckpoint(
                Active: treeActive,
                Phase: phase,
                Count: treeCount,
                Iteration: iteration,
                Seed: seed,
                UShape: uShape,
                UToken: uToken,
                UTarget: uTarget,
                UScan: uScan,
                UStart: uStart,
                PlayoutPlies: playoutPlies,
                Parent: arrays[0],
                FirstChild: arrays[1],
                ChildCount: arrays[2],
                Visits: arrays[3],
                Total: arrays[4],
                Shape: arrays[5],
                Token: arrays[6],
                Target: arrays[7],
                Expanded: arrays[8],
                Path: arrays[9],
                PathLength: pathLength,
                UctValues: arrays[10],
                PlayValues: arrays[11]
            );
        }

        return new SearchJobCheckpoint(
            Active: active,
            Alpha: alpha,
            BaseTurn: baseTurn,
            Best: best,
            BestTarget: bestTarget,
            BestToken: bestToken,
            Beta: beta,
            Count: count,
            Counts: counts,
            Done: done,
            Legal: legal,
            Levels: levels,
            Name: name,
            Nodes: nodes,
            PassDepth: passDepth,
            Running: running,
            Shape: shape,
            Stamp: stamp,
            Target: target,
            Token: token,
            Tree: tree,
            TtKey: ttKey,
            TtMeta: ttMeta,
            TtValue: ttValue,
            Wide: wide
        );
    }
    private static byte[] EncodeSearch(SearchCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Jobs,
            writeItem: WriteSearchJob
        );

        return writer.ToArray();
    }
    private static bool TryDecodeSearch(byte[] bytes, out string reason, out SearchCheckpoint section) {
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

        section = new SearchCheckpoint(Jobs: jobs);
        reason = string.Empty;

        return true;
    }
}
