using Puck.Maths;
using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>The shared byte layout for the leaves every binary codec over this project's types encodes identically:
/// the live submission wire (<see cref="WorldSubmissionCodec"/>), the persisted <c>.puckreplay</c> tape
/// (<c>Puck.World.WorldReplaySnapshot</c>), the authority checkpoint
/// (<c>Puck.World.Server.WorldAuthorityCheckpointCodec</c>), and the federation frames
/// (<c>Puck.World.Server.WorldFederationCodec</c>). Sibling to <see cref="WorldWireTags"/>, which pins the
/// enum-to-byte tables these leaves cross on; this type pins the field order around them. Frozen: changing a layout
/// here invalidates every saved tape, every checkpoint, and every in-flight envelope.</summary>
/// <remarks>Each layout carries two overloads — one over <see cref="BinaryReader"/>/<see cref="BinaryWriter"/>,
/// one over <see cref="WireReader"/>/<see cref="WireWriter"/> — because the framing around them differs while
/// the bytes do not: <see cref="WireWriter.WriteFixed"/> emits the same raw <see cref="long"/> lane
/// <see cref="BinaryWriter.Write(long)"/> does, and both string forms carry the same present flag. Decodes that can
/// meet an undeclared byte are <c>Try</c>-shaped rather than throwing, and writers that can refuse write nothing
/// before returning <see langword="false"/> — each codec raises its own refusal (a <c>WorldCodecRefusal</c> leaf
/// failure on the wire, a tape exception on the tape, a <c>WireReader.Fail</c> narration on a wire frame) at the call
/// site, so no caller inherits another's wording.</remarks>
public static class WorldWireCodec {
    /// <summary>Reads the whole channel vector: <see cref="ChannelLimits.MaxChannels"/> raw <see cref="FixedQ4816"/>
    /// lanes, one per ordinal, unconditionally.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The decoded intent.</returns>
    public static PlayerIntent ReadIntent(BinaryReader reader) {
        var channels = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            channels[ordinal] = new FixedQ4816(Value: reader.ReadInt64());
        }

        return new PlayerIntent(Channels: channels);
    }
    /// <summary>Writes the whole channel vector: <see cref="ChannelLimits.MaxChannels"/> raw
    /// <see cref="FixedQ4816"/> lanes, one per ordinal, unconditionally. The vector's capacity is what is wire-shaped,
    /// not a document's declared channel count, so neither codec needs the world's channel table to decode.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="intent">The intent to write.</param>
    public static void WriteIntent(BinaryWriter writer, PlayerIntent intent) {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            writer.Write(value: intent[ordinal].Value);
        }
    }
    /// <summary>Reads a present-flag-prefixed string.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The string, or <see langword="null"/> when the present flag is clear.</returns>
    public static string? ReadNullableString(BinaryReader reader) => (reader.ReadBoolean()
        ? reader.ReadString()
        : null
    );
    /// <summary>Writes a present-flag-prefixed string: the flag alone for <see langword="null"/>, the flag then the
    /// string otherwise.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The string, or <see langword="null"/>.</param>
    public static void WriteNullableString(BinaryWriter writer, string? value) {
        writer.Write(value: (value is not null));

        if (value is not null) {
            writer.Write(value: value);
        }
    }
    /// <summary>Reads the intent-source union: one discriminant byte (<c>0</c> live, <c>1</c> idle, <c>2</c>
    /// producer), followed by the producer name for <c>2</c>.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="source">The decoded source, on success.</param>
    /// <param name="wire">The discriminant byte read, declared or not; the caller's refusal names it.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a declared source.</returns>
    public static bool TryReadIntentSource(BinaryReader reader, out IntentSource source, out byte wire) {
        wire = reader.ReadByte();

        switch (wire) {
            case 0: source = IntentSource.Live; return true;
            case 1: source = IntentSource.Idle; return true;
            case 2: source = IntentSource.Producer(name: reader.ReadString()); return true;
            default: source = default; return false;
        }
    }
    /// <summary>Writes the intent-source union. Nothing is written when the source names no declared shape.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="source">The source to write.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> has a wire shape.</returns>
    public static bool TryWriteIntentSource(BinaryWriter writer, IntentSource source) {
        if (source.IsLive) {
            writer.Write(value: ((byte)0));

            return true;
        }

        if (source.IsIdle) {
            writer.Write(value: ((byte)1));

            return true;
        }

        if (source.ProducerName is { } name) {
            writer.Write(value: ((byte)2));
            writer.Write(value: name);

            return true;
        }

        return false;
    }
    /// <summary>Reads a principal: the <see cref="WorldWireTags"/> kind byte, <c>Index</c>, <c>Generation</c>, then a
    /// present-flag-prefixed <c>Name</c>. On an undeclared kind byte only that byte is consumed.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="principal">The decoded principal, on success.</param>
    /// <param name="kindWire">The kind byte read, declared or not; the caller's refusal names it.</param>
    /// <returns><see langword="true"/> when <paramref name="kindWire"/> names a declared kind.</returns>
    public static bool TryReadPrincipal(BinaryReader reader, out WorldPrincipal principal, out byte kindWire) {
        kindWire = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out PrincipalKind kind,
            wire: kindWire
        )) {
            principal = default;

            return false;
        }

        principal = new WorldPrincipal(
            Kind: kind,
            Index: reader.ReadInt32(),
            Generation: reader.ReadInt32(),
            Name: ReadNullableString(reader: reader)
        );

        return true;
    }
    /// <summary>Writes a principal. Nothing is written when the kind has no wire value. A caller imposing a shape
    /// ruling beyond the kind table checks it before this call.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="principal">The principal to write.</param>
    /// <returns><see langword="true"/> when <paramref name="principal"/>'s kind has a wire value.</returns>
    public static bool TryWritePrincipal(BinaryWriter writer, WorldPrincipal principal) {
        if (!WorldWireTags.TryToWire(
            value: principal.Kind,
            wire: out var kindWire
        )) {
            return false;
        }

        writer.Write(value: kindWire);
        writer.Write(value: principal.Index);
        writer.Write(value: principal.Generation);
        WriteNullableString(
            value: principal.Name,
            writer: writer
        );

        return true;
    }
    /// <summary>Reads the whole channel vector off a wire frame: <see cref="ChannelLimits.MaxChannels"/> raw
    /// <see cref="FixedQ4816"/> lanes, one per ordinal, unconditionally.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The decoded intent; every lane reads zero once a refusal has latched.</returns>
    public static PlayerIntent ReadIntent(ref WireReader reader) {
        var channels = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            channels[ordinal] = reader.ReadFixed();
        }

        return new PlayerIntent(Channels: channels);
    }
    /// <summary>Writes the whole channel vector onto a wire frame: <see cref="ChannelLimits.MaxChannels"/> raw
    /// <see cref="FixedQ4816"/> lanes, one per ordinal, unconditionally.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="intent">The intent to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void WriteIntent(WireWriter writer, PlayerIntent intent) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            writer.WriteFixed(value: intent[ordinal]);
        }
    }
    /// <summary>Reads the intent-source union off a wire frame: one discriminant byte (<c>0</c> live, <c>1</c> idle,
    /// <c>2</c> producer), followed by the producer name for <c>2</c>. A producer name that reads back blank latches
    /// the reader's own required-string refusal and yields <see cref="IntentSource.Live"/>, so an untrusted frame
    /// never drives the closed union's own argument check.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="producerNameField">The field name the producer-name refusal narrates.</param>
    /// <param name="source">The decoded source, on success.</param>
    /// <param name="wire">The discriminant byte read, declared or not; the caller's refusal names it.</param>
    /// <returns><see langword="true"/> when <paramref name="wire"/> names a declared source.</returns>
    public static bool TryReadIntentSource(ref WireReader reader, string producerNameField, out IntentSource source, out byte wire) {
        wire = reader.ReadByte();

        switch (wire) {
            case 0: source = IntentSource.Live; return true;
            case 1: source = IntentSource.Idle; return true;
            case 2: {
                    var name = reader.ReadRequiredString(field: producerNameField);

                    source = (reader.Failed
                        ? IntentSource.Live
                        : IntentSource.Producer(name: name)
                    );

                    return true;
                }
            default: source = default; return false;
        }
    }
    /// <summary>Writes the intent-source union onto a wire frame. Nothing is written when the source names no
    /// declared shape.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="source">The source to write.</param>
    /// <returns><see langword="true"/> when <paramref name="source"/> has a wire shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWriteIntentSource(WireWriter writer, IntentSource source) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (source.IsLive) {
            writer.WriteByte(value: 0);

            return true;
        }

        if (source.IsIdle) {
            writer.WriteByte(value: 1);

            return true;
        }

        if (source.ProducerName is { } name) {
            writer.WriteByte(value: 2);
            writer.WriteString(value: name);

            return true;
        }

        return false;
    }
    /// <summary>Reads a principal off a wire frame: the <see cref="WorldWireTags"/> kind byte, <c>Index</c>,
    /// <c>Generation</c>, then a present-flag-prefixed <c>Name</c>. On an undeclared kind byte only that byte is
    /// consumed.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="nameField">The field name the <c>Name</c> refusal narrates.</param>
    /// <param name="principal">The decoded principal, on success.</param>
    /// <param name="kindWire">The kind byte read, declared or not; the caller's refusal names it.</param>
    /// <returns><see langword="true"/> when <paramref name="kindWire"/> names a declared kind.</returns>
    public static bool TryReadPrincipal(ref WireReader reader, string nameField, out WorldPrincipal principal, out byte kindWire) {
        kindWire = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out PrincipalKind kind,
            wire: kindWire
        )) {
            principal = default;

            return false;
        }

        var index = reader.ReadInt32();
        var generation = reader.ReadInt32();
        var name = reader.ReadNullableString(field: nameField);

        principal = new WorldPrincipal(
            Generation: generation,
            Index: index,
            Kind: kind,
            Name: name
        );

        return true;
    }
    /// <summary>Writes a principal onto a wire frame. Nothing is written when the kind has no wire value. A caller
    /// imposing a shape ruling beyond the kind table checks it before this call.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="principal">The principal to write.</param>
    /// <returns><see langword="true"/> when <paramref name="principal"/>'s kind has a wire value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWritePrincipal(WireWriter writer, WorldPrincipal principal) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (!WorldWireTags.TryToWire(
            value: principal.Kind,
            wire: out var kindWire
        )) {
            return false;
        }

        writer.WriteByte(value: kindWire);
        writer.WriteInt32(value: principal.Index);
        writer.WriteInt32(value: principal.Generation);
        writer.WriteNullableString(value: principal.Name);

        return true;
    }
}
