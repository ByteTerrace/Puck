using System.Numerics;
using Puck.Abstractions.Gpu;
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
public sealed partial class WorldAdjacencySceneEmitter : ISdfSceneEmitter {
    // The moving half of the same per-band reservation. KEEP IN SYNC: the probe branch reserves exactly this many
    // worst-case rigs per band and EmitEntities selects at most this many delivered bodies, so a live band can never
    // outgrow the envelope SdfWorldTables.UploadProgram freezes.
    internal const int MaxEntitiesPerBand = WorldAdjacencyGeometry.MaximumEntitiesPerBand;
    // The per-face worst-case reservation: generous for the shipped quilt's own solid census (ground + two walls +
    // a corner post) with headroom for a live-edited neighbour, without letting one border's content spend the whole
    // program's word budget. A capacity constant, not a world-tunable — see this
    // emitter's own remarks and AGENTS.md's authored-vs-constant rule: every world wants THE SAME adjacency-instance
    // ceiling, because it sizes the reservation this emitter itself declares, never gameplay feel.
    internal const int MaxInstancesPerBand = WorldAdjacencyGeometry.MaximumPlacementsPerBand;
    // The neighbour body indices a band keeps presentation state for, laid out band by band at this stride. A band draws
    // at most MaxEntitiesPerBand bodies, so the stride is a presentation bound, not the neighbour's population: a body
    // at or past it is not drawn across the seam and counts as truncated (SelectBand), which narrates by name.
    internal const int BodyStride = 4096;

    private readonly int m_bandCount;
    private readonly Func<WorldDefinition> m_definition;

    // The neighbours' static placements' palettes, reused across rebuilds (WorldPlacementStamper.EmitStatic).
    private readonly WorldStaticPalettes m_palettes = new();

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
    private readonly WorldAvatarGait[] m_gaits;
    private readonly WorldTransformOwners m_motionOwners;
    private readonly IWorldAdjacencySource m_source;
    private readonly Func<WorldEntityAddress, bool>? m_suppressEntity;

    private int m_neighbourRevision;
    private int m_selectionRevision;

    // The neighbours' static placements' mesh draws the last live Emit fixed, in the source world's frame.
    private IReadOnlyList<SdfMeshDraw> m_meshDraws = [];
    // EmitCurrent's own scratch for one band's source-mapped placements, bounded by MaxInstancesPerBand (the same
    // reservation WorldAdjacencyGeometry.Select's default `maximum` honors) and reused across bands and rebuilds —
    // EmitStatic consumes it synchronously and never retains the reference.
    private readonly WorldPlacement[] m_mappedPlacements = new WorldPlacement[MaxInstancesPerBand];
    // Per band, the frame thread's view of the bodies it renders: the active bodies inside the band before suppression
    // and the budget, ascending, kept from the neighbour's entity journal (IWorldAdjacencyNeighbour.CollectEntityChanges)
    // so a frame visits only the bodies that changed; the cursor on that journal and the band test the set was made
    // under; and this frame's selection of them, at most MaxEntitiesPerBand a band, ascending.
    private readonly List<int>[] m_near;
    private readonly GpuRowCursor[] m_nearCursors;
    private readonly (WorldFaceFrame Frame, FixedQ4816 Depth)?[] m_nearBands;
    private readonly GpuUploadRuns m_nearRuns = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly int[] m_selected;
    private readonly int[] m_selectedCount;
    // Per band, the bodies the latest emission compiled, ascending (published whole by the build that emits, read by the
    // frame thread), and the list the frame thread last packed, whose bodies that left are unseeded.
    private readonly int[]?[] m_emittedLists;
    private readonly int[]?[] m_packedLists;
    // The motion indices this frame gave a live slot range, reset to none at the next frame's start.
    private readonly List<int> m_liveSet = [];
    // EmitEntities' selection scratch; a build emits one band at a time.
    private readonly List<int> m_emitting = [];
    // WriteRevision's own scratch for the reachability poll's "still present this call" set, cleared and refilled
    // every call rather than reallocated.
    private readonly HashSet<string> m_observedAdjacencies = new(comparer: StringComparer.Ordinal);
    // WriteRevision's own scratch for the polled-revision keys no longer observed this call. Collected in a first
    // pass over m_polledRevisions.Keys (read-only — Dictionary forbids mutating a table a live Keys enumerator is
    // open over), then applied in a second pass, so the two-pass shape stays without allocating a LINQ array.
    private readonly List<string> m_missingAdjacencies = [];
    // The last-polled reachability/revision per band, keyed by (placementId, faceName) — WriteRevision's own poll
    // compares against this to decide whether a rebuild is owed, without ever emitting from inside WriteRevision
    // itself (emission belongs to Emit alone).
    private readonly Dictionary<string, (int Definition, int Snapshot)> m_polledRevisions = new(comparer: StringComparer.Ordinal);
    // Each neighbour's bound colors, read through a state mirror over the neighbour's pinned image as the local build
    // reads the client's (WorldBakedColors), so a state-cell write that moves a bound color rebuilds the border as it
    // would a local placement, and only once the neighbour's pin advances to the image carrying it.
    private readonly Dictionary<string, NeighbourColors> m_neighbourColors = new(comparer: StringComparer.Ordinal);
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
        m_gaits = new WorldAvatarGait[(m_bandCount * BodyStride)];
        m_motionOwners = new WorldTransformOwners(capacity: m_gaits.Length);
        m_emittedRigs = new int[m_gaits.Length];
        m_emittedScales = new float[m_gaits.Length];
        m_emittedGaitAmplitudes = new float[m_gaits.Length];
        m_emittedEntities = new bool[m_gaits.Length];
        m_liveSlots = new int[m_gaits.Length];
        Array.Fill(array: m_liveSlots, value: -1);
        m_near = new List<int>[m_bandCount];
        m_nearCursors = new GpuRowCursor[m_bandCount];
        for (var band = 0; (band < m_bandCount); band++) {
            m_near[band] = [];
            m_nearCursors[band] = new GpuRowCursor();
        }
        m_nearBands = new (WorldFaceFrame, FixedQ4816)?[m_bandCount];
        m_selected = new int[(m_bandCount * MaxEntitiesPerBand)];
        m_selectedCount = new int[m_bandCount];
        m_emittedLists = new int[]?[m_bandCount];
        m_packedLists = new int[]?[m_bandCount];
    }

    // Each body's first slot in this frame's layout, by band and body (-1 for a body the frame renders none of).
    private readonly int[] m_liveSlots;

    private int m_liveSlotCount;

    /// <inheritdoc/>
    /// <remarks>Selects each band's bodies from the bodies its neighbour changed since the last frame, and lays out the
    /// selected bodies' slots.</remarks>
    public void PrepareFrame() {
        foreach (var motion in m_liveSet) { m_liveSlots[motion] = -1; }
        m_liveSet.Clear();
        m_liveSlotCount = 0;
        var visuals = m_source.Visuals();

        for (var band = 0; (band < Math.Min(visuals.Count, m_bandCount)); band++) {
            Select(band: band, projection: visuals[band]);
            for (var at = 0; (at < m_selectedCount[band]); at++) {
                var motion = ((band * BodyStride) + m_selected[((band * MaxEntitiesPerBand) + at)]);

                m_liveSlots[motion] = m_liveSlotCount;
                m_liveSet.Add(item: motion);
                m_liveSlotCount += WorldRigCatalog.TransformSlotsPerBody;
            }
        }
        for (var band = Math.Min(visuals.Count, m_bandCount); (band < m_bandCount); band++) { m_selectedCount[band] = 0; }
        PrepareStaticProjections(visuals);
    }
    // Brings a band's near set up to its neighbour's latest image, reading only the bodies the neighbour's journal names
    // changed (every active body when it cannot name them, or when the band's frame or depth moved), then selects this
    // frame's bodies from it: the unsuppressed ones in slot order, up to MaxEntitiesPerBand. The one selection rule
    // SelectBand applies to a whole projection, here kept from the bodies that changed.
    private void Select(int band, WorldAdjacencyProjection projection) {
        var neighbour = projection.Neighbour;
        var near = m_near[band];
        var frame = projection.Path[0].Neighbour;
        var depth = projection.OverlapDepth;
        var bound = Math.Min(val1: neighbour.EntityCapacity, val2: BodyStride);
        var whole = (neighbour.CollectEntityChanges(cursor: m_nearCursors[band], into: m_nearRuns) || (m_nearBands[band] != (frame, depth)));

        m_nearBands[band] = (frame, depth);
        if (whole) {
            near.Clear();
            foreach (var index in neighbour.ActiveEntities) {
                if ((index < bound) && Near(depth: depth, frame: frame, index: index, neighbour: neighbour)) { near.Add(item: index); }
            }
        } else {
            for (var run = 0; (run < m_nearRuns.Count); run++) {
                var end = Math.Min(val1: (m_nearRuns.Start(index: run) + m_nearRuns.Length(index: run)), val2: bound);

                for (var index = m_nearRuns.Start(index: run); (index < end); index++) {
                    var at = near.BinarySearch(item: index);
                    var inside = (neighbour.IsEntityActive(index: index) && Near(depth: depth, frame: frame, index: index, neighbour: neighbour));

                    if (inside && (at < 0)) { near.Insert(index: ~at, item: index); } else if (!inside && (at >= 0)) { near.RemoveAt(index: at); }
                }
            }
        }
        m_nearCursors[band].Commit();

        var count = 0;

        foreach (var index in near) {
            if (IsSuppressed(index: index, neighbour: neighbour)) { continue; }
            if (count == MaxEntitiesPerBand) { break; }
            m_selected[((band * MaxEntitiesPerBand) + count)] = index;
            count++;
        }
        m_selectedCount[band] = count;
    }
    // Whether a body stands inside a band: the band test SelectBand applies, before activity, suppression and the budget.
    private static bool Near(IWorldAdjacencyNeighbour neighbour, WorldFaceFrame frame, FixedQ4816 depth, int index) =>
        WorldAdjacencyGeometry.IsWithinBand(
            frame: frame,
            overlapDepth: ((float)depth),
            position: neighbour.CurrentPosition(index: index),
            reach: (WorldRigCatalog.Reach * MathF.Max(
                x: neighbour.Look(index: index).Scale,
                y: 1f
            ))
        );

    public int DynamicSlotCount => m_liveSlotCount;
    public IReadOnlyList<SdfMeshInstance> MeshInstances {
        get { PublishStaticCopies(); return m_staticInstances; }
    }
    /// <inheritdoc/>
    public bool OwnsMaterialScope => false;
    /// <inheritdoc/>
    /// <remarks>Two components: neighbour reachability/revision, and the per-band selected-body set. The second
    /// exists because a band renders the bodies inside its own overlap — a purely positional fact no delivered
    /// revision moves for, so without it a body would enter or leave a border only when something unrelated
    /// happened to force a rebuild.</remarks>
    public int RevisionComponentCount => 2;
    /// <inheritdoc/>
    /// <remarks>The reachable neighbours' static placements' meshes, mapped into this world and fixed by each live
    /// <see cref="Emit"/>, like their shapes.</remarks>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDraws;

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

    // The bound colors a projection's neighbour bakes, read from definition, the neighbour image the caller read once
    // and draws the border's geometry from, so geometry and colors always come from one image.
    private WorldBakedColors ColorsOf(WorldAdjacencyProjection projection, WorldDefinition definition) {
        if (!m_neighbourColors.TryGetValue(
            key: projection.Name,
            value: out var colors
        )) {
            colors = new NeighbourColors(definition: definition);
            m_neighbourColors[projection.Name] = colors;
        }

        return colors.Follow(definition: definition);
    }

    // A state mirror over one neighbour image and the colors a border bakes through it. Owned by the presentation that
    // builds the border, so nothing else writes it; it reinstalls over a new image object, which re-reads every slot
    // and reports a move only for a baked color whose value changed (WorldBakedColors.TryTakeMove).
    private sealed class NeighbourColors {
        private WorldDefinition m_definition;

        public NeighbourColors(WorldDefinition definition) {
            m_definition = definition;
            Mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => m_definition));
            Mirror.Install(
                engineTick: 0UL,
                tick: 0UL
            );
            Colors = new WorldBakedColors(mirror: Mirror);
        }

        public WorldBakedColors Colors { get; }
        public WorldStateMirror Mirror { get; }

        // Follows a neighbour image: the same image object reads as it did, a new one reinstalls the mirror over it.
        public WorldBakedColors Follow(WorldDefinition definition) {
            if (!ReferenceEquals(
                objA: definition,
                objB: m_definition
            )) {
                m_definition = definition;
                Mirror.Install(
                    engineTick: 0UL,
                    tick: 0UL
                );
            }

            return Colors;
        }
    }

    // A plain indexed copy of the first `count` entries — the ONLY place EmitCurrent allocates a fresh
    // m_emittedProjections array, reached only when a live edit authors more adjacency projections than this
    // composition reserved at boot.
    private static WorldAdjacencyProjection[] CopyProjections(IReadOnlyList<WorldAdjacencyProjection> source, int count) {
        var copy = new WorldAdjacencyProjection[count];

        for (var index = 0; (index < count); index++) {
            copy[index] = source[index];
        }

        return copy;
    }
    // Selects and emits the delivered bodies this band actually renders. Two bounds, both load-bearing:
    //   - the neighbour's OWN delivered table width, never the engine-wide body ceiling (the bound contact
    //     resolution and PackDynamicTransforms already walk);
    //   - the band itself, then MaxEntitiesPerBand in delivered-slot order, exactly as WorldAdjacencyGeometry.Select
    //     bounds the static half. A body outside the band is standing on terrain this border does not render, and at a
    //     derived corner the same neighbour is projected twice — the band test is what keeps one body from appearing
    //     at two places at once.
    private void EmitEntities(SdfProgramBuilder builder, WorldAdjacencyProjection projection, int bandIndex, int slotBase) {
        var neighbour = projection.Neighbour;
        var bodyMaterials = new int[BodyStride];
        var accentMaterials = new int[BodyStride];
        var noseFactor = neighbour.Definition.PlayerDefaults.NoseFactor;
        var appearanceBase = (bandIndex * BodyStride);
        var emitting = m_emitting;

        emitting.Clear();
        var truncated = SelectBand(
            destination: emitting,
            projection: projection
        );

        Array.Clear(array: m_emittedEntities, index: appearanceBase, length: BodyStride);
        foreach (var index in emitting) { m_emittedEntities[(appearanceBase + index)] = true; }

        if (
            truncated &&
            m_truncationNarrated.Add(item: projection.Name)
        ) {
            WorldClientDiagnostics.Report(channel: "world.adjacency", text: $"'{projection.Name}' neighbour bodies truncated for rendering: a band draws at most {MaxEntitiesPerBand} bodies, each below body {BodyStride}");
        }

        foreach (var index in emitting) {
            var appearanceIndex = (appearanceBase + index);

            WorldMirroredAvatarBand.EmitPalette(
                accentMaterials: accentMaterials,
                bodyColor: new DisplayColor(rgb: neighbour.BodyColor(index: index)),
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
            slotBase: slotBase,
            rigFor: index => m_emittedRigs[(appearanceBase + index)],
            slotFor: index => m_liveSlots[(appearanceBase + index)]
        );
        // Published last, so the frame thread that reads the list sees the rigs, scales and gaits written for it.
        Volatile.Write(location: ref m_emittedLists[bandIndex], value: [.. emitting]);
    }
    private bool IsSuppressed(IWorldAdjacencyNeighbour neighbour, int index) =>
        ((m_suppressEntity is { } suppress) && suppress(neighbour.EntityAddress(index: index)));
    // Maps a neighbour placement's authored transform into the SOURCE side's own coordinates through the EXACT SAME
    // isometry WorldFrameIsometry.MapArrival uses for a crossing traveler's arrival, anchored at the two faces' own
    // frames (never a crossing's swept seam — this maps arbitrary geometry, not one traveler's own crossing point).
    // Fixed point throughout except the two float<->fixed boundary conversions (the one sanctioned rendering seam),
    // so the strip a body sees is placed by the IDENTICAL math the strip it stands on already uses.
    private static WorldPlacement MapIntoSource(WorldPlacement placement, IReadOnlyList<WorldAdjacencyFramePair> path, WorldDefinition definition) {
        var frame = WorldDefinitionRows.ResolvedFrame(definition: definition, placement: placement);
        var mapped = placement with { Position = frame.FixedPosition, YawDegrees = frame.YawDegrees, Scale = frame.Scale, Parent = null };
        var position = frame.FixedPosition;

        foreach (var stage in path) {
            var yaw = FixedQ4816.FromDegrees(degrees: mapped.YawDegrees);
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

            position = WorldFrameIsometry.MapPoint(
                point: position,
                source: stage.Neighbour,
                destination: stage.Source
            );
            mapped = mapped with {
                Position = position,
                YawDegrees = ((float)double.RadiansToDegrees(radians: ((double)FixedQ4816.Atan2(
                y: mappedForward.X,
                x: mappedForward.Z
            )))),
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
    // The ONE selection rule, read by emission and by the rebuild watch alike so the program and the counter that
    // decides whether to rebuild it can never disagree about which bodies a band renders.
    // Appends the bodies a band renders, ascending, and returns whether the budget truncated them. Read whole by
    // emission; the frame thread keeps the same rule from the bodies that changed (Select).
    private bool SelectBand(WorldAdjacencyProjection projection, List<int> destination) {
        var neighbour = projection.Neighbour;
        var frame = projection.Path[0].Neighbour;
        var bound = Math.Min(
            val1: neighbour.EntityCapacity,
            val2: BodyStride
        );
        var selected = 0;
        var truncated = false;

        foreach (var index in neighbour.ActiveEntities) {
            if (
                IsSuppressed(
                index: index,
                neighbour: neighbour
            ) ||
                !Near(depth: projection.OverlapDepth, frame: frame, index: index, neighbour: neighbour)
            ) {
                continue;
            }

            if (index >= bound) {
                truncated = true;

                continue;
            }

            if (selected >= MaxEntitiesPerBand) {
                return true;
            }

            selected++;
            destination.Add(item: index);
        }

        return truncated;
    }

    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        var meshDraws = new List<SdfMeshDraw>();

        EmitCurrent(
            builder: builder,
            slotBase: context.SlotBase,
            includeEntities: true,
            meshDraws: meshDraws
        );
        m_meshDraws = meshDraws;
    }
    /// <summary>Emits the currently reachable live adjacency geometry without the capacity-probe branch. Camera
    /// clearance uses this to evaluate the same static strip the renderer composes.</summary>
    /// <param name="builder">The program builder.</param>
    /// <param name="slotBase">The emitter's first dynamic-transform slot.</param>
    /// <param name="includeEntities">Whether each band's bodies are emitted too.</param>
    /// <param name="meshDraws">Receives the mesh draws of the neighbours' static placements, mapped into this world, or
    /// <see langword="null"/> to collect none.</param>
    /// <param name="queryOnly">Admit only placements the static contact evaluator supports, for presentation queries.</param>
    public void EmitCurrent(SdfProgramBuilder builder, int slotBase = 0, bool includeEntities = false, ICollection<SdfMeshDraw>? meshDraws = null, bool queryOnly = false) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        // One Visuals() read, indexed rather than foreach'd — the compile-time type is the IReadOnlyList seam, and
        // an indexer read costs no enumerator where a foreach over it would allocate one.
        var projections = m_source.Visuals();
        var bandLimit = Math.Min(
            val1: projections.Count,
            val2: m_bandCount
        );

        if (includeEntities) {
            // m_emittedProjections stays a bare array — PackDynamicTransforms foreach's it every produced frame, and
            // an array foreach costs no enumerator where an IReadOnlyList foreach would. Visuals()'s own concrete
            // return is already a freshly-built array for this tick, so the untruncated case stores it directly
            // rather than copying; only an over-authored live band (more than this composition reserved at boot)
            // pays for a copy, bounded to what actually fits.
            m_emittedProjections = ((projections.Count > m_bandCount)
                ? CopyProjections(
                    count: bandLimit,
                    source: projections
                )
                : ((projections is WorldAdjacencyProjection[] array)
                    ? array
                    : CopyProjections(
                        count: projections.Count,
                        source: projections
                    )
            ));
        }

        for (var bandIndex = 0; (bandIndex < projections.Count); bandIndex++) {
            var projection = projections[bandIndex];

            // The per-band appearance arrays and the frozen instance reservation are both sized from the BOOT
            // document's projection capacity. A live mutation that authors another adjacency row can outrun both, so
            // the extra band is dropped by name rather than indexed past the arrays it has no room in.
            if (bandIndex >= m_bandCount) {
                if (m_truncationNarrated.Add(item: projection.Name)) {
                    WorldClientDiagnostics.Report(channel: "world.adjacency", text: $"'{projection.Name}' is beyond the {m_bandCount} band(s) this render composition reserved at boot; its border renders nothing until the world reloads");
                }

                break;
            }

            var neighbour = projection.Neighbour;
            // The neighbour image is read once: the border's geometry and its bound colors both come from it.
            var image = neighbour.Definition;
            var selection = WorldAdjacencyGeometry.Select(
                definition: image,
                frame: projection.Path[0].Neighbour,
                overlapDepth: projection.OverlapDepth
            );
            var placements = selection.Placements;
            var mappedCount = placements.Count;

            for (var index = 0; (index < mappedCount); index++) {
                m_mappedPlacements[index] = MapIntoSource(
                    placement: placements[index],
                    path: projection.Path,
                    definition: image
                );
            }

            if (mappedCount > 0) {
                var prepared = ((!queryOnly && includeEntities) ? m_staticProjections.GetValueOrDefault(projection.Name) : null);
                var mappedRows = (prepared?.Rows ?? m_mappedPlacements.AsSpan(0, mappedCount).ToArray());
                // The mapped rows are standalone source-space frames. Looking their ids up in the neighbour's
                // original placement table would discard the mapped positions.
                var mappedImage = (prepared?.Image ?? (image with { PlacementRowsRaw = mappedRows }));
                var colors = ColorsOf(
                    definition: image,
                    projection: projection
                );

                colors.Begin();
                // Adjacency delivery currently carries the neighbouring document, not its hash-pinned font asset
                // bytes. Emit its ordinary geometry but omit creation text until federation owns asset transport and
                // the local renderer can merge remote catalogs into its one glyph binding.
                WorldPlacementStamper.EmitStatic(
                    builder: builder,
                    colors: colors,
                    definition: mappedImage,
                    creations: image.Creations,
                    placements: (queryOnly ? WorldPlacementStamper.Queryable(
                        definition: mappedImage,
                        placements: mappedRows,
                        excluded: (placement, reason) => WorldClientDiagnostics.Report(channel: "world.presentation-field", text: $"excluded adjacency '{projection.Name}' placement '{placement.Id}': {reason}")
                    ) : mappedRows),
                    palettes: m_palettes,
                    meshDraws: meshDraws,
                    meshScope: projection.Name,
                    sharedAssets: prepared?.Assets
                );
                if (prepared is not null) {
                    prepared.Assets.Emit(builder, colors, 0, null, prepared.Picks);
                }
            }

            if (includeEntities) {
                EmitEntities(
                    builder: builder,
                    projection: projection,
                    bandIndex: bandIndex,
                    slotBase: slotBase
                );
            }
        }
    }
    /// <inheritdoc/>
    /// <remarks>An entity repacks only while its mapped pose moves. The emitted set changes only with a rebuild, which
    /// owes the whole table, so an unemitted entity's range stays parked.</remarks>
    public void PackDynamicTransforms(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved) {
        var bandIndex = 0;

        foreach (var projection in m_emittedProjections) {
            var neighbour = projection.Neighbour;
            var bandBase = context.SlotBase;
            var alpha = neighbour.InterpolationAlpha;

            var bound = Math.Min(
                val1: neighbour.EntityCapacity,
                val2: BodyStride
            );
            // The emitted latch, not a fresh liveness read: the program's geometry was compiled for exactly this set, and
            // re-deriving the set here would pack a body whose leaves this program never emitted. A body that left the
            // set seeds afresh if it returns.
            var emitted = (Volatile.Read(location: ref m_emittedLists[bandIndex]) ?? []);

            if (!ReferenceEquals(objA: emitted, objB: m_packedLists[bandIndex])) {
                foreach (var left in (m_packedLists[bandIndex] ?? [])) {
                    if (Array.BinarySearch(array: emitted, value: left) < 0) { m_gaits[((bandIndex * BodyStride) + left)].Seeded = false; }
                }
                m_packedLists[bandIndex] = emitted;
            }

            foreach (var entity in emitted) {
                var motionIndex = ((bandIndex * BodyStride) + entity);

                if (entity >= bound) {
                    continue;
                }

                var position = WorldPoseInterpolation.Position(
                    previous: neighbour.PreviousPosition(index: entity),
                    current: neighbour.CurrentPosition(index: entity),
                    alpha: alpha
                );
                var orientation = WorldPoseInterpolation.Orientation(
                    previous: neighbour.PreviousOrientation(index: entity),
                    current: neighbour.CurrentOrientation(index: entity),
                    alpha: alpha
                );
                if (!WorldMirroredAvatarBand.Wake(
                    address: neighbour.EntityAddress(index: entity),
                    epoch: neighbour.PoseEpoch(index: entity),
                    gait: ref m_gaits[motionIndex],
                    moved: moved,
                    orientation: orientation,
                    owner: motionIndex,
                    owners: m_motionOwners,
                    position: position
                )) {
                    continue;
                }

                var mapped = MapPoseIntoSource(
                    position: position,
                    orientation: orientation,
                    path: projection.Path
                );

                WorldMirroredAvatarBand.Pack(
                    avatar: entity,
                    catalogBase: bandBase,
                    firstSlot: m_liveSlots[motionIndex],
                    gait: in m_gaits[motionIndex],
                    gaitAmplitude: m_emittedGaitAmplitudes[motionIndex],
                    moved: moved,
                    owner: motionIndex,
                    owners: m_motionOwners,
                    rig: m_emittedRigs[motionIndex],
                    rootOrientation: mapped.Orientation,
                    rootPosition: mapped.Position,
                    scale: m_emittedScales[motionIndex],
                    table: slots
                );
            }
            bandIndex++;
        }
        PublishStaticCopies();
    }

    /// <summary>Gets the revision of the neighbours' geometry as the last <see cref="WriteRevision"/> polled it: it moves
    /// when a neighbour's definition or snapshot moves, a neighbour becomes reachable or unreachable, or a bound color its
    /// border baked moves, and not when only the bodies each band selects change.</summary>
    public int NeighbourRevision => m_neighbourRevision;

    /// <inheritdoc/>
    public void WriteRevision(Span<int> destination) {
        // Poll every band's neighbour reachability/definition-revision — a live neighbour edit (or a neighbour
        // becoming reachable/unreachable) bumps this component so the host rebuilds, exactly like every other
        // watched counter in this composition. One Visuals() call, reused by both passes below.
        var visuals = m_source.Visuals();

        m_observedAdjacencies.Clear();

        for (var index = 0; (index < visuals.Count); index++) {
            var projection = visuals[index];

            _ = m_observedAdjacencies.Add(item: projection.Name);

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
            // A bound color the border baked moving in the neighbour's state mirror rebuilds it, as the local scene
            // emitter's baked-color component does for its own placements.
            if (ColorsOf(
                definition: projection.Neighbour.Definition,
                projection: projection
            ).TryTakeMove()) {
                m_neighbourRevision++;
            }
        }

        m_missingAdjacencies.Clear();

        foreach (var key in m_polledRevisions.Keys) {
            if (!m_observedAdjacencies.Contains(item: key)) {
                m_missingAdjacencies.Add(item: key);
            }
        }
        foreach (var missing in m_missingAdjacencies) {
            _ = m_neighbourColors.Remove(key: missing);

            if (m_polledRevisions[missing] != default) {
                m_polledRevisions[missing] = default;
                m_neighbourRevision++;
            }
        }

        // Re-run the ONE selection rule and compare it against what the live program was compiled for. A band whose
        // set moved needs a rebuild; a band the emitted layout no longer has room for is already covered by the
        // reachability component above.
        var bandLimit = Math.Min(
            val1: visuals.Count,
            val2: m_bandCount
        );

        for (var bandIndex = 0; (bandIndex < bandLimit); bandIndex++) {
            var emitted = (Volatile.Read(location: ref m_emittedLists[bandIndex]) ?? []);
            var selected = m_selected.AsSpan(length: m_selectedCount[bandIndex], start: (bandIndex * MaxEntitiesPerBand));

            if (!selected.SequenceEqual(other: emitted)) {
                m_selectionRevision++;
            }
        }

        destination[0] = m_neighbourRevision;
        destination[1] = m_selectionRevision;
    }
}
