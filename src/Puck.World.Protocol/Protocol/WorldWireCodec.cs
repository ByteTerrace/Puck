using Puck.Commands;
using Puck.Maths;
using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>The shared byte layout for the leaves every binary codec over this project's types encodes identically:
/// the live submission wire (<see cref="WorldSubmissionCodec"/>), the persisted <c>.puckreplay</c> tape
/// (<c>Puck.World.WorldReplaySnapshot</c>), the authority checkpoint
/// (<c>Puck.World.Server.WorldAuthorityCheckpointCodec</c>), and the federation frames
/// (<c>Puck.World.Server.WorldFederationCodec</c>). Every one of them reads and writes through
/// <see cref="WireReader"/>/<see cref="WireWriter"/>. Sibling to <see cref="WorldWireTags"/>, which holds the
/// enum-to-byte tables these leaves cross on; this type holds the field order around them.</summary>
/// <remarks>A read never throws: an undeclared byte latches <see cref="WireRefusal.EnumValueUnknown"/> on the reader,
/// naming the type and the value, and the caller learns of it at <see cref="WireReader.TryFinish"/>. A write that
/// cannot represent its value writes nothing and returns <see langword="false"/>, so each codec raises its own
/// host-bug exception (a <c>WorldCodecRefusal</c> leaf failure, a tape codec exception, an invalid-operation throw)
/// in its own wording.</remarks>
public static class WorldWireCodec {
    private static void Undeclared(ref WireReader reader, string type, byte wire) => reader.Fail(
        detail: $"{type} wire value {wire} is not declared",
        refusal: WireRefusal.EnumValueUnknown
    );
    // One tag byte through its WorldWireTags table; an undeclared byte latches the reader's refusal by type name.
    private static T ReadTag<T>(ref WireReader reader, TryFromWire<T> fromWire) where T : struct, Enum {
        var wire = reader.ReadByte();

        if (!fromWire(
            wire,
            out var value
        )) {
            Undeclared(
                reader: ref reader,
                type: typeof(T).Name,
                wire: wire
            );
        }

        return value;
    }

    /// <summary>Reads a <see cref="WorldCapability"/> through its <see cref="WorldWireTags"/> byte.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The capability; an undeclared byte latches a refusal.</returns>
    public static WorldCapability ReadCapability(ref WireReader reader) => ReadTag<WorldCapability>(
        fromWire: WorldWireTags.TryFromWire,
        reader: ref reader
    );
    /// <summary>Reads an intent in <see cref="WriteIntent"/>'s layout: <see cref="ChannelLimits.MaxChannels"/> raw
    /// <see cref="FixedQ4816"/> lanes, one per ordinal, then the pointer-ray flag byte (<c>0</c> absent, <c>1</c>
    /// present) and, when present, the ray's origin and direction as six raw <see cref="FixedQ4816"/> values.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The decoded intent; every lane reads zero, and the ray absent, once a refusal has latched. An undeclared
    /// flag byte latches <see cref="WireRefusal.EnumValueUnknown"/>.</returns>
    public static PlayerIntent ReadIntent(ref WireReader reader) {
        var channels = default(ChannelValues);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            channels[ordinal] = reader.ReadFixed();
        }

        var flag = reader.ReadByte();
        SourceRay? ray = null;

        switch (flag) {
            case 0:
                break;
            case 1: {
                    var origin = reader.ReadFixedVector();
                    var direction = reader.ReadFixedVector();

                    ray = new SourceRay(
                        Direction: direction,
                        Origin: origin
                    );

                    break;
                }
            default:
                Undeclared(
                    reader: ref reader,
                    type: nameof(SourceRay),
                    wire: flag
                );

                break;
        }

        return new PlayerIntent(
            Channels: channels,
            SourceRay: (reader.Failed
                ? null
                : ray
            )
        );
    }
    /// <summary>Reads the intent-source union: one discriminant byte (<c>0</c> live, <c>1</c> idle, <c>2</c>
    /// producer), followed by the producer name for <c>2</c>. A producer name that reads back blank latches the
    /// reader's own required-string refusal and yields <see cref="IntentSource.Live"/>, so an untrusted frame never
    /// drives the closed union's own argument check.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="producerNameField">The field name the producer-name refusal narrates.</param>
    /// <returns>The source; an undeclared discriminant latches a refusal and yields <see cref="IntentSource.Live"/>.</returns>
    public static IntentSource ReadIntentSource(ref WireReader reader, string producerNameField = "intent source producer name") {
        var wire = reader.ReadByte();

        switch (wire) {
            case 0: return IntentSource.Live;
            case 1: return IntentSource.Idle;
            case 2: {
                    var name = reader.ReadRequiredString(field: producerNameField);

                    return (reader.Failed
                        ? IntentSource.Live
                        : IntentSource.Producer(name: name)
                    );
                }
            default:
                Undeclared(
                    reader: ref reader,
                    type: nameof(IntentSource),
                    wire: wire
                );

                return IntentSource.Live;
        }
    }
    /// <summary>Reads one <see cref="IntentSubmission"/>: tick, entity index, intent, principal, held channels, and
    /// the measured hold ticks.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The submission.</returns>
    public static IntentSubmission ReadIntentSubmission(ref WireReader reader) {
        var tick = reader.ReadUInt64();
        var entityIndex = reader.ReadInt32();
        var intent = ReadIntent(reader: ref reader);
        var principal = ReadPrincipal(reader: ref reader);
        var heldChannels = ReadIntent(reader: ref reader);
        var measuredHoldTicks = reader.ReadInt32();

        return new IntentSubmission(
            EntityIndex: entityIndex,
            HeldChannels: heldChannels,
            Intent: intent,
            MeasuredHoldTicks: measuredHoldTicks,
            Principal: principal,
            Tick: tick
        );
    }
    /// <summary>Reads a principal: the <see cref="WorldWireTags"/> kind byte, <c>Index</c>, <c>Generation</c>, then a
    /// present-flag-prefixed <c>Name</c>. On an undeclared kind byte only that byte is consumed and a refusal
    /// latches.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="nameField">The field name the <c>Name</c> refusal narrates.</param>
    /// <returns>The principal, or <see langword="default"/> once a refusal has latched.</returns>
    public static Principal ReadPrincipal(ref WireReader reader, string nameField = "principal name") {
        var kindWire = reader.ReadByte();

        if (!WorldWireTags.TryFromWire(
            value: out PrincipalKind kind,
            wire: kindWire
        )) {
            Undeclared(
                reader: ref reader,
                type: nameof(PrincipalKind),
                wire: kindWire
            );

            return default;
        }

        var index = reader.ReadInt32();
        var generation = reader.ReadInt32();
        var name = reader.ReadNullableString(field: nameField);

        return new Principal(
            Generation: generation,
            Index: index,
            Kind: kind,
            Name: name
        );
    }
    /// <summary>Reads a grantee: the <see cref="WorldWireTags"/> grantee-kind byte, then a principal
    /// (<see cref="ReadPrincipal"/>) for an actor or the group id for a group.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The grantee, or <see langword="default"/> once a refusal has latched.</returns>
    public static Grantee ReadGrantee(ref WireReader reader) {
        var kind = ReadTag<GranteeKind>(
            fromWire: WorldWireTags.TryFromWire,
            reader: ref reader
        );

        return (kind switch {
            GranteeKind.Group => Grantee.Group(id: reader.ReadString(field: "group id")),
            _ => ReadPrincipal(reader: ref reader),
        });
    }
    /// <summary>Reads a <see cref="WorldRebuildKind"/> through its <see cref="WorldWireTags"/> byte.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The rebuild kind; an undeclared byte latches a refusal.</returns>
    public static WorldRebuildKind ReadRebuildKind(ref WireReader reader) => ReadTag<WorldRebuildKind>(
        fromWire: WorldWireTags.TryFromWire,
        reader: ref reader
    );
    /// <summary>Reads a <see cref="WorldSection"/> through its <see cref="WorldWireTags"/> byte.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The section; an undeclared byte latches a refusal.</returns>
    public static WorldSection ReadSection(ref WireReader reader) => ReadTag<WorldSection>(
        fromWire: WorldWireTags.TryFromWire,
        reader: ref reader
    );
    /// <summary>Reads a <see cref="SnapPoseMode"/> through its <see cref="WorldWireTags"/> byte.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The snap mode; an undeclared byte latches a refusal.</returns>
    public static SnapPoseMode ReadSnapPoseMode(ref WireReader reader) => ReadTag<SnapPoseMode>(
        fromWire: WorldWireTags.TryFromWire,
        reader: ref reader
    );
    /// <summary>Reads a grant subject: the <see cref="WorldWireTags"/> kind byte, then the value — a
    /// <see cref="WorldSection"/> byte for a section subject, a 32-bit integer otherwise — then the present-flag-prefixed
    /// id.</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="idField">The field name the id refusal narrates.</param>
    /// <returns>The subject; an undeclared byte latches a refusal.</returns>
    public static GrantSubject ReadSubject(ref WireReader reader, string idField = "subject id") {
        var kind = ReadTag<GrantSubjectKind>(
            fromWire: WorldWireTags.TryFromWire,
            reader: ref reader
        );
        var value = ((kind == GrantSubjectKind.Section)
            ? ((int)ReadSection(reader: ref reader))
            : reader.ReadInt32()
        );
        var id = reader.ReadNullableString(field: idField);

        return new GrantSubject(
            Id: id,
            Kind: kind,
            Value: value
        );
    }
    /// <summary>Writes a <see cref="WorldCapability"/> as its <see cref="WorldWireTags"/> byte. Nothing is written
    /// when the capability has no wire value.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="capability">The capability.</param>
    /// <returns><see langword="true"/> when <paramref name="capability"/> has a wire value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWriteCapability(WireWriter writer, WorldCapability capability) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (!WorldWireTags.TryToWire(
            value: capability,
            wire: out var wire
        )) {
            return false;
        }

        writer.WriteByte(value: wire);

        return true;
    }
    /// <summary>Writes the intent-source union. Nothing is written when the source names no declared shape.</summary>
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
    /// <summary>Writes one <see cref="IntentSubmission"/> in <see cref="ReadIntentSubmission"/>'s order. Nothing past
    /// the entity index is meaningful when the principal has no wire value, so the caller discards the writer on
    /// <see langword="false"/>.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="submission">The submission.</param>
    /// <returns><see langword="true"/> when the submission's principal has a wire value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWriteIntentSubmission(WireWriter writer, in IntentSubmission submission) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteUInt64(value: submission.Tick);
        writer.WriteInt32(value: submission.EntityIndex);
        WriteIntent(
            intent: submission.Intent,
            writer: writer
        );

        if (!TryWritePrincipal(
            principal: submission.Principal,
            writer: writer
        )) {
            return false;
        }

        WriteIntent(
            intent: submission.HeldChannels,
            writer: writer
        );
        writer.WriteInt32(value: submission.MeasuredHoldTicks);

        return true;
    }
    /// <summary>Writes a principal. Nothing is written when the kind has no wire value. A caller imposing a shape
    /// ruling beyond the kind table checks it before this call.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="principal">The principal to write.</param>
    /// <returns><see langword="true"/> when <paramref name="principal"/>'s kind has a wire value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWritePrincipal(WireWriter writer, Principal principal) {
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
    /// <summary>Writes a grantee in <see cref="ReadGrantee"/>'s layout. Nothing is written when the grantee's kind, or
    /// an actor's principal kind, has no live wire value.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="grantee">The grantee to write.</param>
    /// <returns><see langword="true"/> when <paramref name="grantee"/> has a live wire shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWriteGrantee(WireWriter writer, Grantee grantee) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (
            !WorldWireTags.TryToWire(
            value: grantee.Kind,
            wire: out var kindWire
        ) ||
            ((grantee.Kind == GranteeKind.Principal) && !WorldWireTags.TryToWire(
            value: grantee.Principal.Kind,
            wire: out _
        ))
        ) {
            return false;
        }

        writer.WriteByte(value: kindWire);

        if (grantee.Kind == GranteeKind.Group) {
            writer.WriteString(value: (grantee.Name ?? string.Empty));

            return true;
        }

        return TryWritePrincipal(
            principal: grantee.Principal,
            writer: writer
        );
    }
    /// <summary>Writes a grant subject in <see cref="ReadSubject"/>'s layout. Nothing is written when the kind, or a
    /// section subject's section, has no wire value.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="subject">The subject to write.</param>
    /// <returns><see langword="true"/> when <paramref name="subject"/> has a wire shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static bool TryWriteSubject(WireWriter writer, GrantSubject subject) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        if (!WorldWireTags.TryToWire(
            value: subject.Kind,
            wire: out var kindWire
        )) {
            return false;
        }

        var sectionWire = default(byte);

        if (
            (subject.Kind == GrantSubjectKind.Section) &&
            !WorldWireTags.TryToWire(
                value: ((WorldSection)subject.Value),
                wire: out sectionWire
            )
        ) {
            return false;
        }

        writer.WriteByte(value: kindWire);

        if (subject.Kind == GrantSubjectKind.Section) {
            writer.WriteByte(value: sectionWire);
        } else {
            writer.WriteInt32(value: subject.Value);
        }

        writer.WriteNullableString(value: subject.Id);

        return true;
    }
    /// <summary>Writes an intent: the whole channel vector as <see cref="ChannelLimits.MaxChannels"/> raw
    /// <see cref="FixedQ4816"/> lanes, one per ordinal, unconditionally, then one flag byte for the pointer ray and,
    /// only when the ray is present, its origin and direction as six raw <see cref="FixedQ4816"/> values, so an absent
    /// ray costs one byte. The vector's capacity is what is wire-shaped, not a document's declared channel count, so no
    /// codec needs the world's channel table to decode. Every intent path shares this layout: submission, held
    /// channels, authority checkpoints, federation, and the replay tape.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="intent">The intent to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    public static void WriteIntent(WireWriter writer, PlayerIntent intent) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            writer.WriteFixed(value: intent[ordinal]);
        }

        if (intent.SourceRay is not { } ray) {
            writer.WriteByte(value: 0);

            return;
        }

        writer.WriteByte(value: 1);
        writer.WriteFixedVector(value: ray.Origin);
        writer.WriteFixedVector(value: ray.Direction);
    }

    private delegate bool TryFromWire<T>(byte wire, out T value);
}
