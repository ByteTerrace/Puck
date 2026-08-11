using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The render half of an adjacency overlap: composes the
/// neighbour's own solid geometry — the same rows <see cref="WorldSolidField"/> would compile for collision, plus
/// its delivered addressable bodies but never unrelated props or screens — through the same isometry <see cref="WorldPortalArrivalMath"/> uses for a
/// crossing traveler, so the ground a body sees continuing past the doorway is the ground
/// <see cref="WorldAdjacencyContactField"/> actually stands it on.
/// </summary>
/// <remarks>
/// <para>Static solids and the neighbour's delivered entity image ride the same held observation. Remote entities
/// are mapped through the identical boundary isometry as terrain and handoff; they are ghosts only in the ownership
/// sense—the source never simulates them—but remain visible and addressable for cross-boundary interaction.</para>
/// <para><b>Which placements cross.</b> A neighbour placement carrying a <c>solid</c> facet participates when either
    /// its creation contains an unbounded primitive (<c>CreationGeometry.GetLocalBounds</c>'s
    /// <c>IsUnbounded</c> — an infinite ground plane is relevant everywhere near the
/// border, regardless of how far its own authored origin sits from the door) or its own reach
    /// (<c>CreationGeometry.Reach</c>, scaled) brings it within the counterpart face's own
/// overlap band. A placement with no <c>solid</c> facet (a portal frame, a decorative prop) never qualifies — the
/// same authorial signal <see cref="WorldSolidField"/> already reads for collision, reused rather than re-derived.</para>
/// <para><b>Cost.</b> Bounded by <see cref="MaxInstancesPerBand"/> instances per adjacency, reserved by the
/// construction-time probe (<see cref="WorldPlacementStamper.EmitProbe"/>) exactly like the boot world's own
/// placement headroom, so it folds into the same frozen ceiling <see cref="WorldFrameSource.ProgramWordCapacity"/>/
/// <see cref="WorldFrameSource.InstanceCapacity"/> already report — no new reservation class, and no verb in this
/// tree reads those two properties back today (a gap that predates this emitter).</para>
/// </remarks>
internal sealed class WorldAdjacencySceneEmitter : ISdfSceneEmitter {
    // The per-face worst-case reservation: generous for the shipped quilt's own solid census (ground + two walls +
    // a corner post) with headroom for a live-edited neighbour, without letting one border's content spend the whole
    // program's word budget. A capacity constant, not a world-tunable — see this
    // emitter's own remarks and CLAUDE.md's authored-vs-constant rule: every world wants THE SAME adjacency-instance
    // ceiling, because it sizes the reservation this emitter itself declares, never gameplay feel.
    internal const int MaxInstancesPerBand = WorldAdjacencyGeometry.MaximumPlacementsPerBand;

    private readonly Func<WorldDefinition> m_definition;
    private readonly IWorldAdjacencySource m_source;
    private readonly int m_reservation;
    // The last-polled reachability/revision per band, keyed by (placementId, faceName) — WriteRevision's own poll
    // compares against this to decide whether a rebuild is owed, without ever emitting from inside WriteRevision
    // itself (emission belongs to Emit alone).
    private readonly Dictionary<string, (int Definition, int Snapshot)> m_polledRevisions = new(comparer: StringComparer.Ordinal);
    private int m_neighbourRevision;
    private readonly int m_bandCount;
    // Per rendered band/entity presentation state. The durable address guards slot reuse and band reordering; gait
    // advances from the neighbour's interpolated pose, exactly like the boot/session avatar paths, rather than being
    // frozen merely because authority moved across a seam.
    private readonly float[] m_gaitPhases;
    private readonly Vector3[] m_previousRenderPositions;
    private readonly WorldEntityAddress[] m_motionAddresses;
    private readonly bool[] m_motionSeeded;

    /// <summary>Initializes the emitter over the boot definition's own adjacency rows.</summary>
    /// <param name="client">The snapshot-fed client view — this world's own definition (never re-read from the
    /// server directly, matching every other client-side emitter in this composition).</param>
    /// <param name="source">The injected neighbour resolver — the same wire-shaped seam
    /// <see cref="WorldAdjacencyContactField"/> reads for collision.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldAdjacencySceneEmitter(WorldClient client, IWorldAdjacencySource source) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = () => client.Definition;
        m_source = source;
        m_bandCount = WorldAdjacencyBands.ProjectionCapacity(definition: client.Definition);
        m_reservation = (m_bandCount * MaxInstancesPerBand);
        m_gaitPhases = new float[m_bandCount * WorldAvatarCatalog.Capacity];
        m_previousRenderPositions = new Vector3[m_gaitPhases.Length];
        m_motionAddresses = new WorldEntityAddress[m_gaitPhases.Length];
        m_motionSeeded = new bool[m_gaitPhases.Length];
    }

    /// <summary>Initializes the same border renderer over a followed instance's delivered mirror.</summary>
    public WorldAdjacencySceneEmitter(WorldSessionMirror mirror, IWorldAdjacencySource source) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = () => mirror.Definition;
        m_source = source;
        m_bandCount = WorldAdjacencyBands.ProjectionCapacity(definition: mirror.Definition);
        m_reservation = (m_bandCount * MaxInstancesPerBand);
        m_gaitPhases = new float[m_bandCount * WorldAvatarCatalog.Capacity];
        m_previousRenderPositions = new Vector3[m_gaitPhases.Length];
        m_motionAddresses = new WorldEntityAddress[m_gaitPhases.Length];
        m_motionSeeded = new bool[m_gaitPhases.Length];
    }

    /// <inheritdoc/>
    public int DynamicSlotCount => (m_bandCount * WorldAvatarCatalog.DynamicTransformCapacity);

    /// <inheritdoc/>
    public bool OwnsMaterialScope => false;

    /// <inheritdoc/>
    public int RevisionComponentCount => 1;

    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        // Poll every band's neighbour reachability/definition-revision — a live neighbour edit (or a neighbour
        // becoming reachable/unreachable) bumps this component so the host rebuilds, exactly like every other
        // watched counter in this composition.
        var observed = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (var projection in m_source.Visuals()) {
            _ = observed.Add(item: projection.Name);
            var polled = (Definition: (projection.Neighbour.DefinitionRevision + 1), Snapshot: (projection.Neighbour.SnapshotRevision + 1));

            if (!m_polledRevisions.TryGetValue(key: projection.Name, value: out var last) || (last != polled)) {
                m_polledRevisions[projection.Name] = polled;
                m_neighbourRevision++;
            }
        }
        foreach (var missing in m_polledRevisions.Keys.Where(predicate: key => !observed.Contains(item: key)).ToArray()) {
            if (m_polledRevisions[missing] != default) {
                m_polledRevisions[missing] = default;
                m_neighbourRevision++;
            }
        }

        destination[0] = m_neighbourRevision;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (context.Probe) {
            WorldPlacementStamper.EmitProbe(builder: builder, reservedCount: m_reservation);

            for (var band = 0; band < m_bandCount; band++) {
                var bodyMaterials = new int[WorldAvatarCatalog.Capacity];
                var accentMaterials = new int[WorldAvatarCatalog.Capacity];
                WorldAvatarCatalog.Emit(builder: builder, isActive: static _ => true, bodyMaterials: bodyMaterials, accentMaterials: accentMaterials, probeWorstCase: true, slotBase: (context.SlotBase + (band * WorldAvatarCatalog.DynamicTransformCapacity)));
            }

            return;
        }

        EmitCurrent(builder: builder, slotBase: context.SlotBase, includeEntities: true);
    }

    /// <summary>Emits the currently reachable live adjacency geometry without the capacity-probe branch. Camera
    /// clearance uses this to evaluate the same static strip the renderer composes.</summary>
    internal void EmitCurrent(SdfProgramBuilder builder, int slotBase = 0, bool includeEntities = false) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var bandIndex = 0;
        foreach (var projection in m_source.Visuals()) {
            var neighbour = projection.Neighbour;
            var selection = WorldAdjacencyGeometry.Select(definition: neighbour.Definition, frame: projection.Path[0].Neighbour, overlapDepth: projection.OverlapDepth);
            var transformed = selection.Placements
                .Select(selector: placement => MapIntoSource(placement: placement, path: projection.Path))
                .ToArray();

            if (transformed.Length > 0) {
                WorldPlacementStamper.EmitStatic(builder: builder, creations: neighbour.Definition.Creations, placements: transformed);
            }

            if (includeEntities) {
                EmitEntities(builder: builder, neighbour: neighbour, slotBase: (slotBase + (bandIndex * WorldAvatarCatalog.DynamicTransformCapacity)));
            }
            bandIndex++;
        }
    }

    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        var bandIndex = 0;
        foreach (var projection in m_source.Visuals()) {
            var neighbour = projection.Neighbour;
            var avatarSlots = slots.Slice(start: (context.SlotBase + (bandIndex * WorldAvatarCatalog.DynamicTransformCapacity)), length: WorldAvatarCatalog.DynamicTransformCapacity);
            var alpha = neighbour.InterpolationAlpha;
            for (var entity = 0; entity < neighbour.EntityCapacity; entity++) {
                var motionIndex = ((bandIndex * WorldAvatarCatalog.Capacity) + entity);
                if (!neighbour.IsEntityActive(index: entity)) {
                    m_motionSeeded[motionIndex] = false;
                    continue;
                }

                var position = Vector3.Lerp(value1: neighbour.PreviousPosition(index: entity), value2: neighbour.CurrentPosition(index: entity), amount: alpha);
                var orientation = Quaternion.Lerp(quaternion1: neighbour.PreviousOrientation(index: entity), quaternion2: neighbour.CurrentOrientation(index: entity), amount: alpha);
                var address = neighbour.EntityAddress(index: entity);

                if (m_motionSeeded[motionIndex] && (m_motionAddresses[motionIndex] == address)) {
                    var travelled = MathF.Min(x: Vector3.Distance(value1: position, value2: m_previousRenderPositions[motionIndex]), y: 0.25f);
                    m_gaitPhases[motionIndex] += (travelled * 8.0f);
                } else {
                    m_motionSeeded[motionIndex] = true;
                    m_gaitPhases[motionIndex] = 0f;
                    m_motionAddresses[motionIndex] = address;
                }

                m_previousRenderPositions[motionIndex] = position;
                var mapped = MapPoseIntoSource(position: position, orientation: orientation, path: projection.Path);
                var look = neighbour.Look(index: entity);
                WorldAvatarCatalog.PackTransforms(avatar: entity, rootPosition: mapped.Position, rootOrientation: mapped.Orientation, gaitPhase: (m_gaitPhases[motionIndex] * look.Motion.GaitAmplitude), castsSoftShadow: true, transforms: avatarSlots, rig: LookRig(look: look, catalogRig: neighbour.CatalogRig(index: entity)), scale: look.Scale);
            }
            bandIndex++;
        }
    }

    private static void EmitEntities(SdfProgramBuilder builder, IWorldAdjacencyNeighbour neighbour, int slotBase) {
        var bodyMaterials = new int[WorldAvatarCatalog.Capacity];
        var accentMaterials = new int[WorldAvatarCatalog.Capacity];
        var noseFactor = neighbour.Definition.PlayerDefaults.NoseFactor;
        for (var index = 0; index < WorldAvatarCatalog.Capacity; index++) {
            var color = neighbour.BodyColor(index: index);
            bodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: color));
            accentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: (color * noseFactor)));
        }
        WorldAvatarCatalog.Emit(builder: builder, isActive: neighbour.IsEntityActive, bodyMaterials: bodyMaterials, accentMaterials: accentMaterials, probeWorstCase: false, slotBase: slotBase, rigFor: index => LookRig(look: neighbour.Look(index: index), catalogRig: neighbour.CatalogRig(index: index)), scaleFor: index => neighbour.Look(index: index).Scale);
    }

    internal static (Vector3 Position, Quaternion Orientation) MapPoseIntoSource(Vector3 position, Quaternion orientation, IReadOnlyList<WorldAdjacencyFramePair> path) {
        var mappedPosition = position;
        var mappedOrientation = orientation;
        foreach (var stage in path) {
            (mappedPosition, mappedOrientation) = MapPoseStage(position: mappedPosition, orientation: mappedOrientation, neighbourFrame: stage.Neighbour, sourceFrame: stage.Source);
        }
        return (mappedPosition, mappedOrientation);
    }

    private static (Vector3 Position, Quaternion Orientation) MapPoseStage(Vector3 position, Quaternion orientation, WorldFaceFrame neighbourFrame, WorldFaceFrame sourceFrame) {
        var yaw = MathF.Atan2((2f * ((orientation.W * orientation.Y) + (orientation.X * orientation.Z))), (1f - (2f * ((orientation.Y * orientation.Y) + (orientation.Z * orientation.Z)))));
        var mapped = WorldPortalArrivalMath.ComputeArrival(travelerPosition: FixedVector3.FromVector3(value: position), travelerYawRadians: FixedQ4816.FromDouble(value: yaw), travelerPlanarVelocity: FixedVector3.Zero, travelerVerticalVelocity: FixedQ4816.Zero, sourcePosition: neighbourFrame.Origin, sourceYawRadians: neighbourFrame.PlanarYawRadians, destinationPosition: sourceFrame.Origin, destinationYawRadians: sourceFrame.PlanarYawRadians);
        var yawDelta = ((float)(double)mapped.YawRadians - yaw);
        var rotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: yawDelta);
        return (mapped.Position.ToVector3(), Quaternion.Normalize(value: (rotation * orientation)));
    }

    private static int LookRig(WorldLook look, byte catalogRig) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned }) ? pinned : catalogRig);

    // Maps a neighbour placement's authored transform into the SOURCE side's own coordinates through the EXACT SAME
    // isometry Server.WorldPortalArrivalMath uses for a crossing traveler's arrival, anchored at the two faces' own
    // frames (never a crossing's swept seam — this maps arbitrary geometry, not one traveler's own crossing point).
    // Fixed point throughout except the two float<->fixed boundary conversions (the one sanctioned rendering seam),
    // so the strip a body sees is placed by the IDENTICAL math the strip it stands on already uses.
    private static WorldPlacement MapIntoSource(WorldPlacement placement, IReadOnlyList<WorldAdjacencyFramePair> path) {
        var mapped = placement;
        foreach (var stage in path) {
            var arrival = WorldPortalArrivalMath.ComputeArrival(
            travelerPosition: FixedVector3.FromVector3(value: mapped.Position),
            travelerYawRadians: FixedQ4816.FromDouble(value: (mapped.YawDegrees * (Math.PI / 180.0))),
            travelerPlanarVelocity: FixedVector3.Zero,
            travelerVerticalVelocity: FixedQ4816.Zero,
            sourcePosition: stage.Neighbour.Origin,
            sourceYawRadians: stage.Neighbour.PlanarYawRadians,
            destinationPosition: stage.Source.Origin,
            destinationYawRadians: stage.Source.PlanarYawRadians
            );
            mapped = mapped with { Position = arrival.Position.ToVector3(), YawDegrees = (float)((double)arrival.YawRadians * (180.0 / Math.PI)) };
        }
        return mapped;
    }
}
