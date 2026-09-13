using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeMachines(WorldMachineHostCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteUInt64(value: section.Revision);
        writer.WriteUInt64(value: section.NextGeneration);
        writer.WriteBoolean(value: section.AnyEverPumped);
        WriteArray(
            writer,
            section.Instances,
            static (w, row) => {
            w.WriteString(value: row.Name); w.WriteString(value: row.Engine);
            w.WriteUInt64(value: row.Generation); w.WriteInt64(value: row.CompletedSteps);
            w.WriteBlock(value: row.RuntimeState);
        }
        );
        return writer.ToArray();
    }
    private static bool TryDecodeMachines(byte[] bytes, out WorldMachineHostCheckpoint section, out string reason) {
        var reader = new WireReader(bytes: bytes);
        var revision = reader.ReadUInt64();
        var next = reader.ReadUInt64();
        var pumped = reader.ReadBoolean();
        var instances = ReadArray(
            ref reader,
            "machine instances",
            static (ref WireReader r) => new WorldMachineCheckpoint(
                r.ReadRequiredString(
                    field: "machine name",
                    maxBytes: MaxStringBytes
                ),
                r.ReadRequiredString(
                    field: "machine engine",
                    maxBytes: MaxStringBytes
                ),
                r.ReadUInt64(),
                r.ReadInt64(),
                r.ReadBlock(
                    field: "machine runtime",
                    maxBytes: MaxSectionBytes
                )
            )
        );

        section = null!;
        if (!reader.TryFinish(failure: out var failure)) { reason = $"machine section: {failure}"; return false; }
        if (
            (next == 0) ||
            instances.Any(predicate: row => ((row.Generation == 0) || (row.Generation >= next) || (row.CompletedSteps < 0) || (row.RuntimeState.Length == 0))) ||
            (instances.Select(selector: row => row.Name).Distinct(comparer: StringComparer.Ordinal).Count() != instances.Length) ||
            (instances.Select(selector: row => row.Generation).Distinct().Count() != instances.Length)
        ) {
            reason = "machine section has invalid inventory or generation bookkeeping";
            return false;
        }
        section = new(
            AnyEverPumped: pumped,
            Instances: instances,
            NextGeneration: next,
            Revision: revision
        );
        reason = string.Empty;
        return true;
    }
}
