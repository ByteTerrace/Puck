using Puck.Networking;
using Puck.World.Protocol;

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
        writer.WriteBoolean(entry.Flock.Seeded);
        writer.WriteInt32(entry.Flock.Generation);
        writer.WriteFixedVector(entry.Flock.Desired);
        writer.WriteUInt64(entry.Flock.RemainingTicks);
        writer.WriteUInt64(entry.Flock.SampleOrdinal);
        writer.WriteBoolean(entry.Flock.Target is not null);
        if (entry.Flock.Target is { } observed) {
            writer.WriteInt32(observed.Index);
            writer.WriteInt32(observed.Generation);
            writer.WriteFixedVector(observed.Position);
        }
        writer.WriteUInt64(entry.Autonomy.MotionPeriodTicks);
        writer.WriteUInt64(entry.Autonomy.MotionElapsedTicks);
        writer.WriteUInt64(entry.Autonomy.MotionRemainingTicks);
        writer.WriteUInt64(entry.Autonomy.SteeringPeriodTicks);
        writer.WriteUInt64(entry.Autonomy.SteeringElapsedTicks);
        writer.WriteUInt64(entry.Autonomy.SteeringRemainingTicks);
        WorldWireCodec.WriteIntent(writer, entry.Autonomy.SteeringIntent);
        writer.WriteBoolean(entry.Autonomy.SteeringSeeded);
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
        WriteOptional(
            writer: writer,
            value: entry.Navigation,
            writeValue: static (w, navigation) => {
                w.WriteInt32(value: navigation.ActiveProducerDomainIndex);
                w.WriteInt32(value: navigation.DomainIndex);
                w.WriteInt32(value: navigation.GoalCell);
                w.WriteInt32(value: navigation.Waypoint);
                w.WriteInt32(value: navigation.ExpandedLast);
                w.WriteByte(value: checked((byte)navigation.Status));
                WorldAuthorityCheckpointCodec.WriteArray(writer: w, items: navigation.Path, writeItem: static (pathWriter, value) => pathWriter.WriteInt32(value: value));
            }
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
        var flock = new WorldPopulation.WorldPopulationFlockCheckpoint(reader.ReadBoolean(), reader.ReadInt32(),
            reader.ReadFixedVector(), reader.ReadUInt64(), reader.ReadUInt64());
        if (reader.ReadBoolean()) {
            flock = flock with { Target = new WorldFlockObservation(reader.ReadInt32(), reader.ReadInt32(), reader.ReadFixedVector()) };
        }
        var autonomy = new WorldPopulation.WorldPopulationAutonomyCheckpoint(
            MotionPeriodTicks: reader.ReadUInt64(),
            MotionElapsedTicks: reader.ReadUInt64(),
            MotionRemainingTicks: reader.ReadUInt64(),
            SteeringPeriodTicks: reader.ReadUInt64(),
            SteeringElapsedTicks: reader.ReadUInt64(),
            SteeringRemainingTicks: reader.ReadUInt64(),
            SteeringIntent: WorldWireCodec.ReadIntent(reader: ref reader),
            SteeringSeeded: reader.ReadBoolean()
        );
        var position = reader.ReadFixedVector();
        var yaw = reader.ReadFixed();
        var dynamicState = ReadTransferState(reader: ref reader);
        var residue = ReadResidue(reader: ref reader);
        var profile = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => ReadIdentityProjection(reader: ref r)
        );
        var navigation = ReadOptional(
            reader: ref reader,
            readValue: static (ref WireReader r) => new WorldPopulation.WorldPopulationNavigationCheckpoint(
                ActiveProducerDomainIndex: r.ReadInt32(),
                DomainIndex: r.ReadInt32(),
                GoalCell: r.ReadInt32(),
                Waypoint: r.ReadInt32(),
                ExpandedLast: r.ReadInt32(),
                Status: (WorldNavigationStatus)r.ReadByte(),
                Path: WorldAuthorityCheckpointCodec.ReadArray(
                    reader: ref r,
                    field: "population entry navigation path",
                    readItem: static (ref WireReader pathReader) => pathReader.ReadInt32(),
                    maximum: WorldNavigationCapacity.MaxPathNodes
                )
            )
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
            ProducerActiveCurveIndex: producerActiveCurveIndex,
            Flock: flock,
            Autonomy: autonomy,
            ProducerActiveName: producerActiveName,
            ProducerActivityPhase: producerActivityPhase,
            ProducerActivityRate: producerActivityRate,
            ProducerCurveArcRaw: producerCurveArcRaw,
            ProducerPhase: producerPhase,
            ProducerPreferredAltitude: producerPreferredAltitude,
            ProducerWeaveFrequency: producerWeaveFrequency,
            Profile: profile,
            Residue: residue,
            SpawnPosition: spawnPosition,
            SpawnYaw: spawnYaw,
            Yaw: yaw,
            Navigation: navigation
        );
    }
    private static byte[] EncodePopulation(WorldPopulation.WorldPopulationCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.SimulatedCount);
        writer.WriteInt32(value: section.Revision);
        writer.WriteByte(value: section.SeatKit);
        WriteArray(writer, section.Generations, static (w, generation) => w.WriteInt32(generation));
        writer.WriteBoolean(section.SharedNavigation is not null);
        if (section.SharedNavigation is { } domains) {
            WriteArray(writer, domains, static (w, domain) => {
                w.WriteInt32(domain.Cursor);
                WriteArray(w, domain.Trees, static (treeWriter, tree) => {
                    treeWriter.WriteInt32(tree.Goal);
                    treeWriter.WriteInt32(tree.Age);
                    WriteArray(treeWriter, tree.Nodes, static (nodeWriter, node) => {
                        nodeWriter.WriteInt32(node.Node);
                        nodeWriter.WriteInt32(node.Cost);
                        nodeWriter.WriteInt32(node.Next);
                        nodeWriter.WriteBoolean(node.Settled);
                    });
                    WriteArray(treeWriter, tree.Pending, static (pendingWriter, node) => pendingWriter.WriteInt32(node));
                });
            });
        }
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
        var generations = ReadArray(ref reader, "population slot generations", static (ref WireReader r) => r.ReadInt32(), maximum: WorldBodiesLimits.CapacityCeiling);
        var shared = reader.ReadBoolean() ? ReadArray(ref reader, "shared navigation domains", static (ref WireReader domainReader) => {
            var cursor = domainReader.ReadInt32();
            var trees = ReadArray(ref domainReader, "shared navigation trees", static (ref WireReader treeReader) => {
                var goal = treeReader.ReadInt32();
                var age = treeReader.ReadInt32();
                var nodes = ReadArray(ref treeReader, "shared navigation nodes", static (ref WireReader nodeReader) =>
                    new WorldNavigationTreeNode(nodeReader.ReadInt32(), nodeReader.ReadInt32(), nodeReader.ReadInt32(), nodeReader.ReadBoolean()),
                    maximum: WorldNavigationCapacity.MaxCellsPerDomain);
                var pending = ReadArray(ref treeReader, "shared navigation pending starts", static (ref WireReader pendingReader) => pendingReader.ReadInt32(),
                    maximum: WorldBodiesLimits.CapacityCeiling);
                return new WorldNavigationTreeCheckpoint(goal, age, nodes, pending);
            }, maximum: WorldNavigationCapacity.MaxSharedGoals);
            return new WorldNavigationSharedCheckpoint(cursor, trees);
        }, maximum: WorldNavigationCapacity.MaxDomains) : null;
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
            Generations: generations,
            SharedNavigation: shared,
            Revision: revision,
            SeatKit: seatKit,
            SimulatedCount: simulatedCount
        );
        reason = string.Empty;

        return true;
    }
    // ---- grants section ----

}
