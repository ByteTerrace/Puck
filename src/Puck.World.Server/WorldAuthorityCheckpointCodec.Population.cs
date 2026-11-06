using Puck.Maths;
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

    /// <summary>Folds the simulation continuation of captured population entries through the checkpoint's field codecs,
    /// excluding rendered appearance: the value <see cref="WorldPopulation.AppendContinuationHash"/> folds from the live
    /// slots the entries were captured from.</summary>
    /// <param name="hash">The hash to fold into.</param>
    /// <param name="entries">The entries, in slot order.</param>
    /// <param name="capacity">The population's slot count; every entry's index is below it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="entries"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An entry's index is outside <paramref name="capacity"/>.</exception>
    public static void AppendPopulationEntries(ref Fnv1aHash hash, IReadOnlyList<WorldPopulationEntryCheckpoint> entries, int capacity) {
        ArgumentNullException.ThrowIfNull(argument: entries);

        var writer = new WireWriter();
        var digests = new ulong[capacity];
        var present = new bool[capacity];

        for (var index = 0; (index < entries.Count); index++) {
            var entry = entries[index];

            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(other: capacity, value: entry.Index);
            present[entry.Index] = true;
            digests[entry.Index] = ContinuationDigest(entry: entry, writer: writer);
        }
        FoldContinuationSegments(digests: digests, hash: ref hash, present: present);
    }
    // One slot's continuation digest: the entry's continuation bytes, hashed alone.
    internal static ulong ContinuationDigest(WireWriter writer, in WorldPopulationEntryCheckpoint entry) {
        writer.Reset();
        WriteContinuationEntry(writer: writer, entry: entry);
        return Fnv1aHash.Compute(values: writer.WrittenSpan);
    }
    // The continuation's shape: the slot count, then one digest per segment of WorldPopulation.HashSegmentSlots slots,
    // each over its slots' presence and the present slots' digests.
    internal static void FoldContinuationSegments(ref Fnv1aHash hash, ReadOnlySpan<ulong> digests, ReadOnlySpan<bool> present) {
        hash.Add(value: ((uint)digests.Length));
        for (var first = 0; (first < digests.Length); first += WorldPopulation.HashSegmentSlots) {
            var segment = Fnv1aHash.Create();
            var end = Math.Min(val1: (first + WorldPopulation.HashSegmentSlots), val2: digests.Length);

            for (var index = first; (index < end); index++) {
                segment.Add(value: (present[index] ? ((byte)1) : ((byte)0)));
                if (present[index]) { segment.Add(value: digests[index]); }
            }
            hash.Add(value: segment.Value);
        }
    }

    /// <summary>Writes one entry's simulation continuation: its checkpoint encoding without the rendered
    /// appearance.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="entry">The entry.</param>
    internal static void WriteContinuationEntry(WireWriter writer, in WorldPopulationEntryCheckpoint entry) => WritePopulationEntry(
        entry: in entry,
        includeAppearance: false,
        writer: writer
    );

    private static void WritePopulationEntry(WireWriter writer, WorldPopulationEntryCheckpoint entry) =>
        WritePopulationEntry(entry: entry, includeAppearance: true, writer: writer);
    private static void WritePopulationEntry(WireWriter writer, in WorldPopulationEntryCheckpoint entry, bool includeAppearance) {
        writer.WriteInt32(value: entry.Index);
        writer.WriteByte(value: entry.KitIndex);
        writer.WriteInt32(value: entry.SelectedKit);
        writer.WriteFixed(value: entry.Scale);
        if (includeAppearance) {
            writer.WriteInt32(value: entry.SelectedLook);
            writer.WriteVector(value: entry.BodyColor);
            writer.WriteByte(value: entry.CatalogRig);
            // The arrival turn a seat's view carries across crossings: presentation, which the simulation never reads.
            writer.WriteFixed(value: entry.TravelTurn);
        }
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
        writer.WriteBoolean(value: entry.Titled);
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
        writer.WriteArray(
            items: entry.RigidContacts,
            writeItem: static (w, slot) => {
                w.WriteInt32(value: slot.Partner);
                w.WriteInt32(value: slot.Slot);
                w.WriteInt32(value: slot.SourceId);
                w.WriteInt32(value: slot.FeatureId);
                w.WriteFixedVector(value: slot.AnchorA);
                w.WriteFixedVector(value: slot.AnchorB);
                w.WriteFixedVector(value: slot.Normal);
                w.WriteInt64(value: slot.NormalImpulseRaw);
                w.WriteFixedVector(value: slot.FrictionImpulse);
                w.WriteInt32(value: slot.IdleSteps);
            }
        );
        WorldIdentityProjectionWire.WriteOptional(
            projection: entry.Profile,
            writer: writer
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
        var selectedKit = reader.ReadInt32();
        var scale = reader.ReadFixed();
        var selectedLook = reader.ReadInt32();
        var bodyColor = reader.ReadFiniteVector(field: "population entry body color");
        var catalogRig = reader.ReadByte();
        var travelTurn = WorldWireLeaves.ReadTravelTurn(
            field: "population entry travel turn",
            reader: ref reader
        );
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
        var titled = reader.ReadBoolean();
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
        var rigidContacts = reader.ReadArray(
            field: "population entry rigid contacts",
            readItem: static (ref WireReader r) => new WorldRigidContactSlotImage(
                Partner: r.ReadInt32(),
                Slot: r.ReadInt32(),
                SourceId: r.ReadInt32(),
                FeatureId: r.ReadInt32(),
                AnchorA: r.ReadFixedVector(),
                AnchorB: r.ReadFixedVector(),
                Normal: r.ReadFixedVector(),
                NormalImpulseRaw: r.ReadInt64(),
                FrictionImpulse: r.ReadFixedVector(),
                IdleSteps: r.ReadInt32()
            ),
            maximum: WorldPopulation.MaximumRigidContactSlotsPerBody
        );
        var profile = WorldIdentityProjectionWire.ReadOptional(reader: ref reader);
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
            Titled: titled,
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
            RigidContacts: rigidContacts,
            Scale: scale,
            SelectedKit: selectedKit,
            SelectedLook: selectedLook,
            SpawnPosition: spawnPosition,
            SpawnYaw: spawnYaw,
            TravelTurn: travelTurn,
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
        writer.WriteArray(items: section.EventBodies, writeItem: static (w, index) => w.WriteInt32(value: index));
        writer.WriteArray(items: section.SleepingGravity, writeItem: static (w, row) => {
            w.WriteInt32(value: row.Body);
            w.WriteOptional(value: row.Acceleration, writeValue: static (v, acceleration) => v.WriteFixedVector(value: acceleration));
        });
        writer.WriteArray(items: (section.AttachedSolids ?? []), writeItem: static (w, row) => {
            w.WriteBoolean(value: row.Resolved); w.WriteFixedVector(value: row.Origin); w.WriteFixedQuaternion(value: row.Rotation);
        });

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
        var eventBodies = reader.ReadArray(field: "population event bodies", maximum: WorldBodiesLimits.CapacityCeiling,
            readItem: static (ref WireReader r) => r.ReadInt32());
        var gravity = reader.ReadArray(field: "population sleeping gravity", maximum: WorldBodiesLimits.CapacityCeiling,
            readItem: static (ref WireReader r) => (r.ReadInt32(), r.ReadOptional(readValue: static (ref WireReader v) => v.ReadFixedVector())));
        var attached = reader.ReadArray(field: "population attached solids", maximum: MaxCollectionCount,
            readItem: static (ref WireReader r) => new WorldAttachedSolidCheckpoint(r.ReadBoolean(), r.ReadFixedVector(), r.ReadFixedQuaternion()));

        if (!reader.TryFinish(failure: out var failure)) {
            section = null!;
            reason = $"population section: {failure}";

            return false;
        }

        section = new WorldPopulationCheckpoint(
            AttachedSolids: attached,
            Entries: entries,
            EventBodies: eventBodies,
            Generations: generations,
            Revision: revision,
            SeatKit: seatKit,
            SharedNavigation: shared,
            SimulatedCount: simulatedCount,
            SleepingGravity: gravity
        );
        reason = string.Empty;

        return true;
    }
    // ---- grants section ----

}
