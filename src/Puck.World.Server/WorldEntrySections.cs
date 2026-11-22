using System.Numerics;
using Puck.Maths;
using Puck.Networking;
using Puck.Physics.Navigation;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Who occupies a slot and under what admission: the occupant's generation and kind flags, its kit, its
/// placement and mobility identity, its park state, where it spawns, the turn its arrivals have accumulated, and the
/// admission identity and grants a remote occupant holds.</summary>
/// <param name="Generation">The slot's occupant generation.</param>
/// <param name="IsAuthorityTransferred">Whether the occupant arrived through an authority transfer.</param>
/// <param name="IsRemoteHuman">Whether the slot is bound to a remote-admitted human connection.</param>
/// <param name="KitIndex">The kit row the assignment policy resolved for the slot.</param>
/// <param name="SelectedKit">The kit row the body's own selector cell picks, or -1.</param>
/// <param name="Mobility">The occupant's cross-authority incarnation and epoch.</param>
/// <param name="MobilityGeneration">The local generation whose occupant owns <paramref name="Mobility"/>.</param>
/// <param name="Parked">Whether the occupant is parked awaiting a reconnect.</param>
/// <param name="ParkedUntilTick">The tick the park expires at, or <see langword="null"/> for a park with no expiry.</param>
/// <param name="PlacementId">The placement row the occupant inhabits.</param>
/// <param name="Titled">Whether the occupant was spawned as an owned traveller.</param>
/// <param name="SpawnPosition">The position a fresh activation places the body at.</param>
/// <param name="SpawnYaw">The heading a fresh activation faces the body toward.</param>
/// <param name="TravelTurn">The turn the occupant's mapped arrivals have accumulated, which every later crossing
/// advances.</param>
/// <param name="InstalledGrantTemplates">The admission templates that reached the live grant table.</param>
/// <param name="RevokedKeys">The explicit runtime revocations, ascending by capability then subject.</param>
/// <param name="IdentityDomain">The verified admission identity's domain.</param>
/// <param name="IdentitySubject">The verified admission identity's subject.</param>
/// <param name="AdmissionTrust">The admission's trust mode and pinned identity.</param>
/// <param name="AdmissionTier">The connection's independent disclosure entitlement.</param>
public readonly record struct EntryAdmissionSection(
    int Generation,
    bool IsAuthorityTransferred,
    bool IsRemoteHuman,
    byte KitIndex,
    int SelectedKit,
    WorldMobilityIdentity? Mobility,
    int MobilityGeneration,
    bool Parked,
    long? ParkedUntilTick,
    string? PlacementId,
    bool Titled,
    FixedVector3 SpawnPosition,
    FixedQ4816 SpawnYaw,
    FixedQ4816 TravelTurn,
    IReadOnlyList<WorldAdmissionGrant> InstalledGrantTemplates,
    IReadOnlyList<(WorldCapability Capability, GrantSubject Subject)> RevokedKeys,
    string IdentityDomain,
    string IdentitySubject,
    WorldAdmissionTrustIdentity? AdmissionTrust,
    WorldDisclosureTier AdmissionTier = WorldDisclosureTier.Frames
) : IContinuationSection<EntryAdmissionSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Admission;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryAdmissionSection section) {
        writer.WriteInt32(value: section.Generation);
        writer.WriteBoolean(value: section.IsAuthorityTransferred);
        writer.WriteBoolean(value: section.IsRemoteHuman);
        writer.WriteByte(value: section.KitIndex);
        writer.WriteInt32(value: section.SelectedKit);
        writer.WriteOptional(
            value: section.Mobility,
            writeValue: WorldWireLeaves.WriteMobility
        );
        writer.WriteInt32(value: section.MobilityGeneration);
        writer.WriteBoolean(value: section.Parked);
        writer.WriteOptional(
            value: section.ParkedUntilTick,
            writeValue: static (w, v) => w.WriteInt64(value: v)
        );
        writer.WriteNullableString(value: section.PlacementId);
        writer.WriteBoolean(value: section.Titled);
        writer.WriteFixedVector(value: section.SpawnPosition);
        writer.WriteFixed(value: section.SpawnYaw);
        writer.WriteFixed(value: section.TravelTurn);
        writer.WriteArray(
            items: (section.InstalledGrantTemplates ?? []),
            writeItem: WorldAuthorityCheckpointCodec.WriteAdmissionGrant
        );
        writer.WriteArray(
            items: (section.RevokedKeys ?? []),
            writeItem: static (w, row) => {
                WorldWireCodec.WriteCapability(capability: row.Capability, writer: w);
                WorldWireCodec.WriteSubject(subject: row.Subject, writer: w);
            }
        );
        writer.WriteString(value: section.IdentityDomain);
        writer.WriteString(value: section.IdentitySubject);
        WorldWireLeaves.WriteAdmissionTrust(trust: section.AdmissionTrust, writer: writer);
        writer.WriteByte((byte)section.AdmissionTier);
    }
    /// <inheritdoc/>
    public static EntryAdmissionSection Read(ref WireReader reader) => new(
        Generation: reader.ReadInt32(),
        IsAuthorityTransferred: reader.ReadBoolean(),
        IsRemoteHuman: reader.ReadBoolean(),
        KitIndex: reader.ReadByte(),
        SelectedKit: reader.ReadInt32(),
        Mobility: reader.ReadOptional(readValue: static (ref WireReader r) => WorldWireLeaves.ReadMobility(reader: ref r)),
        MobilityGeneration: reader.ReadInt32(),
        Parked: reader.ReadBoolean(),
        ParkedUntilTick: reader.ReadOptional(readValue: static (ref WireReader r) => r.ReadInt64()),
        PlacementId: reader.ReadNullableString(field: "population entry placement id"),
        Titled: reader.ReadBoolean(),
        SpawnPosition: reader.ReadFixedVector(),
        SpawnYaw: reader.ReadFixed(),
        TravelTurn: WorldWireLeaves.ReadTravelTurn(field: "population entry travel turn", reader: ref reader),
        InstalledGrantTemplates: reader.ReadArray(
            field: "population entry admission templates",
            maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
            readItem: static (ref WireReader r) => WorldAuthorityCheckpointCodec.ReadAdmissionGrant(reader: ref r)
        ),
        RevokedKeys: reader.ReadArray(
            field: "population entry admission revoked keys",
            maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
            readItem: static (ref WireReader r) => {
                var capability = WorldWireCodec.ReadCapability(reader: ref r);
                var subject = WorldWireCodec.ReadSubject(reader: ref r);

                return (capability, subject);
            }
        ),
        IdentityDomain: reader.ReadString(field: "population entry identity domain"),
        IdentitySubject: reader.ReadString(field: "population entry identity subject"),
        AdmissionTrust: WorldWireLeaves.ReadAdmissionTrust(reader: ref reader),
        AdmissionTier: (WorldDisclosureTier)reader.ReadByte()
    );
}
/// <summary>The targets a slot's occupant has designated, one per designation register.</summary>
/// <param name="Targets">The designations, in register order.</param>
public readonly record struct EntryDesignationsSection(ReadOnlyMemory<WorldTargetDesignation> Targets) : IContinuationTableSection<EntryDesignationsSection, WorldTargetDesignation> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Designations;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryDesignationsSection section) =>
        WriteTable(elements: section.Targets.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<WorldTargetDesignation> elements) {
        writer.WriteArrayCount(count: elements.Length);
        for (var index = 0; (index < elements.Length); index++) {
            WorldAuthorityCheckpointCodec.WriteTargetDesignation(target: elements[index], writer: writer);
        }
    }
    /// <inheritdoc/>
    public static EntryDesignationsSection Read(ref WireReader reader) => new(Targets: reader.ReadArray(
        field: "population entry designations",
        maximum: WorldAuthorityCheckpointCodec.MaxCollectionCount,
        readItem: static (ref WireReader r) => WorldAuthorityCheckpointCodec.ReadTargetDesignation(reader: ref r)
    ));
}
/// <summary>How a slot's occupant is drawn. A checkpoint captures and restores it; it is no part of the simulation
/// continuation, so no digest folds it.</summary>
/// <param name="SelectedLook">The look row the body's own selector cell picks, or -1.</param>
/// <param name="BodyColor">The rendered body colour.</param>
/// <param name="CatalogRig">The occupant's implicit procedural rig.</param>
public readonly record struct EntryLookSection(
    int SelectedLook,
    Vector3 BodyColor,
    byte CatalogRig
) : IContinuationSection<EntryLookSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Look;
    /// <inheritdoc/>
    public static bool Folded => false;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryLookSection section) {
        writer.WriteInt32(value: section.SelectedLook);
        writer.WriteVector(value: section.BodyColor);
        writer.WriteByte(value: section.CatalogRig);
    }
    /// <inheritdoc/>
    public static EntryLookSection Read(ref WireReader reader) => new(
        SelectedLook: reader.ReadInt32(),
        BodyColor: reader.ReadFiniteVector(field: "population entry body color"),
        CatalogRig: reader.ReadByte()
    );
}
/// <summary>A slot's producer and flock runtime: the active producer's phases, curve progress and bindings, and the
/// cached neighbour perception a flocking producer steers by.</summary>
/// <param name="AcquiredTarget">The population index of the acquired target, or -1.</param>
/// <param name="ActivityPhase">The activity oscillator's phase.</param>
/// <param name="ActivityRate">The activity oscillator's rate.</param>
/// <param name="Phase">The producer's own phase.</param>
/// <param name="PreferredAltitude">The altitude the producer holds.</param>
/// <param name="WeaveFrequency">The weave oscillator's frequency.</param>
/// <param name="CurveArcRaw">A curve-follow target's travelled arc length, a Q32 raw.</param>
/// <param name="ActiveName">The producer the curve progress was last advanced under.</param>
/// <param name="ActiveCurveIndex">That producer's compiled curve, or -1 when it follows none.</param>
/// <param name="ActiveNavigationDomainIndex">The navigation domain that producer resolves through, or -1.</param>
/// <param name="Flock">The cached perception, its cadence and its attention stream.</param>
public readonly record struct EntryProducerSection(
    int AcquiredTarget,
    FixedQ4816 ActivityPhase,
    FixedQ4816 ActivityRate,
    FixedQ4816 Phase,
    FixedQ4816 PreferredAltitude,
    FixedQ4816 WeaveFrequency,
    long CurveArcRaw,
    string? ActiveName,
    int ActiveCurveIndex,
    int ActiveNavigationDomainIndex,
    WorldPopulationFlockCheckpoint Flock
) : IContinuationSection<EntryProducerSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Producer;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryProducerSection section) {
        writer.WriteInt32(value: section.AcquiredTarget);
        writer.WriteFixed(value: section.ActivityPhase);
        writer.WriteFixed(value: section.ActivityRate);
        writer.WriteFixed(value: section.Phase);
        writer.WriteFixed(value: section.PreferredAltitude);
        writer.WriteFixed(value: section.WeaveFrequency);
        writer.WriteInt64(value: section.CurveArcRaw);
        writer.WriteNullableString(value: section.ActiveName);
        writer.WriteInt32(value: section.ActiveCurveIndex);
        writer.WriteInt32(value: section.ActiveNavigationDomainIndex);
        writer.WriteBoolean(value: section.Flock.Seeded);
        writer.WriteInt32(value: section.Flock.Generation);
        writer.WriteFixedVector(value: section.Flock.Desired);
        writer.WriteUInt64(value: section.Flock.RemainingTicks);
        writer.WriteUInt64(value: section.Flock.SampleOrdinal);
        writer.WriteBoolean(value: (section.Flock.Target is not null));
        if (section.Flock.Target is { } observed) {
            writer.WriteInt32(value: observed.Index);
            writer.WriteInt32(value: observed.Generation);
            writer.WriteFixedVector(value: observed.Position);
        }
    }
    /// <inheritdoc/>
    public static EntryProducerSection Read(ref WireReader reader) => new(
        AcquiredTarget: reader.ReadInt32(),
        ActivityPhase: reader.ReadFixed(),
        ActivityRate: reader.ReadFixed(),
        Phase: reader.ReadFixed(),
        PreferredAltitude: reader.ReadFixed(),
        WeaveFrequency: reader.ReadFixed(),
        CurveArcRaw: reader.ReadInt64(),
        ActiveName: reader.ReadNullableString(field: "population entry producer active name"),
        ActiveCurveIndex: reader.ReadInt32(),
        ActiveNavigationDomainIndex: reader.ReadInt32(),
        Flock: new WorldPopulationFlockCheckpoint(
            Seeded: reader.ReadBoolean(),
            Generation: reader.ReadInt32(),
            Desired: reader.ReadFixedVector(),
            RemainingTicks: reader.ReadUInt64(),
            SampleOrdinal: reader.ReadUInt64(),
            Target: (reader.ReadBoolean()
                ? new WorldFlockObservation(Index: reader.ReadInt32(), Generation: reader.ReadInt32(), Position: reader.ReadFixedVector())
                : null)
        )
    );
}
/// <summary>A non-human body's reusable steering image: the producer intent its steering cadence last computed.</summary>
/// <param name="Intent">The cached steering intent.</param>
/// <param name="Seeded">Whether <paramref name="Intent"/> has been computed.</param>
public readonly record struct EntrySteeringSection(PlayerIntent Intent, bool Seeded) : IContinuationSection<EntrySteeringSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Steering;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntrySteeringSection section) {
        WorldWireCodec.WriteIntent(intent: section.Intent, writer: writer);
        writer.WriteBoolean(value: section.Seeded);
    }
    /// <inheritdoc/>
    public static EntrySteeringSection Read(ref WireReader reader) => new(
        Intent: WorldWireCodec.ReadIntent(reader: ref reader),
        Seeded: reader.ReadBoolean()
    );
}
/// <summary>A non-human body's phased motion and steering cadence, in engine ticks.</summary>
/// <param name="MotionPeriodTicks">The motion cadence's period, or 0 when the kit authors none.</param>
/// <param name="MotionElapsedTicks">The ticks elapsed within the motion period.</param>
/// <param name="MotionRemainingTicks">The ticks until the next motion update.</param>
/// <param name="SteeringPeriodTicks">The steering cadence's period, or 0 when the kit authors none.</param>
/// <param name="SteeringElapsedTicks">The ticks elapsed within the steering period.</param>
/// <param name="SteeringRemainingTicks">The ticks until the next steering update.</param>
public readonly record struct EntryCadenceSection(
    ulong MotionPeriodTicks,
    ulong MotionElapsedTicks,
    ulong MotionRemainingTicks,
    ulong SteeringPeriodTicks,
    ulong SteeringElapsedTicks,
    ulong SteeringRemainingTicks
) : IContinuationSection<EntryCadenceSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Cadence;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryCadenceSection section) {
        writer.WriteUInt64(value: section.MotionPeriodTicks);
        writer.WriteUInt64(value: section.MotionElapsedTicks);
        writer.WriteUInt64(value: section.MotionRemainingTicks);
        writer.WriteUInt64(value: section.SteeringPeriodTicks);
        writer.WriteUInt64(value: section.SteeringElapsedTicks);
        writer.WriteUInt64(value: section.SteeringRemainingTicks);
    }
    /// <inheritdoc/>
    public static EntryCadenceSection Read(ref WireReader reader) => new(
        MotionPeriodTicks: reader.ReadUInt64(),
        MotionElapsedTicks: reader.ReadUInt64(),
        MotionRemainingTicks: reader.ReadUInt64(),
        SteeringPeriodTicks: reader.ReadUInt64(),
        SteeringElapsedTicks: reader.ReadUInt64(),
        SteeringRemainingTicks: reader.ReadUInt64()
    );
}
/// <summary>A body's cached route, without its path (<see cref="EntryRouteSection"/>).</summary>
/// <param name="DomainIndex">The navigation domain the route was searched in, or -1.</param>
/// <param name="GoalCell">The route's goal cell, or -1.</param>
/// <param name="Waypoint">The next path node the body steers toward.</param>
/// <param name="ExpandedLast">The nodes the last search expanded.</param>
/// <param name="Status">The route's status.</param>
public readonly record struct EntryNavigationSection(
    int DomainIndex,
    int GoalCell,
    int Waypoint,
    int ExpandedLast,
    NavigationStatus Status
) : IContinuationSection<EntryNavigationSection> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Navigation;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryNavigationSection section) {
        writer.WriteInt32(value: section.DomainIndex);
        writer.WriteInt32(value: section.GoalCell);
        writer.WriteInt32(value: section.Waypoint);
        writer.WriteInt32(value: section.ExpandedLast);
        writer.WriteByte(value: checked((byte)section.Status));
    }
    /// <inheritdoc/>
    public static EntryNavigationSection Read(ref WireReader reader) => new(
        DomainIndex: reader.ReadInt32(),
        GoalCell: reader.ReadInt32(),
        Waypoint: reader.ReadInt32(),
        ExpandedLast: reader.ReadInt32(),
        Status: ((NavigationStatus)reader.ReadByte())
    );
}
/// <summary>A cached route's path.</summary>
/// <param name="Path">The path's cells, from the start to the goal.</param>
public readonly record struct EntryRouteSection(ReadOnlyMemory<int> Path) : IContinuationTableSection<EntryRouteSection, int> {
    /// <inheritdoc/>
    public static WorldContinuationSectionKind Section => WorldContinuationSectionKind.Route;

    /// <inheritdoc/>
    public static void Write(WireWriter writer, in EntryRouteSection section) =>
        WriteTable(elements: section.Path.Span, writer: writer);
    /// <inheritdoc/>
    public static void WriteTable(WireWriter writer, ReadOnlySpan<int> elements) {
        writer.WriteArrayCount(count: elements.Length);
        for (var index = 0; (index < elements.Length); index++) { writer.WriteInt32(value: elements[index]); }
    }
    /// <inheritdoc/>
    public static EntryRouteSection Read(ref WireReader reader) => new(Path: reader.ReadArray(
        field: "population entry navigation path",
        maximum: WorldNavigationCapacity.MaxPathNodes,
        readItem: static (ref WireReader r) => r.ReadInt32()
    ));
}
