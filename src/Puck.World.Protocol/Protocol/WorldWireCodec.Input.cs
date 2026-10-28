using Puck.Maths;
using Puck.Networking;

namespace Puck.World.Protocol;

public static partial class WorldWireCodec {
    /// <summary>Writes source identity once at admission/publication/checkpoint, never in each held contribution.</summary>
    public static void WriteInputSource(WireWriter writer, WorldInputSource source) {
        writer.WriteUInt16(value: source.Handle); writer.WriteUInt16(value: source.Channel); writer.WriteByte(value: ((byte)source.Kind));
        writer.WriteString(value: source.Name); writer.WriteString(value: source.Binding);
    }
    /// <summary>Reads the shared admitted-source contract.</summary>
    public static WorldInputSource ReadInputSource(ref WireReader reader) {
        var handle = reader.ReadUInt16(); var channel = reader.ReadUInt16(); var kind = reader.ReadByte();
        var name = reader.ReadRequiredString("input source identity");
        var binding = reader.ReadString("input source binding", maxBytes: 16_384);

        if ((channel >= WorldInputCatalog.MaximumLogicalChannels) || (kind > ((byte)WorldInputContributionKind.Relative)) ||
            (name.Length is < 1 or > 256) || (binding.Length > 4096)) {
            reader.Fail(detail: "input source contract is invalid", refusal: WireRefusal.PayloadMalformed);
        }
        return new(Binding: binding, Channel: channel, Handle: handle, Kind: ((WorldInputContributionKind)kind), Name: name);
    }
    /// <summary>Writes the owner-filtered authority decisions accompanying a snapshot.</summary>
    public static bool TryWriteInputPublications(WireWriter writer, ReadOnlySpan<WorldInputPublication> publications) {
        if (publications.Length > ushort.MaxValue) { return false; }
        writer.WriteUInt16(value: ((ushort)publications.Length));
        foreach (var publication in publications) {
            if (!TryWritePrincipal(writer, publication.Owner) || (publication.Sources.Length > ushort.MaxValue)) { return false; }
            writer.WriteInt32(value: publication.Body); writer.WriteInt32(value: publication.Generation);
            writer.WriteUInt32(value: publication.Revision); writer.WriteUInt64(value: publication.Epoch); writer.WriteUInt16(value: publication.Mapping);
            WriteIntent(writer, publication.Baseline);
            writer.WriteUInt16(value: ((ushort)publication.Sources.Length));
            foreach (var source in publication.Sources.Span) {
                WriteInputSource(writer, source.Source);
                writer.WriteInt64(value: source.RawValue); writer.WriteUInt64(value: source.Activation); writer.WriteBoolean(value: source.AwaitingNeutral);
            }
            WriteInputSwitch(writer, publication.Boundary);
            writer.WriteInt32(value: publication.SeatStates.Length);
            foreach (var cell in publication.SeatStates.Span) { writer.WriteString(value: cell.Family); writer.WriteString(value: cell.State); }
        }
        return true;
    }
    /// <summary>Reads bounded authority decisions, checking counts against remaining bytes before allocation.</summary>
    public static WorldInputPublication[] ReadInputPublications(ref WireReader reader) {
        var count = reader.ReadUInt16();

        if ((((long)count) * 339) > reader.Remaining) { reader.Fail(detail: "input publication count exceeds payload", refusal: WireRefusal.CountOutOfRange); return []; }
        var publications = ((count == 0) ? [] : new WorldInputPublication[count]);

        for (var index = 0; ((index < publications.Length) && !reader.Failed); index++) {
            var owner = ReadPrincipal(ref reader);
            var body = reader.ReadInt32(); var generation = reader.ReadInt32();
            var revision = reader.ReadUInt32(); var epoch = reader.ReadUInt64(); var mapping = reader.ReadUInt16();
            var baseline = ReadIntent(reader: ref reader);
            var sourceCount = reader.ReadUInt16();

            if ((revision == 0) || (epoch == 0) || (mapping >= WorldInputCatalog.MaximumTuples) || (((sourceCount * 30L) + 48) > reader.Remaining)) {
                reader.Fail(detail: "input publication metadata or source count is invalid", refusal: WireRefusal.PayloadMalformed); break;
            }
            var sources = ((sourceCount == 0) ? [] : new WorldInputSourceState[sourceCount]);

            for (var sourceIndex = 0; ((sourceIndex < sources.Length) && !reader.Failed); sourceIndex++) {
                sources[sourceIndex] = new(ReadInputSource(reader: ref reader), reader.ReadInt64(), reader.ReadUInt64(), reader.ReadBoolean());
            }
            var boundary = ReadInputSwitch(reader: ref reader);
            var states = reader.ReadArray("input seat states", static (ref WireReader r) =>
                new WorldInputSeatState(Family: r.ReadRequiredString("input seat family"), State: r.ReadRequiredString("input seat state")), WorldInputCatalog.MaximumLogicalChannels);

            publications[index] = new(Baseline: baseline, Body: body, Boundary: boundary, Epoch: epoch, Generation: generation, Mapping: mapping, Owner: owner, Revision: revision, SeatStates: states, Sources: sources);
        }
        return publications;
    }

    private const int ContributionBytes = 22;
    private const int InputEventBytes = 39;
    private const int MaximumInputBytes = (((37 + 256) + 48) + (ushort.MaxValue * (ContributionBytes + InputEventBytes)));

    /// <summary>Writes the one length-delimited semantic ingress leaf shared by transport, tape and checkpoint.</summary>
    public static bool TryWriteInputBatch(WireWriter writer, in WorldInputBatch batch) {
        if ((batch.NumericCount > ChannelLimits.MaxAxes) || (batch.Contributions.Length > ushort.MaxValue) || (batch.Events.Length > ushort.MaxValue)) { return false; }
        for (var axis = ((int)batch.NumericCount); (axis < ChannelLimits.MaxAxes); axis++) {
            if (batch.Image.Channels.Axes[axis] != 0) { return false; }
        }
        foreach (var source in batch.Contributions.Span) {
            if ((source.Kind > WorldInputContributionKind.Relative) || (source.Channel >= WorldInputCatalog.MaximumLogicalChannels)) { return false; }
        }
        foreach (var input in batch.Events.Span) {
            if ((input.Kind > WorldInputEventKind.Text) || (input.Channel >= WorldInputCatalog.MaximumLogicalChannels)) { return false; }
        }
        writer.WriteInt32(value: ((((37 + (batch.NumericCount * 8)) + (batch.Image.SourceRay.HasValue ? 48 : 0)) +
            (batch.Contributions.Length * ContributionBytes)) + (batch.Events.Length * InputEventBytes)));
        writer.WriteUInt32(value: batch.Revision);
        writer.WriteUInt64(value: batch.Epoch);
        writer.WriteUInt16(value: batch.Mapping);
        writer.WriteUInt64(value: batch.Tick);
        writer.WriteByte(value: batch.NumericCount);
        writer.WriteBoolean(value: batch.CaptureLost);
        WriteInputImage(writer, batch.Image, batch.NumericCount);
        writer.WriteUInt16(value: ((ushort)batch.Contributions.Length));
        foreach (var source in batch.Contributions.Span) {
            writer.WriteUInt16(value: source.Handle); writer.WriteUInt16(value: source.Channel); writer.WriteByte(value: ((byte)source.Kind));
            writer.WriteInt64(value: source.RawValue); writer.WriteUInt64(value: source.Activation);
            writer.WriteBoolean(value: source.NeutralObserved);
        }
        writer.WriteUInt16(value: ((ushort)batch.Events.Length));
        foreach (var input in batch.Events.Span) { WriteInputEvent(input: input, writer: writer); }
        return true;
    }
    /// <summary>Reads bounded ingress. Counts are checked against admitted limits and remaining bytes before allocation.</summary>
    public static WorldInputBatch ReadInputBatch(ref WireReader reader, int maximumSources, int maximumEvents) {
        var payload = reader.ReadBlock(field: "input batch", maxBytes: MaximumInputBytes);

        if (reader.Failed) { return default; }
        var leaf = new WireReader(bytes: payload);
        var revision = leaf.ReadUInt32();
        var epoch = leaf.ReadUInt64();
        var mapping = leaf.ReadUInt16();
        var tick = leaf.ReadUInt64();
        var numericCount = leaf.ReadByte();
        var captureLost = leaf.ReadBoolean();

        if (numericCount > ChannelLimits.MaxAxes) { leaf.Fail(detail: "input numeric extent exceeds 32", refusal: WireRefusal.CountOutOfRange); }
        var image = ReadInputImage(numericCount: numericCount, reader: ref leaf);
        var count = leaf.ReadUInt16();

        if ((count > maximumSources) || (((count * ContributionBytes) + 2) > leaf.Remaining)) {
            leaf.Fail(detail: "input source count exceeds admitted profile or payload", refusal: WireRefusal.CountOutOfRange);
        }
        var sources = ((leaf.Failed || (count == 0)) ? [] : new WorldInputContribution[count]);

        for (var index = 0; ((index < sources.Length) && !leaf.Failed); index++) {
            var handle = leaf.ReadUInt16(); var channel = leaf.ReadUInt16(); var kind = leaf.ReadByte();

            if ((kind > ((byte)WorldInputContributionKind.Relative)) || (channel >= WorldInputCatalog.MaximumLogicalChannels)) {
                leaf.Fail(detail: "input source kind or logical id is invalid", refusal: WireRefusal.PayloadMalformed);
            }
            sources[index] = new(handle, channel, ((WorldInputContributionKind)kind), leaf.ReadInt64(), leaf.ReadUInt64(), leaf.ReadBoolean());
        }
        var eventCount = leaf.ReadUInt16();

        if ((eventCount > maximumEvents) || ((eventCount * InputEventBytes) > leaf.Remaining)) {
            leaf.Fail(detail: "input event count exceeds document limit or payload", refusal: WireRefusal.CountOutOfRange);
        }
        var events = ((leaf.Failed || (eventCount == 0)) ? [] : new WorldInputEvent[eventCount]);

        for (var index = 0; ((index < events.Length) && !leaf.Failed); index++) {
            events[index] = ReadInputEvent(reader: ref leaf);
            if (events[index].Kind > WorldInputEventKind.Text) { leaf.Fail(detail: "derived input event cannot arrive over ingress", refusal: WireRefusal.EnumValueUnknown); }
        }
        if (!leaf.TryFinish(failure: out var failure)) { reader.Fail(failure.Refusal, failure.Detail); return default; }
        return new(CaptureLost: captureLost, Contributions: sources, Epoch: epoch, Events: events, Image: image, Mapping: mapping, NumericCount: numericCount, Revision: revision, Tick: tick);
    }
    /// <summary>Writes a semantic event, also used inside authority history records.</summary>
    public static void WriteInputEvent(WireWriter writer, WorldInputEvent input) {
        writer.WriteUInt16(value: input.Channel); writer.WriteByte(value: ((byte)input.Kind)); writer.WriteInt64(value: input.RawValue);
        writer.WriteUInt64(value: input.CaptureTick); writer.WriteInt64(value: input.Offset.Value); writer.WriteUInt32(value: input.Sequence); writer.WriteUInt64(value: input.Activation);
    }
    /// <summary>Reads one event. Authority-derived kinds are admitted here only for history leaves.</summary>
    public static WorldInputEvent ReadInputEvent(ref WireReader reader) {
        var channel = reader.ReadUInt16(); var kind = reader.ReadByte();

        if ((channel >= WorldInputCatalog.MaximumLogicalChannels) || (kind > ((byte)WorldInputEventKind.Cancel))) {
            reader.Fail(detail: "input event logical id or kind is invalid", refusal: WireRefusal.PayloadMalformed);
        }
        return new(channel, ((WorldInputEventKind)kind), reader.ReadInt64(), reader.ReadUInt64(),
            FixedQ4816.FromRawBits(value: reader.ReadInt64()), reader.ReadUInt32(), reader.ReadUInt64());
    }
}
