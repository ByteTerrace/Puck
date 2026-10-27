using Puck.Networking;

namespace Puck.World.Protocol;

public static partial class WorldWireCodec {
    /// <summary>Writes the complete barrier leaf shared by publications and checkpoints.</summary>
    public static void WriteInputSwitch(WireWriter writer, WorldInputSwitch value) {
        var interval = value.Interval;
        writer.WriteUInt64(interval.Tick); writer.WriteUInt64(interval.EngineStart); writer.WriteUInt64(interval.EngineDuration);
        writer.WriteOptional(interval.Music, static (w, music) => { w.WriteUInt64(music.ElapsedTicks); w.WriteInt64(music.TicksPerBeat); w.WriteString(music.Segment); });
        writer.WriteUInt64(value.Epoch); writer.WriteUInt16(value.From); writer.WriteUInt16(value.To);
        if (value.ConsumedActivations.Length > WorldInputSwitch.MaximumConsumedActivations) { throw new ArgumentException("input boundary exceeds its activation budget", nameof(value)); }
        writer.WriteInt32(value.ConsumedActivations.Length);
        foreach (var activation in value.ConsumedActivations.Span) { writer.WriteUInt64(activation); }
        writer.WriteBoolean(value.Reset);
        if (value.Decisions.Length > ChannelLimits.MaxChannels) { throw new ArgumentException("input boundary exceeds the live action budget", nameof(value)); }
        writer.WriteUInt16((ushort)value.Decisions.Length);
        foreach (var decision in value.Decisions.Span) {
            writer.WriteUInt16(decision.Channel); writer.WriteInt64(decision.PreviousRaw); writer.WriteInt64(decision.CurrentRaw);
            writer.WriteByte((byte)decision.Reason); writer.WriteUInt64(decision.HoldPress); writer.WriteFixed(decision.HeldSince);
        }
    }

    /// <summary>Reads a bounded barrier, preserving each action's reason and original hold.</summary>
    public static WorldInputSwitch ReadInputSwitch(ref WireReader reader) {
        var interval = new WorldInputInterval(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
            reader.ReadOptional(static (ref WireReader r) => new WorldInputMusicSegment(r.ReadUInt64(), r.ReadInt64(), r.ReadString("input music segment"))));
        var epoch = reader.ReadUInt64(); var from = reader.ReadUInt16(); var to = reader.ReadUInt16();
        var activations = reader.ReadArray("input transition activations", static (ref WireReader r) => r.ReadUInt64(), WorldInputSwitch.MaximumConsumedActivations);
        var reset = reader.ReadBoolean();
        var count = reader.ReadUInt16();
        if (count > ChannelLimits.MaxChannels || count * 35L > reader.Remaining) {
            reader.Fail(WireRefusal.CountOutOfRange, "input boundary exceeds live actions or remaining payload");
            return default;
        }
        var decisions = count == 0 ? [] : new WorldInputContinuation[count];
        for (var index = 0; index < decisions.Length && !reader.Failed; index++) {
            var channel = reader.ReadUInt16(); var previous = reader.ReadInt64(); var current = reader.ReadInt64(); var reason = reader.ReadByte();
            var press = reader.ReadUInt64(); var since = reader.ReadFixed();
            if (channel >= WorldInputCatalog.MaximumLogicalChannels || reason > (byte)WorldInputContinuationReason.Reset) {
                reader.Fail(WireRefusal.PayloadMalformed, "input boundary channel or reason is invalid");
                break;
            }
            decisions[index] = new(channel, previous, current, (WorldInputContinuationReason)reason, press, since);
        }
        return new(interval, epoch, from, to, activations, reset, decisions);
    }
}
