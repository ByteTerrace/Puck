using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The Server-side leaves more than one codec carries: the <see cref="WorldEntityAddress"/>/
/// <see cref="WorldMobilityIdentity"/> leaf shared by <see cref="WorldAuthorityCheckpointCodec"/> and
/// <see cref="WorldFederationCodec"/> — a checkpoint's own body and a federation peer's cohort/route/intent leaves all
/// carry the identical <c>[authority][index][generation]</c> address plus <c>[address][epoch]</c> mobility shape — and
/// the <see cref="WorldPeerEventEntry"/> leaf shared by the checkpoint and the <c>.puckreplay</c> tape. <see cref="ReadEntityAddress"/> refuses a blank authority by name
/// (<see cref="WireReader.ReadRequiredString"/>) rather than accepting one and reading a mobility credential no live
/// address can ever hold — deliberately stricter than the checkpoint codec's own leaf used to be, since a checkpoint
/// is trusted local state while a federation peer's bytes are not: the one shared reader applies the untrusted-input
/// discipline everywhere. Public so a law can read and write exactly the leaf a tape or checkpoint carries.</summary>
public static class WorldWireLeaves {
    /// <summary>Writes a live intent-source union, refusing an undeclared source before writing any bytes.</summary>
    public static void WriteIntentSource(WireWriter writer, IntentSource source) {
        if (!WorldWireCodec.TryWriteIntentSource(source: source, writer: writer)) {
            throw new InvalidOperationException(message: $"intent source '{source}' is not defined");
        }
    }
    /// <summary>Writes the source authority and id that identify one crossing.</summary>
    public static void WriteTransferKey(WireWriter writer, WorldTransferKey key) {
        writer.WriteString(value: key.SourceAuthority);
        writer.WriteUInt64(value: key.TransferId);
    }
    /// <summary>Reads one crossing key, refusing a blank source authority on both live and durable paths.</summary>
    public static WorldTransferKey ReadTransferKey(ref WireReader reader) => new(
        SourceAuthority: reader.ReadRequiredString(field: "transfer key source authority"),
        TransferId: reader.ReadUInt64()
    );
    /// <summary>Writes a complete arriving member, including its travelling identity projection.</summary>
    public static void WriteCommitMember(WireWriter writer, WorldTransferCommitMember member) {
        WorldIdentityProjectionWire.WriteOptional(projection: member.Profile, writer: writer);
        WriteCommitMemberMotion(member: member, writer: writer);
        WriteMemberIdentity(companion: member.Companion, memberId: member.MemberId, writer: writer);
    }
    /// <summary>Reads the complete member shared by federation commits and durable crossing state.</summary>
    public static WorldTransferCommitMember ReadCommitMember(ref WireReader reader) {
        var profile = WorldIdentityProjectionWire.ReadOptional(reader: ref reader);
        var motion = ReadCommitMemberMotion(reader: ref reader);
        var (memberId, companion) = ReadMemberIdentity(reader: ref reader);

        return (motion with { Companion = companion, MemberId = memberId, Profile = profile });
    }
    /// <summary>Writes the authentication provenance retained by an admission.</summary>
    public static void WriteAdmissionTrust(WireWriter writer, WorldAdmissionTrustIdentity? trust) {
        writer.WriteBoolean(value: (trust is not null));
        if (trust is null) { return; }
        writer.WriteByte(value: ((byte)trust.Mode));
        writer.WriteString(value: trust.Domain);
        writer.WriteNullableString(value: trust.Subject);
        writer.WriteNullableString(value: trust.Algorithm);
        writer.WriteNullableString(value: trust.KeyHash);
    }
    /// <summary>Reads the authentication provenance retained by an admission.</summary>
    public static WorldAdmissionTrustIdentity? ReadAdmissionTrust(ref WireReader reader) {
        if (!reader.ReadBoolean()) { return null; }
        var mode = ((WorldAdmissionTrustMode)reader.ReadByte());

        if (!Enum.IsDefined(value: mode)) { reader.Fail(detail: "unknown admission trust mode", refusal: WireRefusal.EnumValueUnknown); }
        return new(mode, reader.ReadString(field: "trust domain"), reader.ReadNullableString(field: "trust subject"),
            reader.ReadNullableString(field: "trust algorithm"), reader.ReadNullableString(field: "trust key"));
    }
    /// <summary>Writes a federation owner's accepted hold and pending ordered ingress through the shared input leaf.</summary>
    public static void WriteFederatedInput(WireWriter writer, WorldFederatedIntentCheckpoint input) {
        writer.WriteInt32(value: input.Index);
        if (!WorldWireCodec.TryWritePrincipal(writer, input.Principal)) { throw new ArgumentException(message: "invalid federated input principal"); }
        writer.WriteOptional(value: input.Submission, writeValue: static (w, submission) => {
            if (!WorldWireCodec.TryWriteIntentSubmission(submission: submission, writer: w)) { throw new ArgumentException(message: "invalid federated held input"); }
        });
        writer.WriteArrayCount(count: input.Pending.Length);
        foreach (var submission in input.Pending.Span) {
            if (!WorldWireCodec.TryWriteIntentSubmission(submission: submission, writer: writer)) { throw new ArgumentException(message: "invalid pending federated input"); }
        }
    }
    /// <summary>Reads one bounded federation input continuation.</summary>
    public static WorldFederatedIntentCheckpoint ReadFederatedInput(ref WireReader reader) => new(
        reader.ReadInt32(), WorldWireCodec.ReadPrincipal(ref reader),
        reader.ReadOptional(readValue: static (ref WireReader r) => WorldWireCodec.ReadIntentSubmission(reader: ref r)),
        reader.ReadArray("pending federated input", static (ref WireReader r) => WorldWireCodec.ReadIntentSubmission(reader: ref r), 1024));
    /// <summary>Writes a crossing member's request-local id and its optional companion leaf.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="memberId">The request-local id, negative for the member's ordinal.</param>
    /// <param name="companion">The companion leaf, or <see langword="null"/>.</param>
    public static void WriteMemberIdentity(WireWriter writer, int memberId, WorldTransferCompanion? companion) {
        writer.WriteInt32(value: memberId);
        writer.WriteOptionalClass(
            value: companion,
            writeValue: WriteTransferCompanion
        );
    }
    /// <summary>Reads what <see cref="WriteMemberIdentity"/> wrote, refusing an id below <c>-1</c>.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The member id and companion leaf.</returns>
    public static (int MemberId, WorldTransferCompanion? Companion) ReadMemberIdentity(ref WireReader reader) {
        var memberId = reader.ReadInt32();

        if (!reader.Failed && (memberId < -1)) {
            reader.Fail(
                detail: $"member id {memberId} is negative",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return (memberId, reader.ReadOptionalClass(readValue: static (ref WireReader r) => ReadTransferCompanion(reader: ref r)));
    }
    /// <summary>Writes what a crossing member carries as a companion: its role, followed member, title authority, driver
    /// pin and custody ticket.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="companion">The companion leaf.</param>
    public static void WriteTransferCompanion(WireWriter writer, WorldTransferCompanion companion) {
        writer.WriteString(value: companion.Role);
        writer.WriteOptional(
            value: companion.FollowsMember,
            writeValue: static (w, member) => w.WriteInt32(value: member)
        );
        writer.WriteString(value: companion.Home);
        writer.WriteNullableString(value: companion.Driver);
        writer.WriteOptional(
            value: companion.Ticket,
            writeValue: static (w, ticket) => {
                w.WriteString(value: ticket.Home);
                WriteEntityAddress(
                    address: ticket.Incarnation,
                    writer: w
                );
                WriteTransferKey(writer: w, key: ticket.Hop);
                w.WriteUInt64(value: ticket.Epoch);
            }
        );
    }
    /// <summary>Reads a companion leaf written by <see cref="WriteTransferCompanion"/>, refusing a blank role or title
    /// authority, and a ticket whose title authority or incarnation names another home than the companion's.</summary>
    /// <param name="reader">The reader.</param>
    /// <returns>The companion leaf.</returns>
    public static WorldTransferCompanion ReadTransferCompanion(ref WireReader reader) {
        var role = reader.ReadRequiredString(field: "companion role");
        var follows = reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadInt32());
        var home = reader.ReadRequiredString(field: "companion home");
        var driver = reader.ReadNullableString(field: "companion driver");
        var ticket = reader.ReadOptional(readValue: static (ref WireReader r) => new WorldCustodyTicket(
            Home: r.ReadRequiredString(field: "ticket home"),
            Incarnation: ReadEntityAddress(reader: ref r),
            Hop: ReadTransferKey(reader: ref r),
            Epoch: r.ReadUInt64()
        ));

        if (
            !reader.Failed &&
            (ticket is { } carried) &&
            (!string.Equals(a: carried.Home, b: home, comparisonType: StringComparison.Ordinal) || !string.Equals(a: carried.Incarnation.Authority, b: home, comparisonType: StringComparison.Ordinal))
        ) {
            reader.Fail(
                detail: $"companion ticket names title authority '{carried.Home}' for an incarnation of '{carried.Incarnation.Authority}', not the companion's home '{home}'",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new WorldTransferCompanion(
            Driver: driver,
            FollowsMember: follows,
            Home: home,
            Role: role,
            Ticket: ticket
        );
    }
    /// <summary>Reads a <see cref="WorldEntityAddress"/>, refusing a blank authority.</summary>
    public static WorldEntityAddress ReadEntityAddress(ref WireReader reader) => new(
        Authority: reader.ReadRequiredString(field: "entity address authority"),
        Index: reader.ReadInt32(),
        Generation: reader.ReadInt32(),
        Titled: reader.ReadBoolean()
    );
    /// <summary>Reads a traveler's accumulated arrival turn (<see cref="WorldFrameIsometry.AccumulateTurn"/>), refusing a
    /// value outside the reduced interval <c>[-pi, pi]</c>, which no accumulation produces.</summary>
    public static Puck.Maths.FixedQ4816 ReadTravelTurn(ref WireReader reader, string field) {
        var turn = reader.ReadFixed();

        if (
            !reader.Failed &&
            !WorldFrameIsometry.IsTurn(turn: turn)
        ) {
            reader.Fail(
                detail: $"{field} {turn} is outside the reduced turn interval [-pi, pi]",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return turn;
    }
    /// <summary>Reads a <see cref="WorldMobilityIdentity"/>.</summary>
    public static WorldMobilityIdentity ReadMobility(ref WireReader reader) => new(
        Incarnation: ReadEntityAddress(reader: ref reader),
        Epoch: reader.ReadUInt64(),
        DepartedFrom: ReadEntityAddress(reader: ref reader)
    );
    /// <summary>Reads a <see cref="WorldPeerEventEntry"/> — the peer admission/disconnect row the authority
    /// checkpoint and the <c>.puckreplay</c> tape both carry — refusing a catalog rig outside the catalog and an
    /// identity that is not the peer principal of the body and generation the row names.</summary>
    public static WorldPeerEventEntry ReadPeerEventEntry(ref WireReader reader) {
        var bodyIndex = reader.ReadInt32();
        var generation = reader.ReadInt32();
        var source = WorldWireCodec.ReadIntentSource(reader: ref reader);
        var identity = WorldWireCodec.ReadPrincipal(reader: ref reader);
        var identityDomain = reader.ReadString(field: "peer identity domain");
        var identitySubject = reader.ReadString(field: "peer identity subject");
        var admissionTrust = ReadAdmissionTrust(reader: ref reader);
        var authorityTransferred = reader.ReadBoolean();
        var placementId = reader.ReadNullableString(field: "peer placement id");
        var catalogRig = reader.ReadByte();
        var travelTurn = ReadTravelTurn(
            field: "peer event entry travel turn",
            reader: ref reader
        );
        var remoteHuman = reader.ReadBoolean();

        if (catalogRig >= WorldLookSource.Catalog.RigCount) {
            reader.Fail(
                detail: $"peer event entry catalog rig {catalogRig} is outside 0..{(WorldLookSource.Catalog.RigCount - 1)}",
                refusal: WireRefusal.PayloadMalformed
            );
        } else if (
            (identity.Kind != PrincipalKind.Peer) ||
            (identity.Index != bodyIndex) ||
            (identity.Generation != generation)
        ) {
            reader.Fail(
                detail: $"peer event entry identity {identity.Describe()} does not match body {bodyIndex}, generation {generation}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return new WorldPeerEventEntry(
            AdmissionTrust: admissionTrust,
            AuthorityTransferred: authorityTransferred,
            BodyIndex: bodyIndex,
            CatalogRig: catalogRig,
            Generation: generation,
            Identity: identity,
            IdentityDomain: identityDomain,
            IdentitySubject: identitySubject,
            PlacementId: placementId,
            RemoteHuman: remoteHuman,
            Source: source,
            TravelTurn: travelTurn
        );
    }
    /// <summary>Writes a <see cref="WorldPeerEventEntry"/> in <see cref="ReadPeerEventEntry"/>'s order. The caller
    /// discards the writer on <see langword="false"/>.</summary>
    /// <returns><see langword="true"/> when the entry's intent source and identity both have a wire value.</returns>
    public static bool TryWritePeerEventEntry(WireWriter writer, WorldPeerEventEntry peer) {
        writer.WriteInt32(value: peer.BodyIndex);
        writer.WriteInt32(value: peer.Generation);

        if (
            !WorldWireCodec.TryWriteIntentSource(
                source: peer.Source,
                writer: writer
            ) ||
            !WorldWireCodec.TryWritePrincipal(
                principal: peer.Identity,
                writer: writer
            )
        ) {
            return false;
        }

        writer.WriteString(value: peer.IdentityDomain);
        writer.WriteString(value: peer.IdentitySubject);
        WriteAdmissionTrust(writer: writer, trust: peer.AdmissionTrust);
        writer.WriteBoolean(value: peer.AuthorityTransferred);
        writer.WriteNullableString(value: peer.PlacementId);
        writer.WriteByte(value: peer.CatalogRig);
        writer.WriteFixed(value: peer.TravelTurn);
        writer.WriteBoolean(value: peer.RemoteHuman);

        return true;
    }
    /// <summary>Writes a <see cref="WorldEntityAddress"/>.</summary>
    public static void WriteEntityAddress(WireWriter writer, WorldEntityAddress address) {
        writer.WriteString(value: address.Authority);
        writer.WriteInt32(value: address.Index);
        writer.WriteInt32(value: address.Generation);
        writer.WriteBoolean(value: address.Titled);
    }
    /// <summary>Writes a <see cref="WorldMobilityIdentity"/>. Kept <c>Action&lt;WireWriter, WorldMobilityIdentity&gt;</c>-shaped
    /// (no <see langword="in"/> parameter) so it keeps serving as a bare method-group argument to the checkpoint
    /// codec's generic <c>WriteOptional</c>/<c>WriteArray</c> helpers.</summary>
    public static void WriteMobility(WireWriter writer, WorldMobilityIdentity mobility) {
        WriteEntityAddress(
            writer: writer,
            address: mobility.Incarnation
        );
        writer.WriteUInt64(value: mobility.Epoch);
        WriteEntityAddress(
            writer: writer,
            address: mobility.DepartedFrom
        );
    }
    public static void WriteContinuum(WireWriter writer, WorldContinuumTrajectory continuum) {
        writer.WriteFixedVector(value: continuum.PreviousPosition);
        writer.WriteUInt64(value: continuum.SourceTick);
        writer.WriteUInt64(value: continuum.ContinuumStartEngineTick);
        writer.WriteUInt64(value: continuum.ContinuumEndEngineTick);
        writer.WriteUInt64(value: continuum.ConsumedThroughEngineTick);
        writer.WriteByte(value: continuum.BoundaryEvents);
    }
    /// <summary>Reads what <see cref="WriteContinuum"/> writes, refusing a segment that is not
    /// <see cref="WorldContinuumTrajectory.IsWellFormed"/>.</summary>
    public static WorldContinuumTrajectory ReadContinuum(ref WireReader reader) {
        var continuum = new WorldContinuumTrajectory(
            PreviousPosition: reader.ReadFixedVector(),
            SourceTick: reader.ReadUInt64(),
            ContinuumStartEngineTick: reader.ReadUInt64(),
            ContinuumEndEngineTick: reader.ReadUInt64(),
            ConsumedThroughEngineTick: reader.ReadUInt64(),
            BoundaryEvents: reader.ReadByte()
        );

        if (
            !reader.Failed &&
            !continuum.IsWellFormed
        ) {
            reader.Fail(
                detail: $"continuum trajectory has invalid interval [{continuum.ContinuumStartEngineTick},{continuum.ContinuumEndEngineTick}), consumed-through {continuum.ConsumedThroughEngineTick}, or boundary count {continuum.BoundaryEvents}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        return continuum;
    }

    private static void WriteActionRegister(WireWriter writer, WorldTransferActionRegister register) {
        writer.WriteString(value: register.Name);
        writer.WriteByte(value: ((byte)register.Kind));
        writer.WriteFixed(value: register.Value);
        writer.WriteUInt64(value: register.TimerTicks);
    }
    private static WorldTransferActionRegister ReadActionRegister(ref WireReader reader) {
        var name = reader.ReadString(
            field: "action register name",
            maxBytes: WireLimits.MaxStringBytes
        );
        var kind = ((ActionStateKind)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: kind)
        ) {
            reader.Fail(
                detail: $"{nameof(ActionStateKind)} wire value {((byte)kind)} is not declared",
                refusal: WireRefusal.EnumValueUnknown
            );
        }

        var value = reader.ReadFixed();
        var timerTicks = reader.ReadUInt64();

        return new WorldTransferActionRegister(
            Kind: kind,
            Name: name,
            TimerTicks: timerTicks,
            Value: value
        );
    }

    public static void WriteActionContinuity(WireWriter writer, WorldTransferActionContinuity continuity) {
        writer.WriteArray(
            items: continuity.Registers,
            writeItem: WriteActionRegister
        );
    }
    public static WorldTransferActionContinuity ReadActionContinuity(ref WireReader reader) {
        var registers = reader.ReadArray(
            field: "action continuity registers",
            readItem: static (ref WireReader r) => ReadActionRegister(reader: ref r),
            maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount
        );

        return new WorldTransferActionContinuity(
            Registers: registers
        );
    }
    /// <summary>Writes a <see cref="WorldTransferCommitMember"/>'s arrival motion: everything but its profile, which
    /// each carrier records in its own form (a checkpoint and a federation commit its identity projection, a tape its
    /// pinned rates). It is the one encoding of a member's motion every carrier uses.</summary>
    public static void WriteCommitMemberMotion(WireWriter writer, WorldTransferCommitMember member) {
        writer.WriteByte(value: ((byte)member.Arrival));
        writer.WriteString(value: member.BodyMotionProgramName);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.YawRadians);
        writer.WriteFixedVector(value: member.PlanarVelocity);
        writer.WriteFixed(value: member.VerticalVelocity);
        writer.WriteFixed(value: member.TravelTurn);
        WriteActionContinuity(
            continuity: member.ActionContinuity,
            writer: writer
        );
        writer.WriteOptional(
            value: member.Continuum,
            writeValue: WriteContinuum
        );
    }
    /// <summary>Reads what <see cref="WriteCommitMemberMotion"/> writes, as a member with no profile.</summary>
    public static WorldTransferCommitMember ReadCommitMemberMotion(ref WireReader reader) {
        var arrival = ((WorldTransferArrival)reader.ReadByte());

        if (
            !reader.Failed &&
            !Enum.IsDefined(value: arrival)
        ) {
            reader.Fail(
                detail: $"commit member carries unknown arrival kind {((byte)arrival)}",
                refusal: WireRefusal.PayloadMalformed
            );
        }

        var bodyMotionProgramName = reader.ReadString(
            field: "commit member body motion program",
            maxBytes: WireLimits.MaxStringBytes
        );
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var planarVelocity = reader.ReadFixedVector();
        var verticalVelocity = reader.ReadFixed();
        var travelTurn = ReadTravelTurn(
            field: "commit member travel turn",
            reader: ref reader
        );
        var actionContinuity = ReadActionContinuity(reader: ref reader);
        var continuum = reader.ReadOptional(
            readValue: static (ref WireReader r) => ReadContinuum(reader: ref r)
        );

        return new WorldTransferCommitMember(
            ActionContinuity: actionContinuity,
            BodyMotionProgramName: bodyMotionProgramName,
            Continuum: continuum,
            Arrival: arrival,
            PlanarVelocity: planarVelocity,
            Position: position,
            Profile: null,
            TravelTurn: travelTurn,
            VerticalVelocity: verticalVelocity,
            YawRadians: yaw
        );
    }
}
