using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static byte[] EncodeOwnedWorlds(WorldOwnedWorldsCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteArray(
            items: section.IdentityDocumentsJson,
            writeItem: static (w, json) => w.WriteBlock(value: json)
        );
        writer.WriteInt64(value: section.Revision);

        return writer.ToArray();
    }
    private static bool TryDecodeOwnedWorlds(byte[] bytes, out string reason, out WorldOwnedWorldsCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var identityDocumentsJson = reader.ReadArray(
            field: "owned worlds documents",
            readItem: static (ref WireReader r) => r.ReadBlock(
                field: "owned world document",
                maxBytes: MaxSectionBytes
            ),
            maximum: MaxCollectionCount
        );
        var revision = reader.ReadInt64();

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"owned worlds section: {failure}";

            return false;
        }

        section = new WorldOwnedWorldsCheckpoint(
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
        writer.WriteArray(
            items: member.Designations,
            writeItem: WriteTargetDesignation
        );
        writer.WriteOptional(
            value: member.Peer,
            writeValue: WritePeerEventEntry
        );
        writer.WriteArray(
            items: member.AdmissionGrants,
            writeItem: WriteAdmissionGrant
        );
        writer.WriteArray(
            items: member.SourceGrants,
            writeItem: WriteWorldGrant
        );
        WorldWireLeaves.WriteMobility(
            writer: writer,
            mobility: member.Mobility
        );
        writer.WriteByte(value: member.FollowedSeatMask);
    }
    private static WorldLandedMemberCheckpoint ReadLandedMember(ref WireReader reader) {
        var sourceSlot = reader.ReadInt32();
        var targetSlot = reader.ReadInt32();
        var bodyColor = reader.ReadFiniteVector(field: "landed member body color");
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var dynamicState = ReadTransferState(reader: ref reader);
        var designations = reader.ReadArray(
            field: "landed member designations",
            readItem: static (ref WireReader r) => ReadTargetDesignation(reader: ref r),
            maximum: MaxCollectionCount
        );
        var peer = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireLeaves.ReadPeerEventEntry(reader: ref r)
        );
        var admissionGrants = reader.ReadArray(
            field: "landed member admission grants",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r),
            maximum: MaxCollectionCount
        );
        var sourceGrants = reader.ReadArray(
            field: "landed member source grants",
            readItem: static (ref WireReader r) => ReadWorldGrant(reader: ref r),
            maximum: MaxCollectionCount
        );
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var followedSeatMask = reader.ReadByte();

        return new WorldLandedMemberCheckpoint(
            AdmissionGrants: admissionGrants,
            BodyColor: bodyColor,
            Designations: designations,
            DynamicState: dynamicState,
            FollowedSeatMask: followedSeatMask,
            Mobility: mobility,
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
        writer.WriteOptionalClass(
            value: row.Continuation,
            writeValue: WriteTransferContinuation
        );
        writer.WriteOptionalClass(
            value: row.TargetDefinitionJson,
            writeValue: static (w, bytes) => w.WriteBlock(value: bytes)
        );
        writer.WriteArray(
            items: row.CommitMembers,
            writeItem: WriteCommitMember
        );
        writer.WriteArray(
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
        var continuation = reader.ReadOptionalClass(
            readValue: ReadTransferContinuation
        );
        var targetDefinition = reader.ReadOptionalClass(
            readValue: static (ref WireReader r) => r.ReadBlock(
                field: "recovery destination definition",
                maxBytes: MaxSectionBytes
            )
        );
        var commitMembers = reader.ReadArray(
            field: "in-doubt transfer commit members",
            readItem: (ref WireReader r) => ReadCommitMember(
                defaults: defaults,
                reader: ref r
            ),
            maximum: MaxCollectionCount
        );
        var landed = reader.ReadArray(
            field: "in-doubt transfer landed members",
            readItem: static (ref WireReader r) => ReadLandedMember(reader: ref r),
            maximum: MaxCollectionCount
        );

        return new WorldInDoubtTransferCheckpoint(
            CommitConfirmed: commitConfirmed,
            CommitMembers: commitMembers,
            Continuation: continuation,
            Landed: landed,
            MemberCount: memberCount,
            RollbackOnly: rollbackOnly,
            SourceDeadlineTick: sourceDeadlineTick,
            SourceInstance: sourceInstance,
            Spawned: spawned,
            TargetAuthority: targetAuthority,
            TargetDefinitionJson: targetDefinition,
            TargetEndpoint: targetEndpoint,
            TargetName: targetName,
            TransferId: transferId
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
        writer.WriteString(value: row.SourceAuthority);
        writer.WriteNullableString(value: row.DestinationEndpoint);
        writer.WriteOptionalClass(
            value: row.DestinationDefinitionJson,
            writeValue: static (w, bytes) => w.WriteBlock(value: bytes)
        );
    }
    private static WorldForwardedBodyCheckpoint ReadForwardedBody(ref WireReader reader) {
        var sourceIncarnation = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var destinationAddress = WorldWireLeaves.ReadEntityAddress(reader: ref reader);
        var destinationBodyIndex = reader.ReadInt32();
        var mobility = WorldWireLeaves.ReadMobility(reader: ref reader);
        var sourceAuthority = reader.ReadString(
            field: "forwarding source authority",
            maxBytes: MaxStringBytes
        );
        var endpoint = reader.ReadNullableString(
            field: "forwarding destination endpoint",
            maxBytes: MaxStringBytes
        );
        var definition = reader.ReadOptionalClass(
            readValue: static (ref WireReader r) => r.ReadBlock(
                field: "forwarding destination definition",
                maxBytes: MaxSectionBytes
            )
        );

        return new WorldForwardedBodyCheckpoint(
            DestinationAddress: destinationAddress,
            DestinationBodyIndex: destinationBodyIndex,
            DestinationDefinitionJson: definition,
            DestinationEndpoint: endpoint,
            Mobility: mobility,
            SourceAuthority: sourceAuthority,
            SourceIncarnation: sourceIncarnation
        );
    }
}
