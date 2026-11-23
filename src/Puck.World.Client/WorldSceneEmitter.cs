using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The overworld's geometry, as one <see cref="ISdfSceneEmitter"/> composed by <see cref="SdfCompositionFrameSource"/>:
/// the diegetic screen slabs, the static placement stamps and the
/// creation-stamp pool (animated and body-attached rows), and the population's active avatars as leaf-level dynamic
/// instances. It owns what geometry
/// exists; <see cref="WorldFramePresenter"/> owns how a frame presents it (views, cameras, gizmos, audio).
/// </summary>
/// <remarks>Live catalog slots precede articulated creation slots and shared asset templates. Composition appends rigid-copy roots. Every anchor resolves through the published slot mapping.</remarks>
public sealed class WorldSceneEmitter : ISdfSceneEmitter {
    // The per-seat perception anchor: the crowd soft-shadow centers resolve each seat's body index through it, so a
    // possession anchor swap moves the crowd bound with the seat's perceived body. The always-cast/footstep gates
    // below stay keyed on the raw body index band instead — see their own comments for why.

    // The static placements' palettes, reused across rebuilds (WorldPlacementStamper.EmitStatic).
    private readonly WorldStaticPalettes m_palettes = new();

    private readonly WorldCreationInstances m_animator;
    private readonly IWorldAudioCueSink m_audio;
    // Boot-consumed authoring policy (WorldPlacementPolicyDefaults), captured once at construction: the initial
    // screen-slot reserve. Later policy edits change the next boot's reserve; live content grows the GPU buffers.
    private readonly int m_authoringHeadroomScreens;
    private readonly WorldClient m_client;
    private readonly WorldContinuum m_continuum;
    // The binder's boot-reserved derived-face slot count — the band the headroom screen scan must leave alone (the
    // binder registers it at boot from the same field, WorldFramePresenter.m_derivedFaceScreens).
    private readonly int m_derivedFaceScreens;
    private readonly float m_noseFactor;
    // The probe's placement reservation, frozen at construction: the boot scoped stamps, and the boot per-shape
    // instances plus the authoring headroom.
    private readonly WorldTextCatalog m_text;

    // The CURRENT derived-face screen ROWS (creation faces derived from placements x creations) — threaded in from
    // WorldFramePresenter.ReconcileDelivery's ONE WorldPrototypeFacets.Derive call each delivery via ObserveDelivery,
    // NEVER re-derived here: the geometry this emitter composes and the binder's bound sources must read the SAME
    // set or a face's slab and its bound texture could disagree about which placement it belongs to. Seeded at
    // construction to the reserved-band PLACEHOLDER rows (WorldPrototypeFacets.ReservedFaceSlots — the identical
    // shape WorldBootComposition's own boot registration uses), so the ONE construction-time capacity probe (which
    // runs before any delivery can land — see SdfCompositionFrameSource's ctor) reserves program-word/instance
    // capacity for the derived band by construction, exactly like every other worst-case branch this type owns.
    // Always exactly m_derivedFaceScreens entries (Derive pads the reserved range with placeholders), so the word
    // cost this band contributes is IDENTICAL between the probe and every live build.
    private IReadOnlyList<WorldScreen> m_derivedFaceRows;
    private float m_pendingDeltaSeconds;

    // The bounded volumes the latest live build's static placements baked (WorldPlacementStamper.EmitStatic);
    // WorldFramePresenter.Dress composes them with the pool's per-frame volumes onto SdfFrame.Volumes.
    private readonly List<SdfVolume> m_staticVolumes = new(capacity: SdfProgramBuilder.MaxVolumes);

    // MeshDraws: the static draws each static rebuild replaces, then the pool's.
    private readonly WorldSceneMeshDraws m_meshDraws;

    private readonly WorldPickMapBuilder m_picks = new();

    /// <inheritdoc/>
    public ISdfPickMap? PickMap => m_picks.Snapshot(pool: m_animator.PickMeshTargets);

    // The catalog avatars: the band, the look identity the live program was compiled for, and each body's gait and root
    // follower (WorldCatalogAvatars, the pack every scene shares).
    private readonly WorldCatalogAvatars m_avatars;
    // The creation-stamp census, refreshed at each rebuild: the body-rooted stamps handed to the pool. A body the pool
    // registered renders its creation there, so the pack/emit path parks its catalog avatar.
    private readonly WorldBodyStampCensus m_census = new();
    private readonly List<WorldCreationInstances.BodyStamp> m_presentedStamps = [];

    // The palette colors the live build baked, and a counter moved when a bound one moves in the state mirror.
    private readonly WorldBakedColors m_bakedColors;

    private int m_bakedColorRevision;
    // A counter moved when a stamped body's live scale moves, so the stamps bake it (WorldBodyStampCensus.TryTakeMove).
    private int m_censusRevision;
    // The screens a body carried at the last revision watch, one bit per screen index, and the counter that moves when
    // the set does: a carried screen draws no slab of its own, so picking it up or putting it down rebuilds the program.
    private uint m_carriedScreens;
    private int m_carriedRevision;
    private bool m_carriedEvaluated;
    private int m_carriedDefRevision = int.MinValue;
    private long m_carriedActiveRevision;
    private long m_carriedCarryRevision;
    private WorldScreen[] m_carryableScreens = [];
    private long m_staticVolumesRevision;

    // The screens, static placement stamps + the creation-stamp pool, then the view's active avatars as leaf-level dynamic
    // instances riding frozen catalog slots. Active-only, never declared-but-parked: the per-tile instance mask width
    // derives from the program's total declared instance count (SdfProgram.InstanceMaskWordCount), so parked avatar
    // declarations widen every shadow-gather pixel's mask walk. Instead the program is rebuilt on population change
    // (the revision watch). Every avatar keeps its own body + accent material (cheap constant words), so a recolor is
    // data, not a resize.
    private void Compose(SdfProgramBuilder builder, IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldScreen> derivedFaces, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldPrototype> creations, int slotBase) {
        var client = m_client;

        m_picks.Clear();
        // The per-avatar body + accent materials, registered up front so the catalog emitter is a straight builder chain.
        // A local seat's colors come from its seated profile (a pending seat renders a desaturated candidate); a stand-in's
        // from its snapshot palette. A color change bumps the revision and rebuilds; a settings-only edit does not.
        m_avatars.EmitPalette(builder: builder, dynamics: client.Definition.Dynamics, noseFactor: m_noseFactor);

        // The diegetic screens: each a sampled ScreenSlab whose lit face samples its bound source (or shades as dark
        // glass when unbound). STATIC data — emitted every build (probe and live), so the
        // capacity floors cover them by construction (no probe-only branch). The sampled overload takes the explicit
        // world frame (Origin/Right/Up) baked into the surface table for UV mapping; the geometry rounded box is
        // placed by translating to its CENTER, which sits one HalfDepth behind the face along the face normal
        // (Right × Up). The material sentinel the overload assigns needs no palette entry.
        //
        // TWO SOURCES, ONE EMISSION PATH: `screens` (the document's declared rows, padded with authoring headroom)
        // and `derivedFaces` (the reserved-band rows a creation's own faces resolve to — see WorldPrototypeFacets;
        // WorldFramePresenter threads the SAME derived set here that it hands the screen binder, so the geometry a
        // derived face's slab occupies and the source the binder samples for it can never disagree about which
        // placement it belongs to). Both index ranges are disjoint by construction (WithAuthoringHeadroom skips the
        // reserved band), so emitting them back-to-back cannot double-declare a screen index.
        WorldStaticSceneEmit.Emit(
            builder: builder,
            carried: CarriedRow,
            derivedFaces: derivedFaces,
            screens: screens
        );

        var meshDraws = new List<SdfMeshDraw>();

        m_staticVolumes.Clear();
        WorldPlacementStamper.EmitStatic(builder: builder, definition: client.Definition, creations: creations,
            placements: placements, textCatalog: m_text.Catalog, tintFor: null, volumes: m_staticVolumes,
            meshDraws: meshDraws, colors: m_bakedColors, palettes: m_palettes, picks: m_picks, isolation: m_animator.Isolation, sharedAssets: m_animator.Rigid);
        m_staticVolumesRevision++;
        m_meshDraws.Static = meshDraws;

        m_animator.Emit(
            builder: builder,
            colors: m_bakedColors,
            slotBase: (slotBase + m_catalog.SlotCount),
            picks: m_picks,
            textCatalog: m_text.Catalog,
            screens: screens
        );

        m_avatars.EmitBodies(builder: builder, picks: m_picks, slotBase: slotBase);
    }
    // A locally followed traveler has exactly one primary avatar for its entire route. While it is in the boot
    // authority this reads the ordinary client slot; after handoff the same frozen local-seat catalog range follows
    // the route seed/continuum pose. Adjacency rendering suppresses that exact address, so a transfer cannot create
    // a one-frame zero-avatar gap or a two-avatar overlap.
    private bool IsAvatarPresented(int index) {
        if (m_animator.HasBodyRegistration(bodyIndex: index)) { return false; }
        if (IsPresentedElsewhere(index: index)) {
            return false;
        }
        if (m_client.IsActive(index: index)) {
            return true;
        }
        if (!TryTravelingRoute(
            index: index,
            route: out var route
        )) {
            return false;
        }
        var entity = route.Entity;

        return route.Endpoint.TryEntityPose(
            entity: in entity,
            orientation: out _,
            position: out _
        );
    }
    // The bodies IsAvatarPresented may answer for, ascending and each once: every local seat, which keeps its catalog
    // range while it travels and is active in no snapshot, then the active bodies past the seats.
    private void CatalogCandidates(List<int> bodies) {
        for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
            bodies.Add(item: seat);
        }

        var first = bodies.Count;
        var seats = 0;

        m_client.CopyActiveBodies(bodies: bodies);
        while (((first + seats) < bodies.Count) && (bodies[(first + seats)] < WorldBodiesLimits.LocalSeatCount)) {
            seats++;
        }

        bodies.RemoveRange(count: seats, index: first);
    }
    private (byte Rig, WorldLook Look, Vector3 Color) CatalogAppearance(int index) {
        // A traveling seat keeps its roster color. Its look and rig come from the authority presenting it.
        var color = m_client.BodyColor(index);
        if (!m_client.IsActive(index) && TryTravelingRoute(index, out var route)) {
            var entity = route.Entity;
            if (route.Endpoint.TryEntityAppearance(in entity, out _, out var look, out var rig)) {
                return (rig, look, color);
            }
        }

        return (m_client.CatalogRig(index), m_client.Look(index), color);
    }
    private bool TryPresentedPose(int index, out Vector3 position, out Quaternion orientation, out WorldEntityAddress address) {
        if (!IsPresentedElsewhere(index: index) && m_client.IsActive(index: index)) {
            position = m_client.Position(index: index);
            orientation = m_client.Orientation(index: index);
            address = m_client.EntityAddress(index: index);
            return true;
        }
        if (
            TryTravelingRoute(
            index: index,
            route: out var route
        ) &&
            m_continuum.TryResolveSeatPose(
            interpolationAlpha: 1f,
            orientation: out orientation,
            position: out position,
            slot: index
        )
        ) {
            address = route.Entity;
            return true;
        }
        position = default;
        orientation = Quaternion.Identity;
        address = default;
        return false;
    }
    // A seat presented elsewhere (WorldContinuum.PresentedElsewhere) is drawn by the scene of the world it is presented
    // in, never at foreign coordinates in this one.
    private bool IsPresentedElsewhere(int index) => (
        (((uint)index) < WorldBodiesLimits.LocalSeatCount) &&
        m_client.Roster.IsJoined(slot: index) &&
        (m_continuum.PresentedElsewhere(slot: index) is not null)
    );
    private bool TryTravelingRoute(int index, out WorldAuthorityRoute route) {
        if (
            (((uint)index) < WorldBodiesLimits.LocalSeatCount) &&
            m_client.Roster.IsJoined(slot: index) &&
            (m_continuum.PresentedElsewhere(slot: index) is null)
        ) {
            route = m_continuum.Route(slot: index);
            return true;
        }
        route = null!;
        return false;
    }

    // The boot screens padded with headroom slabs at free engine indices (bounded by the engine surface ceiling), so a
    // runtime UpsertScreen of a NEW index fits the probed envelope. BOOT-CONSUMED: reads the frozen
    // m_authoringHeadroomScreens field.
    //
    // FREE means neither authored NOR reserved: the derived-face band belongs to the binder's boot-reserved
    // placeholders, which no authored screen may claim either (WorldDefinitionValidator refuses one, through the SAME
    // WorldPrototypeFacets.IsReservedFaceIndex test). A first-fit scan that knew only the authored set would, with
    // enough authored screens, hand a headroom slab an index the derived-face registration also uses. There is no
    // narrowing fallback: a headroom count the free indices cannot satisfy is a document the boot envelope cannot
    // honour. Silently reserving fewer slots would misrepresent the authored policy; buffer growth cannot add screen
    // indices beyond the engine's screen-surface limit.
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        // A live rebuild. The stamp pool reconciles FIRST on every rebuild (a definition delivery OR a population/look
        // change): animated placements root statically, attached ones root on their target body; the creation-stamp bodies (inhabitants + crowd creation-looks)
        // root on live body poses, so the set follows the active-entity/look census, not just the document.
        var definition = m_client.Definition;

        m_bakedColors.Begin();
        Compose(
            builder: builder,
            screens: definition.Screens,
            derivedFaces: m_derivedFaceRows,
            placements: definition.Placements,
            creations: definition.Creations,
            slotBase: context.SlotBase
        );
    }

    private readonly WorldCatalogBand m_catalog;
    // The delegates the avatar pack is handed each frame, made once.
    private readonly WorldCatalogAvatarPose m_presentedPose;
    private readonly Action<Vector3> m_footstep;

    private readonly int[] m_preparedRevision = new int[WorldClient.RevisionComponentCount];

    private bool m_prepared;

    private int m_preparedPresentation = -1;

    private int m_preparedCode;

    public void PrepareFrame() {
        PrepareRegistrations();
        m_animator.PrepareGuess(source: m_client);
    }
    private void PrepareRegistrations() {
        Span<int> revision = stackalloc int[WorldClient.RevisionComponentCount];

        m_client.WriteRevision(destination: revision);
        if (m_prepared && revision.SequenceEqual(other: m_preparedRevision) && (m_preparedHolding == m_client.HoldingRevision) && (m_preparedPresentation == m_continuum.PresentationRevision) && (m_preparedCode == Puck.Abstractions.CodeRevision.Current) && !m_census.TryTakeMove()) { return; }
        revision.CopyTo(destination: m_preparedRevision);
        m_prepared = true;
        m_preparedHolding = m_client.HoldingRevision;
        m_preparedPresentation = m_continuum.PresentationRevision;
        m_preparedCode = Puck.Abstractions.CodeRevision.Current;
        ReconcileStamps();
        m_animator.Rigid.PresentHoldings(placements: m_client.Definition.Placements);
    }
    // The holding revision the latest reconcile read (WorldClient.HoldingRevision).
    private int m_preparedHolding;

    private void ReconcileStamps() {
        var definition = m_client.Definition;
        m_census.Refresh(source: m_client);
        m_presentedStamps.Clear();
        foreach (var stamp in m_census.Stamps) {
            if (!IsPresentedElsewhere(index: stamp.BodyIndex)) {
                m_presentedStamps.Add(item: stamp);
            }
        }
        m_catalog.ObserveArticulation(m_presentedStamps);
        m_animator.Rigid.ReconcileStatic(definition, definition.Placements);
        m_animator.Reconcile(
            placementFrames: definition.PlacementFrames,
            looks: definition.Looks,
            placements: definition.Placements,
            creations: definition.Creations,
            dynamics: definition.Dynamics,
            bodyStamps: m_presentedStamps
        );
        m_avatars.Prepare();
    }

    public IReadOnlyList<int> MeshAssetSlots => m_animator.Rigid.Slots;
    public IReadOnlyList<SdfMeshInstance> MeshInstances => m_animator.Rigid.Copies;
    /// <inheritdoc/>
    public IReadOnlyList<int> MeshInstanceChanges => m_animator.Rigid.ChangedCopies;
    /// <inheritdoc/>
    public long MeshInstancesRevision => m_animator.Rigid.CopiesRevision;

    /// <summary>Re-points this emitter's derived-face screen rows at the same set <see cref="WorldFramePresenter"/>
    /// just derived and handed the screen binder — never re-derived here (see <see cref="WorldPrototypeFacets.Derive"/>'s
    /// one call site), so the slab geometry this emitter composes and the binder's bound sources can never disagree
    /// about which face belongs to which placement. Call at the delivery boundary (a definition-revision move).</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="derivedFaces">This delivery's derived-face screen rows — always exactly
    /// <see cref="WorldDefinition.DerivedFaceScreens"/> entries (<see cref="WorldPrototypeFacets.Derive"/> pads the reserved range
    /// with placeholders), composed alongside <paramref name="definition"/>'s own declared screens on every
    /// subsequent rebuild until the next delivery.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="derivedFaces"/> is
    /// <see langword="null"/>.</exception>
    public void ObserveDelivery(WorldDefinition definition, IReadOnlyList<WorldScreen> derivedFaces) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: derivedFaces);

        m_derivedFaceRows = derivedFaces;
    }
    /// <inheritdoc/>
    /// <remarks>Every active avatar's leaves ride its interpolated snapshot pose plus a distance-driven gait phase (an
    /// idle avatar holds its pose; a teleport is clamped so it cannot spin the limbs through dozens of cycles), and the
    /// animated/body-rooted stamps follow in the replay pool's range (<see cref="WorldCatalogAvatars.Pack"/>). A local
    /// seat traveling another authority's route follows the route's pose in its own range, and local seats fire one
    /// footstep cue per gait half-cycle.</remarks>
    public void PackDynamicTransforms(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved) {
        m_animator.SetCatalogSlots(m_catalog.Slots, context.SlotBase);
        var deltaSeconds = m_pendingDeltaSeconds;

        m_pendingDeltaSeconds = 0f;
        m_avatars.Pack(
            context: in context,
            deltaSeconds: deltaSeconds,
            footstep: m_footstep,
            moved: moved,
            pool: m_animator,
            pose: m_presentedPose,
            slots: slots,
            source: m_client
        );

        // The stamp pool packs after the avatar catalog (its reserved slots sit past the frozen avatar capacity):
        // animated placements ride their static pose, attached and body-rooted stamps ride the client's live body pose; hidden slots
        // park below the floor.
        m_animator.PresentCues(m_client.CueFrames, m_client);
        m_animator.PackTransforms(
            transforms: slots,
            client: m_client,
            moved: moved,
            slotBase: (context.SlotBase + m_catalog.SlotCount),
            parkPosition: context.ParkPosition
        );
    }
    /// <summary>Latches <paramref name="deltaSeconds"/> for the catalog-avatar root followers, stepped once by the
    /// next <see cref="PackDynamicTransforms"/>. Call once per produced frame, alongside the stamp pool's own
    /// <c>Tick</c>.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(float deltaSeconds) {
        m_pendingDeltaSeconds += deltaSeconds;
    }
    /// <summary>Writes the program-rebuild watch counters this scene composes over: the client's three
    /// (<see cref="WorldClient.WriteRevision"/> — roster, server snapshot, definition delivery), then the continuum
    /// watch, then a counter that moves when a bound color the live build baked moves in the state mirror
    /// (<see cref="WorldBakedColors.TryTakeMove"/>), then a counter that moves when a
    /// stamped body's live scale moves (<see cref="WorldBodyStampCensus.TryTakeMove"/>), the continuum's pinned
    /// presentation decisions (<see cref="WorldContinuum.PresentationRevision"/>), the operator's shape isolation
    /// (<see cref="WorldShapeIsolation.Revision"/>), and a counter that moves when the set of screens a body carries
    /// moves (<see cref="WorldCreationInstances.TryCarriedScreen"/>).
    /// <para>
    /// Separate components, not their sum, and the client's three stay split too. One of them — the client's server
    /// revision — is assigned from a snapshot and can move down, so any addition anywhere on this path can cancel: a
    /// server revision falling by one while the continuum counter rises by one would leave a sum unmoved and hold a
    /// stale program. Flattening every counter through to the composition host's componentwise compare is what makes
    /// that impossible rather than merely unlikely (see <see cref="ISdfSceneEmitter.WriteRevision"/>).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        m_client.WriteRevision(destination: destination[..WorldClient.RevisionComponentCount]);
        destination[0] = m_catalog.ArticulatedRevision;
        destination[1] = m_catalog.Revision;

        destination[WorldClient.RevisionComponentCount] = m_continuum.Revision;

        if (m_bakedColors.TryTakeMove()) {
            m_bakedColorRevision++;
        }

        destination[(WorldClient.RevisionComponentCount + 1)] = m_bakedColorRevision;

        if (m_census.TryTakeMove()) {
            m_censusRevision++;
        }

        destination[(WorldClient.RevisionComponentCount + 2)] = m_censusRevision;
        destination[(WorldClient.RevisionComponentCount + 3)] = m_continuum.PresentationRevision;
        destination[(WorldClient.RevisionComponentCount + 4)] = m_animator.Isolation.Revision;

        var defRevision = m_client.DefinitionRevision;
        var activeRevision = m_client.ActiveRevision;
        var carryRevision = m_animator.CarryRevision;

        if (!m_carriedEvaluated || (defRevision != m_carriedDefRevision) || (activeRevision != m_carriedActiveRevision) || (carryRevision != m_carriedCarryRevision)) {
            if (defRevision != m_carriedDefRevision) {
                RefreshCarryableScreens();
            }

            m_carriedEvaluated = true;
            m_carriedDefRevision = defRevision;
            m_carriedActiveRevision = activeRevision;
            m_carriedCarryRevision = carryRevision;

            var carried = CarriedScreens();

            if (carried != m_carriedScreens) {
                m_carriedScreens = carried;
                m_carriedRevision++;
            }
        }

        destination[(WorldClient.RevisionComponentCount + 5)] = m_carriedRevision;
        destination[(WorldClient.RevisionComponentCount + 6)] = m_animator.Rigid.HoldingComposeRevision;
        destination[(WorldClient.RevisionComponentCount + 7)] = m_animator.Rigid.StaticTemplateRevision;
    }

    private void RefreshCarryableScreens() {
        var screens = m_client.Definition.Screens;
        var count = 0;

        for (var row = 0; (row < screens.Count); row++) {
            var screen = screens[row];

            if ((screen.Carry is not null) && (((uint)screen.Index) < SdfProgramBuilder.MaxScreenSurfaces)) {
                count++;
            }
        }

        if (count == 0) {
            m_carryableScreens = [];

            return;
        }

        var carryable = new WorldScreen[count];
        var write = 0;

        for (var row = 0; (row < screens.Count); row++) {
            var screen = screens[row];

            if ((screen.Carry is not null) && (((uint)screen.Index) < SdfProgramBuilder.MaxScreenSurfaces)) {
                carryable[write++] = screen;
            }
        }

        m_carryableScreens = carryable;
    }
    // The screens a body carries this frame, one bit per screen index (WorldCreationInstances.IsCarriedScreen).
    private uint CarriedScreens() {
        var carried = 0u;
        var screens = m_carryableScreens;

        for (var row = 0; (row < screens.Length); row++) {
            var screen = screens[row];

            if (m_animator.IsCarriedScreen(
                client: m_client,
                screen: screen
            )) {
                carried |= (1u << screen.Index);
            }
        }

        return carried;
    }
    // A screen the latest revision found carried, at the frame its carrier holds it now; null for any other, which draws
    // its own slab.
    private WorldScreen? CarriedRow(WorldScreen screen) => (((((uint)screen.Index) < SdfProgramBuilder.MaxScreenSurfaces) &&
        ((m_carriedScreens & (1u << screen.Index)) != 0u) &&
        m_animator.TryCarriedScreen(
            carried: out var held,
            client: m_client,
            screen: screen
        ))
        ? held
        : null);

    /// <summary>Initializes a new instance of the <see cref="WorldSceneEmitter"/> class over the boot definition,
    /// freezing the screen headroom and the placement reservation the probe branch reserves.</summary>
    /// <param name="client">The snapshot-fed entity view every pose, color, look, and active flag is read from.</param>
    /// <param name="animator">The creation-stamp pool (animated placements, attached placements, body-rooted stamps).</param>
    /// <param name="audio">The narrow cue-submission seam the distance-driven footstep cue fires into while packing.</param>
    /// <param name="continuum">The route-to-presentation-frame resolver used to keep locally followed travelers in
    /// their original catalog slot across authority handoffs.</param>
    /// <param name="text">The world-relative font catalog used by creation text runs.</param>
    /// <param name="envelope">The boot scene's envelope, whose headroom the probe reserves, or <see langword="null"/>
    /// for one counted from the client's boot definition.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldSceneEmitter(WorldClient client, WorldCreationInstances animator, IWorldAudioCueSink audio, WorldContinuum continuum, WorldTextCatalog text, WorldSceneEnvelope? envelope = null) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: animator);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: continuum);
        ArgumentNullException.ThrowIfNull(argument: text);

        m_client = client;
        m_bakedColors = new WorldBakedColors(mirror: client.StateMirror);
        m_continuum = continuum;
        m_animator = animator;
        m_meshDraws = new WorldSceneMeshDraws(pool: animator);
        m_text = text;
        m_audio = audio;
        m_catalog = new WorldCatalogBand(CatalogCandidates, IsAvatarPresented, CatalogAppearance);
        m_avatars = new WorldCatalogAvatars(band: m_catalog);
        m_presentedPose = TryPresentedPose;
        m_footstep = site => m_audio.SubmitCue(eventToken: WorldAudioCue.PlayerFootstep, site: site);

        var definition = client.Definition;

        envelope ??= new WorldSceneEnvelope(boot: definition);
        m_noseFactor = definition.PlayerDefaults.NoseFactor;

        m_authoringHeadroomScreens = envelope.HeadroomScreens;
        m_derivedFaceScreens = definition.DerivedFaceScreens;
        // Placeholder rows until the first delivery calls ObserveDelivery with the real derived faces — matters only
        // for the construction-time capacity probe (SdfCompositionFrameSource's ctor runs it synchronously, before
        // WorldFramePresenter can ever reconcile a delivery), so the probe reserves this band's word/instance cost even
        // though no delivery has landed yet.
        m_derivedFaceRows = WorldPrototypeFacets.ReservedFaceSlots(
            derivedFaceBase: WorldPlacementPolicy.DerivedFaceBase(definition: definition),
            derivedFaceScreens: m_derivedFaceScreens
        );

        // The capacity probe sees the same registered stamp and avatar bands as the live emitter.
        ReconcileStamps();
    }

    /// <summary>The frozen transform-slot count this emitter declares: maximum-sized rigs for the detailed body band,
    /// one root slot per remaining crowd body, then the reserved creation-stamp pool.</summary>
    public int DynamicSlotCount => (m_catalog.SlotCount + m_animator.DynamicSlotCount);
    /// <summary>Gets the number of catalog avatars the latest <see cref="PackDynamicTransforms"/> read: every avatar on a
    /// frame that owes the whole table or follows a prepare, otherwise only those whose body moved or whose slots have
    /// not come to rest.</summary>
    public int AvatarVisitCount => m_avatars.VisitCount;
    /// <summary>Always <see langword="true"/>: this emitter's material palette is its own. Sole tenancy makes this
    /// true; the scope makes it structural, so a positional stride this
    /// scene grows later (a wallpaper fold or polar repeat over a sculpted creation) can only ever recolor materials
    /// this emitter itself registered. The scene emits no positional stride today, so the clamp is inert and the
    /// composed words are byte-identical to the unscoped build.</summary>
    public bool OwnsMaterialScope => true;
    /// <inheritdoc/>
    public int RevisionComponentCount => (WorldClient.RevisionComponentCount + 8);
    /// <summary>Gets the bounded volumes the latest live build's static placements baked into world space.</summary>
    public IReadOnlyList<SdfVolume> StaticVolumes => m_staticVolumes;
    /// <summary>Gets a monotonic revision bumped whenever <see cref="StaticVolumes"/> is rebuilt.</summary>
    public long StaticVolumesRevision => m_staticVolumesRevision;
    /// <inheritdoc/>
    /// <remarks>One draw per static placement instance of a prototype that carries a mesh, from the last static
    /// rebuild, then the stamp pool's (<see cref="WorldCreationInstances.MeshDraws"/>): an animated, inhabited or attached
    /// stamp's mesh at its root this frame. Recomposed only when the static list is a new one or the pool rewrote its own.</remarks>
    public IReadOnlyList<SdfMeshDraw> MeshDraws => m_meshDraws.Draws;
    /// <inheritdoc/>
    public IReadOnlyList<SdfSlotClip> SlotClips => m_animator.SlotClips;
    /// <inheritdoc/>
    public long MeshDrawsRevision => m_meshDraws.Revision;
    /// <inheritdoc/>
    public Puck.Abstractions.Gpu.GpuRowChanges? MeshDrawChanges => m_meshDraws.Changes;
    /// <inheritdoc/>
    public long MeshDrawChangesSerial => m_meshDraws.Serial;
}
