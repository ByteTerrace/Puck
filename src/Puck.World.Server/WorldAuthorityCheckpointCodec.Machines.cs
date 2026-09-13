using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeMachines(WorldMachineHostCheckpoint section) {
        var writer = new WireWriter();
        writer.WriteUInt64(section.Revision);
        writer.WriteUInt64(section.NextGeneration);
        writer.WriteBoolean(section.AnyEverPumped);
        WriteArray(writer, section.Instances, static (w, row) => {
            w.WriteString(row.Name); w.WriteString(row.Engine);
            w.WriteUInt64(row.Generation); w.WriteInt64(row.CompletedSteps);
            w.WriteBlock(row.RuntimeState);
        });
        return writer.ToArray();
    }

    private static bool TryDecodeMachines(byte[] bytes, out WorldMachineHostCheckpoint section, out string reason) {
        var reader = new WireReader(bytes);
        var revision = reader.ReadUInt64();
        var next = reader.ReadUInt64();
        var pumped = reader.ReadBoolean();
        var instances = ReadArray(ref reader, "machine instances", static (ref WireReader r) => new WorldMachineCheckpoint(
            r.ReadRequiredString("machine name", MaxStringBytes), r.ReadRequiredString("machine engine", MaxStringBytes),
            r.ReadUInt64(), r.ReadInt64(), r.ReadBlock("machine runtime", MaxSectionBytes)));
        section = null!;
        if (!reader.TryFinish(out var failure)) { reason = $"machine section: {failure}"; return false; }
        if (next == 0 || instances.Any(row => row.Generation == 0 || row.Generation >= next || row.CompletedSteps < 0 || row.RuntimeState.Length == 0) ||
            instances.Select(row => row.Name).Distinct(StringComparer.Ordinal).Count() != instances.Length ||
            instances.Select(row => row.Generation).Distinct().Count() != instances.Length) {
            reason = "machine section has invalid inventory or generation bookkeeping";
            return false;
        }
        section = new(revision, next, pumped, instances);
        reason = string.Empty;
        return true;
    }
}
