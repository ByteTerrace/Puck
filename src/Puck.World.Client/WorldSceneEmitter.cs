using System.Numerics;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The overworld's geometry, as one <see cref="ISdfSceneEmitter"/> composed by <see cref="SdfCompositionFrameSource"/>:
/// the diegetic screen slabs, the static placement stamps and the
/// creation-stamp pool (animated and body-attached rows), and the population's active avatars as leaf-level dynamic
/// instances. It owns what geometry
/// exists; <see cref="WorldFrameSource"/> owns how a frame presents it (views, cameras, gizmos, audio).
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
/// <see cref="Puck.World.Client.WorldFrameSource"/>'s joint measurer, which composes it alongside whatever
/// <c>puck.sdf.v1</c> document is currently loaded before measuring the counts the render envelope compares, so a
/// mutation is judged against the same program that would actually be built.
/// </para>
/// <para>
/// The rebuild cadence is a set of numbers, never a time the host queries: <see cref="WriteRevision"/> reports the
/// client/selection/drag/workbench watches and a shimmer counter this type bumps itself from the content clock
/// (<see cref="AdvanceContentClock"/>), so the composition host's existing "rebuild when a revision component moved"
/// predicate carries the shimmer pulse's per-frame cadence without knowing what a shimmer is. They are reported side by
/// side rather than combined — see <see cref="WriteRevision"/> for why any sum on this path could silently cancel.
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
    private const float SelectionTintBlend = DesignTokens.Feedback.SelectionTintBlend;
    private const float ShimmerBlendMax = DesignTokens.Feedback.ChangeShimmerBlendMax;

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
    // binder registers it at boot from the same field, WorldFrameSource.m_derivedFaceScreens).
    private readonly int m_derivedFaceScreens;
    private readonly WorldEditorDrag m_drag;
    private readonly float m_noseFactor;
    // The placement capacity reservation, in worst-case stamp instances: boot static instances + the authoring
    // headroom. Frozen at construction; the apply-time measure charges max(candidate instances, this).
    private readonly int m_placementReservation;
    private readonly WorldRenderSettings m_settings;
    private readonly WorldEditorTargeting m_targeting;
    private readonly WorldTextCatalog m_text;
    private readonly WorldWorkbench m_workbench;

    // The content clock the shimmer's pulse table is keyed by — the SAME number the tint reads and the revision is
    // derived from, so a rebuild's cadence and its content can never disagree.
    private double m_contentSeconds;
    // The CURRENT derived-face screen ROWS (creation faces derived from placements x creations) — threaded in from
    // WorldFrameSource.ReconcileDelivery's ONE WorldCreationFacets.Derive call each delivery via ObserveDelivery,
    // NEVER re-derived here: the geometry this emitter composes and the binder's bound sources must read the SAME
    // set or a face's slab and its bound texture could disagree about which placement it belongs to. Seeded at
    // construction to the reserved-band PLACEHOLDER rows (WorldCreationFacets.ReservedFaceSlots — the identical
    // shape WorldBootComposition's own boot registration uses), so the ONE construction-time capacity probe (which
    // runs before any delivery can land — see SdfCompositionFrameSource's ctor) reserves program-word/instance
    // capacity for the derived band by construction, exactly like every other worst-case branch this type owns.
    // Always exactly m_derivedFaceScreens entries (Derive pads the reserved range with placeholders), so the word
    // cost this band contributes is IDENTICAL between the probe and every live build.
    private IReadOnlyList<WorldScreen> m_derivedFaceRows;
    private int m_shimmerRevision;
    // Latched from the ONE construction-time probe: the host assigns SlotBase once and it is stable for this emitter's
    // lifetime, so the candidate measure composes against the same base a live program does.
    private int m_slotBase;

    // The editor's presentation feedback tints/blends — DesignTokens.Feedback (the one C# token source; these are
    // palette values fed to the SDF program CPU-side, the sibling of the overlay's GPU token slab).
    private static readonly Vector3 ShimmerTint = DesignTokens.Feedback.ChangeShimmerTint.Rgb;
    private static readonly Vector3 SelectionTint = DesignTokens.Feedback.SelectionTint.Rgb;

    // Where a creation-stamp body's catalog avatar parks (below the floor, culled) — the body renders its creation.
    // The same point the composition host parks every unused slot at (WorldFrameSource sets it as ParkPosition).
    internal static readonly Vector3 HiddenAvatar = new(
        x: 0f,
        y: -1000f,
        z: 0f
    );

    private readonly WorldChangeShimmer m_shimmer = new();
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

    /// <summary>Initializes a new instance of the <see cref="WorldSceneEmitter"/> class over the boot definition,
    /// freezing the authoring-headroom policy and the placement reservation the probe branch reserves against.</summary>
    /// <param name="client">The snapshot-fed entity view every pose, color, look, and active flag is read from.</param>
    /// <param name="settings">The live render settings (the crowd soft-shadow radius is read while packing).</param>
    /// <param name="targeting">The editor selection state (the render highlight + rebuild watch).</param>
    /// <param name="drag">The editor drag channel (the pending-row overlay + rebuild watch).</param>
    /// <param name="workbench">The sculpt workbench (the preview creation/placement overlay + rebuild watch).</param>
    /// <param name="animator">The creation-stamp pool (animated placements, attached placements, body-rooted stamps).</param>
    /// <param name="audio">The narrow cue-submission seam the distance-driven footstep cue fires into while packing.</param>
    /// <param name="anchor">The per-seat perception anchor the crowd soft-shadow centers resolve their body indices
    /// through.</param>
    /// <param name="continuum">The route-to-presentation-frame resolver used to keep locally followed travelers in
    /// their original catalog slot across authority handoffs.</param>
    /// <param name="text">The world-relative font catalog used by creation text runs.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSceneEmitter(WorldClient client, WorldRenderSettings settings, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldWorkbench workbench, WorldStampPool animator, IWorldAudioCueSink audio, WorldPerceptionAnchor anchor, WorldContinuum continuum, WorldTextCatalog text) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: targeting);
        ArgumentNullException.ThrowIfNull(argument: drag);
        ArgumentNullException.ThrowIfNull(argument: workbench);
        ArgumentNullException.ThrowIfNull(argument: animator);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: anchor);
        ArgumentNullException.ThrowIfNull(argument: continuum);
        ArgumentNullException.ThrowIfNull(argument: text);

        m_client = client;
        m_continuum = continuum;
        m_anchor = anchor;
        m_settings = settings;
        m_targeting = targeting;
        m_drag = drag;
        m_workbench = workbench;
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
        // WorldFrameSource can ever reconcile a delivery), so the probe reserves this band's word/instance cost even
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
            bodyStamps: m_bodyStamps
        );
        m_placementReservation = (WorldPlacementStamper.StaticStampInstances(
            creations: definition.Creations,
            placements: definition.Placements
        ) + m_authoringHeadroomPlacements);
        // The boot placements + speakers are the shimmer baseline — the first delivery pulses only what it changed.
        m_shimmer.Observe(
            placements: definition.Placements,
            speakers: definition.Speakers,
            now: 0d
        );
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
    public int RevisionComponentCount => (WorldClient.RevisionComponentCount + 5);

    // The screens, static placement stamps + the creation-stamp pool, then the view's active avatars as leaf-level dynamic
    // instances riding frozen catalog slots. Active-only, never declared-but-parked: the per-tile instance mask width
    // derives from the program's total declared instance count (SdfProgram.InstanceMaskWordCount), so parked avatar
    // declarations widen every shadow-gather pixel's mask walk. Instead the program is rebuilt on population change
    // (the revision watch), and the 128-avatar worst case is held by the probed capacity floors. Every avatar keeps its
    // own body + accent material (cheap constant words), so a recolor is data, not a resize. `placementProbe` replaces
    // the static stamps with the reserved worst case (the construction probe only); the animated pool and the avatars
    // follow `probeWorstCase` (worst case for both the construction probe AND the apply-time measure).
    private void Compose(SdfProgramBuilder builder, IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldScreen> derivedFaces, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations, bool probeWorstCase, bool placementProbe, WorldEditorTargeting? highlight, float maxPlacementScale, double shimmerNow, int slotBase) {
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

        var shimmer = ((highlight is not null)
            ? m_shimmer
            : null
        );

        // The diegetic screens: each a sampled ScreenSlab whose lit face samples its bound source (or the engine's
        // procedural no-signal fallback when unbound). STATIC data — emitted every build (probe and live), so the
        // capacity floors cover them by construction (no probe-only branch). The sampled overload takes the explicit
        // world frame (Origin/Right/Up) baked into the surface table for UV mapping; the geometry rounded box is
        // placed by translating to its CENTER, which sits one HalfDepth behind the face along the face normal
        // (Right × Up). The material sentinel the overload assigns needs no palette entry.
        //
        // TWO SOURCES, ONE EMISSION PATH: `screens` (the document's declared rows, padded with authoring headroom)
        // and `derivedFaces` (the reserved-band rows a creation's own faces resolve to — see WorldCreationFacets;
        // WorldFrameSource threads the SAME derived set here that it hands the screen binder, so the geometry a
        // derived face's slab occupies and the source the binder samples for it can never disagree about which
        // placement it belongs to). Both index ranges are disjoint by construction (WithAuthoringHeadroom skips the
        // reserved band), so emitting them back-to-back cannot double-declare a screen index.
        foreach (var screen in screens) {
            WorldScreenStamper.Emit(
                builder: builder,
                screen: screen
            );
        }

        foreach (var screen in derivedFaces) {
            WorldScreenStamper.Emit(
                builder: builder,
                screen: screen
            );
        }

        // The placement stamps: the construction probe reserves (boot static instances + the authoring headroom)
        // worst-case stamps, and the APPLY-TIME MEASURE charges a candidate's static placements at that same
        // worst-case unit — max(candidate instances, the reservation) — so the placement term stays CONSTANT between
        // probe and measure while placements are inside their headroom. That constancy is load-bearing: a cheaper
        // as-authored measure would hand the reservation's word slack to SCENE/SCREEN floods (their ceilings would
        // silently widen by thousands of words), and a placement flood still rejects exactly one instance past the
        // headroom. Only the LIVE build emits the rows as authored — static stamps baked into instructions, animated
        // rows through the replay pool (worst-case under any probe). Selection amber and the change shimmer tint a
        // stamp's palette (albedo-only; the all-distinct probe bound covers the extra registrations).
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
                tintFor: ((highlight is null)
                ? null
                : id => {
                    if (highlight.IsPlacementSelected(id: id)) {
                        return (SelectionTint, SelectionTintBlend);
                    }

                    if (
                        (shimmer is { } pulses) &&
                        (pulses.PlacementIntensity(
                        id: id,
                        now: shimmerNow
                    ) is > 0f and var pulse)
                    ) {
                        return (ShimmerTint, (pulse * ShimmerBlendMax));
                    }

                    return null;
                })
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

                m_emittedAvatarRigs[index] = LookRig(
                    catalogRig: catalogRig,
                    look: look
                );
                m_emittedAvatarScales[index] = look.Scale;
                m_emittedAvatarGaitAmplitudes[index] = look.Motion.GaitAmplitude;
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
    // The catalog geometry-source rig for a look: an authored Catalog(Index) pin, or the occupant-owned carried rig
    // for an unpinned catalog OR a Creation look. A Creation look's body renders through the stamp pool (its catalog avatar
    // parks below the floor via HiddenAvatar), so this catalog rig is reached only as the pool-pressure fallback — a
    // body a full stamp pool starved renders as a catalog avatar rather than vanishing.
    private static int LookRig(WorldLook look, byte catalogRig) => ((look.Source is WorldLookSource.Catalog { Index: { } pinned })
        ? pinned
        : catalogRig
    );
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
                    Scale: (placement.Scale * look.Scale)
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
                    Scale: look.Scale
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

        var added = 0;

        for (var index = 0; ((index < SdfProgramBuilder.MaxScreenSurfaces) && (added < m_authoringHeadroomScreens)); index++) {
            if (!used.Add(item: index)) {
                continue;
            }

            padded.Add(item: new WorldScreen(
                Index: index,
                Origin: Vector3.Zero,
                Right: Vector3.UnitX,
                Up: Vector3.UnitY,
                HalfWidth: 1f,
                HalfHeight: 1f,
                HalfDepth: 0.1f,
                Round: 0.05f,
                Source: new WorldScreenSource.None(),
                Route: WorldScreenRoute.Passive
            ));
            added++;
        }

        if (added < m_authoringHeadroomScreens) {
            throw new InvalidOperationException(message: $"authoring.authoringHeadroomScreens asks for {m_authoringHeadroomScreens} reserved screen slot(s), but only {added} of the engine's {SdfProgramBuilder.MaxScreenSurfaces} surfaces are free: {authored} carry an authored screen and {m_derivedFaceScreens} are reserved for derived creation faces at indices {WorldCreationFacets.DerivedFaceBase}..{((WorldCreationFacets.DerivedFaceBase + m_derivedFaceScreens) - 1)}. Lower authoring.authoringHeadroomScreens by {(m_authoringHeadroomScreens - added)}, lower authoring.derivedFaceScreens, or author fewer screens.");
        }

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

    /// <summary>Advances this emitter's content clock and, while a change-shimmer pulse is live, bumps the shimmer
    /// component of <see cref="WriteRevision"/> so the composition host rebuilds and the pulse's decay animates. Call once per produced
    /// frame, before the host captures, with the world's presentation clock — never a wall-clock read: the number the
    /// host compares and the number the pulse tint is computed from are the same one.</summary>
    /// <param name="seconds">The world's content clock, in seconds.</param>
    public void AdvanceContentClock(double seconds) {
        m_contentSeconds = seconds;

        if (m_shimmer.HasLivePulse(now: seconds)) {
            m_shimmerRevision++;
        }
    }
    /// <summary>Composes a candidate definition's render-relevant sections into <paramref name="builder"/> — World
    /// admission policy (what may enter the document is the document owner's question, never a mode on the generic
    /// emitter contract), factored so the render-capacity oracle can measure it composed alongside whatever
    /// <c>puck.sdf.v1</c> document (see <see cref="WorldSdfDocumentEmitter"/>) is currently loaded — see
    /// <see cref="Puck.World.Client.WorldFrameSource"/>'s joint measurer. The packed tables a program carries (the
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
                highlight: null,
                maxPlacementScale: candidate.Authoring.MaxPlacementScale,
                shimmerNow: 0d,
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
                highlight: null,
                maxPlacementScale: boot.Authoring.MaxPlacementScale,
                shimmerNow: 0d,
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
            bodyStamps: m_bodyStamps
        );
        // The editor's pending rows compose over the delivered truth: this ONE rebuild path renders the drag preview at
        // drag cadence, and release retires the overlay against the identical committed document — no second render
        // path. The sculpt preview composes OVER the drag-composed rows, so the bench's synthetic creation + placement
        // render through the same stamp path a committed row uses (stamp-equals-preview).
        Compose(
            builder: builder,
            screens: m_drag.ComposeScreens(live: definition.Screens),
            derivedFaces: m_derivedFaceRows,
            placements: m_workbench.ComposePlacements(live: m_drag.ComposePlacements(live: definition.Placements)),
            creations: m_workbench.ComposeCreations(live: definition.Creations),
            probeWorstCase: false,
            placementProbe: false,
            highlight: m_targeting,
            maxPlacementScale: definition.Authoring.MaxPlacementScale,
            shimmerNow: m_contentSeconds,
            slotBase: context.SlotBase
        );
    }
    /// <summary>Re-baselines the change shimmer against a freshly delivered definition, pulsing every row the delivery
    /// changed, and re-points this emitter's derived-face screen rows at the same set <see cref="WorldFrameSource"/>
    /// just derived and handed the screen binder — never re-derived here (see <see cref="WorldCreationFacets.Derive"/>'s
    /// one call site), so the slab geometry this emitter composes and the binder's bound sources can never disagree
    /// about which face belongs to which placement. Call at the delivery boundary (a definition-revision move), with
    /// the content clock.</summary>
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

        m_shimmer.Observe(
            placements: definition.Placements,
            speakers: definition.Speakers,
            now: m_contentSeconds
        );
        m_derivedFaceRows = derivedFaces;
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

            if (
                m_avatarPoseSeeded[index] &&
                (m_avatarMotionAddresses[index] == address)
            ) {
                // Phase advances by DISTANCE, not wall time: idle avatars hold their pose; walking speed controls cadence.
                // Clamp a teleport/server snap so it cannot spin the limbs through dozens of cycles in one frame.
                var travelled = MathF.Min(
                    x: Vector3.Distance(
                        value1: position,
                        value2: m_avatarPreviousPositions[index]
                    ),
                    y: 0.25f
                );
                var previousPhase = m_avatarGaitPhases[index];

                m_avatarGaitPhases[index] += (travelled * 8.0f);

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
            }

            m_avatarPreviousPositions[index] = position;
            // Resolve the entity's LOOK: a catalog rig pin, a uniform render scale, and a gait-amplitude phase scale.
            // GaitAmplitude scales m_avatarGaitPhases (1 = the pre-look swing; 0 stills the limbs at their rest pose).
            // A creation-STAMP body (inhabitant / crowd creation-look) renders its creation through the stamp pool, so
            // its catalog avatar packs HIDDEN below the floor (culled) — the "never black, never vanished" degradation
            // is gone: the body shows its actual creation geometry instead.
            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: (m_rendersAsStamp[index]
                ? HiddenAvatar
                : position),
                rootOrientation: orientation,
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
            slotBase: (context.SlotBase + WorldAvatarCatalog.DynamicTransformCapacity)
        );
    }
    /// <summary>The pulse intensity for one speaker row — the editor gizmo chip's held tier, read by the frame source
    /// while it projects chips (a speaker has no world geometry, so its pulse rides its chip).</summary>
    /// <param name="name">The speaker name.</param>
    /// <returns>The intensity in [0, 1]; 0 when the row is quiet.</returns>
    public float SpeakerPulse(string name) => m_shimmer.SpeakerIntensity(
        name: name,
        now: m_contentSeconds
    );
    /// <summary>Writes the program-rebuild watch counters this scene composes over: the client's three
    /// (<see cref="WorldClient.WriteRevision"/> — roster, server snapshot, definition delivery), then the editor's
    /// selection (highlight), drag-overlay, and sculpt-workbench counters, and the shimmer counter this type bumps from
    /// the content clock.
    /// <para>
    /// Seven components, not their sum, and the client's three stay split too. One of them — the client's server
    /// revision — is assigned from a snapshot and can move down, so any addition anywhere on this path can cancel: a
    /// server revision falling by one while the targeting counter rises by one would leave a sum unmoved and hold a
    /// stale program. Flattening every counter through to the composition host's componentwise compare is what makes
    /// that impossible rather than merely unlikely (see <see cref="ISdfSceneEmitter.WriteRevision"/>).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        m_client.WriteRevision(destination: destination[..WorldClient.RevisionComponentCount]);

        destination[WorldClient.RevisionComponentCount] = m_targeting.Revision;
        destination[(WorldClient.RevisionComponentCount + 1)] = m_drag.Revision;
        destination[(WorldClient.RevisionComponentCount + 2)] = m_workbench.Revision;
        destination[(WorldClient.RevisionComponentCount + 3)] = m_shimmerRevision;
        destination[(WorldClient.RevisionComponentCount + 4)] = m_continuum.Revision;
    }
}
