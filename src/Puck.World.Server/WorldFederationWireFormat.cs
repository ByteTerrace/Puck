using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The authority-to-authority wire used for projection and transfer. Local colocation invokes the same
/// server methods directly; this codec is only the transport underneath that contract.</summary>
public static class WorldFederationWireFormat {
    /// <summary>A protocol discriminator distinct from the interactive peer wire key.</summary>
    public const ulong WireKey = 0x35444546554B4350UL; // "PCKUFED5", little endian on the wire.
    /// <summary>The maximum framed federation payload.</summary>
    public const int MaxFrameBytes = (32 * 1024 * 1024);

    /// <summary>Federation request kinds.</summary>
    public enum RequestKind : byte { Observe = 1, Reserve = 2, Commit = 3, Abort = 4, Submission = 5, Intent = 6, Authenticate = 7, Status = 8, Route = 9, IntentStream = 10, IntentStreamHandoff = 11, AcknowledgeTransfer = 12 }
    /// <summary>Federation response/event kinds.</summary>
    public enum ResponseKind : byte { Definition = 1, Snapshot = 2, Reservation = 3, Commit = 4, Ack = 5, Refusal = 6, Completion = 7, Challenge = 8, Status = 9, Route = 10 }

    /// <summary>Encodes a committed-transfer credential without a gameplay payload.</summary>
    public static byte[] EncodeRouteCredential(string sourceAuthority, in WorldMobilityIdentity mobility) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); WriteMobility(writer: writer, value: mobility);
        return output.ToArray();
    }

    public static bool TryDecodeRouteCredential(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); mobility = ReadMobility(reader: reader);
            return !string.IsNullOrWhiteSpace(value: sourceAuthority) && (input.Position == input.Length);
        } catch (Exception exception) when (exception is IOException or FormatException) {
            sourceAuthority = string.Empty; mobility = default; return false;
        }
    }

    /// <summary>Encodes the final observable authority epoch for one traveler.</summary>
    public static byte[] EncodeRoute(in WorldAuthorityRouteDescription route) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(route.Endpoint);
        writer.Write(route.Entity.Authority);
        writer.Write(route.Entity.Index);
        writer.Write(route.Entity.Generation);
        writer.Write(route.Tick);
        WriteFixedVector(writer: writer, value: route.Position);
        writer.Write(route.Orientation.X.Value);
        writer.Write(route.Orientation.Y.Value);
        writer.Write(route.Orientation.Z.Value);
        writer.Write(route.Orientation.W.Value);
        WriteVector(writer: writer, value: route.BodyColor);
        writer.Write(route.Kit);
        writer.Write(route.Look);
        writer.Write(route.CatalogRig);
        writer.Write(route.PlacementId is not null);
        if (route.PlacementId is { } placementId) {
            writer.Write(placementId);
        }
        var definition = EncodeDefinition(definition: route.Definition);
        writer.Write(definition.Length);
        writer.Write(definition);
        return output.ToArray();
    }

    public static bool TryDecodeRoute(ReadOnlySpan<byte> body, out WorldAuthorityRouteDescription route) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            var endpoint = reader.ReadString();
            var authority = reader.ReadString();
            var bodyIndex = reader.ReadInt32();
            var generation = reader.ReadInt32();
            var tick = reader.ReadUInt64();
            var position = ReadFixedVector(reader: reader);
            var orientation = new FixedQuaternion(
                X: new FixedQ4816(reader.ReadInt64()),
                Y: new FixedQ4816(reader.ReadInt64()),
                Z: new FixedQ4816(reader.ReadInt64()),
                W: new FixedQ4816(reader.ReadInt64()));
            var bodyColor = ReadVector(reader: reader);
            var kit = reader.ReadByte();
            var look = reader.ReadByte();
            var catalogRig = reader.ReadByte();
            var placementId = (reader.ReadBoolean() ? reader.ReadString() : null);
            var definitionLength = reader.ReadInt32();
            if ((definitionLength <= 0) || (definitionLength > MaxFrameBytes)) {
                throw new FormatException();
            }
            var definitionBytes = reader.ReadBytes(definitionLength);
            if (definitionBytes.Length != definitionLength) {
                throw new FormatException();
            }
            var definition = DecodeDefinition(body: definitionBytes);
            route = new WorldAuthorityRouteDescription(
                Endpoint: endpoint,
                Entity: new WorldEntityAddress(Authority: authority, Index: bodyIndex, Generation: generation),
                Tick: tick,
                Position: position,
                Orientation: orientation,
                BodyColor: bodyColor,
                Kit: kit,
                Look: look,
                CatalogRig: catalogRig,
                PlacementId: placementId,
                Definition: definition);
            return !string.IsNullOrWhiteSpace(value: endpoint) && !string.IsNullOrWhiteSpace(value: authority) &&
                ((uint)bodyIndex < (uint)WorldPopulationLimits.CapacityCeiling) && (generation >= 0) &&
                ((uint)kit < (uint)definition.Kits.Count) && (look < Math.Max(val1: definition.Looks.Count, val2: 1)) &&
                (catalogRig < WorldLookSource.Catalog.RigCount) &&
                float.IsFinite(bodyColor.X) && float.IsFinite(bodyColor.Y) && float.IsFinite(bodyColor.Z) && (input.Position == input.Length);
        } catch (Exception exception) when (exception is IOException or FormatException or JsonException or ArgumentException) {
            route = default; return false;
        }
    }

    /// <summary>Writes the federation discriminator.</summary>
    public static async Task WriteHelloAsync(Stream stream, CancellationToken ct) {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes, value: WireKey);
        await stream.WriteAsync(buffer: bytes, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Encodes the source identity and challenge proof sent before every federation operation.</summary>
    public static byte[] EncodeAuthentication(string sourceAuthority, byte[] proof) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); writer.Write(proof.Length); writer.Write(proof);
        return output.ToArray();
    }

    public static bool TryDecodeAuthentication(ReadOnlySpan<byte> body, out string sourceAuthority, out byte[] proof) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); var length = reader.ReadInt32();
            if (string.IsNullOrWhiteSpace(value: sourceAuthority) || (length != WorldFederationSecurity.ProofBytes)) { throw new FormatException(); }
            proof = reader.ReadBytes(length);
            return (proof.Length == length) && (input.Position == input.Length);
        } catch (Exception exception) when (exception is IOException or FormatException) {
            sourceAuthority = string.Empty; proof = []; return false;
        }
    }

    /// <summary>Writes one framed request.</summary>
    public static Task WriteRequestAsync(Stream stream, RequestKind kind, byte[] body, CancellationToken ct) => WriteFrameAsync(stream: stream, kind: (byte)kind, body: body, ct: ct);
    /// <summary>Writes one framed response.</summary>
    public static Task WriteResponseAsync(Stream stream, ResponseKind kind, byte[] body, CancellationToken ct) => WriteFrameAsync(stream: stream, kind: (byte)kind, body: body, ct: ct);

    /// <summary>Reads one framed kind/body pair.</summary>
    public static async Task<(byte Kind, byte[] Body)?> ReadFrameAsync(Stream stream, CancellationToken ct) {
        var prefix = new byte[sizeof(uint)];

        if (!await WorldTcpWireFormat.TryReadExactAsync(stream: stream, buffer: prefix, ct: ct).ConfigureAwait(false)) {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(source: prefix);

        if ((length < 1U) || (length > MaxFrameBytes)) {
            return null;
        }

        var frame = new byte[length];

        if (!await WorldTcpWireFormat.TryReadExactAsync(stream: stream, buffer: frame, ct: ct).ConfigureAwait(false)) {
            return null;
        }

        return (frame[0], frame[1..]);
    }

    /// <summary>Encodes a reservation including each traveler's attested owned-world document.</summary>
    public static byte[] EncodeReservation(WorldTransferReservationRequest request) {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(request.TransferId);
        writer.Write(request.SourceAuthority);
        writer.Write(request.SourceRateHz);
        writer.Write(request.SourceTick);
        writer.Write(request.DeadlineSourceTick);
        writer.Write(request.Border);
        writer.Write(request.BorderCapacity.HasValue);
        if (request.BorderCapacity is { } capacity) {
            writer.Write(capacity);
        }
        writer.Write(request.PartyAllOrNothing);
        writer.Write(true); // A wire reservation always requests entity-table peer admission.
        writer.Write(request.Members.Count);

        foreach (var member in request.Members) {
            writer.Write(member.PreferredSlot);
            if (member.Mobility is not { } mobility) {
                throw new InvalidOperationException(message: "federated reservation traveler has no mobility identity");
            }
            WriteEntityAddress(writer: writer, value: mobility.Incarnation);
            writer.Write(mobility.Epoch);
            WriteIntentSource(writer: writer, source: member.Source);
            WriteVector(writer: writer, value: member.BodyColor);
            writer.Write(member.CatalogRig);
            var document = member.Identity?.Document;
            var identityBytes = (document is null ? [] : WorldDefinitionSerialization.Serialize(definition: document));
            writer.Write(identityBytes.Length);
            writer.Write(identityBytes);
        }

        return output.ToArray();
    }

    /// <summary>Decodes an untrusted reservation, rebuilding identities through the canonical document codec.</summary>
    public static bool TryDecodeReservation(ReadOnlySpan<byte> body, WorldPlayerDefaults defaults, out WorldTransferReservationRequest? request, out string reason) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false);
            using var reader = new BinaryReader(input);
            var transferId = reader.ReadUInt64();
            var sourceAuthority = reader.ReadString();
            var sourceRate = reader.ReadInt32();
            var sourceTick = reader.ReadUInt64();
            var deadline = reader.ReadUInt64();
            var border = reader.ReadString();
            int? capacity = (reader.ReadBoolean() ? reader.ReadInt32() : null);
            var party = reader.ReadBoolean();
            var remote = reader.ReadBoolean();
            var count = reader.ReadInt32();

            if ((count < 1) || (count > WorldPopulationLimits.CapacityCeiling)) {
                throw new FormatException($"traveler count {count} is outside 1..{WorldPopulationLimits.CapacityCeiling}");
            }

            var members = new WorldTransferReservationMember[count];
            for (var index = 0; index < count; index++) {
                var preferred = reader.ReadInt32();
                var mobility = new WorldMobilityIdentity(Incarnation: ReadEntityAddress(reader: reader), Epoch: reader.ReadUInt64());
                if (string.IsNullOrWhiteSpace(value: mobility.Incarnation.Authority) || (mobility.Incarnation.Index < 0) || (mobility.Incarnation.Generation <= 0)) {
                    throw new FormatException($"traveler {index + 1} mobility incarnation is invalid");
                }
                var source = ReadIntentSource(reader: reader);
                var bodyColor = ReadVector(reader: reader);
                if (!float.IsFinite(bodyColor.X) || !float.IsFinite(bodyColor.Y) || !float.IsFinite(bodyColor.Z)) {
                    throw new FormatException($"traveler {index + 1} body color is not finite");
                }
                var catalogRig = reader.ReadByte();
                var byteCount = reader.ReadInt32();
                if ((byteCount < 0) || (byteCount > (16 * 1024 * 1024))) {
                    throw new FormatException($"traveler {index + 1} identity document length {byteCount} is invalid");
                }
                var bytes = reader.ReadBytes(byteCount);
                if (bytes.Length != byteCount) {
                    throw new EndOfStreamException();
                }
                WorldIdentity? identity = null;
                if (byteCount > 0) {
                    var definition = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);
                    identity = new WorldIdentity(document: definition, defaults: defaults);
                }
                members[index] = new WorldTransferReservationMember(Principal: WorldPrincipal.Console, PreferredSlot: preferred, Identity: identity, Source: source, BodyColor: bodyColor, CatalogRig: catalogRig, Mobility: mobility);
            }

            if (input.Position != input.Length) {
                throw new FormatException("reservation carries trailing bytes");
            }
            request = new WorldTransferReservationRequest(transferId, sourceAuthority, sourceRate, sourceTick, deadline, border, capacity, party, remote, members);
            reason = string.Empty;
            return true;
        } catch (Exception exception) when (exception is IOException or FormatException or InvalidOperationException or ArgumentException) {
            request = null;
            reason = exception.Message.ReplaceLineEndings(" ");
            return false;
        }
    }

    /// <summary>Encodes a reservation verdict.</summary>
    public static byte[] EncodeReservationReply(WorldTransferReservationReply reply) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(reply.Accepted); writer.Write(reply.Reason); writer.Write(reply.DeadlineDestinationTick); writer.Write(reply.BodyIndices.Count);
        foreach (var slot in reply.BodyIndices) {
            writer.Write(slot);
        }
        var definition = (reply.DestinationDefinition is null ? [] : WorldDefinitionSerialization.Serialize(definition: reply.DestinationDefinition));
        writer.Write(definition.Length);
        writer.Write(definition);
        return output.ToArray();
    }

    /// <summary>Decodes a reservation verdict.</summary>
    public static WorldTransferReservationReply DecodeReservationReply(ReadOnlySpan<byte> body) {
        using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
        var accepted = reader.ReadBoolean(); var reason = reader.ReadString(); var deadline = reader.ReadUInt64(); var count = reader.ReadInt32();
        if ((count < 0) || (count > WorldPopulationLimits.CapacityCeiling)) {
            throw new FormatException($"reservation reply body count {count} is invalid");
        }
        var slots = new int[count];
        for (var i = 0; i < count; i++) {
            slots[i] = reader.ReadInt32();
        }
        var definitionLength = reader.ReadInt32();
        if ((definitionLength < 0) || (definitionLength > (16 * 1024 * 1024))) {
            throw new FormatException($"reservation reply definition length {definitionLength} is invalid");
        }
        var definitionBytes = reader.ReadBytes(definitionLength);
        if (definitionBytes.Length != definitionLength) {
            throw new EndOfStreamException();
        }
        var definition = ((definitionLength == 0) ? null : WorldDefinitionSerialization.Deserialize(utf8Json: definitionBytes));
        if (input.Position != input.Length) {
            throw new FormatException("reservation reply carries trailing bytes");
        }
        return new WorldTransferReservationReply(accepted, reason, deadline, slots, definition);
    }

    /// <summary>Encodes a cohort commit.</summary>
    public static byte[] EncodeCommit(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); writer.Write(transferId); writer.Write(members.Count);
        foreach (var member in members) {
            writer.Write(member.HasMappedArrival);
            writer.Write(member.BodyMotionProgramName);
            WriteFixedVector(writer, member.Position); writer.Write(member.YawRadians.Value);
            WriteFixedVector(writer, member.PlanarVelocity); writer.Write(member.VerticalVelocity.Value);
            var continuity = member.ActionContinuity ?? new WorldTransferActionContinuity(Channels: [], Registers: []);
            writer.Write(continuity.Channels.Count);
            foreach (var channel in continuity.Channels) {
                writer.Write(channel.Name); writer.Write(channel.PreviousBit); writer.Write(channel.HeldValue.Value);
            }
            writer.Write(continuity.Registers.Count);
            foreach (var register in continuity.Registers) {
                writer.Write(register.Name); writer.Write((byte)register.Kind); writer.Write(register.Value.Value); writer.Write(register.TimerTicks);
            }
            writer.Write(member.Continuum.HasValue);
            if (member.Continuum is { } continuum) {
                WriteFixedVector(writer: writer, value: continuum.PreviousPosition);
                writer.Write(continuum.SourceTick);
                writer.Write(continuum.ContinuumStartEngineTick);
                writer.Write(continuum.ContinuumEndEngineTick);
                writer.Write(continuum.ConsumedThroughEngineTick);
                writer.Write(continuum.BoundaryEvents);
            }
        }
        return output.ToArray();
    }

    /// <summary>Decodes a cohort commit.</summary>
    public static bool TryDecodeCommit(ReadOnlySpan<byte> body, out string sourceAuthority, out ulong transferId, out WorldTransferCommitMember[] members, out string reason) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); transferId = reader.ReadUInt64(); var count = reader.ReadInt32();
            if (string.IsNullOrWhiteSpace(value: sourceAuthority)) { throw new FormatException("commit carries no source authority"); }
            if ((count < 1) || (count > WorldPopulationLimits.CapacityCeiling)) {
                throw new FormatException($"commit count {count} is invalid");
            }
            members = new WorldTransferCommitMember[count];
            for (var i = 0; i < count; i++) {
                var mapped = reader.ReadBoolean();
                var program = reader.ReadString();
                var position = ReadFixedVector(reader);
                var yaw = new FixedQ4816(reader.ReadInt64());
                var planar = ReadFixedVector(reader);
                var vertical = new FixedQ4816(reader.ReadInt64());
                var channelCount = reader.ReadInt32();
                if ((channelCount < 0) || (channelCount > ChannelLimits.MaxChannels)) { throw new FormatException($"commit channel count {channelCount} is invalid"); }
                var channels = new WorldTransferChannelEdge[channelCount];
                for (var channel = 0; (channel < channelCount); channel++) {
                    channels[channel] = new WorldTransferChannelEdge(Name: reader.ReadString(), PreviousBit: reader.ReadBoolean(), HeldValue: new FixedQ4816(reader.ReadInt64()));
                }
                var registerCount = reader.ReadInt32();
                if ((registerCount < 0) || (registerCount > ChannelLimits.MaxChannels)) { throw new FormatException($"commit action register count {registerCount} is invalid"); }
                var registers = new WorldTransferActionRegister[registerCount];
                for (var register = 0; (register < registerCount); register++) {
                    var name = reader.ReadString();
                    var kind = (ActionStateKind)reader.ReadByte();
                    if (!Enum.IsDefined(value: kind)) { throw new FormatException($"commit action register kind {(byte)kind} is invalid"); }
                    registers[register] = new WorldTransferActionRegister(Name: name, Kind: kind, Value: new FixedQ4816(reader.ReadInt64()), TimerTicks: reader.ReadUInt64());
                }
                WorldContinuumTrajectory? continuum = null;
                if (reader.ReadBoolean()) {
                    var previousPosition = ReadFixedVector(reader: reader);
                    var sourceTick = reader.ReadUInt64();
                    var continuumStartEngineTick = reader.ReadUInt64();
                    var continuumEndEngineTick = reader.ReadUInt64();
                    var consumedThroughEngineTick = reader.ReadUInt64();
                    var boundaryEvents = reader.ReadByte();
                    if ((continuumEndEngineTick <= continuumStartEngineTick) ||
                        (consumedThroughEngineTick < continuumEndEngineTick) ||
                        (boundaryEvents == 0) ||
                        (boundaryEvents > WorldContinuumTrajectory.MaxBoundaryEvents)) {
                        throw new FormatException($"continuum trajectory has invalid interval [{continuumStartEngineTick},{continuumEndEngineTick}), consumed-through {consumedThroughEngineTick}, or boundary count {boundaryEvents}");
                    }
                    continuum = new WorldContinuumTrajectory(PreviousPosition: previousPosition, SourceTick: sourceTick, ContinuumStartEngineTick: continuumStartEngineTick, ContinuumEndEngineTick: continuumEndEngineTick, ConsumedThroughEngineTick: consumedThroughEngineTick, BoundaryEvents: boundaryEvents);
                }
                members[i] = new WorldTransferCommitMember(null, mapped, program, position, yaw, planar, vertical, new WorldTransferActionContinuity(Channels: channels, Registers: registers), continuum);
            }
            if (input.Position != input.Length) {
                throw new FormatException("commit carries trailing bytes");
            }
            reason = string.Empty; return true;
        } catch (Exception exception) when (exception is IOException or FormatException) {
            sourceAuthority = string.Empty; transferId = 0; members = []; reason = exception.Message.ReplaceLineEndings(" "); return false;
        }
    }

    /// <summary>Encodes the destination's canonical definition revision.</summary>
    public static byte[] EncodeDefinition(WorldDefinition definition) => WorldDefinitionSerialization.Serialize(definition: definition);
    /// <summary>Decodes a canonical definition revision.</summary>
    public static WorldDefinition DecodeDefinition(ReadOnlySpan<byte> body) => WorldDefinitionSerialization.Deserialize(utf8Json: body.ToArray());

    /// <summary>Copies and encodes one per-tick projection record.</summary>
    public static byte[] EncodeSnapshot(in WorldSnapshot snapshot) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(snapshot.Tick); writer.Write(snapshot.Revision); writer.Write(snapshot.StepTicks); writer.Write(snapshot.Authority); writer.Write(snapshot.Entries.Length);
        foreach (var entry in snapshot.Entries.Span) {
            writer.Write(entry.Index); WriteVector(writer, entry.Position); WriteQuaternion(writer, entry.Orientation); WriteVector(writer, entry.BodyColor);
            writer.Write(entry.Active); writer.Write(entry.Kit); writer.Write(entry.Look); writer.Write(entry.CatalogRig); writer.Write((byte)entry.Continuity.Kind); writer.Write(entry.Continuity.Seconds); writer.Write(entry.Generation);
            writer.Write(entry.PlacementId is not null);
            if (entry.PlacementId is { } placementId) {
                writer.Write(placementId);
            }
        }
        return output.ToArray();
    }

    /// <summary>Decodes one per-tick projection record.</summary>
    public static WorldSnapshot DecodeSnapshot(ReadOnlySpan<byte> body) {
        using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
        var tick = reader.ReadUInt64(); var revision = reader.ReadInt32(); var stepTicks = reader.ReadUInt64(); var authority = reader.ReadString(); var count = reader.ReadInt32(); var entries = new EntitySnapshot[count];
        for (var i = 0; i < count; i++) {
            entries[i] = new EntitySnapshot(reader.ReadInt32(), ReadVector(reader), ReadQuaternion(reader), ReadVector(reader), reader.ReadBoolean(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), new EntityContinuity((EntityContinuityKind)reader.ReadByte(), reader.ReadSingle()), reader.ReadInt32(), reader.ReadBoolean() ? reader.ReadString() : null);
            if (entries[i].CatalogRig >= WorldLookSource.Catalog.RigCount) {
                throw new FormatException(message: $"snapshot entity {entries[i].Index} catalog rig {entries[i].CatalogRig} is outside 0..{WorldLookSource.Catalog.RigCount - 1}");
            }
        }
        return new WorldSnapshot(tick, revision, stepTicks, entries, authority);
    }

    /// <summary>Encodes a transferred body's per-tick intent with its lease credential.</summary>
    public static byte[] EncodeIntent(string sourceAuthority, in WorldMobilityIdentity mobility, in IntentSubmission submission) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); WriteMobility(writer: writer, value: mobility); writer.Write(submission.Tick); writer.Write(submission.MeasuredHoldTicks);
        for (var channel = 0; channel < ChannelLimits.MaxChannels; channel++) {
            writer.Write(submission.Intent[channel].Value);
        }
        for (var channel = 0; channel < ChannelLimits.MaxChannels; channel++) {
            writer.Write(submission.HeldChannels[channel].Value);
        }
        return output.ToArray();
    }

    /// <summary>Decodes a transferred body's per-tick intent.</summary>
    public static bool TryDecodeIntent(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility, out IntentSubmission submission) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); mobility = ReadMobility(reader: reader); var tick = reader.ReadUInt64(); var measured = reader.ReadInt32();
            if (string.IsNullOrWhiteSpace(value: sourceAuthority)) { throw new FormatException(); }
            var intent = default(PlayerIntent); var held = default(PlayerIntent);
            for (var channel = 0; channel < ChannelLimits.MaxChannels; channel++) {
                intent = intent.WithChannel(channel, new FixedQ4816(reader.ReadInt64()));
            }
            for (var channel = 0; channel < ChannelLimits.MaxChannels; channel++) {
                held = held.WithChannel(channel, new FixedQ4816(reader.ReadInt64()));
            }
            submission = new IntentSubmission(tick, -1, intent, WorldPrincipal.Console, held, measured);
            return (input.Position == input.Length);
        } catch (Exception exception) when (exception is IOException or FormatException) {
            sourceAuthority = string.Empty; mobility = default; submission = default; return false;
        }
    }

    private static void WriteIntentSource(BinaryWriter writer, IntentSource source) {
        if (source.IsLive) {
            writer.Write((byte)0);
        } else if (source.IsIdle) {
            writer.Write((byte)1);
        } else if (source.IsProducer && (source.ProducerName is { } producerName)) {
            writer.Write((byte)2);
            writer.Write(producerName);
        } else {
            throw new InvalidOperationException($"intent source '{source}' is not defined");
        }
    }

    private static IntentSource ReadIntentSource(BinaryReader reader) => reader.ReadByte() switch {
        0 => IntentSource.Live,
        1 => IntentSource.Idle,
        2 => IntentSource.Producer(name: reader.ReadString()),
        var kind => throw new FormatException($"intent source tag {kind} is not defined"),
    };

    public static byte[] EncodeTransferKey(string sourceAuthority, ulong transferId) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); writer.Write(transferId); return output.ToArray();
    }

    public static bool TryDecodeTransferKey(ReadOnlySpan<byte> body, out string sourceAuthority, out ulong transferId) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); transferId = reader.ReadUInt64();
            return !string.IsNullOrWhiteSpace(value: sourceAuthority) && (input.Position == input.Length);
        } catch (Exception exception) when (exception is IOException or FormatException or ArgumentException) {
            sourceAuthority = string.Empty; transferId = 0; return false;
        }
    }

    public static byte[] EncodeSubmission(string sourceAuthority, in WorldMobilityIdentity mobility, byte[] frame) {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write(sourceAuthority); WriteMobility(writer: writer, value: mobility); writer.Write(frame);
        return output.ToArray();
    }

    public static bool TryDecodeSubmission(ReadOnlySpan<byte> body, out string sourceAuthority, out WorldMobilityIdentity mobility, out byte[] frame) {
        try {
            using var input = new MemoryStream(body.ToArray(), writable: false); using var reader = new BinaryReader(input);
            sourceAuthority = reader.ReadString(); mobility = ReadMobility(reader: reader); frame = reader.ReadBytes(checked((int)(input.Length - input.Position)));
            return !string.IsNullOrWhiteSpace(value: sourceAuthority) && (frame.Length >= WorldFrameCodec.PrefixBytes);
        } catch (Exception exception) when (exception is IOException or FormatException or ArgumentException or OverflowException) {
            sourceAuthority = string.Empty; mobility = default; frame = []; return false;
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte kind, byte[] body, CancellationToken ct) {
        var prefix = new byte[sizeof(uint) + sizeof(byte)]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, checked((uint)(body.Length + 1))); prefix[sizeof(uint)] = kind;
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false); await stream.WriteAsync(body, ct).ConfigureAwait(false); await stream.FlushAsync(ct).ConfigureAwait(false);
    }
    private static void WriteFixedVector(BinaryWriter writer, FixedVector3 value) { writer.Write(value.X.Value); writer.Write(value.Y.Value); writer.Write(value.Z.Value); }
    private static FixedVector3 ReadFixedVector(BinaryReader reader) => new(new FixedQ4816(reader.ReadInt64()), new FixedQ4816(reader.ReadInt64()), new FixedQ4816(reader.ReadInt64()));
    private static void WriteEntityAddress(BinaryWriter writer, WorldEntityAddress value) { writer.Write(value.Authority); writer.Write(value.Index); writer.Write(value.Generation); }
    private static WorldEntityAddress ReadEntityAddress(BinaryReader reader) => new(Authority: reader.ReadString(), Index: reader.ReadInt32(), Generation: reader.ReadInt32());
    private static void WriteMobility(BinaryWriter writer, WorldMobilityIdentity value) { WriteEntityAddress(writer: writer, value: value.Incarnation); writer.Write(value.Epoch); }
    private static WorldMobilityIdentity ReadMobility(BinaryReader reader) => new(Incarnation: ReadEntityAddress(reader: reader), Epoch: reader.ReadUInt64());
    private static void WriteVector(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    private static void WriteQuaternion(BinaryWriter writer, Quaternion value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }
    private static Quaternion ReadQuaternion(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
