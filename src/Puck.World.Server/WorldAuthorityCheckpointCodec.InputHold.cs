using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteSubmittedInput(WireWriter writer, WorldSubmittedInput input) {
        writer.WriteBoolean(value: input.HasIntent);
        WorldWireCodec.WriteIntent(
            intent: input.Intent,
            writer: writer
        );
        WorldWireCodec.WriteIntent(
            intent: input.HeldChannels,
            writer: writer
        );
    }
    private static WorldSubmittedInput ReadSubmittedInput(ref WireReader reader) {
        var hasIntent = reader.ReadBoolean();
        var intent = WorldWireCodec.ReadIntent(reader: ref reader);
        var heldChannels = WorldWireCodec.ReadIntent(reader: ref reader);

        return new WorldSubmittedInput(
            HasIntent: hasIntent,
            HeldChannels: heldChannels,
            Intent: intent
        );
    }
    private static void WriteInputHoldParticipant(WireWriter writer, WorldInputHoldParticipantCheckpoint row) {
        // An inactive participant carries no principal: its runtime slot holds the unstamped default, which names no
        // one and has no wire value.
        writer.WriteBoolean(value: row.Active);

        if (row.Active) {
            WritePrincipal(
                writer: writer,
                principal: row.Principal
            );
        }

        writer.WriteInt32(value: row.Measured);
        writer.WriteInt32(value: row.Target);
        writer.WriteInt32(value: row.Applied);
        writer.WriteInt32(value: row.LowerTarget);
        writer.WriteInt32(value: row.LowerStableTicks);
        writer.WriteInt32(value: row.HistoryStart);
        writer.WriteArray(
            items: row.History,
            writeItem: WriteSubmittedInput
        );
    }
    private static WorldInputHoldParticipantCheckpoint ReadInputHoldParticipant(ref WireReader reader) {
        var active = reader.ReadBoolean();
        var principal = (active
            ? WorldWireCodec.ReadPrincipal(reader: ref reader)
            : default);
        var measured = reader.ReadInt32();
        var target = reader.ReadInt32();
        var applied = reader.ReadInt32();
        var lowerTarget = reader.ReadInt32();
        var lowerStableTicks = reader.ReadInt32();
        var historyStart = reader.ReadInt32();
        var history = reader.ReadArray(
            field: "input hold history",
            readItem: static (ref WireReader r) => ReadSubmittedInput(reader: ref r),
            maximum: MaxCollectionCount
        );

        return new WorldInputHoldParticipantCheckpoint(
            Active: active,
            Applied: applied,
            History: history,
            HistoryStart: historyStart,
            LowerStableTicks: lowerStableTicks,
            LowerTarget: lowerTarget,
            Measured: measured,
            Principal: principal,
            Target: target
        );
    }
    private static byte[] EncodeInputHold(WorldInputHoldCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.MaximumSetter);
        writer.WriteArray(
            items: section.Participants,
            writeItem: WriteInputHoldParticipant
        );

        return writer.ToArray();
    }
    private static bool TryDecodeInputHold(byte[] bytes, out string reason, out WorldInputHoldCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var maximumSetter = reader.ReadInt32();
        var participants = reader.ReadArray(
            field: "input hold participants",
            readItem: static (ref WireReader r) => ReadInputHoldParticipant(reader: ref r),
            maximum: MaxCollectionCount
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"input hold section: {failure}";

            return false;
        }

        section = new WorldInputHoldCheckpoint(
            MaximumSetter: maximumSetter,
            Participants: participants
        );
        reason = string.Empty;

        return true;
    }
    // ---- event feed section ----

}
