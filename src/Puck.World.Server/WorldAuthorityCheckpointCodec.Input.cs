using Puck.Networking;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteInputInterval(WireWriter writer, WorldInputInterval interval) {
        writer.WriteUInt64(interval.Tick); writer.WriteUInt64(interval.EngineStart); writer.WriteUInt64(interval.EngineDuration);
        writer.WriteOptional(interval.Music, static (w, music) => { w.WriteUInt64(music.ElapsedTicks); w.WriteInt64(music.TicksPerBeat); w.WriteString(music.Segment); });
    }
    private static WorldInputInterval ReadInputInterval(ref WireReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadOptional(static (ref WireReader r) => new WorldInputMusicSegment(r.ReadUInt64(), r.ReadInt64(), r.ReadString("input music segment"))));
    private static void WriteInputRecord(WireWriter writer, WorldInputRecord record) {
        writer.WriteUInt64(record.Id); writer.WriteUInt64(record.Epoch); WorldWireCodec.WriteInputEvent(writer, record.Event);
        WriteInputInterval(writer, record.Capture); WriteInputInterval(writer, record.Admission);
        writer.WriteUInt64(record.HoldPress); writer.WriteInt32(record.Crossing); writer.WriteFixed(record.Calibration);
    }
    private static WorldInputRecord ReadInputRecord(ref WireReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(),
        WorldWireCodec.ReadInputEvent(ref reader), ReadInputInterval(ref reader), ReadInputInterval(ref reader), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadFixed());

    private static T[] ReadInputArray<T>(ref WireReader reader, int maximum, int minimumBytes, WireReadItem<T> read) {
        var count = reader.ReadCount("input checkpoint array", 0, maximum);
        if (count > reader.Remaining / minimumBytes) { reader.Fail(WireRefusal.CountOutOfRange, "input checkpoint array exceeds remaining bytes"); }
        if (reader.Failed || count == 0) { return []; }
        var values = new T[count];
        for (var index = 0; index < values.Length && !reader.Failed; index++) { values[index] = read(ref reader); }
        return values;
    }

    private static void WriteInputRuntime(WireWriter writer, WorldInputRuntimeCheckpoint state) {
        WriteInputInterval(writer, state.Interval);
        writer.WriteArray(state.Intervals, WriteInputInterval);
        writer.WriteArray(state.Owners, static (w, participant) => {
            if (!WorldWireCodec.TryWritePrincipal(w, participant.Principal)) { throw new ArgumentException("input checkpoint principal is invalid"); }
            w.WriteInt32(participant.Body); w.WriteInt32(participant.Generation);
            w.WriteOptional(participant.Controller, static (w, controller) => {
                // This is an authority checkpoint, not a submission. World stays forbidden on the live wire.
                w.WriteBoolean(controller.Actor.Kind == PrincipalKind.World);
                if (controller.Actor.Kind != PrincipalKind.World && !WorldWireCodec.TryWritePrincipal(w, controller.Actor)) { throw new ArgumentException("input controller actor is invalid"); }
                w.WriteString(controller.Source); w.WriteUInt64(controller.ActivationSequence); w.WriteUInt64(controller.ObservationTick);
            });
            w.WriteArray(participant.SeatStates, static (w, cell) => { w.WriteString(cell.Family); w.WriteString(cell.State); });
            var input = participant.Input;
            w.WriteUInt32(input.Revision); w.WriteUInt64(input.Epoch); w.WriteUInt16(input.Mapping);
            WorldWireCodec.WriteIntent(w, input.Image); w.WriteFixed(input.Calibration);
            WorldWireCodec.WriteInputSwitch(w, input.LastSwitch);
            w.WriteUInt64(input.RefusedBatches); w.WriteUInt32(input.Sequence); w.WriteUInt64(input.EventId);
            WriteInputInterval(w, input.LastCapture); w.WriteFixed(input.LastOffset); w.WriteUInt64(input.Step); w.WriteInt32(input.StepEvents);
            w.WriteArray(input.Sources, static (w, source) => {
                WorldWireCodec.WriteInputSource(w, source.Source);
                w.WriteInt64(source.RawValue); w.WriteUInt64(source.Activation); w.WriteBoolean(source.AwaitingNeutral);
            });
            w.WriteArray(input.Holds, static (w, hold) => {
                w.WriteUInt16(hold.Channel); w.WriteUInt64(hold.Press); WriteInputRecord(w, hold.Origin); w.WriteBoolean(hold.Consumed);
            });
            w.WriteArray(input.History, WriteInputRecord);
            w.WriteArray(input.Crossings, static (w, held) => w.WriteBoolean(held));
            w.WriteArray(input.Claims, static (w, claim) => { w.WriteInt32(claim.Group); w.WriteUInt64(claim.Event); });
            w.WriteArray(input.Switches, WorldWireCodec.WriteInputSwitch);
        });
    }
    private static WorldInputRuntimeCheckpoint ReadInputRuntime(ref WireReader reader) {
        var interval = ReadInputInterval(ref reader);
        var intervals = ReadInputArray(ref reader, 1_000_000, 24, ReadInputInterval);
        var owners = ReadInputArray(ref reader, ushort.MaxValue, 64, static (ref WireReader r) => {
            var principal = WorldWireCodec.ReadPrincipal(ref r);
            var body = r.ReadInt32(); var generation = r.ReadInt32();
            var controller = r.ReadOptional(static (ref WireReader r) => new WorldInputControllerOrigin(
                r.ReadBoolean() ? Principal.World : WorldWireCodec.ReadPrincipal(ref r), r.ReadRequiredString("input controller source", maxBytes: 256), r.ReadUInt64(), r.ReadUInt64()));
            var cells = ReadInputArray(ref r, WorldInputCatalog.MaximumTuples, 8, static (ref WireReader r) =>
                new WorldInputSeatState(r.ReadRequiredString("input seat family"), r.ReadRequiredString("input seat state")));
            var revision = r.ReadUInt32(); var epoch = r.ReadUInt64(); var mapping = r.ReadUInt16();
            var image = WorldWireCodec.ReadIntent(ref r); var calibration = r.ReadFixed();
            var boundary = WorldWireCodec.ReadInputSwitch(ref r);
            var refused = r.ReadUInt64(); var sequence = r.ReadUInt32(); var eventId = r.ReadUInt64();
            var capture = ReadInputInterval(ref r); var offset = r.ReadFixed(); var step = r.ReadUInt64(); var stepEvents = r.ReadInt32();
            var sources = ReadInputArray(ref r, ushort.MaxValue, 30, static (ref WireReader r) => new WorldInputSourceState(
                WorldWireCodec.ReadInputSource(ref r), r.ReadInt64(), r.ReadUInt64(), r.ReadBoolean()));
            var holds = ReadInputArray(ref r, ChannelLimits.MaxChannels, 134, static (ref WireReader r) =>
                new WorldInputHold(r.ReadUInt16(), r.ReadUInt64(), ReadInputRecord(ref r), r.ReadBoolean()));
            var history = ReadInputArray(ref r, ushort.MaxValue + ChannelLimits.MaxButtons, 123, ReadInputRecord);
            var crossings = ReadInputArray(ref r, ushort.MaxValue, 1, static (ref WireReader r) => r.ReadBoolean());
            var claims = ReadInputArray(ref r, 64 * (ushort.MaxValue + ChannelLimits.MaxButtons), 12, static (ref WireReader r) => new WorldInputClaim(r.ReadInt32(), r.ReadUInt64()));
            var switches = ReadInputArray(ref r, ushort.MaxValue, 48, WorldWireCodec.ReadInputSwitch);
            return new WorldInputParticipantCheckpoint(principal, body, generation, cells,
                new(revision, epoch, mapping, image, calibration, boundary, refused, sequence, eventId, capture, offset, step, stepEvents, sources, holds, history, crossings, claims, switches), controller);
        });
        return new(interval, intervals, owners);
    }
}
