using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The one <see cref="WorldEntityAddress"/>/<see cref="WorldMobilityIdentity"/> leaf codec, shared by
/// <see cref="WorldAuthorityCheckpointCodec"/> and <see cref="WorldFederationCodec"/> — a checkpoint's own body and a
/// federation peer's cohort/route/intent leaves all carry the identical <c>[authority][index][generation]</c> address
/// plus <c>[address][epoch]</c> mobility shape. <see cref="ReadEntityAddress"/> refuses a blank authority by name
/// (<see cref="WireReader.ReadRequiredString"/>) rather than accepting one and reading a mobility credential no live
/// address can ever hold — deliberately stricter than the checkpoint codec's own leaf used to be, since a checkpoint
/// is trusted local state while a federation peer's bytes are not: the one shared reader applies the untrusted-input
/// discipline everywhere.</summary>
internal static class WorldWireLeaves {
    /// <summary>Writes a <see cref="WorldEntityAddress"/>.</summary>
    public static void WriteEntityAddress(WireWriter writer, WorldEntityAddress address) {
        writer.WriteString(value: address.Authority);
        writer.WriteInt32(value: address.Index);
        writer.WriteInt32(value: address.Generation);
    }
    /// <summary>Reads a <see cref="WorldEntityAddress"/>, refusing a blank authority.</summary>
    public static WorldEntityAddress ReadEntityAddress(ref WireReader reader) => new(
        Authority: reader.ReadRequiredString(field: "entity address authority"),
        Index: reader.ReadInt32(),
        Generation: reader.ReadInt32()
    );
    /// <summary>Writes a <see cref="WorldMobilityIdentity"/>. Kept <c>Action&lt;WireWriter, WorldMobilityIdentity&gt;</c>-shaped
    /// (no <see langword="in"/> parameter) so it keeps serving as a bare method-group argument to the checkpoint
    /// codec's generic <c>WriteOptional</c>/<c>WriteArray</c> helpers.</summary>
    public static void WriteMobility(WireWriter writer, WorldMobilityIdentity mobility) {
        WriteEntityAddress(
            writer: writer,
            address: mobility.Incarnation
        );
        writer.WriteUInt64(value: mobility.Epoch);
    }
    /// <summary>Reads a <see cref="WorldMobilityIdentity"/>.</summary>
    public static WorldMobilityIdentity ReadMobility(ref WireReader reader) => new(
        Incarnation: ReadEntityAddress(reader: ref reader),
        Epoch: reader.ReadUInt64()
    );
}
