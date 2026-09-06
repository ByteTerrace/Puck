using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteBoardVerdict(WireWriter writer, (string PlacementId, long Verdict) entry) {
        writer.WriteString(value: entry.PlacementId);
        writer.WriteInt64(value: entry.Verdict);
    }
    private static (string PlacementId, long Verdict) ReadBoardVerdict(ref WireReader reader) {
        var placementId = reader.ReadRequiredString(
            field: "board enforcement placement id",
            maxBytes: MaxStringBytes
        );
        var verdict = reader.ReadInt64();

        return (placementId, verdict);
    }
    private static byte[] EncodeBoardEnforcement(WorldBoardEnforcementCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Entries,
            writeItem: WriteBoardVerdict
        );

        return writer.ToArray();
    }
    private static bool TryDecodeBoardEnforcement(byte[] bytes, out string reason, out WorldBoardEnforcementCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var entries = ReadArray(
            reader: ref reader,
            field: "board enforcement entries",
            readItem: static (ref WireReader r) => ReadBoardVerdict(reader: ref r)
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"board enforcement section: {failure}";

            return false;
        }

        section = new WorldBoardEnforcementCheckpoint(Entries: entries);
        reason = string.Empty;

        return true;
    }
}
