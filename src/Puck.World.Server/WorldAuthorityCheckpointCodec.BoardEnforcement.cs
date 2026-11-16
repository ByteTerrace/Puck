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

        writer.WriteArray(
            items: section.Entries,
            writeItem: WriteBoardVerdict
        );

        return writer.ToArray();
    }
    private static bool TryDecodeBoardEnforcement(byte[] bytes, out string reason, out WorldBoardEnforcementCheckpoint section) => TryDecodeSection(
        bytes: bytes,
        name: "board enforcement",
        read: ReadBoardEnforcement,
        reason: out reason,
        section: out section
    );
    private static WorldBoardEnforcementCheckpoint ReadBoardEnforcement(ref WireReader reader) => new(Entries: reader.ReadArray(
        field: "board enforcement entries",
        readItem: static (ref WireReader r) => ReadBoardVerdict(reader: ref r),
        maximum: MaxCollectionCount
    ));
}
