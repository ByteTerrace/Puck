using Puck.Networking;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteInputInterval(WireWriter writer, WorldInputInterval interval) {
        writer.WriteUInt64(value: interval.Tick); writer.WriteUInt64(value: interval.EngineStart); writer.WriteUInt64(value: interval.EngineDuration);
        writer.WriteOptional(value: interval.Music, writeValue: static (w, music) => { w.WriteUInt64(value: music.ElapsedTicks); w.WriteInt64(value: music.TicksPerBeat); w.WriteString(value: music.Segment); });
    }
    private static WorldInputInterval ReadInputInterval(ref WireReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadOptional(readValue: static (ref WireReader r) => new WorldInputMusicSegment(r.ReadUInt64(), r.ReadInt64(), r.ReadString("input music segment"))));
    private static void WriteInputRecord(WireWriter writer, WorldInputRecord record) {
        writer.WriteUInt64(value: record.Id); writer.WriteUInt64(value: record.Epoch); WorldWireCodec.WriteInputEvent(writer, record.Event);
        WriteInputInterval(writer, record.Capture); WriteInputInterval(writer, record.Admission);
        writer.WriteUInt64(value: record.HoldPress); writer.WriteInt32(value: record.Crossing); writer.WriteFixed(value: record.Calibration);
    }
    private static WorldInputRecord ReadInputRecord(ref WireReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(),
        WorldWireCodec.ReadInputEvent(reader: ref reader), ReadInputInterval(reader: ref reader), ReadInputInterval(reader: ref reader), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadFixed());
    private static T[] ReadInputArray<T>(ref WireReader reader, int maximum, int minimumBytes, WireReadItem<T> read) {
        var count = reader.ReadCount(field: "input checkpoint array", maximum: maximum, minimum: 0);

        if (count > (reader.Remaining / minimumBytes)) { reader.Fail(detail: "input checkpoint array exceeds remaining bytes", refusal: WireRefusal.CountOutOfRange); }
        if (reader.Failed || (count == 0)) { return []; }
        var values = new T[count];

        for (var index = 0; ((index < values.Length) && !reader.Failed); index++) { values[index] = read(ref reader); }
        return values;
    }
    private static void WriteInputRuntime(WireWriter writer, WorldInputRuntimeCheckpoint state) {
        WriteInputInterval(writer, state.Interval);
        writer.WriteArray(items: state.Intervals, writeItem: WriteInputInterval);
        writer.WriteArray(items: state.Owners, writeItem: static (w, participant) => {
            if (!WorldWireCodec.TryWritePrincipal(w, participant.Principal)) { throw new ArgumentException(message: "input checkpoint principal is invalid"); }
            w.WriteInt32(value: participant.Body); w.WriteInt32(value: participant.Generation);
            w.WriteOptional(value: participant.Controller, writeValue: static (w, controller) => {
                // This is an authority checkpoint, not a submission. World stays forbidden on the live wire.
                w.WriteBoolean(value: (controller.Actor.Kind == PrincipalKind.World));
                if ((controller.Actor.Kind != PrincipalKind.World) && !WorldWireCodec.TryWritePrincipal(w, controller.Actor)) { throw new ArgumentException(message: "input controller actor is invalid"); }
                w.WriteString(value: controller.Source); w.WriteUInt64(value: controller.ActivationSequence); w.WriteUInt64(value: controller.ObservationTick);
            });
            w.WriteArray(items: participant.SeatStates, writeItem: static (w, cell) => { w.WriteString(value: cell.Family); w.WriteString(value: cell.State); });
            var input = participant.Input;

            w.WriteUInt32(value: input.Revision); w.WriteUInt64(value: input.Epoch); w.WriteUInt16(value: input.Mapping);
            WorldWireCodec.WriteIntent(w, input.Image); w.WriteFixed(value: input.Calibration);
            WorldWireCodec.WriteInputSwitch(w, input.LastSwitch);
            w.WriteUInt64(value: input.RefusedBatches); w.WriteUInt32(value: input.Sequence); w.WriteUInt64(value: input.EventId);
            WriteInputInterval(w, input.LastCapture); w.WriteFixed(value: input.LastOffset); w.WriteUInt64(value: input.Step); w.WriteInt32(value: input.StepEvents);
            w.WriteArray(items: input.Sources, writeItem: static (w, source) => {
                WorldWireCodec.WriteInputSource(w, source.Source);
                w.WriteInt64(value: source.RawValue); w.WriteUInt64(value: source.Activation); w.WriteBoolean(value: source.AwaitingNeutral);
            });
            w.WriteArray(items: input.Holds, writeItem: static (w, hold) => {
                w.WriteUInt16(value: hold.Channel); w.WriteUInt64(value: hold.Press); WriteInputRecord(w, hold.Origin); w.WriteBoolean(value: hold.Consumed);
            });
            w.WriteArray(items: input.History, writeItem: WriteInputRecord);
            w.WriteArray(items: input.Crossings, writeItem: static (w, held) => w.WriteBoolean(value: held));
            w.WriteArray(items: input.Claims, writeItem: static (w, claim) => { w.WriteInt32(value: claim.Group); w.WriteUInt64(value: claim.Event); });
            w.WriteArray(items: input.Switches, writeItem: WorldWireCodec.WriteInputSwitch);
        });
    }
    private static WorldInputRuntimeCheckpoint ReadInputRuntime(ref WireReader reader) {
        var interval = ReadInputInterval(reader: ref reader);
        var intervals = ReadInputArray(maximum: 1_000_000, minimumBytes: 24, read: ReadInputInterval, reader: ref reader);
        var owners = ReadInputArray(ref reader, ushort.MaxValue, 64, static (ref WireReader r) => {
            var principal = WorldWireCodec.ReadPrincipal(ref r);
            var body = r.ReadInt32(); var generation = r.ReadInt32();
            var controller = r.ReadOptional(readValue: static (ref WireReader r) => new WorldInputControllerOrigin(
                (r.ReadBoolean() ? Principal.World : WorldWireCodec.ReadPrincipal(ref r)), r.ReadRequiredString("input controller source", maxBytes: 256), r.ReadUInt64(), r.ReadUInt64()));
            var cells = ReadInputArray(ref r, WorldInputCatalog.MaximumTuples, 8, static (ref WireReader r) =>
                new WorldInputSeatState(Family: r.ReadRequiredString("input seat family"), State: r.ReadRequiredString("input seat state")));
            var revision = r.ReadUInt32(); var epoch = r.ReadUInt64(); var mapping = r.ReadUInt16();
            var image = WorldWireCodec.ReadIntent(reader: ref r); var calibration = r.ReadFixed();
            var boundary = WorldWireCodec.ReadInputSwitch(reader: ref r);
            var refused = r.ReadUInt64(); var sequence = r.ReadUInt32(); var eventId = r.ReadUInt64();
            var capture = ReadInputInterval(reader: ref r); var offset = r.ReadFixed(); var step = r.ReadUInt64(); var stepEvents = r.ReadInt32();
            var sources = ReadInputArray(ref r, ushort.MaxValue, 30, static (ref WireReader r) => new WorldInputSourceState(
                WorldWireCodec.ReadInputSource(reader: ref r), r.ReadInt64(), r.ReadUInt64(), r.ReadBoolean()));
            var holds = ReadInputArray(ref r, ChannelLimits.MaxChannels, 134, static (ref WireReader r) =>
                new WorldInputHold(r.ReadUInt16(), r.ReadUInt64(), ReadInputRecord(reader: ref r), r.ReadBoolean()));
            var history = ReadInputArray(maximum: (ushort.MaxValue + ChannelLimits.MaxButtons), minimumBytes: 123, read: ReadInputRecord, reader: ref r);
            var crossings = ReadInputArray(ref r, ushort.MaxValue, 1, static (ref WireReader r) => r.ReadBoolean());
            var claims = ReadInputArray(ref r, (64 * (ushort.MaxValue + ChannelLimits.MaxButtons)), 12, static (ref WireReader r) => new WorldInputClaim(r.ReadInt32(), r.ReadUInt64()));
            var switches = ReadInputArray(maximum: ushort.MaxValue, minimumBytes: 48, read: WorldWireCodec.ReadInputSwitch, reader: ref r);

            return new WorldInputParticipantCheckpoint(principal, body, generation, cells,
                new(Calibration: calibration, Claims: claims, Crossings: crossings, Epoch: epoch, EventId: eventId, History: history, Holds: holds, Image: image, LastCapture: capture, LastOffset: offset, LastSwitch: boundary, Mapping: mapping, RefusedBatches: refused, Revision: revision, Sequence: sequence, Sources: sources, Step: step, StepEvents: stepEvents, Switches: switches), controller);
        });

        return new(Interval: interval, Intervals: intervals, Owners: owners);
    }
}
