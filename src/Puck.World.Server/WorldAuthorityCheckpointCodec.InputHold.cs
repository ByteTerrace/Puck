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
    private static void WriteInputHoldParticipant(WireWriter writer, WorldInputHoldRuntime.WorldInputHoldParticipantCheckpoint row) {
        writer.WriteBoolean(value: row.Active);
        WritePrincipal(
            writer: writer,
            principal: row.Principal
        );
        writer.WriteInt32(value: row.Measured);
        writer.WriteInt32(value: row.Target);
        writer.WriteInt32(value: row.Applied);
        writer.WriteInt32(value: row.LowerTarget);
        writer.WriteInt32(value: row.LowerStableTicks);
        writer.WriteInt32(value: row.HistoryStart);
        WriteArray(
            writer: writer,
            items: row.History,
            writeItem: WriteSubmittedInput
        );
    }
    private static WorldInputHoldRuntime.WorldInputHoldParticipantCheckpoint ReadInputHoldParticipant(ref WireReader reader) {
        var active = reader.ReadBoolean();
        var principal = ReadPrincipal(reader: ref reader);
        var measured = reader.ReadInt32();
        var target = reader.ReadInt32();
        var applied = reader.ReadInt32();
        var lowerTarget = reader.ReadInt32();
        var lowerStableTicks = reader.ReadInt32();
        var historyStart = reader.ReadInt32();
        var history = ReadArray(
            reader: ref reader,
            field: "input hold history",
            readItem: static (ref WireReader r) => ReadSubmittedInput(reader: ref r)
        );

        return new WorldInputHoldRuntime.WorldInputHoldParticipantCheckpoint(
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
    private static byte[] EncodeInputHold(WorldInputHoldRuntime.WorldInputHoldCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.MaximumSetter);
        WriteArray(
            writer: writer,
            items: section.Participants,
            writeItem: WriteInputHoldParticipant
        );

        return writer.ToArray();
    }
    private static bool TryDecodeInputHold(byte[] bytes, out string reason, out WorldInputHoldRuntime.WorldInputHoldCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var maximumSetter = reader.ReadInt32();
        var participants = ReadArray(
            reader: ref reader,
            field: "input hold participants",
            readItem: static (ref WireReader r) => ReadInputHoldParticipant(reader: ref r)
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"input hold section: {failure}";

            return false;
        }

        section = new WorldInputHoldRuntime.WorldInputHoldCheckpoint(
            MaximumSetter: maximumSetter,
            Participants: participants
        );
        reason = string.Empty;

        return true;
    }
    // ---- event feed section ----

}
