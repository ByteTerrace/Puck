using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldFederationCodec {
    /// <summary>Encodes a destination's hop confirmation question: the hop's key, the decision it belongs to and the
    /// companions it asks about.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnations">The companions.</param>
    /// <param name="phase">The decision the question belongs to.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="incarnations"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeHopQuery(WorldTransferKey key, IReadOnlyList<WorldEntityAddress> incarnations, WorldHopPhase phase) {
        ArgumentNullException.ThrowIfNull(argument: incarnations);

        var writer = new WireWriter();

        WorldWireLeaves.WriteTransferKey(writer: writer, key: key);
        writer.WriteByte(value: (byte)phase);
        writer.WriteArray(
            items: incarnations,
            writeItem: WorldWireLeaves.WriteEntityAddress
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeHopQuery"/>.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnations">The companions.</param>
    /// <param name="phase">The decision the question belongs to.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeHopQuery(ReadOnlySpan<byte> body, out WorldTransferKey key, out WorldEntityAddress[] incarnations, out WorldHopPhase phase, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
        phase = (WorldHopPhase)reader.ReadByte();
        if (!Enum.IsDefined(value: phase)) {
            incarnations = [];
            failure = new WireFailure(Detail: $"hop query phase {(byte)phase} is undefined", Refusal: WireRefusal.PayloadMalformed);
            return false;
        }
        incarnations = reader.ReadArray(
            field: "hop query companions",
            readItem: static (ref WireReader r) => WorldWireLeaves.ReadEntityAddress(reader: ref r),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Encodes home's answers to a hop confirmation question, one per companion.</summary>
    /// <param name="answers">The landing epoch of each current grant, or <see langword="null"/>.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="answers"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeHopQueryReply(IReadOnlyList<ulong?> answers) {
        ArgumentNullException.ThrowIfNull(argument: answers);

        var writer = new WireWriter();

        writer.WriteArray(
            items: answers,
            writeItem: static (w, next) => w.WriteOptional(
                value: next,
                writeValue: static (n, value) => n.WriteUInt64(value: value)
            )
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeHopQueryReply"/>.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="answers">The answers.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeHopQueryReply(ReadOnlySpan<byte> body, out ulong?[] answers, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        answers = reader.ReadArray(
            field: "hop query answers",
            readItem: static (ref WireReader r) => r.ReadOptional(readValue: static (ref WireReader n) => n.ReadUInt64()),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Encodes a custodian's request to move companions onward: the crossing's key, its destination, and each
    /// companion with the epoch the custodian holds it at.</summary>
    /// <param name="key">The crossing's key.</param>
    /// <param name="destination">Where the crossing goes.</param>
    /// <param name="members">The companions.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> or <paramref name="members"/> is
    /// <see langword="null"/>.</exception>
    public static byte[] EncodeHopRequest(WorldTransferKey key, string destination, IReadOnlyList<WorldHopRequestMember> members) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        ArgumentNullException.ThrowIfNull(argument: members);

        var writer = new WireWriter();

        WorldWireLeaves.WriteTransferKey(writer: writer, key: key);
        writer.WriteString(value: destination);
        writer.WriteArray(
            items: members,
            writeItem: static (w, member) => {
                WorldWireLeaves.WriteEntityAddress(
                    address: member.Incarnation,
                    writer: w
                );
                w.WriteUInt64(value: member.Epoch);
            }
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeHopRequest"/>.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="key">The crossing's key.</param>
    /// <param name="destination">Where the crossing goes.</param>
    /// <param name="members">The companions.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeHopRequest(ReadOnlySpan<byte> body, out WorldTransferKey key, out string destination, out WorldHopRequestMember[] members, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
        destination = reader.ReadRequiredString(field: "hop destination");
        members = reader.ReadArray(
            field: "hop request companions",
            readItem: static (ref WireReader r) => new WorldHopRequestMember(
                Incarnation: WorldWireLeaves.ReadEntityAddress(reader: ref r),
                Epoch: r.ReadUInt64()
            ),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Encodes home's answers to a hop request, one per companion.</summary>
    /// <param name="answers">The answers.</param>
    /// <returns>The encoded leaf.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="answers"/> is <see langword="null"/>.</exception>
    public static byte[] EncodeHopAnswer(IReadOnlyList<WorldHopAnswer> answers) {
        ArgumentNullException.ThrowIfNull(argument: answers);

        var writer = new WireWriter();

        writer.WriteArray(
            items: answers,
            writeItem: static (w, answer) => {
                WorldWireLeaves.WriteEntityAddress(
                    address: answer.Incarnation,
                    writer: w
                );
                w.WriteByte(value: (byte)answer.Verdict);
                w.WriteUInt64(value: answer.Next);
            }
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeHopAnswer"/>, refusing an undeclared verdict.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="answers">The answers.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeHopAnswer(ReadOnlySpan<byte> body, out WorldHopAnswer[] answers, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        answers = reader.ReadArray(
            field: "hop answers",
            readItem: static (ref WireReader r) => {
                var incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref r);
                var verdict = r.ReadByte();

                if (!r.Failed && !Enum.IsDefined(value: (WorldHopVerdict)verdict)) {
                    r.Fail(
                        detail: $"hop verdict {verdict} is not declared",
                        refusal: WireRefusal.EnumValueUnknown
                    );
                }
                return new WorldHopAnswer(
                    Incarnation: incarnation,
                    Next: r.ReadUInt64(),
                    Verdict: (WorldHopVerdict)verdict
                );
            },
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Encodes one companion's hop: the hop's key and the companion, the leaf a custody query and a settlement
    /// notice share.</summary>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeCompanionHop(WorldTransferKey key, WorldEntityAddress incarnation) {
        var writer = new WireWriter();

        WorldWireLeaves.WriteTransferKey(writer: writer, key: key);
        WorldWireLeaves.WriteEntityAddress(
            address: incarnation,
            writer: writer
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeCompanionHop"/>.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="key">The hop's key.</param>
    /// <param name="incarnation">The companion.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly.</returns>
    public static bool TryDecodeCompanionHop(ReadOnlySpan<byte> body, out WorldTransferKey key, out WorldEntityAddress incarnation, out WireFailure failure) {
        var reader = new WireReader(bytes: body);

        key = WorldWireLeaves.ReadTransferKey(reader: ref reader);
        incarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        return Finish(
            failure: out failure,
            reader: ref reader
        );
    }
    /// <summary>Encodes a transfer's status and, for a committed transfer, the request-local ids it landed.</summary>
    /// <param name="status">The status.</param>
    /// <param name="landed">The landed ids, or <see langword="null"/>.</param>
    /// <returns>The encoded leaf.</returns>
    public static byte[] EncodeStatus(WorldTransferStatus status, IReadOnlyList<int>? landed) {
        var writer = new WireWriter();

        writer.WriteByte(value: (byte)status);
        writer.WriteOptionalClass(
            value: landed,
            writeValue: static (w, ids) => w.WriteArray(
                items: ids,
                writeItem: static (i, id) => i.WriteInt32(value: id)
            )
        );
        return writer.ToArray();
    }
    /// <summary>Decodes a leaf written by <see cref="EncodeStatus"/>.</summary>
    /// <param name="body">The leaf bytes.</param>
    /// <param name="status">The status.</param>
    /// <param name="landed">The landed ids, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the leaf decoded exactly to a declared status.</returns>
    public static bool TryDecodeStatus(ReadOnlySpan<byte> body, out WorldTransferStatus status, out int[]? landed) {
        var reader = new WireReader(bytes: body);
        var raw = reader.ReadByte();

        landed = reader.ReadOptionalClass(readValue: static (ref WireReader r) => r.ReadArray(
            field: "status landed members",
            readItem: static (ref WireReader i) => i.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        ));
        status = (WorldTransferStatus)raw;
        return reader.TryFinish(failure: out _) && Enum.IsDefined(value: status);
    }
}
