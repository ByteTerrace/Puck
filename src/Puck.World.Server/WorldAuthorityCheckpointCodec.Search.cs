using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSearchLevel(WireWriter writer, ArenaSearchLevelCheckpoint level) {
        writer.WriteInt32(value: level.Shape);
        writer.WriteInt32(value: level.Token);
        writer.WriteInt32(value: level.Target);
        writer.WriteInt64(value: level.Alpha);
        writer.WriteInt64(value: level.Beta);
        writer.WriteInt64(value: level.Best);
        writer.WriteInt32(value: level.BestToken);
        writer.WriteInt32(value: level.BestTarget);
        writer.WriteInt64(value: level.BaseTurn);
        writer.WriteUInt64(value: level.Key);
        writer.WriteInt64(value: level.AlphaEntry);
        writer.WriteInt32(value: level.EntryTarget);
        writer.WriteInt64(value: level.ChanceSumHigh);
        writer.WriteUInt64(value: level.ChanceSumLow);
        writer.WriteUInt64(value: level.ChanceWeight);
        writer.WriteBoolean(value: (level.Seats is not null));

        if (level.Seats is { } seats) {
            WriteLongArray(
                values: seats,
                writer: writer
            );
        }
    }
    private static ArenaSearchLevelCheckpoint ReadSearchLevel(ref WireReader reader) {
        var shape = reader.ReadInt32();
        var token = reader.ReadInt32();
        var target = reader.ReadInt32();
        var alpha = reader.ReadInt64();
        var beta = reader.ReadInt64();
        var best = reader.ReadInt64();
        var bestToken = reader.ReadInt32();
        var bestTarget = reader.ReadInt32();
        var baseTurn = reader.ReadInt64();
        var key = reader.ReadUInt64();
        var alphaEntry = reader.ReadInt64();
        var entryTarget = reader.ReadInt32();
        var chanceSumHigh = reader.ReadInt64();
        var chanceSumLow = reader.ReadUInt64();
        var chanceWeight = reader.ReadUInt64();
        long[]? seats = null;

        if (reader.ReadBoolean()) {
            seats = ReadLongArray(
                field: "search level seats",
                reader: ref reader
            );
        }

        return new ArenaSearchLevelCheckpoint(
            Alpha: alpha,
            AlphaEntry: alphaEntry,
            BaseTurn: baseTurn,
            Best: best,
            BestTarget: bestTarget,
            BestToken: bestToken,
            Beta: beta,
            ChanceSumHigh: chanceSumHigh,
            ChanceSumLow: chanceSumLow,
            ChanceWeight: chanceWeight,
            EntryTarget: entryTarget,
            Key: key,
            Seats: seats,
            Shape: shape,
            Target: target,
            Token: token
        );
    }
    private static void WriteSearchScope(WireWriter writer, ArenaSearchScopeCheckpoint scope) {
        writer.WriteInt32(value: scope.Shape);
        writer.WriteInt32(value: scope.Token);
        writer.WriteInt32(value: scope.Target);
    }
    private static ArenaSearchScopeCheckpoint ReadSearchScope(ref WireReader reader) {
        var shape = reader.ReadInt32();
        var token = reader.ReadInt32();
        var target = reader.ReadInt32();

        return new ArenaSearchScopeCheckpoint(
            Shape: shape,
            Target: target,
            Token: token
        );
    }
    private static void WriteSearchJob(WireWriter writer, ArenaSearchJobCheckpoint job) {
        writer.WriteString(value: job.Name);
        writer.WriteUInt64(value: job.Stamp);
        writer.WriteBoolean(value: job.Running);
        writer.WriteBoolean(value: job.Done);
        WriteSearchLevel(
            level: job.Root,
            writer: writer
        );
        writer.WriteInt64(value: job.Count);
        WriteLongArray(
            values: job.Legal,
            writer: writer
        );
        WriteLongArray(
            values: job.Counts,
            writer: writer
        );
        WriteLongArray(
            values: job.Wide,
            writer: writer
        );
        writer.WriteInt64(value: job.Nodes);
        writer.WriteInt64(value: job.Work);
        writer.WriteInt64(value: job.PeakStepWork);
        writer.WriteInt32(value: job.TokenCount);
        writer.WriteInt32(value: job.PassDepth);
        writer.WriteInt32(value: job.Active);
        writer.WriteArray(
            items: job.Levels,
            writeItem: WriteSearchLevel
        );
        writer.WriteArray(
            items: job.Scopes,
            writeItem: WriteSearchScope
        );
        WriteULongArray(
            values: job.TtKey,
            writer: writer
        );
        WriteLongArray(
            values: job.TtValue,
            writer: writer
        );
        WriteLongArray(
            values: job.TtMeta,
            writer: writer
        );
        writer.WriteBoolean(value: (job.Tree is not null));

        if (job.Tree is { } tree) {
            writer.WriteBoolean(value: tree.Active);
            writer.WriteInt32(value: tree.Phase);
            writer.WriteInt32(value: tree.Count);
            writer.WriteInt32(value: tree.Iteration);
            writer.WriteUInt64(value: tree.Seed);
            writer.WriteInt32(value: tree.Scan);
            writer.WriteInt32(value: tree.Start);
            writer.WriteInt32(value: tree.PlayoutPlies);
            writer.WriteInt32(value: tree.PlayCount);
            writer.WriteInt32(value: tree.PathLength);

            foreach (var values in new[] { tree.Parent, tree.FirstChild, tree.NextSibling, tree.ChildCount, tree.Shape, tree.Token, tree.Target, tree.Scanned, tree.ScanStart, tree.Path }) {
                WriteIntArray(
                    values: values,
                    writer: writer
                );
            }
            foreach (var values in new[] { tree.Visits, tree.Total }) {
                WriteLongArray(
                    values: values,
                    writer: writer
                );
            }
        }
    }
    private static ArenaSearchJobCheckpoint ReadSearchJob(ref WireReader reader) {
        var name = reader.ReadRequiredString(
            field: "search job name",
            maxBytes: MaxHashChars
        );
        var stamp = reader.ReadUInt64();
        var running = reader.ReadBoolean();
        var done = reader.ReadBoolean();
        var root = ReadSearchLevel(reader: ref reader);
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
        var work = reader.ReadInt64();
        var peakStepWork = reader.ReadInt64();
        var tokenCount = reader.ReadInt32();
        var passDepth = reader.ReadInt32();
        var active = reader.ReadInt32();
        var levels = reader.ReadArray(
            field: "search job levels",
            readItem: static (ref WireReader r) => ReadSearchLevel(reader: ref r),
            maximum: MaxCollectionCount
        );
        var scopes = reader.ReadArray(
            field: "search job scopes",
            readItem: static (ref WireReader r) => ReadSearchScope(reader: ref r),
            maximum: MaxCollectionCount
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
        ArenaSearchTreeCheckpoint? tree = null;

        if (reader.ReadBoolean()) {
            var treeActive = reader.ReadBoolean();
            var phase = reader.ReadInt32();
            var treeCount = reader.ReadInt32();
            var iteration = reader.ReadInt32();
            var seed = reader.ReadUInt64();
            var scan = reader.ReadInt32();
            var start = reader.ReadInt32();
            var playoutPlies = reader.ReadInt32();
            var playCount = reader.ReadInt32();
            var pathLength = reader.ReadInt32();
            var ints = new int[10][];
            var longs = new long[2][];

            for (var index = 0; (index < ints.Length); index++) {
                ints[index] = ReadIntArray(
                    field: "search job tree",
                    reader: ref reader
                );
            }
            for (var index = 0; (index < longs.Length); index++) {
                longs[index] = ReadLongArray(
                    field: "search job tree",
                    reader: ref reader
                );
            }

            tree = new ArenaSearchTreeCheckpoint(
                Active: treeActive,
                ChildCount: ints[3],
                Count: treeCount,
                FirstChild: ints[1],
                Iteration: iteration,
                NextSibling: ints[2],
                Parent: ints[0],
                Path: ints[9],
                PathLength: pathLength,
                PlayCount: playCount,
                PlayoutPlies: playoutPlies,
                Phase: phase,
                Scan: scan,
                ScanStart: ints[8],
                Scanned: ints[7],
                Seed: seed,
                Shape: ints[4],
                Start: start,
                Target: ints[6],
                Token: ints[5],
                Total: longs[1],
                Visits: longs[0]
            );
        }

        return new ArenaSearchJobCheckpoint(
            Active: active,
            Count: count,
            Counts: counts,
            Done: done,
            Legal: legal,
            Levels: levels,
            Name: name,
            Nodes: nodes,
            PassDepth: passDepth,
            PeakStepWork: peakStepWork,
            Root: root,
            Running: running,
            Scopes: scopes,
            Stamp: stamp,
            TokenCount: tokenCount,
            Tree: tree,
            TtKey: ttKey,
            TtMeta: ttMeta,
            TtValue: ttValue,
            Wide: wide,
            Work: work
        );
    }
    private static byte[] EncodeSearch(ArenaSearchCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteArray(
            items: section.Jobs,
            writeItem: WriteSearchJob
        );

        return writer.ToArray();
    }
    private static bool TryDecodeSearch(byte[] bytes, out string reason, out ArenaSearchCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var jobs = reader.ReadArray(
            field: "search jobs",
            readItem: static (ref WireReader r) => ReadSearchJob(reader: ref r),
            maximum: MaxCollectionCount
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"search section: {failure}";

            return false;
        }

        section = new ArenaSearchCheckpoint(Jobs: jobs);
        reason = string.Empty;

        return true;
    }
}
