using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeOwnedWorlds(WorldOwnedWorlds.WorldOwnedWorldsCheckpoint section) {
        var writer = new WireWriter();

        WriteArray(
            writer: writer,
            items: section.IdentityDocumentsJson,
            writeItem: static (w, json) => w.WriteBlock(value: json)
        );
        writer.WriteInt64(value: section.Revision);

        return writer.ToArray();
    }
    private static bool TryDecodeOwnedWorlds(byte[] bytes, out string reason, out WorldOwnedWorlds.WorldOwnedWorldsCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var identityDocumentsJson = ReadArray(
            reader: ref reader,
            field: "owned worlds documents",
            readItem: static (ref WireReader r) => r.ReadBlock(
                field: "owned world document",
                maxBytes: MaxSectionBytes
            )
        );
        var revision = reader.ReadInt64();

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"owned worlds section: {failure}";

            return false;
        }

        section = new WorldOwnedWorlds.WorldOwnedWorldsCheckpoint(
            IdentityDocumentsJson: identityDocumentsJson,
            Revision: revision
        );
        reason = string.Empty;

        return true;
    }
    // ---- host row section ----

    private static void WriteLandedMember(WireWriter writer, WorldLandedMemberCheckpoint member) {
        writer.WriteInt32(value: member.SourceSlot);
        writer.WriteInt32(value: member.TargetSlot);
        writer.WriteVector(value: member.BodyColor);
        writer.WriteFixedVector(value: member.Position);
        writer.WriteFixed(value: member.Yaw);
        WriteTransferState(
            writer: writer,
            state: member.DynamicState
        );
        WriteArray(
            writer: writer,
            items: member.Designations,
            writeItem: WriteTargetDesignation
        );
        WriteOptional(
            writer: writer,
            value: member.Peer,
            writeValue: WritePeerEventEntry
        );
        WriteArray(
            writer: writer,
            items: member.AdmissionGrants,
            writeItem: WriteAdmissionGrant
        );
        WriteArray(
            writer: writer,
            items: member.SourceGrants,
            writeItem: WriteWorldGrant
        );
        WorldWireLeaves.WriteMobility(
            writer: writer,
            mobility: member.Mobility
        );
        writer.WriteByte(member.FollowedSeatMask);
    }
    private static WorldLandedMemberCheckpoint ReadLandedMember(ref WireReader reader) {
        var sourceSlot = reader.ReadInt32();
        var targetSlot = reader.ReadInt32();
        var bodyColor = reader.ReadFiniteVector(field: "landed member body color");
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var dynamicState = ReadTransferState(reader: ref reader);
        var designations = ReadArray(
            reader: ref reader,
            field: "landed member designations",
            readItem: static (ref WireReader r) => ReadTargetDesignation(reader: ref r)
        );
        var peer = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadPeerEventEntry(reader: ref r)
        );
        var admissionGrants = ReadArray(
            reader: ref reader,
            field: "landed member admission grants",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r)
        );
        var sourceGrants = ReadArray(
            reader: ref reader,
            field: "landed member source grants",
            readItem: static (ref WireReader r) => ReadWorldGrant(reader: ref r)
        );
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var followedSeatMask = reader.ReadByte();

        return new WorldLandedMemberCheckpoint(
            AdmissionGrants: admissionGrants,
            BodyColor: bodyColor,
            Designations: designations,
            DynamicState: dynamicState,
            Mobility: mobility,
            FollowedSeatMask: followedSeatMask,
            Peer: peer,
            Position: position,
            SourceGrants: sourceGrants,
            SourceSlot: sourceSlot,
            TargetSlot: targetSlot,
            Yaw: yaw
        );
    }
    private static void WriteInDoubtTransfer(WireWriter writer, WorldInDoubtTransferCheckpoint row) {
        writer.WriteString(value: row.SourceInstance);
        writer.WriteUInt64(value: row.TransferId);
        writer.WriteNullableString(value: row.TargetName);
        writer.WriteString(value: row.TargetAuthority);
        writer.WriteNullableString(value: row.TargetEndpoint);
        writer.WriteBoolean(value: row.Spawned);
        writer.WriteUInt64(value: row.SourceDeadlineTick);
        writer.WriteInt32(value: row.MemberCount);
        writer.WriteBoolean(value: row.RollbackOnly);
        writer.WriteBoolean(value: row.CommitConfirmed);
        WriteOptionalClass(writer, row.Continuation, WriteTransferContinuation);
        WriteOptionalClass(writer, row.TargetDefinitionJson, static (w, bytes) => w.WriteBlock(bytes));
        WriteArray(
            writer: writer,
            items: row.CommitMembers,
            writeItem: WriteCommitMember
        );
        WriteArray(
            writer: writer,
            items: row.Landed,
            writeItem: WriteLandedMember
        );
    }
    private static WorldInDoubtTransferCheckpoint ReadInDoubtTransfer(ref WireReader reader, WorldPlayerDefaults defaults) {
        var sourceInstance = reader.ReadString(
            field: "in-doubt transfer source instance",
            maxBytes: MaxStringBytes
        );
        var transferId = reader.ReadUInt64();
        var targetName = reader.ReadNullableString(
            field: "in-doubt transfer target name",
            maxBytes: MaxStringBytes
        );
        var targetAuthority = reader.ReadString(
            field: "in-doubt transfer target authority",
            maxBytes: MaxStringBytes
        );
        var targetEndpoint = reader.ReadNullableString(
            field: "in-doubt transfer target endpoint",
            maxBytes: MaxStringBytes
        );
        var spawned = reader.ReadBoolean();
        var sourceDeadlineTick = reader.ReadUInt64();
        var memberCount = reader.ReadInt32();
        var rollbackOnly = reader.ReadBoolean();
        var commitConfirmed = reader.ReadBoolean();
        var continuation = ReadOptionalClass(ref reader, ReadTransferContinuation);
        var targetDefinition = ReadOptionalClass(ref reader, static (ref WireReader r) => r.ReadBlock("recovery destination definition", MaxSectionBytes));
        var commitMembers = ReadArray(
            reader: ref reader,
            field: "in-doubt transfer commit members",
            readItem: (ref WireReader r) => ReadCommitMember(
                defaults: defaults,
                reader: ref r
            )
        );
        var landed = ReadArray(
            reader: ref reader,
            field: "in-doubt transfer landed members",
            readItem: static (ref WireReader r) => ReadLandedMember(reader: ref r)
        );

        return new WorldInDoubtTransferCheckpoint(
            CommitMembers: commitMembers,
            Landed: landed,
            MemberCount: memberCount,
            SourceDeadlineTick: sourceDeadlineTick,
            SourceInstance: sourceInstance,
            Spawned: spawned,
            TargetAuthority: targetAuthority,
            TargetEndpoint: targetEndpoint,
            TargetName: targetName,
            TransferId: transferId,
            RollbackOnly: rollbackOnly,
            CommitConfirmed: commitConfirmed,
            Continuation: continuation,
            TargetDefinitionJson: targetDefinition
        );
    }
    private static void WriteForwardedBody(WireWriter writer, WorldForwardedBodyCheckpoint row) {
        WorldWireLeaves.WriteEntityAddress(
            writer: writer,
            address: row.SourceIncarnation
        );
        WorldWireLeaves.WriteEntityAddress(
            writer: writer,
            address: row.DestinationAddress
        );
        writer.WriteInt32(value: row.DestinationBodyIndex);
        WorldWireLeaves.WriteMobility(
            writer: writer,
            mobility: row.Mobility
        );
        writer.WriteString(row.SourceAuthority);
        writer.WriteNullableString(row.DestinationEndpoint);
        WriteOptionalClass(writer, row.DestinationDefinitionJson, static (w, bytes) => w.WriteBlock(bytes));
    }
    private static WorldForwardedBodyCheckpoint ReadForwardedBody(ref WireReader reader) {
        var sourceIncarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var destinationAddress = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var destinationBodyIndex = reader.ReadInt32();
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var sourceAuthority = reader.ReadString("forwarding source authority", MaxStringBytes);
        var endpoint = reader.ReadNullableString("forwarding destination endpoint", MaxStringBytes);
        var definition = ReadOptionalClass(ref reader, static (ref WireReader r) => r.ReadBlock("forwarding destination definition", MaxSectionBytes));

        return new WorldForwardedBodyCheckpoint(
            DestinationAddress: destinationAddress,
            DestinationBodyIndex: destinationBodyIndex,
            Mobility: mobility,
            SourceIncarnation: sourceIncarnation,
            SourceAuthority: sourceAuthority,
            DestinationEndpoint: endpoint,
            DestinationDefinitionJson: definition
        );
    }
}
