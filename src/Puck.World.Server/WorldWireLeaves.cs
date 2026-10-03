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
[FormatLeaf]
public static class WorldWireLeaves {
    /// <summary>Reads a <see cref="WorldEntityAddress"/>, refusing a blank authority.</summary>
    public static WorldEntityAddress ReadEntityAddress(ref WireReader reader) => new(
        Authority: reader.ReadRequiredString(field: "entity address authority"),
        Index: reader.ReadInt32(),
        Generation: reader.ReadInt32()
    );
    /// <summary>Reads a traveler's accumulated arrival turn (<see cref="WorldFrameIsometry.AccumulateTurn"/>), refusing a
    /// value outside the reduced interval <c>[-pi, pi)</c>, which no accumulation produces.</summary>
    public static Puck.Maths.FixedQ4816 ReadTravelTurn(ref WireReader reader, string field) {
        var turn = reader.ReadFixed();

        if (
            !reader.Failed &&
            !WorldFrameIsometry.IsTurn(turn: turn)
        ) {
            reader.Fail(
                detail: $"{field} {turn} is outside the reduced turn interval [-pi, pi)",
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
        var authorityTransferred = reader.ReadBoolean();
        var placementId = reader.ReadNullableString(field: "peer placement id");
        var catalogRig = reader.ReadByte();
        var travelTurn = ReadTravelTurn(
            field: "peer event entry travel turn",
            reader: ref reader
        );

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
            AuthorityTransferred: authorityTransferred,
            BodyIndex: bodyIndex,
            CatalogRig: catalogRig,
            Generation: generation,
            Identity: identity,
            IdentityDomain: identityDomain,
            IdentitySubject: identitySubject,
            PlacementId: placementId,
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
        writer.WriteBoolean(value: peer.AuthorityTransferred);
        writer.WriteNullableString(value: peer.PlacementId);
        writer.WriteByte(value: peer.CatalogRig);
        writer.WriteFixed(value: peer.TravelTurn);

        return true;
    }
    /// <summary>Writes a <see cref="WorldEntityAddress"/>.</summary>
    public static void WriteEntityAddress(WireWriter writer, WorldEntityAddress address) {
        writer.WriteString(value: address.Authority);
        writer.WriteInt32(value: address.Index);
        writer.WriteInt32(value: address.Generation);
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
    public static WorldContinuumTrajectory ReadContinuum(ref WireReader reader) => new(
        PreviousPosition: reader.ReadFixedVector(),
        SourceTick: reader.ReadUInt64(),
        ContinuumStartEngineTick: reader.ReadUInt64(),
        ContinuumEndEngineTick: reader.ReadUInt64(),
        ConsumedThroughEngineTick: reader.ReadUInt64(),
        BoundaryEvents: reader.ReadByte()
    );

    private static void WriteChannelEdge(WireWriter writer, WorldTransferChannelEdge edge) {
        writer.WriteString(value: edge.Name);
        writer.WriteBoolean(value: edge.PreviousBit);
        writer.WriteFixed(value: edge.HeldValue);
    }
    private static WorldTransferChannelEdge ReadChannelEdge(ref WireReader reader) => new(
        Name: reader.ReadString(
            field: "channel edge name",
            maxBytes: WireLimits.MaxStringBytes
        ),
        PreviousBit: reader.ReadBoolean(),
        HeldValue: reader.ReadFixed()
    );
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
            items: continuity.Channels,
            writeItem: WriteChannelEdge
        );
        writer.WriteArray(
            items: continuity.Registers,
            writeItem: WriteActionRegister
        );
    }
    public static WorldTransferActionContinuity ReadActionContinuity(ref WireReader reader) {
        var channels = reader.ReadArray(
            field: "action continuity channels",
            readItem: static (ref WireReader r) => ReadChannelEdge(reader: ref r),
            maximum: ChannelLimits.MaxChannels
        );
        var registers = reader.ReadArray(
            field: "action continuity registers",
            readItem: static (ref WireReader r) => ReadActionRegister(reader: ref r),
            maximum: ChannelLimits.MaxChannels
        );

        return new WorldTransferActionContinuity(
            Channels: channels,
            Registers: registers
        );
    }
    /// <summary>Writes a <see cref="WorldTransferCommitMember"/>'s arrival motion: everything but its profile, which
    /// each carrier records in its own form (a checkpoint its identity, a tape its pinned rates).</summary>
    public static void WriteCommitMemberMotion(WireWriter writer, WorldTransferCommitMember member) {
        writer.WriteBoolean(value: member.HasMappedArrival);
        writer.WriteString(value: member.BodyMotionProgramName);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.YawRadians);
        writer.WriteFixedVector(value: member.PlanarVelocity);
        writer.WriteFixed(value: member.VerticalVelocity);
        writer.WriteFixed(value: member.TravelTurn);
        writer.WriteOptionalClass(
            value: member.ActionContinuity,
            writeValue: WriteActionContinuity
        );
        writer.WriteOptional(
            value: member.Continuum,
            writeValue: WriteContinuum
        );
    }
    /// <summary>Reads what <see cref="WriteCommitMemberMotion"/> writes, as a member with no profile.</summary>
    public static WorldTransferCommitMember ReadCommitMemberMotion(ref WireReader reader) {
        var hasMappedArrival = reader.ReadBoolean();
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
        var actionContinuity = reader.ReadOptionalClass(
            readValue: static (ref WireReader r) => ReadActionContinuity(reader: ref r)
        );
        var continuum = reader.ReadOptional(
            readValue: static (ref WireReader r) => ReadContinuum(reader: ref r)
        );

        return new WorldTransferCommitMember(
            ActionContinuity: actionContinuity,
            BodyMotionProgramName: bodyMotionProgramName,
            Continuum: continuum,
            HasMappedArrival: hasMappedArrival,
            PlanarVelocity: planarVelocity,
            Position: position,
            Profile: null,
            TravelTurn: travelTurn,
            VerticalVelocity: verticalVelocity,
            YawRadians: yaw
        );
    }
}
