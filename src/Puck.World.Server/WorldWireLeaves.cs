using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The Server-side leaves more than one codec carries: the <see cref="WorldEntityAddress"/>/
/// <see cref="WorldMobilityIdentity"/> leaf shared by <see cref="WorldAuthorityCheckpointCodec"/> and
/// <see cref="WorldFederationCodec"/> — a checkpoint's own body and a federation peer's cohort/route/intent leaves all
/// carry the identical <c>[authority][index][generation]</c> address plus <c>[address][epoch]</c> mobility shape — and
/// the <see cref="WorldPeerEventEntry"/> leaf shared by the checkpoint and the <c>.puckreplay</c> tape. <see cref="ReadEntityAddress"/> refuses a blank authority by name
/// (<see cref="WireReader.ReadRequiredString"/>) rather than accepting one and reading a mobility credential no live
/// address can ever hold — deliberately stricter than the checkpoint codec's own leaf used to be, since a checkpoint
/// is trusted local state while a federation peer's bytes are not: the one shared reader applies the untrusted-input
/// discipline everywhere.</summary>
internal static class WorldWireLeaves {
    /// <summary>Reads a <see cref="WorldEntityAddress"/>, refusing a blank authority.</summary>
    public static WorldEntityAddress ReadEntityAddress(ref WireReader reader) => new(
        Authority: reader.ReadRequiredString(field: "entity address authority"),
        Index: reader.ReadInt32(),
        Generation: reader.ReadInt32()
    );
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
            Source: source
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
}
