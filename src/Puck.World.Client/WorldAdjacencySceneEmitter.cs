using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The render half of an adjacency overlap: composes the
/// neighbour's own solid geometry — the same rows <c>WorldSolidField</c> would compile for collision, plus
/// its delivered addressable bodies but never unrelated props or screens — through the same
/// <see cref="WorldFrameIsometry"/> a crossing traveler arrives by, so the ground a body sees continuing past the doorway is the ground
/// <c>WorldAdjacencyContactField</c> actually stands it on.
/// </summary>
/// <remarks>
/// <para>Static solids and the neighbour's delivered entity image ride the same held observation. Remote entities
/// are mapped through the identical boundary isometry as terrain and handoff; they are ghosts only in the ownership
/// sense—the source never simulates them—but remain visible and addressable for cross-boundary interaction.</para>
/// <para><b>Which placements cross.</b> A neighbour placement carrying a <c>solid</c> facet participates when either
/// its creation contains an unbounded primitive (<c>SdfSolidGeometry.GetLocalBounds</c>'s
/// <c>IsUnbounded</c> — an infinite ground plane is relevant everywhere near the
/// border, regardless of how far its own authored origin sits from the door) or its own reach
/// (<c>CreationGeometry.Reach</c>, scaled) brings it within the counterpart face's own
/// overlap band. A placement with no <c>solid</c> facet (a portal frame, a decorative prop) never qualifies — the
/// same authorial signal <c>WorldSolidField</c> already reads for collision, reused rather than re-derived.</para>
/// <para><b>Which bodies cross.</b> The same rule, applied to the neighbour's delivered entity image: a body inside
/// the counterpart band, in delivered-slot order, up to <see cref="MaxEntitiesPerBand"/>. A body outside the band is
/// standing on terrain this border does not render, and at a derived corner the same neighbour is projected twice —
/// the band test is what keeps one body from being drawn at two places at once. Crossing the budget is stated once
/// per border on stderr, the way the contact half already states its own truncation.</para>
/// <para><b>Cost.</b> Bounded per adjacency by <see cref="MaxInstancesPerBand"/> solid instances plus
/// <see cref="MaxEntitiesPerBand"/> worst-case rigs, reserved by the construction-time probe exactly like the boot
/// world's own placement headroom, so it folds into the same frozen ceiling
/// <c>WorldFramePresenter.ProgramWordCapacity</c>/<c>WorldFramePresenter.InstanceCapacity</c> already report — no new
/// reservation class. A band is reserved for every direct edge PLUS every derivable corner pair
/// (<c>WorldAdjacencyBands.ProjectionCapacity</c>), so the reservation grows quadratically in authored edges.</para>
/// </remarks>
public sealed class WorldAdjacencySceneEmitter : ISdfSceneEmitter {
    // The per-face worst-case reservation: generous for the shipped quilt's own solid census (ground + two walls +
    // a corner post) with headroom for a live-edited neighbour, without letting one border's content spend the whole
    // program's word budget. A capacity constant, not a world-tunable — see this
    // emitter's own remarks and CLAUDE.md's authored-vs-constant rule: every world wants THE SAME adjacency-instance
    // ceiling, because it sizes the reservation this emitter itself declares, never gameplay feel.
    internal const int MaxInstancesPerBand = WorldAdjacencyGeometry.MaximumPlacementsPerBand;
    // The moving half of the same per-band reservation. KEEP IN SYNC: the probe branch reserves exactly this many
    // worst-case rigs per band and EmitEntities selects at most this many delivered bodies, so a live band can never
    // outgrow the envelope SdfWorldEngine.UploadProgram freezes.
    internal const int MaxEntitiesPerBand = WorldAdjacencyGeometry.MaximumEntitiesPerBand;

    private readonly int m_bandCount;
    private readonly Func<WorldDefinition> m_definition;
    // The band's own selected-body latch: which delivered entity slots THIS program's geometry was compiled for.
    // Emission and the per-frame transform pack must read the SAME set — packing a body the program never compiled
    // writes a pose into a slot no instance reads, and skipping one it did compile parks live geometry.
    private readonly bool[] m_emittedEntities;
    private readonly float[] m_emittedGaitAmplitudes;
    // Geometry and transforms must use one appearance epoch. A socket delivery can replace a slot between the
    // program rebuild and this frame's transform pack; latching the emitted rig/scale/gait here prevents the new
    // occupant's skeleton from being packed into the old occupant's compiled geometry for even one frame.
    private readonly int[] m_emittedRigs;
    private readonly float[] m_emittedScales;
    // Per rendered band/entity presentation state. The durable address guards slot reuse and band reordering; gait
    // advances from the neighbour's interpolated pose, exactly like the boot/session avatar paths, rather than being
    // frozen merely because authority moved across a seam.
    private readonly float[] m_gaitPhases;
    private readonly WorldEntityAddress[] m_motionAddresses;
    private readonly bool[] m_motionSeeded;
    private readonly Vector3[] m_previousRenderPositions;
    private readonly IWorldAdjacencySource m_source;
    private readonly Func<WorldEntityAddress, bool>? m_suppressEntity;

    private int m_neighbourRevision;
    private int m_selectionRevision;

    // WriteRevision's own scratch: the rebuild watch must not disturb the emitted latch it is comparing against.
    private readonly bool[] m_polledEntities = new bool[WorldRigCatalog.Capacity];

    // The last-polled reachability/revision per band, keyed by (placementId, faceName) — WriteRevision's own poll
    // compares against this to decide whether a rebuild is owed, without ever emitting from inside WriteRevision
    // itself (emission belongs to Emit alone).
    private readonly Dictionary<string, (int Definition, int Snapshot)> m_polledRevisions = new(comparer: StringComparer.Ordinal);
    // A band whose delivered body count has already crossed MaxEntitiesPerBand, so the truncation is stated once per
    // border rather than once per program rebuild.
    private readonly HashSet<string> m_truncationNarrated = new(comparer: StringComparer.Ordinal);
    // The emitted SDF program assigns one frozen transform range to each projection in order. Resolution can change
    // between program emission and a later frame's transform pack; re-querying Visuals there would write a newly
    // shifted direct/corner list into the old program's ranges, producing the border-riding flicker. WriteRevision
    // still observes topology changes and requests a rebuild; until that rebuild lands, transforms follow exactly
    // the projection image that compiled the current program.
    private WorldAdjacencyProjection[] m_emittedProjections = [];

    /// <summary>Initializes the emitter over the boot definition's own adjacency rows.</summary>
    /// <param name="client">The snapshot-fed client view — this world's own definition (never re-read from the
    /// server directly, matching every other client-side emitter in this composition).</param>
    /// <param name="source">The injected neighbour resolver — the same wire-shaped seam
    /// <c>WorldAdjacencyContactField</c> reads for collision.</param>
    /// <param name="suppressEntity">Optional exact-address predicate for entities whose primary presentation is
    /// owned elsewhere (locally followed travelers).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldAdjacencySceneEmitter(WorldClient client, IWorldAdjacencySource source, Func<WorldEntityAddress, bool>? suppressEntity = null) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: source);

        m_definition = () => client.Definition;
        m_source = source;
        m_suppressEntity = suppressEntity;
        m_bandCount = WorldAdjacencyBands.ProjectionCapacity(definition: client.Definition);
        m_gaitPhases = new float[(m_bandCount * WorldRigCatalog.Capacity)];
        m_previousRenderPositions = new Vector3[m_gaitPhases.Length];
        m_motionAddresses = new WorldEntityAddress[m_gaitPhases.Length];
        m_motionSeeded = new bool[m_gaitPhases.Length];
        m_emittedRigs = new int[m_gaitPhases.Length];
        m_emittedScales = new float[m_gaitPhases.Length];
        m_emittedGaitAmplitudes = new float[m_gaitPhases.Length];
        m_emittedEntities = new bool[m_gaitPhases.Length];
    }

    /// <inheritdoc/>
    public int DynamicSlotCount => (m_bandCount * WorldRigCatalog.DynamicTransformCapacity);
    /// <inheritdoc/>
    public bool OwnsMaterialScope => false;
    /// <inheritdoc/>
    /// <remarks>Two components: neighbour reachability/revision, and the per-band selected-body set. The second
    /// exists because a band renders the bodies inside its own overlap — a purely positional fact no delivered
    /// revision moves for, so without it a body would enter or leave a border only when something unrelated
    /// happened to force a rebuild.</remarks>
    public int RevisionComponentCount => 2;

    /// <summary>Emits the whole per-band reservation — the one declaration of what adjacency composition may spend,
    /// shared by this emitter's own construction probe and by the candidate measurer the render-capacity oracle
    /// admits scene mutations against, so the two can never disagree about the room a border holds.</summary>
    /// <param name="builder">The shared program builder under construction.</param>
    /// <param name="bandCount">The projections to reserve for
    /// (<see cref="WorldAdjacencyBands.ProjectionCapacity(WorldDefinition)"/>).</param>
    /// <param name="slotBase">The owning emitter's first dynamic-transform slot.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static void EmitReservation(SdfProgramBuilder builder, int bandCount, int slotBase) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        WorldPlacementStamper.EmitProbe(
            builder: builder,
            reservedCount: (bandCount * MaxInstancesPerBand)
        );

        for (var band = 0; (band < bandCount); band++) {
            var bodyMaterials = new int[WorldRigCatalog.Capacity];
            var accentMaterials = new int[WorldRigCatalog.Capacity];

            // The palette a live band adds — one body plus one accent per rendered body. Reserved because AddMaterial
            // never dedupes, so an unreserved live palette would spend program words nothing measured.
            for (var entity = 0; (entity < MaxEntitiesPerBand); entity++) {
                bodyMaterials[entity] = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
                accentMaterials[entity] = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
            }

            WorldRigCatalog.Emit(
                builder: builder,
                isActive: static _ => true,
                bodyMaterials: bodyMaterials,
                accentMaterials: accentMaterials,
                probeAvatarLimit: MaxEntitiesPerBand,
                probeWorstCase: true,
                slotBase: (slotBase + (band * WorldRigCatalog.DynamicTransformCapacity))
            );
        }
    }
    /// <summary>Emits the currently reachable live adjacency geometry without the capacity-probe branch. Camera
    /// clearance uses this to evaluate the same static strip the renderer composes.</summary>
    public void EmitCurrent(SdfProgramBuilder builder, int slotBase = 0, bool includeEntities = false) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var projections = m_source.Visuals().ToArray();

        if (includeEntities) {
            m_emittedProjections = ((projections.Length > m_bandCount)
                ? projections[..m_bandCount]
                : projections
            );
        }

        var bandIndex = 0;

        foreach (var projection in projections) {
            // The per-band appearance arrays and the frozen instance reservation are both sized from the BOOT
            // document's projection capacity. A live mutation that authors another adjacency row can outrun both, so
            // the extra band is dropped by name rather than indexed past the arrays it has no room in.
            if (bandIndex >= m_bandCount) {
                if (m_truncationNarrated.Add(item: projection.Name)) {
                    Console.Error.WriteLine(value: $"[world.adjacency: '{projection.Name}' is beyond the {m_bandCount} band(s) this render composition reserved at boot; its border renders nothing until the world reloads]");
                }

                break;
            }

            var neighbour = projection.Neighbour;
            var selection = WorldAdjacencyGeometry.Select(
                definition: neighbour.Definition,
                frame: projection.Path[0].Neighbour,
                overlapDepth: projection.OverlapDepth
            );
            var transformed = selection.Placements
                .Select(selector: placement => MapIntoSource(
                placement: placement,
                path: projection.Path
            ))
                .ToArray();

            if (transformed.Length > 0) {
                // Adjacency delivery currently carries the neighbouring document, not its hash-pinned font asset
                // bytes. Emit its ordinary geometry but omit creation text until federation owns asset transport and
                // the local renderer can merge remote catalogs into its one glyph binding.
                WorldPlacementStamper.EmitStatic(
                    builder: builder,
                    definition: neighbour.Definition,
                    creations: neighbour.Definition.Creations,
                    placements: transformed
                );
            }

            if (includeEntities) {
                EmitEntities(
                    builder: builder,
                    projection: projection,
                    bandIndex: bandIndex,
                    slotBase: (slotBase + (bandIndex * WorldRigCatalog.DynamicTransformCapacity))
                );
            }
            bandIndex++;
        }
    }

    internal static (Vector3 Position, Quaternion Orientation) MapPoseIntoSource(Vector3 position, Quaternion orientation, IReadOnlyList<WorldAdjacencyFramePair> path) {
        var mappedPosition = position;
        var mappedOrientation = orientation;

        foreach (var stage in path) {
            (mappedPosition, mappedOrientation) = MapPoseStage(
                position: mappedPosition,
                orientation: mappedOrientation,
                neighbourFrame: stage.Neighbour,
                sourceFrame: stage.Source
            );
        }
        return (mappedPosition, mappedOrientation);
    }

    // Selects and emits the delivered bodies this band actually renders. Two bounds, both load-bearing:
    //   - the neighbour's OWN delivered table width, never the renderer's 128-rig catalog width (the bound contact
    //     resolution and PackDynamicTransforms already walk);
    //   - the band itself, then MaxEntitiesPerBand in delivered-slot order, exactly as WorldAdjacencyGeometry.Select
    //     bounds the static half. A body outside the band is standing on terrain this border does not render, and at a
    //     derived corner the same neighbour is projected twice — the band test is what keeps one body from appearing
    //     at two places at once.
    private void EmitEntities(SdfProgramBuilder builder, WorldAdjacencyProjection projection, int bandIndex, int slotBase) {
        var neighbour = projection.Neighbour;
        var bodyMaterials = new int[WorldRigCatalog.Capacity];
        var accentMaterials = new int[WorldRigCatalog.Capacity];
        var noseFactor = neighbour.Definition.PlayerDefaults.NoseFactor;
        var appearanceBase = (bandIndex * WorldRigCatalog.Capacity);
        var truncated = SelectBand(
            destination: m_emittedEntities.AsSpan(
                length: WorldRigCatalog.Capacity,
                start: appearanceBase
            ),
            projection: projection
        );

        if (
            truncated &&
            m_truncationNarrated.Add(item: projection.Name)
        ) {
            Console.Error.WriteLine(value: $"[world.adjacency: '{projection.Name}' neighbour bodies truncated for rendering at {MaxEntitiesPerBand} bodies]");
        }

        for (var index = 0; (index < WorldRigCatalog.Capacity); index++) {
            var appearanceIndex = (appearanceBase + index);

            if (!m_emittedEntities[appearanceIndex]) {
                continue;
            }

            WorldMirroredAvatarBand.EmitPalette(
                accentMaterials: accentMaterials,
                bodyColor: neighbour.BodyColor(index: index),
                bodyMaterials: bodyMaterials,
                builder: builder,
                catalogRig: neighbour.CatalogRig(index: index),
                emittedGaitAmplitudes: m_emittedGaitAmplitudes,
                emittedRigs: m_emittedRigs,
                emittedScales: m_emittedScales,
                identityIndex: appearanceIndex,
                look: neighbour.Look(index: index),
                materialIndex: index,
                noseFactor: noseFactor
            );
        }

        WorldRigCatalog.Emit(
            builder: builder,
            isActive: index => m_emittedEntities[(appearanceBase + index)],
            bodyMaterials: bodyMaterials,
            accentMaterials: accentMaterials,
            probeWorstCase: false,
            slotBase: slotBase,
            rigFor: index => m_emittedRigs[(appearanceBase + index)],
            scaleFor: index => m_emittedScales[(appearanceBase + index)]
        );
    }
    private bool IsSuppressed(IWorldAdjacencyNeighbour neighbour, int index) =>
        ((m_suppressEntity is { } suppress) && suppress(neighbour.EntityAddress(index: index)));
    // The ONE selection rule, read by emission and by the rebuild watch alike so the program and the counter that
    // decides whether to rebuild it can never disagree about which bodies a band renders.
    private bool SelectBand(WorldAdjacencyProjection projection, Span<bool> destination) {
        destination.Clear();

        var neighbour = projection.Neighbour;
        var frame = projection.Path[0].Neighbour;
        var overlapDepth = ((float)((double)projection.OverlapDepth));
        var bound = Math.Min(
            val1: neighbour.EntityCapacity,
            val2: destination.Length
        );
        var selected = 0;
        var truncated = false;

        for (var index = 0; (index < bound); index++) {
            if (
                !neighbour.IsEntityActive(index: index) ||
                IsSuppressed(
                index: index,
                neighbour: neighbour
            ) ||
                !WorldAdjacencyGeometry.IsWithinBand(
                frame: frame,
                overlapDepth: overlapDepth,
                position: neighbour.CurrentPosition(index: index),
                reach: (WorldRigCatalog.Reach * MathF.Max(
                    x: neighbour.Look(index: index).Scale,
                    y: 1f
                ))
            )
            ) {
                continue;
            }

            if (selected >= MaxEntitiesPerBand) {
                truncated = true;

                continue;
            }

            selected++;
            destination[index] = true;
        }

        return truncated;
    }
    // Maps a neighbour placement's authored transform into the SOURCE side's own coordinates through the EXACT SAME
    // isometry Server.WorldPortalArrivalMath uses for a crossing traveler's arrival, anchored at the two faces' own
    // frames (never a crossing's swept seam — this maps arbitrary geometry, not one traveler's own crossing point).
    // Fixed point throughout except the two float<->fixed boundary conversions (the one sanctioned rendering seam),
    // so the strip a body sees is placed by the IDENTICAL math the strip it stands on already uses.
    private static WorldPlacement MapIntoSource(WorldPlacement placement, IReadOnlyList<WorldAdjacencyFramePair> path) {
        var mapped = placement;

        foreach (var stage in path) {
            var yaw = FixedQ4816.FromDouble(value: (mapped.YawDegrees * (Math.PI / 180.0)));
            var forward = FixedQuaternion.FromAxisAngle(
                axis: new FixedVector3(
                    X: FixedQ4816.Zero,
                    Y: FixedQ4816.One,
                    Z: FixedQ4816.Zero
                ),
                angle: yaw
            )
                .Rotate(vector: new FixedVector3(
                X: FixedQ4816.Zero,
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.One
            ));
            var mappedForward = WorldFrameIsometry.MapVector(
                value: forward,
                source: stage.Neighbour,
                destination: stage.Source
            );

            mapped = mapped with {
                Position = WorldFrameIsometry.MapPoint(
                point: FixedVector3.FromVector3(value: mapped.Position),
                source: stage.Neighbour,
                destination: stage.Source
            ).ToVector3(),
                YawDegrees = ((float)(((double)FixedQ4816.Atan2(
                y: mappedForward.X,
                x: mappedForward.Z
            )) * (180.0 / Math.PI))),
            };
        }
        return mapped;
    }
    private static (Vector3 Position, Quaternion Orientation) MapPoseStage(Vector3 position, Quaternion orientation, WorldFaceFrame neighbourFrame, WorldFaceFrame sourceFrame) {
        var mappedPosition = WorldFrameIsometry.MapPoint(
            point: FixedVector3.FromVector3(value: position),
            source: neighbourFrame,
            destination: sourceFrame
        );
        var rotation = WorldFrameIsometry.Rotation(
            destination: sourceFrame,
            source: neighbourFrame
        ).ToQuaternion();

        return (mappedPosition.ToVector3(), Quaternion.Normalize(value: (rotation * orientation)));
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        if (context.Probe) {
            EmitReservation(
                bandCount: m_bandCount,
                builder: builder,
                slotBase: context.SlotBase
            );

            return;
        }

        EmitCurrent(
            builder: builder,
            slotBase: context.SlotBase,
            includeEntities: true
        );
    }
    /// <inheritdoc/>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        var bandIndex = 0;

        foreach (var projection in m_emittedProjections) {
            var neighbour = projection.Neighbour;
            var avatarSlots = slots.Slice(
                start: (context.SlotBase + (bandIndex * WorldRigCatalog.DynamicTransformCapacity)),
                length: WorldRigCatalog.DynamicTransformCapacity
            );
            var alpha = neighbour.InterpolationAlpha;

            var bound = Math.Min(
                val1: neighbour.EntityCapacity,
                val2: WorldRigCatalog.Capacity
            );

            for (var entity = 0; (entity < bound); entity++) {
                var motionIndex = ((bandIndex * WorldRigCatalog.Capacity) + entity);

                // The emitted latch, not a fresh liveness read: the program's geometry was compiled for exactly this
                // set, and re-deriving the set here would pack a body whose leaves this program never emitted.
                if (!m_emittedEntities[motionIndex]) {
                    m_motionSeeded[motionIndex] = false;
                    continue;
                }

                var position = Vector3.Lerp(
                    value1: neighbour.PreviousPosition(index: entity),
                    value2: neighbour.CurrentPosition(index: entity),
                    amount: alpha
                );
                var orientation = Quaternion.Lerp(
                    quaternion1: neighbour.PreviousOrientation(index: entity),
                    quaternion2: neighbour.CurrentOrientation(index: entity),
                    amount: alpha
                );
                WorldMirroredAvatarBand.AdvanceGait(
                    address: neighbour.EntityAddress(index: entity),
                    gaitPhase: ref m_gaitPhases[motionIndex],
                    lastAddress: ref m_motionAddresses[motionIndex],
                    lastPosition: ref m_previousRenderPositions[motionIndex],
                    position: position,
                    seeded: ref m_motionSeeded[motionIndex]
                );

                var mapped = MapPoseIntoSource(
                    position: position,
                    orientation: orientation,
                    path: projection.Path
                );

                WorldRigCatalog.PackTransforms(
                    avatar: entity,
                    rootPosition: mapped.Position,
                    rootOrientation: mapped.Orientation,
                    gaitPhase: (m_gaitPhases[motionIndex] * m_emittedGaitAmplitudes[motionIndex]),
                    castsSoftShadow: true,
                    transforms: avatarSlots,
                    rig: m_emittedRigs[motionIndex],
                    scale: m_emittedScales[motionIndex]
                );
            }
            bandIndex++;
        }
    }
    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        // Poll every band's neighbour reachability/definition-revision — a live neighbour edit (or a neighbour
        // becoming reachable/unreachable) bumps this component so the host rebuilds, exactly like every other
        // watched counter in this composition.
        var observed = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var projection in m_source.Visuals()) {
            _ = observed.Add(item: projection.Name);
            var polled = (Definition: (projection.Neighbour.DefinitionRevision + 1), Snapshot: (projection.Neighbour.SnapshotRevision + 1));

            if (
                !m_polledRevisions.TryGetValue(
                key: projection.Name,
                value: out var last
            ) ||
                (last != polled)
            ) {
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

        // Re-run the ONE selection rule and compare it against what the live program was compiled for. A band whose
        // set moved needs a rebuild; a band the emitted layout no longer has room for is already covered by the
        // reachability component above.
        var bandIndex = 0;

        foreach (var projection in m_source.Visuals()) {
            if (bandIndex >= m_bandCount) {
                break;
            }

            var appearanceBase = (bandIndex * WorldRigCatalog.Capacity);
            var polled = m_polledEntities.AsSpan();

            _ = SelectBand(
                destination: polled,
                projection: projection
            );

            if (!polled.SequenceEqual(other: m_emittedEntities.AsSpan(
                length: WorldRigCatalog.Capacity,
                start: appearanceBase
            ))) {
                m_selectionRevision++;
            }
            bandIndex++;
        }

        destination[0] = m_neighbourRevision;
        destination[1] = m_selectionRevision;
    }
}
