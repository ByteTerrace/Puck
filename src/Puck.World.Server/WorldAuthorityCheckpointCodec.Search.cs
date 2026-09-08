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
            reader: ref reader,
            field: "search level values"
        );
        var key = reader.ReadUInt64();
        var alphaEntry = reader.ReadInt64();

        return new SearchLevelCheckpoint(
            Shape: shape,
            Token: token,
            Target: target,
            Alpha: alpha,
            Beta: beta,
            Best: best,
            BestToken: bestToken,
            BestTarget: bestTarget,
            BaseTurn: baseTurn,
            Values: values,
            Key: key,
            AlphaEntry: alphaEntry
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
                    writer: writer,
                    values: values
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
        var ttKey = ReadULongArray(
            reader: ref reader,
            field: "search job transposition keys"
        );
        var ttValue = ReadLongArray(
            reader: ref reader,
            field: "search job transposition values"
        );
        var ttMeta = ReadLongArray(
            reader: ref reader,
            field: "search job transposition meta"
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

            for (var index = 0; index < arrays.Length; index++) {
                arrays[index] = ReadLongArray(
                    reader: ref reader,
                    field: "search job tree"
                );
            }

            tree = new SearchTreeCheckpoint(
                Active: treeActive, Phase: phase, Count: treeCount, Iteration: iteration, Seed: seed, UShape: uShape, UToken: uToken, UTarget: uTarget, UScan: uScan, UStart: uStart, PlayoutPlies: playoutPlies,
                Parent: arrays[0], FirstChild: arrays[1], ChildCount: arrays[2], Visits: arrays[3], Total: arrays[4], Shape: arrays[5], Token: arrays[6], Target: arrays[7], Expanded: arrays[8],
                Path: arrays[9], PathLength: pathLength, UctValues: arrays[10], PlayValues: arrays[11]
            );
        }

        return new SearchJobCheckpoint(
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
            Levels: levels,
            TtKey: ttKey,
            TtValue: ttValue,
            TtMeta: ttMeta,
            Tree: tree
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
