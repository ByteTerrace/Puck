using Puck.Networking;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteTransferKey(WireWriter writer, WorldTransferKey key) {
        writer.WriteString(value: key.SourceAuthority);
        writer.WriteUInt64(value: key.TransferId);
    }
    private static WorldTransferKey ReadTransferKey(ref WireReader reader) => new(
        SourceAuthority: reader.ReadString(
            field: "transfer key source authority",
            maxBytes: MaxStringBytes
        ),
        TransferId: reader.ReadUInt64()
    );
    private static void WriteContinuum(WireWriter writer, WorldContinuumTrajectory continuum) {
        writer.WriteFixedVector(value: continuum.PreviousPosition);
        writer.WriteUInt64(value: continuum.SourceTick);
        writer.WriteUInt64(value: continuum.ContinuumStartEngineTick);
        writer.WriteUInt64(value: continuum.ContinuumEndEngineTick);
        writer.WriteUInt64(value: continuum.ConsumedThroughEngineTick);
        writer.WriteByte(value: continuum.BoundaryEvents);
    }
    private static WorldContinuumTrajectory ReadContinuum(ref WireReader reader) => new(
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
            maxBytes: MaxStringBytes
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
            maxBytes: MaxStringBytes
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
    private static void WriteActionContinuity(WireWriter writer, WorldTransferActionContinuity continuity) {
        WriteArray(
            writer: writer,
            items: continuity.Channels,
            writeItem: WriteChannelEdge
        );
        WriteArray(
            writer: writer,
            items: continuity.Registers,
            writeItem: WriteActionRegister
        );
    }
    private static WorldTransferActionContinuity ReadActionContinuity(ref WireReader reader) {
        var channels = ReadArray(
            reader: ref reader,
            field: "action continuity channels",
            readItem: static (ref WireReader r) => ReadChannelEdge(reader: ref r)
        );
        var registers = ReadArray(
            reader: ref reader,
            field: "action continuity registers",
            readItem: static (ref WireReader r) => ReadActionRegister(reader: ref r)
        );

        return new WorldTransferActionContinuity(
            Channels: channels,
            Registers: registers
        );
    }
    private static void WriteCommitMember(WireWriter writer, WorldTransferCommitMember member) {
        WriteIdentityOptional(
            writer: writer,
            identity: member.Profile
        );
        writer.WriteBoolean(value: member.HasMappedArrival);
        writer.WriteString(value: member.BodyMotionProgramName);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.YawRadians);
        writer.WriteFixedVector(value: member.PlanarVelocity);
        writer.WriteFixed(value: member.VerticalVelocity);
        WriteOptionalClass(
            writer: writer,
            value: member.ActionContinuity,
            writeValue: WriteActionContinuity
        );
        WriteOptional(
            writer: writer,
            value: member.Continuum,
            writeValue: WriteContinuum
        );
    }
    private static WorldTransferCommitMember ReadCommitMember(ref WireReader reader, WorldPlayerDefaults defaults) {
        var profile = ReadIdentityOptional(
            defaults: defaults,
            reader: ref reader
        );
        var hasMappedArrival = reader.ReadBoolean();
        var bodyMotionProgramName = reader.ReadString(
            field: "commit member body motion program",
            maxBytes: MaxStringBytes
        );
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var planarVelocity = reader.ReadFixedVector();
        var verticalVelocity = reader.ReadFixed();
        var actionContinuity = ReadOptionalClass(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadActionContinuity(reader: ref r)
        );
        var continuum = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadContinuum(reader: ref r)
        );

        return new WorldTransferCommitMember(
            ActionContinuity: actionContinuity,
            BodyMotionProgramName: bodyMotionProgramName,
            Continuum: continuum,
            HasMappedArrival: hasMappedArrival,
            PlanarVelocity: planarVelocity,
            Position: position,
            Profile: profile,
            VerticalVelocity: verticalVelocity,
            YawRadians: yaw
        );
    }
    private static void WriteReservationMember(WireWriter writer, WorldTransferReservationMember member) {
        WritePrincipal(
            writer: writer,
            principal: member.Principal
        );
        writer.WriteInt32(value: member.PreferredSlot);
        WriteIdentityOptional(
            writer: writer,
            identity: member.Identity
        );
        WriteIntentSource(
            writer: writer,
            source: member.Source
        );
        writer.WriteVector(value: member.BodyColor);
        writer.WriteByte(value: member.CatalogRig);
        WriteOptional(
            writer: writer,
            value: member.Mobility,
            writeValue: WriteMobility
        );
    }
    private static WorldTransferReservationMember ReadReservationMember(ref WireReader reader, WorldPlayerDefaults defaults) {
        var principal = ReadPrincipal(reader: ref reader);
        var preferredSlot = reader.ReadInt32();
        var identity = ReadIdentityOptional(
            defaults: defaults,
            reader: ref reader
        );
        var source = ReadIntentSource(reader: ref reader);
        var bodyColor = reader.ReadFiniteVector(field: "reservation member body color");
        var catalogRig = reader.ReadByte();
        var mobility = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadMobility(reader: ref r)
        );

        return new WorldTransferReservationMember(
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Identity: identity,
            Mobility: mobility,
            PreferredSlot: preferredSlot,
            Principal: principal,
            Source: source
        );
    }
    private static void WriteReservationRequest(WireWriter writer, WorldTransferReservationRequest request) {
        writer.WriteUInt64(value: request.TransferId);
        writer.WriteString(value: request.SourceAuthority);
        writer.WriteInt32(value: request.SourceRateHz);
        writer.WriteUInt64(value: request.SourceTick);
        writer.WriteUInt64(value: request.DeadlineSourceTick);
        writer.WriteString(value: request.Border);
        WriteOptional(
            writer: writer,
            value: request.BorderCapacity,
            writeValue: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteBoolean(value: request.PartyAllOrNothing);
        writer.WriteBoolean(value: request.PeerAdmission);
        WriteArray(
            writer: writer,
            items: request.Members,
            writeItem: WriteReservationMember
        );
    }
    private static WorldTransferReservationRequest ReadReservationRequest(ref WireReader reader, WorldPlayerDefaults defaults) {
        var transferId = reader.ReadUInt64();
        var sourceAuthority = reader.ReadString(
            field: "reservation source authority",
            maxBytes: MaxStringBytes
        );
        var sourceRateHz = reader.ReadInt32();
        var sourceTick = reader.ReadUInt64();
        var deadlineSourceTick = reader.ReadUInt64();
        var border = reader.ReadString(
            field: "reservation border",
            maxBytes: MaxStringBytes
        );
        var borderCapacity = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadInt32()
        );
        var partyAllOrNothing = reader.ReadBoolean();
        var peerAdmission = reader.ReadBoolean();
        var members = ReadArray(
            reader: ref reader,
            field: "reservation members",
            readItem: (ref WireReader r) => ReadReservationMember(
                defaults: defaults,
                reader: ref r
            )
        );

        return new WorldTransferReservationRequest(
            Border: border,
            BorderCapacity: borderCapacity,
            DeadlineSourceTick: deadlineSourceTick,
            Members: members,
            PartyAllOrNothing: partyAllOrNothing,
            PeerAdmission: peerAdmission,
            SourceAuthority: sourceAuthority,
            SourceRateHz: sourceRateHz,
            SourceTick: sourceTick,
            TransferId: transferId
        );
    }
    private static void WriteLease(WireWriter writer, WorldTransferEscrow.WorldTransferLeaseCheckpoint lease) {
        WriteTransferKey(
            writer: writer,
            key: lease.Key
        );
        WriteReservationRequest(
            writer: writer,
            request: lease.Request
        );
        writer.WriteUInt64(value: lease.DeadlineTick);
        WriteArray(
            writer: writer,
            items: lease.Slots,
            writeItem: static (w, v) => w.WriteInt32(value: v)
        );
        writer.WriteBlock(value: lease.DestinationDefinitionJson);
        WriteOptionalClass(
            writer: writer,
            value: lease.Arrival,
            writeValue: WriteAdmissionVerdict
        );
    }
    private static WorldTransferEscrow.WorldTransferLeaseCheckpoint ReadLease(ref WireReader reader, WorldPlayerDefaults defaults) {
        var key = ReadTransferKey(reader: ref reader);
        var request = ReadReservationRequest(
            defaults: defaults,
            reader: ref reader
        );
        var deadlineTick = reader.ReadUInt64();
        var slots = ReadArray(
            reader: ref reader,
            field: "lease slots",
            readItem: static (ref WireReader r) => r.ReadInt32()
        );
        var destinationDefinitionJson = reader.ReadBlock(
            field: "lease destination definition",
            maxBytes: MaxSectionBytes
        );
        var arrival = ReadOptionalClass(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadAdmissionVerdict(reader: ref r)
        );

        return new WorldTransferEscrow.WorldTransferLeaseCheckpoint(
            Arrival: arrival,
            DeadlineTick: deadlineTick,
            DestinationDefinitionJson: destinationDefinitionJson,
            Key: key,
            Request: request,
            Slots: slots
        );
    }
    private static void WriteCommitted(WireWriter writer, WorldTransferEscrow.WorldTransferCommittedCheckpoint committed) {
        WriteTransferKey(
            writer: writer,
            key: committed.Key
        );
        WriteArray(
            writer: writer,
            items: committed.Members,
            writeItem: WriteCommitMember
        );
        WriteArray(
            writer: writer,
            items: committed.Principals,
            writeItem: WritePrincipal
        );
        WriteArray(
            writer: writer,
            items: committed.Incarnations,
            writeItem: WriteEntityAddress
        );
    }
    private static WorldTransferEscrow.WorldTransferCommittedCheckpoint ReadCommitted(ref WireReader reader, WorldPlayerDefaults defaults) {
        var key = ReadTransferKey(reader: ref reader);
        var members = ReadArray(
            reader: ref reader,
            field: "committed members",
            readItem: (ref WireReader r) => ReadCommitMember(
                defaults: defaults,
                reader: ref r
            )
        );
        var principals = ReadArray(
            reader: ref reader,
            field: "committed principals",
            readItem: static (ref WireReader r) => ReadPrincipal(reader: ref r)
        );
        var incarnations = ReadArray(
            reader: ref reader,
            field: "committed incarnations",
            readItem: static (ref WireReader r) => ReadEntityAddress(reader: ref r)
        );

        return new WorldTransferEscrow.WorldTransferCommittedCheckpoint(
            Incarnations: incarnations,
            Key: key,
            Members: members,
            Principals: principals
        );
    }
    private static byte[] EncodeEscrow(WorldTransferEscrow.WorldTransferEscrowCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.Leases,
            writeItem: WriteLease
        );
        WriteArray(
            writer: writer,
            items: section.Committed,
            writeItem: WriteCommitted
        );
        WriteArray(
            writer: writer,
            items: section.LatestCommittedTransfer,
            writeItem: static (w, row) => {
                WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                WriteTransferKey(
                    key: row.Transfer,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.MobilityLeases,
            writeItem: static (w, row) => {
                WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                WriteTransferKey(
                    key: row.Transfer,
                    writer: w
                );
                w.WriteUInt64(value: row.ExpectedEpoch);
            }
        );
        WriteArray(
            writer: writer,
            items: section.MobilityAdmissions,
            writeItem: static (w, row) => {
                w.WriteString(value: row.SourceAuthority);
                WriteEntityAddress(
                    address: row.Incarnation,
                    writer: w
                );
                w.WriteUInt64(value: row.Epoch);
                WritePrincipal(
                    principal: row.Principal,
                    writer: w
                );
            }
        );
        WriteArray(
            writer: writer,
            items: section.BorderAdmissions,
            writeItem: static (w, row) => {
                w.WriteInt32(value: row.Slot);
                w.WriteString(value: row.Border);
            }
        );

        return writer.ToArray();
    }
    private static bool TryDecodeEscrow(byte[] bytes, WorldPlayerDefaults defaults, out string reason, out WorldTransferEscrow.WorldTransferEscrowCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var leases = ReadArray(
            reader: ref reader,
            field: "escrow leases",
            readItem: (ref WireReader r) => ReadLease(
                defaults: defaults,
                reader: ref r
            )
        );
        var committed = ReadArray(
            reader: ref reader,
            field: "escrow committed",
            readItem: (ref WireReader r) => ReadCommitted(
                defaults: defaults,
                reader: ref r
            )
        );
        var latest = ReadArray(
            reader: ref reader,
            field: "escrow latest committed transfer",
            readItem: static (ref WireReader r) => {
                var incarnation = ReadEntityAddress(reader: ref r);
                var transfer = ReadTransferKey(reader: ref r);

                return (incarnation, transfer);
            }
        );
        var mobilityLeases = ReadArray(
            reader: ref reader,
            field: "escrow mobility leases",
            readItem: static (ref WireReader r) => {
                var incarnation = ReadEntityAddress(reader: ref r);
                var transfer = ReadTransferKey(reader: ref r);
                var expectedEpoch = r.ReadUInt64();

                return (incarnation, transfer, expectedEpoch);
            }
        );
        var mobilityAdmissions = ReadArray(
            reader: ref reader,
            field: "escrow mobility admissions",
            readItem: static (ref WireReader r) => {
                var sourceAuthority = r.ReadString(
                    field: "mobility admission source authority",
                    maxBytes: MaxStringBytes
                );
                var incarnation = ReadEntityAddress(reader: ref r);
                var epoch = r.ReadUInt64();
                var principal = ReadPrincipal(reader: ref r);

                return (sourceAuthority, incarnation, epoch, principal);
            }
        );
        var borderAdmissions = ReadArray(
            reader: ref reader,
            field: "escrow border admissions",
            readItem: static (ref WireReader r) => {
                var slot = r.ReadInt32();
                var border = r.ReadString(
                    field: "border admission border",
                    maxBytes: MaxStringBytes
                );

                return (slot, border);
            }
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"escrow section: {failure}";

            return false;
        }

        section = new WorldTransferEscrow.WorldTransferEscrowCheckpoint(
            BorderAdmissions: borderAdmissions,
            Committed: committed,
            LatestCommittedTransfer: latest,
            Leases: leases,
            MobilityAdmissions: mobilityAdmissions,
            MobilityLeases: mobilityLeases
        );
        reason = string.Empty;

        return true;
    }
    // ---- input hold section ----

}
