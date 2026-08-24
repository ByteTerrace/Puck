using System.Numerics;
using Puck.SdfVm;
using Puck.SdfVm.Views;
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
/// <remarks>
/// <para>
/// The probe branch (the contract in <see cref="ISdfSceneEmitter"/>): <see cref="Emit"/> under
/// <see cref="SdfEmitContext.Probe"/> emits the complete 128-rig catalog, the worst-case animated pool, the reserved
/// placement instances, and the authoring headroom (screens and placement stamps) that reserves live editing
/// room. The headroom padding lives here, inside the worst case, rather than being applied to the emitter's inputs by
/// a caller — the capacity-probe doctrine's rule that an emitter's probe branch must dominate its own live branch on
/// its own.
/// </para>
/// <para>
/// Admission (<see cref="ComposeCandidate"/>) is world policy, not composition-host business: what may enter the
/// document is the document owner's question, so it is a member on this type rather than a mode on the generic
/// emitter contract. It composes a candidate through the same emit path into a shared builder — see
/// <see cref="Puck.World.Client.WorldFramePresenter"/>'s joint measurer, which composes it alongside whatever
/// <c>puck.sdf.v1</c> document is currently loaded before measuring the counts the render envelope compares, so a
/// mutation is judged against the same program that would actually be built.
/// </para>
/// <para>
/// The rebuild cadence is a set of numbers, never a time the host queries: <see cref="WriteRevision"/> reports the
/// client watch and the continuum watch side by side rather than combined — see <see cref="WriteRevision"/> for why
/// any sum on this path could silently cancel.
/// </para>
/// <para>
/// Slot layout: this emitter's declared range (<see cref="DynamicSlotCount"/>) is the frozen avatar catalog followed by
/// the creation-stamp pool, both addressed from the base the host assigns
/// (<see cref="SdfEmitContext.SlotBase"/>). Standing condition: two readers outside this emitter —
/// <see cref="WorldEntityPartResolver"/> and the audio director's entity-part resolution — index the shared buffer
/// absolutely, so they are correct only while this emitter's base is 0. It is today (it is the composition's only
/// emitter); registering another emitter ahead of it must re-base those two readers in the same change.
/// </para>
/// </remarks>
internal sealed class WorldSceneEmitter : ISdfSceneEmitter {
    // The per-seat perception anchor: the crowd soft-shadow centers resolve each seat's body index through it, so a
    // possession anchor swap moves the crowd bound with the seat's perceived body. The always-cast/footstep gates
    // below stay keyed on the raw body index band instead — see their own comments for why.
    private readonly WorldPerceptionAnchor m_anchor;
    private readonly WorldStampPool m_animator;
    private readonly IWorldAudioCueSink m_audio;
    private readonly int m_authoringHeadroomPlacements;
    // BOOT-CONSUMED authoring policy (WorldAuthoringDefaults): captured ONCE at construction from the boot definition's
    // Authoring row — never re-read live. These feed the frozen render-envelope probe (screen-slot/
    // placement-instance reservation), so a later SetAuthoringDefaults mutation is journaled but cannot retroactively
    // grow a running session's capacity floor; it narrates "next boot" honestly.
    private readonly int m_authoringHeadroomScreens;
    private readonly WorldClient m_client;
    private readonly WorldContinuum m_continuum;
    // The binder's boot-reserved derived-face slot count — the band the headroom screen scan must leave alone (the
    // binder registers it at boot from the same field, WorldFramePresenter.m_derivedFaceScreens).
    private readonly int m_derivedFaceScreens;
    private readonly float m_noseFactor;
    // The placement capacity reservation, in worst-case stamp instances: boot static instances + the authoring
    // headroom. Frozen at construction; the apply-time measure charges max(candidate instances, this).
    private readonly int m_placementReservation;
    private readonly WorldRenderSettings m_settings;
    private readonly WorldTextCatalog m_text;

    // The CURRENT derived-face screen ROWS (creation faces derived from placements x creations) — threaded in from
    // WorldFramePresenter.ReconcileDelivery's ONE WorldCreationFacets.Derive call each delivery via ObserveDelivery,
    // NEVER re-derived here: the geometry this emitter composes and the binder's bound sources must read the SAME
    // set or a face's slab and its bound texture could disagree about which placement it belongs to. Seeded at
    // construction to the reserved-band PLACEHOLDER rows (WorldCreationFacets.ReservedFaceSlots — the identical
    // shape WorldBootComposition's own boot registration uses), so the ONE construction-time capacity probe (which
    // runs before any delivery can land — see SdfCompositionFrameSource's ctor) reserves program-word/instance
    // capacity for the derived band by construction, exactly like every other worst-case branch this type owns.
    // Always exactly m_derivedFaceScreens entries (Derive pads the reserved range with placeholders), so the word
    // cost this band contributes is IDENTICAL between the probe and every live build.
    private IReadOnlyList<WorldScreen> m_derivedFaceRows;
    // Latched from the ONE construction-time probe: the host assigns SlotBase once and it is stable for this emitter's
    // lifetime, so the candidate measure composes against the same base a live program does.
    private int m_slotBase;

    // Per-frame scratch reused to keep packing allocation-free: movement-driven gait state per avatar.
    private readonly float[] m_avatarGaitPhases = new float[WorldAvatarCatalog.Capacity];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldAvatarCatalog.Capacity];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldAvatarCatalog.Capacity];
    private readonly WorldEntityAddress[] m_avatarMotionAddresses = new WorldEntityAddress[WorldAvatarCatalog.Capacity];
    // The live program's avatar geometry and its transform pack share this rebuild-latched appearance image. This
    // closes the delivery-thread gap where a reused slot could otherwise pack a new rig into old compiled geometry.
    private readonly int[] m_emittedAvatarRigs = new int[WorldAvatarCatalog.Capacity];
    private readonly float[] m_emittedAvatarScales = new float[WorldAvatarCatalog.Capacity];
    private readonly float[] m_emittedAvatarGaitAmplitudes = new float[WorldAvatarCatalog.Capacity];
    // The creation-STAMP census, refreshed at each rebuild: the body-rooted stamps handed to the pool, plus the
    // per-entity flag the pack/emit path reads to skip the catalog avatar (the body renders its creation instead).
    private readonly List<WorldStampPool.BodyStamp> m_bodyStamps = new();
    private readonly bool[] m_rendersAsStamp = new bool[WorldAvatarCatalog.Capacity];

    // The catalog-look root follower: a NON-stamp-rendered avatar (a Catalog-sourced look, or a Creation look the
    // stamp pool had no free slot for) whose look names a root Motion.Dynamics row lags the whole avatar toward its
    // raw interpolated pose instead of drawing it directly — resolved once per rebuild (Compose, beside the rig/scale/
    // gait arrays above), stepped once per produced frame (PackDynamicTransforms) from the Tick-latched delta.
    private readonly bool[] m_avatarFollows = new bool[WorldAvatarCatalog.Capacity];
    private readonly SecondOrderResponse[] m_avatarResponse = new SecondOrderResponse[WorldAvatarCatalog.Capacity];
    private readonly SecondOrderFollower3[] m_avatarPositionFollower = new SecondOrderFollower3[WorldAvatarCatalog.Capacity];
    private readonly SecondOrderFollower4[] m_avatarOrientationFollower = new SecondOrderFollower4[WorldAvatarCatalog.Capacity];
    // The last PoseEpoch(index) this emitter observed, per avatar — a jump within the same entity address (a
    // teleport, an over-threshold correction) reseeds the follower exactly like an address change does, so the boom
    // never streaks the follower across the jump. -1 (never observed) reseeds on the first frame.
    private readonly int[] m_avatarDynamicsPoseEpoch = NewPoseEpochs();
    private float m_pendingDeltaSeconds;

    private static int[] NewPoseEpochs() {
        var epochs = new int[WorldAvatarCatalog.Capacity];

        Array.Fill(array: epochs, value: -1);

        return epochs;
    }

    /// <summary>Initializes a new instance of the <see cref="WorldSceneEmitter"/> class over the boot definition,
    /// freezing the authoring-headroom policy and the placement reservation the probe branch reserves against.</summary>
    /// <param name="client">The snapshot-fed entity view every pose, color, look, and active flag is read from.</param>
    /// <param name="settings">The live render settings (the crowd soft-shadow radius is read while packing).</param>
    /// <param name="animator">The creation-stamp pool (animated placements, attached placements, body-rooted stamps).</param>
    /// <param name="audio">The narrow cue-submission seam the distance-driven footstep cue fires into while packing.</param>
    /// <param name="anchor">The per-seat perception anchor the crowd soft-shadow centers resolve their body indices
    /// through.</param>
    /// <param name="continuum">The route-to-presentation-frame resolver used to keep locally followed travelers in
    /// their original catalog slot across authority handoffs.</param>
    /// <param name="text">The world-relative font catalog used by creation text runs.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSceneEmitter(WorldClient client, WorldRenderSettings settings, WorldStampPool animator, IWorldAudioCueSink audio, WorldPerceptionAnchor anchor, WorldContinuum continuum, WorldTextCatalog text) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: animator);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: anchor);
        ArgumentNullException.ThrowIfNull(argument: continuum);
        ArgumentNullException.ThrowIfNull(argument: text);

        m_client = client;
        m_continuum = continuum;
        m_anchor = anchor;
        m_settings = settings;
        m_animator = animator;
        m_text = text;
        m_audio = audio;

        var definition = client.Definition;

        m_noseFactor = definition.PlayerDefaults.NoseFactor;

        m_authoringHeadroomScreens = definition.Authoring.AuthoringHeadroomScreens;
        m_authoringHeadroomPlacements = definition.Authoring.AuthoringHeadroomPlacements;
        m_derivedFaceScreens = definition.Authoring.DerivedFaceScreens;
        // Placeholder rows until the first delivery calls ObserveDelivery with the real derived faces — matters only
        // for the construction-time capacity probe (SdfCompositionFrameSource's ctor runs it synchronously, before
        // WorldFramePresenter can ever reconcile a delivery), so the probe reserves this band's word/instance cost even
        // though no delivery has landed yet.
        m_derivedFaceRows = WorldCreationFacets.ReservedFaceSlots(
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: m_derivedFaceScreens
        );

        // A booted world may already stamp animated/attached placements or inhabited bodies — register them before the probe
        // so the worst-case build sees the same pool the first live build will (body stamps are empty until the first
        // snapshot).
        RefreshBodyStamps();
        m_animator.Reconcile(
            placements: definition.Placements,
            creations: definition.Creations,
            dynamics: definition.Dynamics,
            bodyStamps: m_bodyStamps
        );
        m_placementReservation = (WorldPlacementStamper.StaticStampInstances(
            creations: definition.Creations,
            placements: definition.Placements
        ) + m_authoringHeadroomPlacements);
    }

    /// <summary>The frozen transform-slot count this emitter declares: every leaf in the all-128 avatar catalog plus
    /// the reserved creation-stamp pool, in that order.</summary>
    public int DynamicSlotCount => (WorldAvatarCatalog.DynamicTransformCapacity + WorldStampPool.DynamicSlotCount);
    /// <summary>Always <see langword="true"/>: this emitter's material palette is its own. Sole tenancy makes this
    /// true; the scope makes it structural, so a positional stride this
    /// scene grows later (a wallpaper fold or polar repeat over a sculpted creation) can only ever recolor materials
    /// this emitter itself registered. The scene emits no positional stride today, so the clamp is inert and the
    /// composed words are byte-identical to the unscoped build.</summary>
    public bool OwnsMaterialScope => true;
    /// <inheritdoc/>
    public int RevisionComponentCount => (WorldClient.RevisionComponentCount + 1);

    // The screens, static placement stamps + the creation-stamp pool, then the view's active avatars as leaf-level dynamic
    // instances riding frozen catalog slots. Active-only, never declared-but-parked: the per-tile instance mask width
    // derives from the program's total declared instance count (SdfProgram.InstanceMaskWordCount), so parked avatar
    // declarations widen every shadow-gather pixel's mask walk. Instead the program is rebuilt on population change
    // (the revision watch), and the 128-avatar worst case is held by the probed capacity floors. Every avatar keeps its
    // own body + accent material (cheap constant words), so a recolor is data, not a resize. `placementProbe` replaces
    // the static stamps with the reserved worst case (the construction probe only); the animated pool and the avatars
    // follow `probeWorstCase` (worst case for both the construction probe AND the apply-time measure).
    private void Compose(SdfProgramBuilder builder, IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldScreen> derivedFaces, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations, bool probeWorstCase, bool placementProbe, float maxPlacementScale, int slotBase) {
        var client = m_client;
        // The per-avatar body + accent materials, allocated up front so the catalog emitter is a straight builder chain.
        // A local seat's colors come from its seated profile (a pending seat renders a desaturated candidate); a stand-in's
        // from its snapshot palette. A color change bumps the revision and rebuilds; a settings-only edit does not.
        var avatarBodyMaterials = new int[WorldAvatarCatalog.Capacity];
        var avatarAccentMaterials = new int[WorldAvatarCatalog.Capacity];

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            var bodyColor = (TryPresentedAppearance(
                bodyColor: out var presentedColor,
                catalogRig: out _,
                index: index,
                look: out _
            )
                ? presentedColor
                : client.BodyColor(index: index)
            );

            avatarBodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
            avatarAccentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: (bodyColor * m_noseFactor)));
        }

        // The diegetic screens: each a sampled ScreenSlab whose lit face samples its bound source (or the engine's
        // procedural no-signal fallback when unbound). STATIC data — emitted every build (probe and live), so the
        // capacity floors cover them by construction (no probe-only branch). The sampled overload takes the explicit
        // world frame (Origin/Right/Up) baked into the surface table for UV mapping; the geometry rounded box is
        // placed by translating to its CENTER, which sits one HalfDepth behind the face along the face normal
        // (Right × Up). The material sentinel the overload assigns needs no palette entry.
        //
        // TWO SOURCES, ONE EMISSION PATH: `screens` (the document's declared rows, padded with authoring headroom)
        // and `derivedFaces` (the reserved-band rows a creation's own faces resolve to — see WorldCreationFacets;
        // WorldFramePresenter threads the SAME derived set here that it hands the screen binder, so the geometry a
        // derived face's slab occupies and the source the binder samples for it can never disagree about which
        // placement it belongs to). Both index ranges are disjoint by construction (WithAuthoringHeadroom skips the
        // reserved band), so emitting them back-to-back cannot double-declare a screen index.
        WorldStaticSceneEmit.Emit(
            builder: builder,
            derivedFaces: derivedFaces,
            screens: screens
        );

        // The placement stamps: the construction probe reserves (boot static instances + the authoring headroom)
        // worst-case stamps, and the APPLY-TIME MEASURE charges a candidate's static placements at that same
        // worst-case unit — max(candidate instances, the reservation) — so the placement term stays CONSTANT between
        // probe and measure while placements are inside their headroom. That constancy is load-bearing: a cheaper
        // as-authored measure would hand the reservation's word slack to SCENE/SCREEN floods (their ceilings would
        // silently widen by thousands of words), and a placement flood still rejects exactly one instance past the
        // headroom. Only the LIVE build emits the rows as authored — static stamps baked into instructions, animated
        // rows through the replay pool (worst-case under any probe).
        if (
            placementProbe ||
            probeWorstCase
        ) {
            var candidateInstances = WorldPlacementStamper.StaticStampInstances(
                creations: creations,
                placements: placements
            );

            WorldPlacementStamper.EmitProbe(
                builder: builder,
                reservedCount: Math.Max(
                    val1: candidateInstances,
                    val2: m_placementReservation
                )
            );
        } else {
            WorldPlacementStamper.EmitStatic(
                builder: builder,
                definition: client.Definition,
                creations: creations,
                placements: placements,
                textCatalog: m_text.Catalog,
                tintFor: null
            );
        }

        m_animator.Emit(
            builder: builder,
            definition: client.Definition,
            probeWorstCase: probeWorstCase,
            maxPlacementScale: maxPlacementScale,
            slotBase: (slotBase + WorldAvatarCatalog.DynamicTransformCapacity),
            textCatalog: m_text.Catalog
        );

        // The view's active avatars: 12..20 independently animated leaves and 60..100 authored VM instructions
        // each. The probe emits every catalog range at unit scale (the frozen worst case); a live build emits only
        // active ranges, each sourcing its LOOK's pinned rig and uniform scale (both clamped so the frozen per-entity
        // slot capacity is never exceeded — see WorldAvatarCatalog.Emit's remarks).
        if (!probeWorstCase) {
            for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
                var hasPresented = TryPresentedAppearance(
                    bodyColor: out _,
                    catalogRig: out var presentedRig,
                    index: index,
                    look: out var presentedLook
                );
                var look = (hasPresented
                    ? presentedLook
                    : m_client.Look(index: index)
                );
                var catalogRig = (hasPresented
                    ? presentedRig
                    : m_client.CatalogRig(index: index)
                );

                // A Creation look's body renders through the stamp pool (its catalog avatar parks at the composed
                // ParkPosition), so RigFor's Creation-look fallback is reached here only as the pool-pressure
                // fallback — a body a full stamp pool starved renders as a catalog avatar rather than vanishing.
                m_emittedAvatarRigs[index] = WorldAvatarCatalog.RigFor(
                    catalogRig: catalogRig,
                    look: look
                );
                m_emittedAvatarScales[index] = look.Scale;
                m_emittedAvatarGaitAmplitudes[index] = look.Motion.GaitAmplitude;

                // A body rendering its creation through the stamp pool already carries its own root follower there
                // (Creation looks: root + parts) — this catalog-avatar follower is the root-only twin for a Catalog
                // look, and the pool-pressure fallback for a Creation look the stamp pool had no free slot for.
                m_avatarFollows[index] = (
                    !m_rendersAsStamp[index] &&
                    WorldDynamicsResponse.TryResolveResponse(
                    name: look.Motion.Dynamics,
                    response: out m_avatarResponse[index],
                    rows: client.Definition.Dynamics
                )
                );
            }
        }

        WorldAvatarCatalog.Emit(
            builder: builder,
            isActive: IsAvatarPresented,
            bodyMaterials: avatarBodyMaterials,
            accentMaterials: avatarAccentMaterials,
            probeWorstCase: probeWorstCase,
            slotBase: slotBase,
            rigFor: (probeWorstCase
            ? null
            : index => m_emittedAvatarRigs[index]),
            scaleFor: (probeWorstCase
            ? null
            : index => m_emittedAvatarScales[index])
        );
    }
    // A locally followed traveler has exactly one primary avatar for its entire route. While it is in the boot
    // authority this reads the ordinary client slot; after handoff the same frozen local-seat catalog range follows
    // the route seed/continuum pose. Adjacency rendering suppresses that exact address, so a transfer cannot create
    // a one-frame zero-avatar gap or a two-avatar overlap.
    private bool IsAvatarPresented(int index) {
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
    // Refresh the creation-stamp census: which active entities render their creation geometry through the stamp pool
    // (inhabitants + crowd creation-looks) instead of a catalog avatar. Called at each rebuild.
    private void RefreshBodyStamps() {
        m_bodyStamps.Clear();
        Array.Clear(array: m_rendersAsStamp);

        var definition = m_client.Definition;

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            if (
                !m_client.IsActive(index: index) ||
                (ResolveStampCreation(
                definition: definition,
                index: index
            ) is not { } stamp)
            ) {
                continue;
            }

            m_bodyStamps.Add(item: stamp);
            m_rendersAsStamp[index] = true;
        }
    }
    // The creation a body renders as a stamp, or null (it renders as a catalog avatar): an INHABITANT wears the look's
    // creation (a Creation look) or its placement's own creation; a crowd body wears its look's creation (a Creation
    // look). The uniform scale folds the placement scale and the look scale.
    private WorldStampPool.BodyStamp? ResolveStampCreation(int index, WorldDefinition definition) {
        var look = m_client.Look(index: index);

        if (m_client.PlacementId(index: index) is { } placementId) {
            if (WorldDefinitionRows.FindPlacement(
                placements: definition.Placements,
                id: placementId
            ) is not { } placement) {
                return null;
            }

            var creationId = ((look.Source is WorldLookSource.Creation inhabitLook)
                ? inhabitLook.CreationId.Value
                : placement.CreationId
            );

            return ((WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: creationId
            ) is { } creation)
                ? new WorldStampPool.BodyStamp(
                    BodyIndex: index,
                    Creation: creation,
                    Scale: (placement.Scale * look.Scale),
                    Motion: look.Motion
                )
                : null
            );
        }

        if (look.Source is WorldLookSource.Creation crowdLook) {
            return ((WorldDefinitionRows.FindCreation(
                creations: definition.Creations,
                id: crowdLook.CreationId
            ) is { } creation)
                ? new WorldStampPool.BodyStamp(
                    BodyIndex: index,
                    Creation: creation,
                    Scale: look.Scale,
                    Motion: look.Motion
                )
                : null
            );
        }

        return null;
    }
    private bool TryPresentedAppearance(int index, out Vector3 bodyColor, out WorldLook look, out byte catalogRig) {
        if (m_client.IsActive(index: index)) {
            bodyColor = m_client.BodyColor(index: index);
            look = m_client.Look(index: index);
            catalogRig = m_client.CatalogRig(index: index);
            return true;
        }
        if (TryTravelingRoute(
            index: index,
            route: out var route
        )) {
            var entity = route.Entity;

            if (route.Endpoint.TryEntityAppearance(
                bodyColor: out bodyColor,
                catalogRig: out catalogRig,
                entity: in entity,
                look: out look
            )) {
                return true;
            }
        }
        bodyColor = default;
        look = WorldLook.Implicit;
        catalogRig = 0;
        return false;
    }
    private bool TryPresentedPose(int index, out Vector3 position, out Quaternion orientation, out WorldEntityAddress address) {
        if (m_client.IsActive(index: index)) {
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
    private bool TryTravelingRoute(int index, out WorldAuthorityRoute route) {
        if (
            (((uint)index) < WorldPopulationLimits.LocalSeatCount) &&
            m_client.Roster.IsJoined(slot: index)
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
    // WorldCreationFacets.IsReservedFaceIndex test). A first-fit scan that knew only the authored set would, with
    // enough authored screens, hand a headroom slab an index the derived-face registration also uses. There is no
    // narrowing fallback: a headroom count the free indices cannot satisfy is a document the boot envelope cannot
    // honour, and silently reserving fewer slots than asked only moves the failure to the runtime UpsertScreen that
    // outgrows the probed envelope, where SdfWorldEngine.UploadProgram throws.
    private IReadOnlyList<WorldScreen> WithAuthoringHeadroom(IReadOnlyList<WorldScreen> screens) {
        var padded = new List<WorldScreen>(capacity: (screens.Count + m_authoringHeadroomScreens));
        var used = new HashSet<int>();

        foreach (var screen in screens) {
            padded.Add(item: screen);
            _ = used.Add(item: screen.Index);
        }

        var authored = used.Count;

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            if (WorldCreationFacets.IsReservedFaceIndex(
                derivedFaceScreens: m_derivedFaceScreens,
                index: index
            )) {
                _ = used.Add(item: index);
            }
        }

        padded.AddRange(collection: WorldScreenHeadroom.Reserve(
            authoredCount: authored,
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: m_derivedFaceScreens,
            headroomCount: m_authoringHeadroomScreens,
            usedIndices: used
        ));

        return padded;
    }
    // Whether `position` lies within the crowd radius of any joined local seat (the stand-in soft-shadow gate). With no
    // joined seats or a zero radius, nothing qualifies.
    private static bool WithinCrowd(Vector3 position, ReadOnlySpan<Vector3> seats, float radiusSquared) {
        foreach (var seat in seats) {
            if (Vector3.DistanceSquared(
                value1: position,
                value2: seat
            ) < radiusSquared) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Composes a candidate definition's render-relevant sections into <paramref name="builder"/> — World
    /// admission policy (what may enter the document is the document owner's question, never a mode on the generic
    /// emitter contract), factored so the render-capacity oracle can measure it composed alongside whatever
    /// <c>puck.sdf.v1</c> document (see <see cref="WorldSdfDocumentEmitter"/>) is currently loaded — see
    /// <see cref="Puck.World.Client.WorldFramePresenter"/>'s joint measurer. The packed tables a program carries (the
    /// instance grid, segment directory, world-segment list, rigid plan) are computed over the whole composed
    /// program and are not additive across emitters, so measuring this emitter alone in a throwaway builder would let
    /// a mutation spend capacity a loaded document already holds.</summary>
    /// <param name="builder">The shared program builder under construction. Opens and closes its own material scope
    /// (this emitter owns its palette; see <see cref="OwnsMaterialScope"/>), so the caller must not already be inside
    /// one belonging to this emitter.</param>
    /// <param name="candidate">The composed candidate definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="candidate"/> is
    /// <see langword="null"/>.</exception>
    public void ComposeCandidate(SdfProgramBuilder builder, WorldDefinition candidate) {
        ArgumentNullException.ThrowIfNull(argument: builder);
        ArgumentNullException.ThrowIfNull(argument: candidate);

        using (var scope = builder.BeginMaterialScope()) {
            Compose(
                builder: builder,
                screens: candidate.Screens,
                derivedFaces: m_derivedFaceRows,
                placements: candidate.Placements,
                creations: candidate.Creations,
                probeWorstCase: true,
                placementProbe: false,
                maxPlacementScale: candidate.Authoring.MaxPlacementScale,
                slotBase: m_slotBase
            );
        }
    }
    /// <inheritdoc/>
    public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
        ArgumentNullException.ThrowIfNull(argument: builder);

        m_slotBase = context.SlotBase;

        if (context.Probe) {
            // The ONE construction-time worst case (never rendered): all 128 avatars, the reserved placement instances,
            // the worst-case animated pool, and the authoring headroom (screens and placement stamps) so a
            // live editor can add rows up to the reserved ceilings. Reads the client's definition live because the
            // probe runs exactly once, inside the composition host's constructor, before any delivery can land.
            var boot = m_client.Definition;

            Compose(
                builder: builder,
                screens: WithAuthoringHeadroom(screens: boot.Screens),
                derivedFaces: m_derivedFaceRows,
                placements: boot.Placements,
                creations: boot.Creations,
                probeWorstCase: true,
                placementProbe: true,
                maxPlacementScale: boot.Authoring.MaxPlacementScale,
                slotBase: context.SlotBase
            );

            return;
        }

        // A live rebuild. The stamp pool reconciles FIRST on every rebuild (a definition delivery OR a population/look
        // change): animated placements root statically, attached ones root on their target body; the creation-stamp bodies (inhabitants + crowd creation-looks)
        // root on live body poses, so the set follows the active-entity/look census, not just the document.
        var definition = m_client.Definition;

        RefreshBodyStamps();
        m_animator.Reconcile(
            placements: definition.Placements,
            creations: definition.Creations,
            dynamics: definition.Dynamics,
            bodyStamps: m_bodyStamps
        );
        Compose(
            builder: builder,
            screens: definition.Screens,
            derivedFaces: m_derivedFaceRows,
            placements: definition.Placements,
            creations: definition.Creations,
            probeWorstCase: false,
            placementProbe: false,
            maxPlacementScale: definition.Authoring.MaxPlacementScale,
            slotBase: context.SlotBase
        );
    }
    /// <summary>Re-points this emitter's derived-face screen rows at the same set <see cref="WorldFramePresenter"/>
    /// just derived and handed the screen binder — never re-derived here (see <see cref="WorldCreationFacets.Derive"/>'s
    /// one call site), so the slab geometry this emitter composes and the binder's bound sources can never disagree
    /// about which face belongs to which placement. Call at the delivery boundary (a definition-revision move).</summary>
    /// <param name="definition">The delivered definition.</param>
    /// <param name="derivedFaces">This delivery's derived-face screen rows — always exactly
    /// <c>authoring.derivedFaceScreens</c> entries (<see cref="WorldCreationFacets.Derive"/> pads the reserved range
    /// with placeholders), composed alongside <paramref name="definition"/>'s own declared screens on every
    /// subsequent rebuild until the next delivery.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="derivedFaces"/> is
    /// <see langword="null"/>.</exception>
    public void ObserveDelivery(WorldDefinition definition, IReadOnlyList<WorldScreen> derivedFaces) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: derivedFaces);

        m_derivedFaceRows = derivedFaces;
    }
    /// <summary>Latches <paramref name="deltaSeconds"/> for the catalog-avatar root followers, stepped once by the
    /// next <see cref="PackDynamicTransforms"/>. Call once per produced frame, alongside the stamp pool's own
    /// <c>Tick</c>.</summary>
    /// <param name="deltaSeconds">Seconds advanced since the previous produced frame.</param>
    public void Tick(float deltaSeconds) {
        m_pendingDeltaSeconds += deltaSeconds;
    }
    /// <inheritdoc/>
    /// <remarks>Every active avatar's leaves ride its interpolated snapshot pose plus a distance-driven gait phase (an
    /// idle avatar holds its pose; a teleport is clamped so it cannot spin the limbs through dozens of cycles), and the
    /// animated/body-rooted stamps follow in the replay pool's range. Local seats fire one footstep cue per gait
    /// half-cycle. Slots this call does not write hold the host's park position, which is the same point below the
    /// floor a hidden avatar and an unused pool slot use.</remarks>
    public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context) {
        // The catalog addresses its own ranges from 0; the slice makes the host-assigned base the origin.
        var avatars = slots.Slice(
            start: context.SlotBase,
            length: WorldAvatarCatalog.DynamicTransformCapacity
        );
        var deltaSeconds = m_pendingDeltaSeconds;

        m_pendingDeltaSeconds = 0f;
        // Per-instance soft-shadow participation (the crowd lever): a local seat always casts; a stand-in casts only
        // within m_settings.ShadowCrowdRadius of some joined seat's PERCEIVED body (the perception anchor's
        // resolution, which follows a possession swap), bounding the dominant GPU term to the crowd around where the
        // real players look from. The always-cast half of the gate is keyed on the RAW body index band
        // (`index < LocalSeatCount`), not the anchor: possession policy is decided — a possessed body is NOT
        // reclassified in v1, it keeps casting (or not) on its own index band exactly like an unpossessed body would,
        // while the source seat's own (now-idle) body keeps casting because it is still a local seat's body.
        // CastsSoftShadow false suppresses the entry from the soft-shadow march only (it still renders and
        // self-lights). Radius 0 => only the seats cast; a large radius => everyone casts.
        Span<Vector3> joinedSeats = stackalloc Vector3[WorldPopulationLimits.LocalSeatCount];
        var joinedSeatCount = 0;

        for (var seat = 0; (seat < WorldPopulationLimits.LocalSeatCount); seat++) {
            var body = m_anchor.PerceivedBody(slot: seat);

            if (TryPresentedPose(
                address: out _,
                index: body,
                orientation: out _,
                position: out var joinedPosition
            )) {
                joinedSeats[joinedSeatCount++] = joinedPosition;
            }
        }

        var crowdRadiusSquared = (m_settings.ShadowCrowdRadius * m_settings.ShadowCrowdRadius);

        for (var index = 0; (index < WorldAvatarCatalog.Capacity); index++) {
            if (!TryPresentedPose(
                address: out var address,
                index: index,
                orientation: out var orientation,
                position: out var position
            )) {
                m_avatarPoseSeeded[index] = false;

                continue;
            }

            var castsSoftShadow = ((index < WorldPopulationLimits.LocalSeatCount) || WithinCrowd(
                position: position,
                seats: joinedSeats[..joinedSeatCount],
                radiusSquared: crowdRadiusSquared
            ));
            // The combined (epoch, address) watch — the pool's own WorldStampPool.RootEpoch/RootAddress shape:
            // EITHER term moving is the SAME discontinuity class (a teleport/over-threshold correction bumps the
            // epoch; a body index reused by a different inhabitant changes the address), so both gate the SAME
            // reseed — gait phase resets to 0 and the footstep cue is skipped for the frame a jump lands on, exactly
            // as an address change already does, rather than only clamping the walked distance.
            var poseEpoch = m_client.PoseEpoch(index: index);

            if (
                m_avatarPoseSeeded[index] &&
                (m_avatarMotionAddresses[index] == address) &&
                (m_avatarDynamicsPoseEpoch[index] == poseEpoch)
            ) {
                // Phase advances by DISTANCE, not wall time: idle avatars hold their pose; walking speed controls cadence.
                // Clamp a teleport/server snap so it cannot spin the limbs through dozens of cycles in one frame.
                var travelled = MathF.Min(
                    x: Vector3.Distance(
                        value1: position,
                        value2: m_avatarPreviousPositions[index]
                    ),
                    y: WorldMirroredAvatarBand.MaxGaitTravelPerFrame
                );
                var previousPhase = m_avatarGaitPhases[index];

                m_avatarGaitPhases[index] += (travelled * WorldMirroredAvatarBand.GaitCadence);

                // The player.footstep cue: LOCAL seat avatars fire one at-site cue per gait-phase half-cycle
                // wrap — one footfall per π of phase (a stride swings one leg through), so cadence follows walking
                // speed and an idle avatar is silent. Presentation-side by design: the phase is the same
                // distance-driven presentation state that swings the limbs. Keyed on the BODY's index band, not the
                // perception anchor: the cue is emitted by the walking body at its own site, a body-owned fact rather
                // than a seat-relative derivation. Possession policy is decided — a possessed body is NOT
                // reclassified in v1, so a possessed higher-index body stays silent here exactly as it would
                // unpossessed, and the source seat's own idle body (still index-banded, still not walking) stays
                // silent too; only the raw index band decides who can ever fire this cue.
                if (
                    (index < WorldPopulationLimits.LocalSeatCount) &&
                    (((int)(m_avatarGaitPhases[index] / MathF.PI)) > ((int)(previousPhase / MathF.PI)))
                ) {
                    m_audio.SubmitCue(
                        eventToken: WorldAudioCue.PlayerFootstep,
                        site: position
                    );
                }
            } else {
                m_avatarPoseSeeded[index] = true;
                m_avatarGaitPhases[index] = 0f;
                m_avatarMotionAddresses[index] = address;
                m_avatarDynamicsPoseEpoch[index] = poseEpoch;
                m_avatarPositionFollower[index].Reseed();
                m_avatarOrientationFollower[index].Reseed();
            }

            m_avatarPreviousPositions[index] = position;

            var followedPosition = position;
            var followedOrientation = orientation;

            if (m_avatarFollows[index]) {
                (followedPosition, followedOrientation) = SecondOrderPoseFollower.StepPose(
                    position: ref m_avatarPositionFollower[index],
                    orientation: ref m_avatarOrientationFollower[index],
                    response: in m_avatarResponse[index],
                    deltaSeconds: deltaSeconds,
                    targetPosition: position,
                    targetOrientation: orientation
                );
            } else {
                m_avatarPositionFollower[index].Reseed();
                m_avatarOrientationFollower[index].Reseed();
            }

            // Resolve the entity's LOOK: a catalog rig pin, a uniform render scale, and a gait-amplitude phase scale.
            // GaitAmplitude scales m_avatarGaitPhases (1 = the pre-look swing; 0 stills the limbs at their rest pose).
            // A creation-STAMP body (inhabitant / crowd creation-look) renders its creation through the stamp pool, so
            // its catalog avatar packs HIDDEN below the floor (culled) — the "never black, never vanished" degradation
            // is gone: the body shows its actual creation geometry instead.
            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: (m_rendersAsStamp[index]
                ? context.ParkPosition
                : followedPosition),
                rootOrientation: followedOrientation,
                gaitPhase: (m_avatarGaitPhases[index] * m_emittedAvatarGaitAmplitudes[index]),
                castsSoftShadow: castsSoftShadow,
                transforms: avatars,
                rig: m_emittedAvatarRigs[index],
                scale: m_emittedAvatarScales[index]
            );
        }

        // The stamp pool packs after the avatar catalog (its reserved slots sit past the frozen avatar capacity):
        // animated placements ride their static pose, attached and body-rooted stamps ride the client's live body pose; hidden slots
        // park below the floor.
        m_animator.PackTransforms(
            transforms: slots,
            client: m_client,
            slotBase: (context.SlotBase + WorldAvatarCatalog.DynamicTransformCapacity),
            parkPosition: context.ParkPosition
        );
    }
    /// <summary>Writes the program-rebuild watch counters this scene composes over: the client's three
    /// (<see cref="WorldClient.WriteRevision"/> — roster, server snapshot, definition delivery), then the continuum
    /// watch.
    /// <para>
    /// Four components, not their sum, and the client's three stay split too. One of them — the client's server
    /// revision — is assigned from a snapshot and can move down, so any addition anywhere on this path can cancel: a
    /// server revision falling by one while the continuum counter rises by one would leave a sum unmoved and hold a
    /// stale program. Flattening every counter through to the composition host's componentwise compare is what makes
    /// that impossible rather than merely unlikely (see <see cref="ISdfSceneEmitter.WriteRevision"/>).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        m_client.WriteRevision(destination: destination[..WorldClient.RevisionComponentCount]);

        destination[WorldClient.RevisionComponentCount] = m_continuum.Revision;
    }
}
