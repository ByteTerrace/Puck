using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteTargetDesignation(WireWriter writer, WorldTargetDesignation target) {
        writer.WriteInt32(value: target.Index);
        writer.WriteFixedVector(value: target.Point);
    }
    private static WorldTargetDesignation ReadTargetDesignation(ref WireReader reader) {
        var index = reader.ReadInt32();
        var point = reader.ReadFixedVector();

        return new WorldTargetDesignation(
            Index: index,
            Point: point
        );
    }
    private static void WritePopulationEntry(WireWriter writer, WorldPopulation.WorldPopulationEntryCheckpoint entry) {
        writer.WriteInt32(value: entry.Index);
        writer.WriteByte(value: entry.KitIndex);
        writer.WriteVector(value: entry.BodyColor);
        writer.WriteByte(value: entry.CatalogRig);
        WriteArray(
            writer: writer,
            items: entry.Designations,
            writeItem: WriteTargetDesignation
        );
        writer.WriteInt32(value: entry.Generation);
        writer.WriteBoolean(value: entry.IsAuthorityTransferred);
        writer.WriteBoolean(value: entry.IsRemoteHuman);
        WriteOptional(
            writer: writer,
            value: entry.Mobility,
            writeValue: WorldWireLeaves.WriteMobility
        );
        writer.WriteInt32(value: entry.MobilityGeneration);
        writer.WriteBoolean(value: entry.Parked);
        WriteOptional(
            writer: writer,
            value: entry.ParkedUntilTick,
            writeValue: static (w, v) => w.WriteInt64(value: v)
        );
        writer.WriteNullableString(value: entry.PlacementId);
        writer.WriteFixedVector(value: entry.SpawnPosition);
        writer.WriteFixed(value: entry.SpawnYaw);
        WriteArray(
            writer: writer,
            items: entry.AdmissionInstalledGrantTemplates,
            writeItem: WriteAdmissionGrant
        );
        WriteArray(
            writer: writer,
            items: entry.AdmissionRevokedKeys,
            writeItem: static (w, row) => {
                WriteCapability(
                    capability: row.Capability,
                    writer: w
                );
                WriteSubject(
                    subject: row.Subject,
                    writer: w
                );
            }
        );
        writer.WriteString(value: entry.IdentityDomain);
        writer.WriteString(value: entry.IdentitySubject);
        writer.WriteInt32(value: entry.ProducerAcquiredTarget);
        writer.WriteFixed(value: entry.ProducerActivityPhase);
        writer.WriteFixed(value: entry.ProducerActivityRate);
        writer.WriteFixed(value: entry.ProducerPhase);
        writer.WriteFixed(value: entry.ProducerPreferredAltitude);
        writer.WriteFixed(value: entry.ProducerWeaveFrequency);
        writer.WriteInt64(value: entry.ProducerCurveArcRaw);
        writer.WriteNullableString(value: entry.ProducerActiveName);
        writer.WriteInt32(value: entry.ProducerActiveCurveIndex);
        writer.WriteFixedVector(value: entry.Position);
        writer.WriteFixed(value: entry.Yaw);
        WriteTransferState(
            writer: writer,
            state: entry.DynamicState
        );
        WriteResidue(
            writer: writer,
            residue: entry.Residue
        );
        WriteOptional(
            writer: writer,
            value: entry.Profile,
            writeValue: WriteIdentityProjection
        );
    }
    private static WorldPopulation.WorldPopulationEntryCheckpoint ReadPopulationEntry(ref WireReader reader) {
        var index = reader.ReadInt32();
        var kitIndex = reader.ReadByte();
        var bodyColor = reader.ReadFiniteVector(field: "population entry body color");
        var catalogRig = reader.ReadByte();
        var designations = ReadArray(
            reader: ref reader,
            field: "population entry designations",
            readItem: static (ref WireReader r) => ReadTargetDesignation(reader: ref r)
        );
        var generation = reader.ReadInt32();
        var isAuthorityTransferred = reader.ReadBoolean();
        var isRemoteHuman = reader.ReadBoolean();
        var mobility = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => WorldWireLeaves.ReadMobility(reader: ref r)
        );
        var mobilityGeneration = reader.ReadInt32();
        var parked = reader.ReadBoolean();
        var parkedUntilTick = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => r.ReadInt64()
        );
        var placementId = reader.ReadNullableString(
            field: "population entry placement id",
            maxBytes: MaxStringBytes
        );
        var spawnPosition = reader.ReadFixedVector();
        var spawnYaw = reader.ReadFixed();
        var admissionInstalledGrantTemplates = ReadArray(
            reader: ref reader,
            field: "population entry admission templates",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r)
        );
        var admissionRevokedKeys = ReadArray(
            reader: ref reader,
            field: "population entry admission revoked keys",
            readItem: static (ref WireReader r) => {
                var capability = ReadCapability(reader: ref r);
                var subject = ReadSubject(reader: ref r);

                return (capability, subject);
            }
        );
        var identityDomain = reader.ReadString(
            field: "population entry identity domain",
            maxBytes: MaxStringBytes
        );
        var identitySubject = reader.ReadString(
            field: "population entry identity subject",
            maxBytes: MaxStringBytes
        );
        var producerAcquiredTarget = reader.ReadInt32();
        var producerActivityPhase = reader.ReadFixed();
        var producerActivityRate = reader.ReadFixed();
        var producerPhase = reader.ReadFixed();
        var producerPreferredAltitude = reader.ReadFixed();
        var producerWeaveFrequency = reader.ReadFixed();
        var producerCurveArcRaw = reader.ReadInt64();
        var producerActiveName = reader.ReadNullableString(
            field: "population entry producer active name",
            maxBytes: MaxStringBytes
        );
        var producerActiveCurveIndex = reader.ReadInt32();
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var dynamicState = ReadTransferState(reader: ref reader);
        var residue = ReadResidue(reader: ref reader);
        var profile = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadIdentityProjection(reader: ref r)
        );

        return new WorldPopulation.WorldPopulationEntryCheckpoint(
            AdmissionInstalledGrantTemplates: admissionInstalledGrantTemplates,
            AdmissionRevokedKeys: admissionRevokedKeys,
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Designations: designations,
            DynamicState: dynamicState,
            Generation: generation,
            IdentityDomain: identityDomain,
            IdentitySubject: identitySubject,
            Index: index,
            IsAuthorityTransferred: isAuthorityTransferred,
            IsRemoteHuman: isRemoteHuman,
            KitIndex: kitIndex,
            Mobility: mobility,
            MobilityGeneration: mobilityGeneration,
            Parked: parked,
            ParkedUntilTick: parkedUntilTick,
            PlacementId: placementId,
            Position: position,
            ProducerAcquiredTarget: producerAcquiredTarget,
            ProducerActivityPhase: producerActivityPhase,
            ProducerActivityRate: producerActivityRate,
            ProducerPhase: producerPhase,
            ProducerPreferredAltitude: producerPreferredAltitude,
            ProducerWeaveFrequency: producerWeaveFrequency,
            ProducerCurveArcRaw: producerCurveArcRaw,
            ProducerActiveName: producerActiveName,
            ProducerActiveCurveIndex: producerActiveCurveIndex,
            Profile: profile,
            Residue: residue,
            SpawnPosition: spawnPosition,
            SpawnYaw: spawnYaw,
            Yaw: yaw
        );
    }
    private static byte[] EncodePopulation(WorldPopulation.WorldPopulationCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.SimulatedCount);
        writer.WriteInt32(value: section.Revision);
        writer.WriteByte(value: section.SeatKit);
        WriteArray(
            writer: writer,
            items: section.Entries,
            writeItem: WritePopulationEntry
        );

        return writer.ToArray();
    }
    private static bool TryDecodePopulation(byte[] bytes, out string reason, out WorldPopulation.WorldPopulationCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var simulatedCount = reader.ReadInt32();
        var revision = reader.ReadInt32();
        var seatKit = reader.ReadByte();
        var entries = ReadArray(
            reader: ref reader,
            field: "population entries",
            readItem: static (ref WireReader r) => ReadPopulationEntry(reader: ref r)
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"population section: {failure}";

            return false;
        }

        section = new WorldPopulation.WorldPopulationCheckpoint(
            Entries: entries,
            Revision: revision,
            SeatKit: seatKit,
            SimulatedCount: simulatedCount
        );
        reason = string.Empty;

        return true;
    }
    // ---- grants section ----

}
