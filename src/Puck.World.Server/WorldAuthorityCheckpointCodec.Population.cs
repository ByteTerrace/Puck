using Puck.Networking;
using Puck.Physics.Navigation;
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
    private static void WritePopulationEntry(WireWriter writer, WorldPopulationEntryCheckpoint entry) {
        writer.WriteInt32(value: entry.Index);
        writer.WriteByte(value: entry.KitIndex);
        writer.WriteVector(value: entry.BodyColor);
        writer.WriteByte(value: entry.CatalogRig);
        writer.WriteArray(
            items: entry.Designations,
            writeItem: WriteTargetDesignation
        );
        writer.WriteInt32(value: entry.Generation);
        writer.WriteBoolean(value: entry.IsAuthorityTransferred);
        writer.WriteBoolean(value: entry.IsRemoteHuman);
        writer.WriteOptional(
            value: entry.Mobility,
            writeValue: WorldWireLeaves.WriteMobility
        );
        writer.WriteInt32(value: entry.MobilityGeneration);
        writer.WriteBoolean(value: entry.Parked);
        writer.WriteOptional(
            value: entry.ParkedUntilTick,
            writeValue: static (w, v) => w.WriteInt64(value: v)
        );
        writer.WriteNullableString(value: entry.PlacementId);
        writer.WriteFixedVector(value: entry.SpawnPosition);
        writer.WriteFixed(value: entry.SpawnYaw);
        writer.WriteArray(
            items: entry.AdmissionInstalledGrantTemplates,
            writeItem: WriteAdmissionGrant
        );
        writer.WriteArray(
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
        writer.WriteBoolean(value: entry.Flock.Seeded);
        writer.WriteInt32(value: entry.Flock.Generation);
        writer.WriteFixedVector(value: entry.Flock.Desired);
        writer.WriteUInt64(value: entry.Flock.RemainingTicks);
        writer.WriteUInt64(value: entry.Flock.SampleOrdinal);
        writer.WriteBoolean(value: (entry.Flock.Target is not null));
        if (entry.Flock.Target is { } observed) {
            writer.WriteInt32(value: observed.Index);
            writer.WriteInt32(value: observed.Generation);
            writer.WriteFixedVector(value: observed.Position);
        }
        writer.WriteUInt64(value: entry.Autonomy.MotionPeriodTicks);
        writer.WriteUInt64(value: entry.Autonomy.MotionElapsedTicks);
        writer.WriteUInt64(value: entry.Autonomy.MotionRemainingTicks);
        writer.WriteUInt64(value: entry.Autonomy.SteeringPeriodTicks);
        writer.WriteUInt64(value: entry.Autonomy.SteeringElapsedTicks);
        writer.WriteUInt64(value: entry.Autonomy.SteeringRemainingTicks);
        WorldWireCodec.WriteIntent(
            writer,
            entry.Autonomy.SteeringIntent
        );
        writer.WriteBoolean(value: entry.Autonomy.SteeringSeeded);
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
        writer.WriteOptional(
            value: entry.Profile,
            writeValue: WriteIdentityProjection
        );
        writer.WriteOptional(
            value: entry.Navigation,
            writeValue: static (w, navigation) => {
                w.WriteInt32(value: navigation.ActiveProducerDomainIndex);
                w.WriteInt32(value: navigation.DomainIndex);
                w.WriteInt32(value: navigation.GoalCell);
                w.WriteInt32(value: navigation.Waypoint);
                w.WriteInt32(value: navigation.ExpandedLast);
                w.WriteByte(value: checked((byte)navigation.Status));
                w.WriteArray(
                    items: navigation.Path,
                    writeItem: static (pathWriter, value) => pathWriter.WriteInt32(value: value)
                );
            }
        );
    }
    private static WorldPopulationEntryCheckpoint ReadPopulationEntry(ref WireReader reader) {
        var index = reader.ReadInt32();
        var kitIndex = reader.ReadByte();
        var bodyColor = reader.ReadFiniteVector(field: "population entry body color");
        var catalogRig = reader.ReadByte();
        var designations = reader.ReadArray(
            field: "population entry designations",
            readItem: static (ref WireReader r) => ReadTargetDesignation(reader: ref r),
            maximum: MaxCollectionCount
        );
        var generation = reader.ReadInt32();
        var isAuthorityTransferred = reader.ReadBoolean();
        var isRemoteHuman = reader.ReadBoolean();
        var mobility = reader.ReadOptional(
            readValue: static (ref WireReader r) => WorldWireLeaves.ReadMobility(reader: ref r)
        );
        var mobilityGeneration = reader.ReadInt32();
        var parked = reader.ReadBoolean();
        var parkedUntilTick = reader.ReadOptional(
            readValue: static (ref WireReader r) => r.ReadInt64()
        );
        var placementId = reader.ReadNullableString(
            field: "population entry placement id",
            maxBytes: MaxStringBytes
        );
        var spawnPosition = reader.ReadFixedVector();
        var spawnYaw = reader.ReadFixed();
        var admissionInstalledGrantTemplates = reader.ReadArray(
            field: "population entry admission templates",
            readItem: static (ref WireReader r) => ReadAdmissionGrant(reader: ref r),
            maximum: MaxCollectionCount
        );
        var admissionRevokedKeys = reader.ReadArray(
            field: "population entry admission revoked keys",
            readItem: static (ref WireReader r) => {
                var capability = WorldWireCodec.ReadCapability(reader: ref r);
                var subject = WorldWireCodec.ReadSubject(reader: ref r);

                return (capability, subject);
            },
            maximum: MaxCollectionCount
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
        var flock = new WorldPopulationFlockCheckpoint(
            reader.ReadBoolean(),
            reader.ReadInt32(),
            reader.ReadFixedVector(),
            reader.ReadUInt64(),
            reader.ReadUInt64()
        );

        if (reader.ReadBoolean()) {
            flock = flock with {
                Target = new WorldFlockObservation(
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadFixedVector()
            ),
            };
        }
        var autonomy = new WorldPopulationAutonomyCheckpoint(
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
        var profile = reader.ReadOptional(
            readValue: static (ref WireReader r) => ReadIdentityProjection(reader: ref r)
        );
        var navigation = reader.ReadOptional(
            readValue: static (ref WireReader r) => new WorldPopulationNavigationCheckpoint(
                ActiveProducerDomainIndex: r.ReadInt32(),
                DomainIndex: r.ReadInt32(),
                GoalCell: r.ReadInt32(),
                Waypoint: r.ReadInt32(),
                ExpandedLast: r.ReadInt32(),
                Status: ((NavigationStatus)r.ReadByte()),
                Path: r.ReadArray(
                    field: "population entry navigation path",
                    readItem: static (ref WireReader pathReader) => pathReader.ReadInt32(),
                    maximum: WorldNavigationCapacity.MaxPathNodes
                )
            )
        );

        return new WorldPopulationEntryCheckpoint(
            AdmissionInstalledGrantTemplates: admissionInstalledGrantTemplates,
            AdmissionRevokedKeys: admissionRevokedKeys,
            Autonomy: autonomy,
            BodyColor: bodyColor,
            CatalogRig: catalogRig,
            Designations: designations,
            DynamicState: dynamicState,
            Flock: flock,
            Generation: generation,
            IdentityDomain: identityDomain,
            IdentitySubject: identitySubject,
            Index: index,
            IsAuthorityTransferred: isAuthorityTransferred,
            IsRemoteHuman: isRemoteHuman,
            KitIndex: kitIndex,
            Mobility: mobility,
            MobilityGeneration: mobilityGeneration,
            Navigation: navigation,
            Parked: parked,
            ParkedUntilTick: parkedUntilTick,
            PlacementId: placementId,
            Position: position,
            ProducerAcquiredTarget: producerAcquiredTarget,
            ProducerActiveCurveIndex: producerActiveCurveIndex,
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
            Yaw: yaw
        );
    }
    private static byte[] EncodePopulation(WorldPopulationCheckpoint section) {
        var writer = new WireWriter();

        writer.WriteInt32(value: section.SimulatedCount);
        writer.WriteInt32(value: section.Revision);
        writer.WriteByte(value: section.SeatKit);
        writer.WriteArray(
            items: section.Generations,
            writeItem: static (w, generation) => w.WriteInt32(value: generation)
        );
        writer.WriteBoolean(value: (section.SharedNavigation is not null));
        if (section.SharedNavigation is { } domains) {
            writer.WriteArray(
                items: domains,
                writeItem: static (w, domain) => {
                    w.WriteInt32(value: domain.Cursor);
                    w.WriteArray(
                        items: domain.Trees,
                        writeItem: static (treeWriter, tree) => {
                            treeWriter.WriteInt32(value: tree.Goal);
                            treeWriter.WriteInt32(value: tree.Age);
                            treeWriter.WriteArray(
                                items: tree.Nodes,
                                writeItem: static (nodeWriter, node) => {
                                    nodeWriter.WriteInt32(value: node.Node);
                                    nodeWriter.WriteInt32(value: node.Cost);
                                    nodeWriter.WriteInt32(value: node.Next);
                                    nodeWriter.WriteBoolean(value: node.Settled);
                                }
                            );
                            treeWriter.WriteArray(
                                items: tree.Pending,
                                writeItem: static (pendingWriter, node) => pendingWriter.WriteInt32(value: node)
                            );
                        }
                    );
                }
            );
        }
        writer.WriteArray(
            items: section.Entries,
            writeItem: WritePopulationEntry
        );

        return writer.ToArray();
    }
    private static bool TryDecodePopulation(byte[] bytes, out string reason, out WorldPopulationCheckpoint section) {
        var reader = new WireReader(bytes: bytes);
        var simulatedCount = reader.ReadInt32();
        var revision = reader.ReadInt32();
        var seatKit = reader.ReadByte();
        var generations = reader.ReadArray(
            field: "population slot generations",
            readItem: static (ref WireReader r) => r.ReadInt32(),
            maximum: WorldBodiesLimits.CapacityCeiling
        );
        var shared = (reader.ReadBoolean()
            ? reader.ReadArray(
                field: "shared navigation domains",
                readItem: static (ref WireReader domainReader) => {
                    var cursor = domainReader.ReadInt32();
                    var trees = domainReader.ReadArray(
                        field: "shared navigation trees",
                        readItem: static (ref WireReader treeReader) => {
                            var goal = treeReader.ReadInt32();
                            var age = treeReader.ReadInt32();
                            var nodes = treeReader.ReadArray(
                                field: "shared navigation nodes",
                                readItem: static (ref WireReader nodeReader) =>
                        new NavigationTreeNode(
                            nodeReader.ReadInt32(),
                            nodeReader.ReadInt32(),
                            nodeReader.ReadInt32(),
                            nodeReader.ReadBoolean()
                        ),
                                maximum: WorldNavigationCapacity.MaxCellsPerDomain
                            );
                            var pending = treeReader.ReadArray(
                                field: "shared navigation pending starts",
                                readItem: static (ref WireReader pendingReader) => pendingReader.ReadInt32(),
                                maximum: WorldBodiesLimits.CapacityCeiling
                            );

                            return new NavigationTreeCheckpoint(
                        Age: age,
                        Goal: goal,
                        Nodes: nodes,
                        Pending: pending
                    );
                        },
                        maximum: WorldNavigationCapacity.MaxSharedGoals
                    );

                    return new NavigationSharedCheckpoint(
                        Cursor: cursor,
                        Trees: trees
                    );
                },
                maximum: WorldNavigationCapacity.MaxDomains
            )
            : null
        );
        var entries = reader.ReadArray(
            field: "population entries",
            readItem: static (ref WireReader r) => ReadPopulationEntry(reader: ref r),
            maximum: MaxCollectionCount
        );

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"population section: {failure}";

            return false;
        }

        section = new WorldPopulationCheckpoint(
            Entries: entries,
            Generations: generations,
            Revision: revision,
            SeatKit: seatKit,
            SharedNavigation: shared,
            SimulatedCount: simulatedCount
        );
        reason = string.Empty;

        return true;
    }
    // ---- grants section ----

}
