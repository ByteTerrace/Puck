using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Cameras;
using Puck.Compositing;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The client's per-frame source: a grass-green ground plane with a loose cluster of smooth-blended stone boulders, and
/// the whole entity table drawn as one avatar system — up to <see cref="WorldPopulation.MaxPopulation"/> distinct
/// articulated rigs, slots 0..3 the local roster seats and slots 4.. the network stand-ins, every pose read from the
/// snapshot-fed <see cref="WorldClient"/> view. Only the four local seats get a chased over-the-shoulder camera into
/// their own viewport (fullscreen → side-by-side → big-top/two-bottom → 2×2 quad as players join); the rest of the
/// population renders into those views as ordinary avatars.
/// </summary>
/// <remarks>The geometry is rebuilt only when the declared set or palette moves (a seat joins/leaves/recolors, or the
/// simulated count changes). A construction probe emits the complete 128-rig catalog and freezes the word, instance,
/// and dynamic-transform envelopes; live programs contain active avatars only.</remarks>
internal sealed class WorldFrameSource : ISdfFrameSource {
    // BOOT-CONSUMED authoring policy (WorldAuthoringDefaults): captured ONCE at construction into the fields
    // below, from the boot definition's Authoring row — never re-read live. These feed the frozen render-envelope
    // probe (scene-row/screen-slot/placement-segment reservation), so a later SetAuthoringDefaults mutation is
    // journaled but cannot retroactively grow a running session's capacity floor; it narrates "next boot" honestly.
    private readonly int m_authoringHeadroomRows;
    private readonly int m_authoringHeadroomScreens;
    private readonly int m_authoringHeadroomPlacements;
    private readonly int m_maxRepeatPerSegment;
    // The editor's presentation feedback tints/blends — DesignTokens.Feedback (the one C# token source; these are
    // palette values fed to the SDF program CPU-side, the sibling of the overlay's GPU token slab).
    private static readonly Vector3 s_shimmerTint = DesignTokens.Feedback.ChangeShimmerTint.Rgb;
    private const float ShimmerBlendMax = DesignTokens.Feedback.ChangeShimmerBlendMax;
    private static readonly Vector3 s_selectionTint = DesignTokens.Feedback.SelectionTint.Rgb;
    private const float SelectionTintBlend = DesignTokens.Feedback.SelectionTintBlend;

    private readonly FrameRateMonitor m_frameRate;
    private readonly PlayerRoster m_roster;
    private readonly WorldClient m_client;
    private readonly WorldSimulation m_simulation;
    // The editor's client-side render seams: the drag channel's pending-row overlay (composed over the delivered
    // definition each rebuild), the sculpt workbench's preview creation/placement overlay, and the targeting
    // state's selection highlight. All fold into the rebuild watch.
    private readonly WorldEditorTargeting m_targeting;
    private readonly WorldChangeShimmer m_shimmer = new();
    private readonly WorldEditorDrag m_drag;
    private readonly WorldWorkbench m_workbench;
    // The animated-placement replay pool: reconciled at the delivery boundary, ticked on the render clock,
    // packed after the avatar transforms every frame.
    private readonly WorldPlacementAnimator m_animator;
    // The audio director: its emitter derivation reconciles at the delivery boundary (AFTER the screen binder —
    // the chiasmus ordering, speakers consume screen slots) and its snapshot publishes at the end of every capture.
    private readonly WorldAudioDirector m_audio;
    // The editor-gizmo feed: geometry-less rows (speakers) projected into each EDITING seat's viewport as
    // overlay chips — published every produced frame (leaving editor mode clears the chips), consumed by the
    // unified overlay's gizmo writer the same frame (CaptureFrame runs before the overlay's FeedTick/writers).
    private readonly EditorGizmoStore m_gizmos;
    private readonly OverlayGizmoSeat[] m_gizmoSeats = new OverlayGizmoSeat[PlayerRoster.MaxSlots];
    private readonly OverlayGizmoChip[][] m_gizmoChips = new OverlayGizmoChip[PlayerRoster.MaxSlots][];
    // Per-frame scratch for the listener policy: each joined seat's resolved view-camera pose, slot-indexed.
    private readonly WorldSeatCameraPose[] m_seatCameraPoses = new WorldSeatCameraPose[PlayerRoster.MaxSlots];
    // One rig slot per local seat, chase (OrientedFollowRig) by default: its defaults (eye up-and-back along the
    // anchor's +Z, target lifted a touch) frame that seat's avatar from behind, tracking its heading. The editor
    // session swaps its own rig in per frame while a seat edits. Only local seats get cameras/views.
    private readonly ISdfCameraRig[] m_cameraRigs;
    // The window composer (layout selection + eased transitions; a shared singleton the world.view.state read also
    // observes), the group-anchor resolver (smoothed centroids for establishing shots), and the shared live
    // composition-override store (view.layout/view.camera). All presentation-only.
    private readonly WorldViewComposer m_composer;
    private readonly WorldGroupAnchors m_groupAnchors = new();
    private readonly WorldCompositionState m_composition;
    // The per-seat editor mode: camera rig swap + the sole-editor layout policy, both read during produce.
    private readonly WorldEditorSession m_editor;
    // Per-frame scratch reused to keep CaptureFrame allocation-free: one transform per leaf in the frozen all-avatar
    // catalog, plus movement-driven gait state per avatar and the joined seats' views. Live programs address only the
    // active avatars' stable slot ranges; stale inactive slots are unreachable.
    private readonly DynamicTransform[] m_transforms = new DynamicTransform[(WorldAvatarCatalog.DynamicTransformCapacity + WorldPlacementAnimator.DynamicSlotCount)];
    private readonly float[] m_avatarGaitPhases = new float[WorldPopulation.MaxPopulation];
    private readonly Vector3[] m_avatarPreviousPositions = new Vector3[WorldPopulation.MaxPopulation];
    private readonly bool[] m_avatarPoseSeeded = new bool[WorldPopulation.MaxPopulation];
    // The seat.join cue's edge detector: a slot's roster presence last frame.
    private readonly bool[] m_seatWasJoined = new bool[PlayerRoster.MaxSlots];
    private readonly List<SdfViewSnapshot> m_views = new(capacity: PlayerRoster.MaxSlots);
    private readonly WorldRenderSettings m_settings;
    // The binder that owns the diegetic screens' CPU-fed GPU sources. The scene (ground + boulders) and the screens are
    // read LIVE from the client's delivered definition each rebuild, so a mutation's new geometry lands on the next
    // program rebuild; the binder's runtime source machinery is reconciled when the definition revision moves.
    private readonly WorldScreenBinder m_binder;
    private SdfProgram m_program;
    private int m_builtRevision;
    private int m_builtDefinitionRevision;
    // The placement capacity reservation, in worst-case stamp SEGMENTS: boot static segments + the authoring
    // headroom. Frozen at construction; the apply-time measure charges max(candidate segments, this).
    private readonly int m_placementReservation;
    private float m_elapsedSeconds;
    private bool m_uploaded;

    /// <summary>Initializes a new instance of the <see cref="WorldFrameSource"/> class, building the static scene over
    /// the snapshot-fed client view (the primer snapshot must already be delivered, so the initial program declares the
    /// boot seats and census active before the first frame).</summary>
    /// <param name="frameRate">The frame-rate witness sampled once per captured frame (the <c>world.fps</c> verb reads it).</param>
    /// <param name="client">The snapshot-fed entity view every pose, color, and active flag is read from.</param>
    /// <param name="simulation">The host-ticked simulation whose completed tick drives presentation sources.</param>
    /// <param name="settings">The live render settings read every captured frame (console-mutated in real time).</param>
    /// <param name="binder">The screen binder owning the declared screens' CPU-fed GPU sources, published each frame.</param>
    /// <param name="envelope">The render-capacity oracle configured here with the probed floors and a candidate
    /// measurer, so the server can reject an over-envelope scene/screen mutation at apply time.</param>
    /// <param name="editor">The per-seat editor mode (camera rig swap + the sole-editor layout policy).</param>
    /// <param name="targeting">The editor selection state (the render highlight + rebuild watch).</param>
    /// <param name="drag">The editor drag channel (the pending-row overlay + rebuild watch).</param>
    /// <param name="animator">The animated-placement replay pool.</param>
    /// <param name="workbench">The sculpt workbench (the preview creation/placement overlay + rebuild watch).</param>
    /// <param name="audio">The audio director — the emitter derivation reconciled at the delivery boundary and the
    /// per-frame snapshot publisher.</param>
    /// <param name="gizmos">The editor-gizmo store the per-frame speaker-chip projections publish into.</param>
    /// <param name="composition">The shared live composition-override store (view.layout/view.camera) the composer reads.</param>
    /// <param name="composer">The shared window composer (layout selection + eased transitions) the world.view.state read observes.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldFrameSource(FrameRateMonitor frameRate, WorldClient client, WorldSimulation simulation, WorldRenderSettings settings, WorldScreenBinder binder, WorldRenderEnvelope envelope, WorldEditorSession editor, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldPlacementAnimator animator, WorldWorkbench workbench, WorldAudioDirector audio, EditorGizmoStore gizmos, WorldCompositionState composition, WorldViewComposer composer) {
        ArgumentNullException.ThrowIfNull(argument: frameRate);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: simulation);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: envelope);
        ArgumentNullException.ThrowIfNull(argument: editor);
        ArgumentNullException.ThrowIfNull(argument: targeting);
        ArgumentNullException.ThrowIfNull(argument: drag);
        ArgumentNullException.ThrowIfNull(argument: animator);
        ArgumentNullException.ThrowIfNull(argument: workbench);
        ArgumentNullException.ThrowIfNull(argument: audio);
        ArgumentNullException.ThrowIfNull(argument: gizmos);
        ArgumentNullException.ThrowIfNull(argument: composition);
        ArgumentNullException.ThrowIfNull(argument: composer);

        m_composition = composition;
        m_composer = composer;
        m_gizmos = gizmos;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            m_gizmoChips[slot] = [];
        }

        m_audio = audio;
        // The machine-source resolver: the director diffs the binder's LIVE machines by
        // reference each produced frame, so a boot/eject/live-swap rebinds the mixer source and a machine booting
        // late into a referenced slot self-heals. Wired here — the produce path's composition point — and only ever
        // invoked from the director's pump-thread Publish.
        audio.MachineSourceResolver = binder.AudioMachine;
        m_frameRate = frameRate;
        m_client = client;
        m_roster = client.Roster;
        m_simulation = simulation;
        m_settings = settings;
        m_binder = binder;
        m_editor = editor;
        m_targeting = targeting;
        m_drag = drag;
        m_animator = animator;
        m_workbench = workbench;
        m_cameraRigs = new ISdfCameraRig[PlayerRoster.MaxSlots];
        RebuildSeatRigs(seatRig: (m_client.Definition.Views ?? WorldViewDefaults.Default).SeatRig);

        // Resolve the primer snapshot's render poses once so the initial program and camera anchors are live before
        // the first frame. Alpha 0 is immaterial — a freshly spawned entity has previous == current pose.
        m_client.UpdateRenderPoses(alpha: 0f);

        var definition = m_client.Definition;

        // Capture the BOOT-CONSUMED authoring policy once — every probe/measure/live build below reads these
        // instance fields, never definition.Authoring again, so the frozen capacity floor and the placement-segment
        // math stay mutually consistent for the life of this frame source (see the fields' remarks).
        m_authoringHeadroomRows = definition.Authoring.AuthoringHeadroomRows;
        m_authoringHeadroomScreens = definition.Authoring.AuthoringHeadroomScreens;
        m_authoringHeadroomPlacements = definition.Authoring.AuthoringHeadroomPlacements;
        m_maxRepeatPerSegment = definition.Authoring.MaxRepeatPerSegment;

        // A booted world may already stamp animated placements — register them before the first build so the initial
        // program emits their live pool slots.
        m_animator.Reconcile(placements: definition.Placements, creations: definition.Creations);
        // The boot emitter derivation (a booted world may already author speakers/facets/sounds).
        m_audio.ReconcileSpeakers(definition: definition);
        m_placementReservation = (WorldPlacementStamper.StaticStampSegments(creations: definition.Creations, placements: definition.Placements, maxRepeatPerSegment: m_maxRepeatPerSegment) + m_authoringHeadroomPlacements);

        // The envelope probe (never rendered): a worst-case all-128-avatars build over the boot scene/screens PLUS the
        // documented authoring headroom (scene rows, screens, AND placement stamps), so a live editor can add rows up
        // to the reserved ceilings. Its word/instance counts become the spec's capacity floors; every active-only
        // live rebuild fits by construction.
        var probe = Build(
            scene: WithAuthoringHeadroom(scene: definition.Scene),
            screens: WithAuthoringHeadroom(screens: definition.Screens),
            placements: definition.Placements,
            creations: definition.Creations,
            probeWorstCase: true,
            placementProbe: true,
            highlight: null,
            maxPlacementScale: definition.Authoring.MaxPlacementScale
        );

        ProgramWordCapacity = probe.Words.Length;
        InstanceCapacity = probe.Instances.Count;

        // Publish the probed envelope + a candidate measurer so a scene/screen/placement mutation is capacity-checked
        // at apply time against the SAME worst-case build (avatars and the animated pool are always at worst case;
        // scene/screens/static placements measure AS AUTHORED, so authoring consumes the reserved room before the
        // loud rejection). The candidate's OWN Authoring.MaxPlacementScale feeds the animated-pool bound radius (a
        // live-consumed value; the segment/headroom math stays on the frozen m_maxRepeatPerSegment/m_authoringHeadroom* fields).
        envelope.Configure(
            programWordCapacity: ProgramWordCapacity,
            instanceCapacity: InstanceCapacity,
            measure: candidate => {
                var measured = Build(
                    scene: candidate.Scene,
                    screens: candidate.Screens,
                    placements: candidate.Placements,
                    creations: candidate.Creations,
                    probeWorstCase: true,
                    placementProbe: false,
                    highlight: null,
                    maxPlacementScale: candidate.Authoring.MaxPlacementScale
                );

                return (Words: measured.Words.Length, Instances: measured.Instances.Count);
            }
        );

        m_builtRevision = RebuildRevision();
        m_builtDefinitionRevision = m_client.DefinitionRevision;
        m_program = Build(
            scene: definition.Scene,
            screens: definition.Screens,
            placements: definition.Placements,
            creations: definition.Creations,
            probeWorstCase: false,
            placementProbe: false,
            highlight: m_targeting,
            maxPlacementScale: definition.Authoring.MaxPlacementScale
        );
        // The boot scene + placements + speakers are the shimmer baseline — the first delivery pulses only what it changed.
        m_shimmer.Observe(scene: definition.Scene, placements: definition.Placements, speakers: definition.Speakers, now: 0d);
    }

    /// <summary>The worst-case (all avatars active) program word count — the spec's <c>ProgramWordCapacity</c> floor.</summary>
    public int ProgramWordCapacity { get; }

    /// <summary>The worst-case (all avatars active) instance count — the spec's <c>InstanceCapacity</c> floor.</summary>
    public int InstanceCapacity { get; }

    /// <summary>The frozen transform-slot count: every leaf in the all-128 avatar catalog plus the reserved
    /// animated-placement replay pool.</summary>
    public int DynamicTransformCapacity => (WorldAvatarCatalog.DynamicTransformCapacity + WorldPlacementAnimator.DynamicSlotCount);

    /// <inheritdoc/>
    public void NotifyDeviceLost() => m_binder.NotifyDeviceLost();

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        // deltaSeconds is the launcher's clamped presentation interval, distinct from its whole-step simulation delta.
        // It may drive visual-only animation and the FPS witness, but never feeds authoritative world state.
        m_elapsedSeconds += deltaSeconds;
        m_frameRate.Sample(deltaSeconds: deltaSeconds);

        // Simulation has already advanced on the launcher's exact fixed ticks; the client view holds the two latest
        // snapshot poses. Each active entry's render pose is Lerp(previous tick → current, alpha) plus any eased
        // server-correction offset, so above the fixed-step rate the crowd glides instead of stepping; a frame that banked zero
        // sub-steps holds a stable lerp (previous == current), no snap-back. Presentation only: every player.where
        // still reads the authoritative sim pose server-side.
        m_client.UpdateRenderPoses(alpha: interpolationAlpha);

        // Retire released drag overlays first (they freeze until their OWN act's apply/rejection resolves, or the
        // missing-response deadline — see WorldEditorDrag), so the revision read below already reflects any retirement.
        m_drag.Reconcile();

        // Advance the animated-placement replay cursors on the render clock (hold-style — transforms move; the
        // program itself never rebuilds for a timeline step).
        m_animator.Tick(deltaSeconds: deltaSeconds);

        // Advance the sculpt workbench: playback ticks, drag-coalescer frame boundary, and its model revisions fold
        // into the monotonic rebuild watch read below.
        m_workbench.Tick(deltaSeconds: deltaSeconds);

        // A declared-set or palette change since the last frame (a seat join/leave/recolor or a simulated-count
        // change), a selection change (the highlight tint), or a drag-overlay move rebuilds the program and marks
        // ProgramChanged so the engine re-uploads it, always within the frozen capacities. The first frame also
        // uploads (the initial program is not yet on the GPU).
        var revision = RebuildRevision();
        // A live shimmer pulse keeps the rebuild running so its decay animates — a bounded window at the proven
        // drag-cadence rebuild cost, entered only when a delivery changed rows.
        var shimmering = m_shimmer.HasLivePulse(now: m_elapsedSeconds);
        var programChanged = (!m_uploaded || shimmering || (revision != m_builtRevision));

        if (shimmering || (revision != m_builtRevision)) {
            // A definition delivery (scene/screen mutation, swap, or undo) landed since the last build: reconcile the
            // binder's runtime source machinery to the new definition BEFORE rebuilding the program off the live
            // geometry — cameras FIRST, so a same-delivery View source change resolves the new camera rows.
            var definitionRevision = m_client.DefinitionRevision;

            if (definitionRevision != m_builtDefinitionRevision) {
                // A views-section edit recompiles each seat's chase rig from the delivered seat rig (world.view.rig live).
                RebuildSeatRigs(seatRig: (m_client.Definition.Views ?? WorldViewDefaults.Default).SeatRig);
                m_binder.ReconcileCameras(cameras: m_client.Definition.Cameras);
                m_binder.ReconcileScreens(screens: m_client.Definition.Screens);
                // The animated-placement pool reconciles at the same delivery boundary: cheap pose
                // edits write in place, creation-content changes release + recreate, removals release (symmetric).
                m_animator.Reconcile(placements: m_client.Definition.Placements, creations: m_client.Definition.Creations);
                // ReconcileSpeakers runs AFTER ReconcileScreens (the chiasmus: speakers consume screen slots) and
                // after the animator (placement-anchored emitters read its registrations).
                m_audio.ReconcileSpeakers(definition: m_client.Definition);
                m_shimmer.Observe(scene: m_client.Definition.Scene, placements: m_client.Definition.Placements, speakers: m_client.Definition.Speakers, now: m_elapsedSeconds);
                m_builtDefinitionRevision = definitionRevision;
            }

            // The editor's pending rows compose over the delivered truth: the EXISTING rebuild path renders the drag
            // preview at drag cadence, and release retires the overlay against the identical committed document — no
            // second render path. The full-rebuild path stays: the evidence does not demand a cheaper preview transform.
            m_program = Build(
                scene: m_drag.ComposeScene(live: m_client.Definition.Scene),
                screens: m_drag.ComposeScreens(live: m_client.Definition.Screens),
                // The sculpt preview composes OVER the drag-composed rows: the bench's synthetic creation +
                // placement render through the same stamp path a committed row uses (stamp-equals-preview).
                placements: m_workbench.ComposePlacements(live: m_drag.ComposePlacements(live: m_client.Definition.Placements)),
                creations: m_workbench.ComposeCreations(live: m_client.Definition.Creations),
                probeWorstCase: false,
                placementProbe: false,
                highlight: m_targeting,
                maxPlacementScale: m_client.Definition.Authoring.MaxPlacementScale,
                shimmerNow: m_elapsedSeconds
            );
            m_builtRevision = revision;
        }

        m_uploaded = true;

        // Emit one view per joined local seat (a 1..MaxSlots count up to the ViewportCapacity floor, so players can join
        // later without freezing the count at the first frame's). Views ride slot order; the layout ladder places each
        // by its position among the joined players. The MaxPopulation dynamic transforms are separate and always
        // supplied in full; the active-only program addresses only its avatars' stable leaf ranges.
        var joinedCount = m_roster.Count;

        // Self-heal a seat that left the roster while editing (its mode layer and camera drop), then resolve this
        // frame's layout policy: a SOLE editing seat among 2+ players takes the dominant workbench region.
        m_editor.PruneDeparted();

        var soleEditorViewIndex = m_editor.SoleEditorViewIndex();

        m_views.Clear();

        // Per-instance soft-shadow participation (the crowd lever): a local seat always casts; a stand-in casts only
        // within m_settings.ShadowCrowdRadius of some joined seat, bounding the dominant GPU term to the crowd around
        // the real players. CastsSoftShadow false suppresses the entry from the soft-shadow march only (it still renders
        // and self-lights). Radius 0 => only the seats cast; a large radius => everyone casts. Parked entries are not
        // emitted, so their flag is irrelevant.
        Span<Vector3> joinedSeats = stackalloc Vector3[WorldPopulation.LocalSeatCount];
        var joinedSeatCount = 0;

        for (var seat = 0; (seat < WorldPopulation.LocalSeatCount); seat++) {
            if (m_client.IsActive(index: seat)) {
                joinedSeats[joinedSeatCount++] = m_client.Position(index: seat);
            }
        }

        var crowdRadiusSquared = (m_settings.ShadowCrowdRadius * m_settings.ShadowCrowdRadius);

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            if (!m_client.IsActive(index: index)) {
                m_avatarPoseSeeded[index] = false;

                continue;
            }

            var position = m_client.Position(index: index);
            var castsSoftShadow = ((index < WorldPopulation.LocalSeatCount) || WithinCrowd(position: position, seats: joinedSeats[..joinedSeatCount], radiusSquared: crowdRadiusSquared));

            if (m_avatarPoseSeeded[index]) {
                // Phase advances by DISTANCE, not wall time: idle avatars hold their pose; walking speed controls cadence.
                // Clamp a teleport/server snap so it cannot spin the limbs through dozens of cycles in one frame.
                var travelled = MathF.Min(x: Vector3.Distance(value1: position, value2: m_avatarPreviousPositions[index]), y: 0.25f);
                var previousPhase = m_avatarGaitPhases[index];

                m_avatarGaitPhases[index] += (travelled * 8.0f);

                // The player.footstep cue: LOCAL seat avatars fire one at-site cue per gait-phase half-cycle
                // wrap — one footfall per π of phase (a stride swings one leg through), so cadence follows walking
                // speed and an idle avatar is silent. Presentation-side by design: the phase is the same
                // distance-driven presentation state that swings the limbs.
                if ((index < WorldPopulation.LocalSeatCount) &&
                    (((int)(m_avatarGaitPhases[index] / MathF.PI)) > ((int)(previousPhase / MathF.PI)))) {
                    m_audio.SubmitCue(eventToken: WorldAudioCue.PlayerFootstep, site: position);
                }
            } else {
                m_avatarPoseSeeded[index] = true;
            }

            m_avatarPreviousPositions[index] = position;
            // Resolve the entity's LOOK: a catalog rig pin, a uniform render scale, and a gait-amplitude phase scale.
            // GaitAmplitude scales m_avatarGaitPhases (1 = the pre-look swing; 0 stills the limbs at their rest pose).
            var look = ResolveLook(index: index);
            WorldAvatarCatalog.PackTransforms(
                avatar: index,
                rootPosition: position,
                rootOrientation: m_client.Orientation(index: index),
                gaitPhase: (m_avatarGaitPhases[index] * look.Motion.GaitAmplitude),
                castsSoftShadow: castsSoftShadow,
                transforms: m_transforms,
                rig: LookRig(look: look),
                scale: look.Scale
            );
        }

        // The animated-placement pool packs after the avatar catalog (its reserved slots sit past the frozen avatar
        // capacity); hidden slots park below the floor.
        m_animator.PackTransforms(transforms: m_transforms);

        Array.Clear(array: m_seatCameraPoses);

        // The gizmo feed's per-frame accumulators (the composed speaker list resolves lazily on the first editing seat).
        IReadOnlyList<WorldSpeaker>? gizmoSpeakers = null;
        var gizmoSeatCount = 0;

        // The roster bookkeeping pass: the seat.join cue (a roster arrival edge, layout-independent) and the ordered
        // list of joined seat slots a composed seat slot binds against by position.
        Span<int> joinedRosterSlots = stackalloc int[PlayerRoster.MaxSlots];
        var joinedRosterCount = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is null) {
                m_seatWasJoined[slot] = false;

                continue;
            }

            if (!m_seatWasJoined[slot]) {
                m_seatWasJoined[slot] = true;
                m_audio.SubmitCue(eventToken: WorldAudioCue.SeatJoin, site: m_client.Position(index: slot));
            }

            joinedRosterSlots[joinedRosterCount++] = slot;
        }

        // Compose the window: layout selection + eased transition. An empty authored layout list (the frozen default)
        // falls through to the built-in seat ladder, reproducing today's composition exactly.
        m_composer.Compose(
            joinedCount: joinedCount,
            soleEditorIndex: soleEditorViewIndex,
            workbenchFraction: m_client.Definition.Authoring.WorkbenchFraction,
            views: (m_client.Definition.Views ?? WorldViewDefaults.Default),
            layoutOverride: m_composition.ActiveLayout,
            cameraOverride: m_composition.SelectedCamera,
            elapsedSeconds: m_elapsedSeconds
        );

        var transitionScale = m_composer.CurrentRenderScale;

        foreach (var composed in m_composer.Slots) {
            var region = composed.Region;

            if (composed.Camera is { } cameraName) {
                // A camera-bearing slot: render the named authored camera into the rect (no seat pose / gizmo).
                if (ResolveNamedCamera(name: cameraName, region: region, width: width, height: height, deltaSeconds: deltaSeconds, camera: out var namedCamera)) {
                    m_views.Add(item: new SdfViewSnapshot(Camera: namedCamera, Region: region) {
                        RenderScale = transitionScale,
                        UpscaleSharpness = m_settings.UpscaleSharpness,
                    });
                }

                continue;
            }

            // A seat slot: bind the seat at this slot's order among the joined seats.
            if ((uint)composed.SeatOrder >= (uint)joinedRosterCount) {
                continue;
            }

            var slot = joinedRosterSlots[composed.SeatOrder];
            var camera = ResolveCamera(slot: slot, region: region, width: width, height: height, deltaSeconds: deltaSeconds, eye: out var eye, target: out var target);

            // The live render-scale tier rides each view's own RenderScale: native = 1.0 is the bit-exact fast path,
            // any lower tier renders that view's SDF at a reduced extent and upsamples. A layout transition dips it.
            m_views.Add(item: new SdfViewSnapshot(Camera: camera, Region: region) {
                RenderScale = (m_settings.RenderScale * transitionScale),
                UpscaleSharpness = m_settings.UpscaleSharpness,
            });
            // The listener-policy candidate: the SAME resolved rig the seat renders through (editor rig included),
            // so "focus" listens where the active view looks.
            m_seatCameraPoses[slot] = new WorldSeatCameraPose(Joined: true, Eye: eye, Forward: (target - eye));

            // The speaker gizmos: EDITOR-MODE-ONLY chips at each speaker's resolved pose, projected through
            // the SAME camera this seat renders with. Pending drag rows compose over the delivered truth, so a
            // dragged chip tracks its snapped position live.
            if (m_editor.IsEditing(slot: slot)) {
                gizmoSpeakers ??= m_drag.ComposeSpeakers(live: m_client.Definition.Speakers);
                m_gizmoSeats[gizmoSeatCount++] = ComposeGizmoSeat(slot: slot, region: region, camera: in camera, width: width, height: height, speakers: gizmoSpeakers);
            }
        }

        // Published EVERY frame: an empty frame clears the chips the moment no seat edits.
        m_gizmos.Publish(frame: new OverlayGizmoFrame(Seats: m_gizmoSeats.AsMemory(start: 0, length: gizmoSeatCount)));

        // Publish this frame's audio snapshot AFTER the transforms are packed and the view rigs resolved: emitter
        // poses read the packed leaf transforms; the listener reads the seat cameras once per produced
        // frame, from the produce path where render poses are already resolved. The presentation delta ages the
        // transient cue pool (visual-only clock use — audio is presentation).
        _ = m_audio.Publish(transforms: m_transforms, seats: m_seatCameraPoses, deltaSeconds: deltaSeconds);

        return new SdfFrame(
            Program: m_program,
            ProgramChanged: programChanged,
            Views: m_views,
            Time: m_elapsedSeconds,
            WarpAmount: 0f
        ) {
            // Shadow reach is continuous: zero skips the march; (0,1) scales gather + march reach; one uses the
            // engine's 0 sentinel for full reach.
            DisableAmbientOcclusion = !m_settings.AmbientOcclusion,
            DisableSoftShadows = (m_settings.ShadowReach <= 0f),
            // Ambient occlusion — the world.ao toggle rides the DisableAmbientOcclusion lane.
            // Far-field isolators (world.far-field): both features ship ON, so the frame's flags are the negated
            // "disable" side of each toggle.
            DisableFarBound = !m_settings.FarBound,
            DisableShadowFarExit = !m_settings.ShadowFarExit,
            DynamicTransforms = m_transforms,
            ShadowDistanceScale = ((m_settings.ShadowReach >= 1f) ? 0f : m_settings.ShadowReach),
            // At the machine-fleet tiers the correctness-complete per-pixel shadow-grid gather is the dominant frame
            // cost (measured 64.5 ms views at 124 stand-ins versus 16.5 ms with the existing camera-tile fallback).
            // Small sessions keep exact off-camera shadow candidates; 16/64/128-player sessions take the explicit crowd
            // approximation, while the independent crowd-radius policy still controls which avatars cast at all.
            UseCameraTileShadowMask = (m_settings.ShadowMask switch {
                ShadowMaskMode.ExactGather => false,
                ShadowMaskMode.CameraTile => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
            UseFastSoftShadowMarch = (m_settings.ShadowMarch switch {
                ShadowMarchMode.Exact => false,
                ShadowMarchMode.Fast => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
            UseFastAmbientOcclusion = (m_settings.AmbientOcclusionQuality switch {
                AmbientOcclusionMode.Exact => false,
                AmbientOcclusionMode.Fast => true,
                _ => (m_client.ActivePeerCount >= 16),
            }),
        };
    }

    /// <inheritdoc/>
    public void PrepareScreenSources(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        // Render + upload every CPU-fed screen for this frame off the sim tick advanced during CaptureFrame, so the
        // provider polled just after this call returns a handle to THIS frame's image. The engine seam calls this
        // AFTER capture and BEFORE the source poll.
        m_binder.Publish(tick: m_simulation.Tick, elapsedTicks: m_simulation.ElapsedTicks, deviceContext: deviceContext, gpu: gpu);
    }

    /// <inheritdoc/>
    public void RenderViews(in Puck.Hosting.FrameContext context) {
        // Render this frame's jumbotron views (the View screens) against the live device, feeding each the SAME world
        // program / dynamic transforms / content clock the room renders with, so a jumbotron shows this world from its
        // placeable camera. Called AFTER PrepareScreenSources (the CPU-fed screens the views sample are already this
        // frame's) and BEFORE the source poll (so a View screen's provider returns this frame's offscreen render).
        m_binder.RenderViews(context: in context, program: m_program, revision: m_builtRevision, transforms: m_transforms, time: m_elapsedSeconds);
    }

    // The per-editing-seat gizmo budget (largechange-09): the projected speaker chips one seat contributes to the
    // shared 192-record overlay table are capped here, nearest-to-camera first, so an author who declares a large
    // speaker field cannot flood the table and starve the binding bar / editor HUD / toast — each of which reserves
    // its own tail capacity, but only against a bounded gizmo writer. A dropped chip is off-screen priority (the
    // farthest speakers), never a nearer one. Worst case: PlayerRoster.MaxSlots seats × this budget × 2 records
    // (ring + icon) stays a documented fraction of MaxElements.
    private const int MaxGizmoChipsPerSeat = 16;

    // One editing seat's gizmo set: every composed speaker row resolved to a world pose (the director's own anchor
    // resolution — leaf/placement anchors track exactly what the audio hears), projected into the seat's viewport, then
    // culled to MaxGizmoChipsPerSeat nearest the camera (largechange-09 — bounded admission into the shared overlay
    // table). Selection lights the ACCENT tier; a live change-shimmer pulse the HELD tier; beds carry their projected
    // support-radius ring. Reuses the per-slot chip array (grown only when the budget-bounded count does).
    private OverlayGizmoSeat ComposeGizmoSeat(int slot, NormalizedRect region, in CameraSnapshot camera, uint width, uint height, IReadOnlyList<WorldSpeaker> speakers) {
        var budget = Math.Min(val1: speakers.Count, val2: MaxGizmoChipsPerSeat);

        if (m_gizmoChips[slot].Length < budget) {
            m_gizmoChips[slot] = new OverlayGizmoChip[budget];
        }

        var chips = m_gizmoChips[slot];
        var count = 0;
        var selection = m_targeting.Selected(slot: slot);
        // The nearest-kept cull: the resolved camera-space depth of the FARTHEST kept chip and its slot, so once the
        // budget fills a nearer speaker evicts the farthest instead of dropping. depths[i] tracks chips[i]'s depth.
        Span<float> depths = stackalloc float[MaxGizmoChipsPerSeat];

        foreach (var speaker in speakers) {
            if (!m_audio.TryResolveSpeakerPose(speaker: speaker, transforms: m_transforms, position: out var world) ||
                !TryProjectGizmo(camera: in camera, region: in region, width: width, height: height, world: world, px: out var px, py: out var py, pixelsPerUnit: out var pixelsPerUnit)) {
                continue;
            }

            var depth = Vector3.Dot(vector1: (world - camera.Position), vector2: camera.Forward);
            int writeSlot;

            if (count < budget) {
                writeSlot = count++;
            } else {
                // Budget full: evict the farthest kept chip only when this one is nearer; otherwise drop this speaker.
                // count stays at budget — we overwrite one slot in place.
                var farthest = 0;

                for (var i = 1; (i < budget); i++) {
                    if (depths[i] > depths[farthest]) {
                        farthest = i;
                    }
                }

                if (depth >= depths[farthest]) {
                    continue;
                }

                writeSlot = farthest;
            }

            depths[writeSlot] = depth;
            chips[writeSlot] = new OverlayGizmoChip(
                CenterX: px,
                CenterY: py,
                RingRadiusPx: ((speaker is WorldSpeaker.Bed bed) ? (bed.Radius * pixelsPerUnit) : 0f),
                Bed: (speaker is WorldSpeaker.Bed),
                Selected: ((selection is { Section: WorldSection.Speakers } selected) && string.Equals(a: selected.Id, b: speaker.Name, comparisonType: StringComparison.Ordinal)),
                Pulse: (m_shimmer.SpeakerIntensity(name: speaker.Name, now: m_elapsedSeconds) > 0f)
            );
        }

        return new OverlayGizmoSeat(Viewport: region, Chips: chips.AsMemory(start: 0, length: count));
    }

    // Perspective-projects a world point into a seat viewport's pixel space through the seat's own CameraSnapshot
    // frame (the render camera's exact basis + FOV). False behind the near plane or generously outside the view
    // (the clip rect would discard the pixels anyway — this just skips the record). pixelsPerUnit is the on-screen
    // scale at the point's DEPTH (the bed ring's world-radius → px conversion; an approximation that reads as a
    // radius indicator, not a perspective-correct 3D circle — deliberate, documented).
    private static bool TryProjectGizmo(in CameraSnapshot camera, in NormalizedRect region, uint width, uint height, Vector3 world, out float px, out float py, out float pixelsPerUnit) {
        px = 0f;
        py = 0f;
        pixelsPerUnit = 0f;

        var delta = (world - camera.Position);
        var depth = Vector3.Dot(vector1: delta, vector2: camera.Forward);

        if (depth < 0.05f) {
            return false;
        }

        var ndcX = (Vector3.Dot(vector1: delta, vector2: camera.Right) / ((depth * camera.TanHalfFieldOfView) * camera.AspectRatio));
        var ndcY = (Vector3.Dot(vector1: delta, vector2: camera.Up) / (depth * camera.TanHalfFieldOfView));

        if ((MathF.Abs(x: ndcX) > 1.5f) || (MathF.Abs(x: ndcY) > 1.5f)) {
            return false;
        }

        var regionHeight = (region.Height * height);

        px = ((region.X * width) + ((0.5f + (0.5f * ndcX)) * (region.Width * width)));
        py = ((region.Y * height) + ((0.5f - (0.5f * ndcY)) * regionHeight));
        pixelsPerUnit = ((regionHeight * 0.5f) / (depth * camera.TanHalfFieldOfView));

        return true;
    }

    // Whether `position` lies within the crowd radius of any joined local seat (the stand-in soft-shadow gate). With no
    // joined seats or a zero radius, nothing qualifies.
    private static bool WithinCrowd(Vector3 position, ReadOnlySpan<Vector3> seats, float radiusSquared) {
        foreach (var seat in seats) {
            if (Vector3.DistanceSquared(value1: position, value2: seat) < radiusSquared) {
                return true;
            }
        }

        return false;
    }

    // Frames the slot's view at the region's pixel size (region × window dims), so each split keeps its own aspect.
    // The rig is the seat's chase rig by default; while the seat edits, the editor session's rig (advanced by this
    // frame's presentation delta) frames it instead. The anchor is the seat's render pose (interpolated and
    // error-eased, resolved by the client view this frame), so the chase camera tracks the pose the avatar is drawn
    // at and the orbit pivot rides it live.
    private CameraSnapshot ResolveCamera(int slot, NormalizedRect region, uint width, uint height, float deltaSeconds, out Vector3 eye, out Vector3 target) {
        var anchor = new SdfAnchor(Position: m_client.Position(index: slot), Orientation: m_client.Orientation(index: slot));
        var rig = m_editor.ResolveRig(slot: slot, chase: m_cameraRigs[slot], anchor: in anchor, time: m_elapsedSeconds, deltaSeconds: deltaSeconds);
        var fieldOfView = 0f;

        (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, time: m_elapsedSeconds);

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );
    }

    // Recompiles every seat's chase rig from the authored seat rig (the built-in default reproduces OrientedFollowRig's
    // own field defaults, so the frozen world's seat framing is byte-identical). Called at construction and on any
    // views-section delivery (world.view.rig live).
    private void RebuildSeatRigs(WorldRig seatRig) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            m_cameraRigs[slot] = WorldRigCompiler.Compile(rig: seatRig);
        }
    }

    // Resolves a named authored camera into a CameraSnapshot framed in `region`: its anchor pose (entity/leaf/placement/
    // group, or null = world), its offset, its rig, and — for a group-anchored chase — its spread pullback. Returns
    // false when the name resolves no camera row (a faulted layout slot renders nothing rather than a bogus view).
    private bool ResolveNamedCamera(string name, NormalizedRect region, uint width, uint height, float deltaSeconds, out CameraSnapshot camera) {
        camera = default;

        WorldCamera? found = null;

        foreach (var row in m_client.Definition.Cameras) {
            if (string.Equals(a: row.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                found = row;

                break;
            }
        }

        if (found is not { } cameraRow) {
            return false;
        }

        var (basePosition, baseOrientation, spread) = ResolveCameraAnchorPose(row: cameraRow, deltaSeconds: deltaSeconds);
        // WHERE the eye rides: an unanchored offset IS the world eye; an anchored offset attaches in the anchor's frame.
        var effectivePosition = ((cameraRow.Anchor is null) ? cameraRow.Offset : (basePosition + Vector3.Transform(value: cameraRow.Offset, rotation: baseOrientation)));
        var pivotLift = ((cameraRow.Rig is WorldRig.Orbit orbit) ? orbit.PivotLift : Vector3.Zero);
        var rig = WorldRigCompiler.Compile(rig: cameraRow.Rig);

        // The establishing shot's spread-adaptive widening: scale a group-riding chase rig's eye offset by the group spread.
        if ((cameraRow.Rig is WorldRig.Chase chase) && (chase.SpreadPullback != 0f) && (spread > 0f)) {
            var factor = (1f + (chase.SpreadPullback * spread));

            switch (rig) {
                case OrientedFollowRig oriented:
                    oriented.EyeOffset *= factor;

                    break;
                case FollowRig follow:
                    follow.EyeOffset *= factor;

                    break;
            }
        }

        // A fixed rig ignores its anchor; its eye is the resolved world position (an unanchored look-at's offset).
        if (rig is FixedRig fixedRig) {
            fixedRig.Eye += effectivePosition;
        }

        var anchor = new SdfAnchor(Position: (effectivePosition + pivotLift), Orientation: baseOrientation);
        var (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, time: m_elapsedSeconds);

        camera = CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );

        return true;
    }

    // The one shared anchor→pose resolver the camera path reads (P9): entity/leaf ride the live snapshot pose, a
    // placement rides its stamped transform (WorldAnchorGeometry, the same math speakers read), a group rides its
    // smoothed centroid + spread, and a null anchor is the world origin. A group has no facing → identity orientation.
    private (Vector3 Position, Quaternion Orientation, float Spread) ResolveCameraAnchorPose(WorldCamera row, float deltaSeconds) {
        switch (row.Anchor) {
            case WorldAnchor.Entity entity:
                return (m_client.Position(index: entity.Index), m_client.Orientation(index: entity.Index), 0f);
            case WorldAnchor.EntityLeaf leaf: {
                var position = m_client.Position(index: leaf.Index);
                var orientation = m_client.Orientation(index: leaf.Index);

                if (WorldAvatarCatalog.TryHumanoidRole(token: leaf.Leaf, role: out var role)) {
                    position += Vector3.Transform(value: WorldAvatarCatalog.RoleOffset(avatar: leaf.Index, role: role), rotation: orientation);
                }

                return (position, orientation, 0f);
            }
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(definition: m_client.Definition, placementId: placement.PlacementId, shapeId: placement.ShapeId), Quaternion.Identity, 0f);
            case WorldAnchor.Group group: {
                var (centroid, spread) = m_groupAnchors.Resolve(key: row.Name, group: group, client: m_client, maxPopulation: WorldPopulation.MaxPopulation, deltaSeconds: deltaSeconds);

                return (centroid, Quaternion.Identity, spread);
            }
            default:
                return (Vector3.Zero, Quaternion.Identity, 0f);
        }
    }

    // The editor-aware viewport resolver: when EXACTLY one seat edits while others play (soleEditorIndex >= 0, 2+
    // joined), the editing view takes the full-height left `workbenchFraction` (LIVE-CONSUMED —
    // WorldAuthoringDefaults.WorkbenchFraction, read fresh by the one caller each captured frame; the workbench wants
    // width and an honest aspect) and the playing seats stack in a live right rail spanning the remaining width (each
    // keeps a visible, playable view). All-playing, single-seat, and multi-editor sessions fall through to the
    // standard ladder.
    internal static NormalizedRect LayoutRegion(int count, int index, int soleEditorIndex, float workbenchFraction) {
        if ((soleEditorIndex >= 0) && (count >= 2)) {
            if (index == soleEditorIndex) {
                return new NormalizedRect(X: 0f, Y: 0f, Width: workbenchFraction, Height: 1f);
            }

            var railCount = (count - 1);
            var railIndex = ((index < soleEditorIndex) ? index : (index - 1));
            var railWidth = (1f - workbenchFraction);

            return new NormalizedRect(X: workbenchFraction, Y: ((float)railIndex / railCount), Width: railWidth, Height: (1f / railCount));
        }

        return LayoutRegion(count: count, index: index);
    }

    // The viewport region for the player at slot-order position `index` of `count`. NormalizedRect convention: origin
    // top-left, Y increasing down. 1 = fullscreen; 2 = side-by-side halves; 3 = big-top (full-width, top half) over two
    // bottom quarters; 4 = the 2×2 quad (index 0=TL, 1=TR, 2=BL, 3=BR). Internal: the overlay feed scopes each seat's
    // screen-space UI (binding bar, later the editor HUD) into the SAME rect the seat renders in.
    internal static NormalizedRect LayoutRegion(int count, int index) {
        return count switch {
            1 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f),
            2 => new NormalizedRect(X: (0.5f * index), Y: 0f, Width: 0.5f, Height: 1f),
            3 => (index switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 0.5f),
                1 => new NormalizedRect(X: 0f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
                _ => new NormalizedRect(X: 0.5f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
            }),
            _ => new NormalizedRect(X: (0.5f * (index % 2)), Y: (0.5f * (index / 2)), Width: 0.5f, Height: 0.5f),
        };
    }

    // The combined program-rebuild watch: the client's roster/snapshot/definition counters plus the editor's
    // selection (highlight), drag-overlay, and sculpt-workbench counters — all monotonic, so the sum only stalls
    // when none has changed.
    private int RebuildRevision() => (((m_client.Revision + m_targeting.Revision) + m_drag.Revision) + m_workbench.Revision);

    // The boot scene padded with the documented authoring-headroom rows (worst-case slabs: per-row material + box) —
    // the probe-only shape that reserves live editing room in the capacity floors. Never validated, never rendered.
    // BOOT-CONSUMED: reads the frozen m_authoringHeadroomRows field (never definition.Authoring live).
    private WorldScene WithAuthoringHeadroom(WorldScene scene) {
        var rows = new List<WorldSceneRow>(capacity: (scene.Rows.Count + m_authoringHeadroomRows));

        rows.AddRange(collection: scene.Rows);

        for (var index = 0; (index < m_authoringHeadroomRows); index++) {
            rows.Add(item: new WorldSceneRow.Slab(
                Id: $"authoring-headroom-{index}",
                Center: Vector3.Zero,
                HalfExtents: Vector3.One,
                Round: 0.05f,
                Smooth: 0.3f,
                Albedo: Vector3.One
            ));
        }

        return (scene with { Rows = rows });
    }

    // The boot screens padded with headroom slabs at free engine indices (bounded by the engine surface ceiling), so a
    // runtime UpsertScreen of a NEW index fits the probed envelope. BOOT-CONSUMED: reads the frozen
    // m_authoringHeadroomScreens field.
    private IReadOnlyList<WorldScreen> WithAuthoringHeadroom(IReadOnlyList<WorldScreen> screens) {
        var padded = new List<WorldScreen>(capacity: (screens.Count + m_authoringHeadroomScreens));
        var used = new HashSet<int>();

        foreach (var screen in screens) {
            padded.Add(item: screen);
            _ = used.Add(item: screen.Index);
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

        return padded;
    }

    // The scene: a grass ground plane, then the scene rows (each smooth-unioned into the accumulated field), the
    // static placement stamps + the animated replay pool, then the view's active avatars as leaf-level dynamic
    // instances riding frozen catalog slots. Active-only, never declared-but-parked: the per-tile instance mask width
    // derives from the program's total declared instance count (SdfProgram.InstanceMaskWordCount), so parked avatar
    // declarations widen every shadow-gather pixel's mask walk. Instead the program is rebuilt on population change
    // (the revision watch), and the 128-avatar worst case is held by the spec's capacity floors (ProgramWordCapacity /
    // InstanceCapacity / DynamicTransformCapacity), probed at construction. Every avatar keeps its own body + accent
    // material (cheap constant words), so a recolor is data, not a resize. `placementProbe` replaces the static stamps
    // with the reserved worst case (construction only); the animated pool and the avatars follow `probeWorstCase`
    // (worst case for both the construction probe AND the apply-time measure).
    private SdfProgram Build(WorldScene scene, IReadOnlyList<WorldScreen> screens, IReadOnlyList<WorldPlacement> placements, IReadOnlyList<WorldCreation> creations, bool probeWorstCase, bool placementProbe, WorldEditorTargeting? highlight, float maxPlacementScale, double shimmerNow = 0d) {
        var client = m_client;
        var builder = new SdfProgramBuilder();
        var grass = builder.AddMaterial(material: new SdfMaterial(Albedo: scene.GroundAlbedo));
        // The per-avatar body + accent materials, allocated up front so the catalog emitter is a straight builder chain.
        // A local seat's colors come from its seated profile (a pending seat renders a desaturated candidate); a stand-in's
        // from its snapshot palette. A color change bumps the revision and rebuilds; a settings-only edit does not.
        var avatarBodyMaterials = new int[WorldPopulation.MaxPopulation];
        var avatarAccentMaterials = new int[WorldPopulation.MaxPopulation];

        for (var index = 0; (index < WorldPopulation.MaxPopulation); index++) {
            var bodyColor = client.BodyColor(index: index);

            avatarBodyMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: bodyColor));
            avatarAccentMaterials[index] = builder.AddMaterial(material: new SdfMaterial(Albedo: WorldColor.Nose(body: bodyColor)));
        }

        var shimmer = ((highlight is not null) ? m_shimmer : null);

        // The static field from the scene data: the grass ground plane, then each scene row (its shape smooth-unioned
        // into the field) translated to its center and reset back for the next. Every row carries its OWN material —
        // uniformly, so the probe's capacity math and a live highlighted build count identical words — with the
        // selected row's albedo pulled toward the selection tint (the editor's material-swap highlight).
        _ = builder.Plane(normal: Vector3.UnitY, offset: 0f, material: grass);

        foreach (var row in scene.Rows) {
            var albedo = ((row is WorldSceneRow.Slab slabRow) ? slabRow.Albedo : scene.StoneAlbedo);

            if ((highlight is { } targeting) && targeting.IsSceneRowSelected(id: row.Id)) {
                albedo = Vector3.Lerp(value1: albedo, value2: s_selectionTint, amount: SelectionTintBlend);
            }

            if ((shimmer is { } pulses) && (pulses.Intensity(id: row.Id, now: shimmerNow) is > 0f and var pulse)) {
                albedo = Vector3.Lerp(value1: albedo, value2: s_shimmerTint, amount: (pulse * ShimmerBlendMax));
            }

            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: albedo));

            _ = builder.Translate(offset: row.Center);
            _ = (row switch {
                WorldSceneRow.Boulder boulder => builder.Sphere(radius: boulder.Radius, material: material, blend: SdfBlendOp.SmoothUnion, smooth: boulder.Smooth),
                WorldSceneRow.Slab slab => builder.Box(halfExtents: slab.HalfExtents, round: slab.Round, material: material, blend: SdfBlendOp.SmoothUnion, smooth: slab.Smooth),
                _ => builder,
            });
            _ = builder.ResetPoint();
        }

        // The diegetic screens: each a sampled ScreenSlab whose lit face samples its bound source (or the engine's
        // procedural no-signal fallback when unbound). STATIC data — emitted every build (probe and live), so the
        // capacity floors cover them by construction (no probe-only branch). The sampled overload takes the explicit
        // world frame (Origin/Right/Up) baked into the surface table for UV mapping; the geometry rounded box is
        // placed by translating to its CENTER, which sits one HalfDepth behind the face along the face normal
        // (Right × Up). The material sentinel the overload assigns needs no palette entry.
        foreach (var screen in screens) {
            var normal = Vector3.Normalize(value: Vector3.Cross(vector1: screen.Right, vector2: screen.Up));
            var center = (screen.Origin - (normal * screen.HalfDepth));

            _ = builder
                .Translate(offset: center)
                .ScreenSlab(
                    halfExtents: new Vector3(x: screen.HalfWidth, y: screen.HalfHeight, z: screen.HalfDepth),
                    round: screen.Round,
                    worldOrigin: screen.Origin,
                    worldRight: screen.Right,
                    worldUp: screen.Up,
                    screenIndex: screen.Index
                )
                .ResetPoint();
        }

        // The placement stamps: the construction probe reserves (boot static segments + the authoring headroom)
        // worst-case stamps, and the APPLY-TIME MEASURE charges a candidate's static placements at that same
        // worst-case unit — max(candidate segments, the reservation) — so the placement term stays CONSTANT between
        // probe and measure while placements are inside their headroom. That constancy is load-bearing: a cheaper
        // as-authored measure would hand the reservation's word slack to SCENE/SCREEN floods (their ceilings would
        // silently widen by thousands of words), and a placement flood still rejects exactly one segment past the
        // headroom. Only the LIVE build emits the rows as authored — static stamps baked into instructions, animated
        // rows through the replay pool (worst-case under any probe). Selection amber and the change shimmer tint a
        // stamp's palette (albedo-only; the all-distinct probe bound covers the extra registrations).
        if (placementProbe || probeWorstCase) {
            var candidateSegments = WorldPlacementStamper.StaticStampSegments(creations: creations, placements: placements, maxRepeatPerSegment: m_maxRepeatPerSegment);

            WorldPlacementStamper.EmitProbe(builder: builder, reservedCount: Math.Max(val1: candidateSegments, val2: m_placementReservation), maxRepeatPerSegment: m_maxRepeatPerSegment);
        } else {
            WorldPlacementStamper.EmitStatic(
                builder: builder,
                creations: creations,
                placements: placements,
                maxRepeatPerSegment: m_maxRepeatPerSegment,
                tintFor: ((highlight is null) ? null : id => {
                    if (highlight.IsPlacementSelected(id: id)) {
                        return (s_selectionTint, SelectionTintBlend);
                    }

                    if ((shimmer is { } pulses) && (pulses.PlacementIntensity(id: id, now: shimmerNow) is > 0f and var pulse)) {
                        return (s_shimmerTint, (pulse * ShimmerBlendMax));
                    }

                    return null;
                })
            );
        }

        m_animator.Emit(builder: builder, probeWorstCase: probeWorstCase, maxPlacementScale: maxPlacementScale);

        // The view's active avatars: 12..20 independently animated leaves and 60..100 authored VM instructions
        // each. The probe emits every catalog range at unit scale (the frozen worst case); a live build emits only
        // active ranges, each sourcing its LOOK's pinned rig and uniform scale (both clamped so the frozen per-entity
        // slot capacity is never exceeded — see WorldAvatarCatalog.Emit's remarks).
        WorldAvatarCatalog.Emit(
            builder: builder,
            isActive: client.IsActive,
            bodyMaterials: avatarBodyMaterials,
            accentMaterials: avatarAccentMaterials,
            probeWorstCase: probeWorstCase,
            rigFor: (probeWorstCase ? null : index => LookRig(look: ResolveLook(index: index))),
            scaleFor: (probeWorstCase ? null : index => ResolveLook(index: index).Scale)
        );

        return builder.Build();
    }

    // The LOOK row an entity wears: the delivered look table indexed by the snapshot's per-entity look byte, or the
    // implicit single catalog look when the world authors no `looks` section (the pre-arc runtime exactly).
    private WorldLook ResolveLook(int index) {
        var looks = m_client.Definition.Looks;

        if (looks is not { Count: > 0 } rows) {
            return WorldLook.Implicit;
        }

        var lookIndex = m_client.LookIndex(index: index);

        return ((lookIndex < rows.Count) ? rows[lookIndex] : WorldLook.Implicit);
    }

    // The catalog geometry-source rig for a look: a Catalog(Index) pin, or -1 (the entity's own index-derived rig) for
    // an unpinned catalog look. A Creation look renders through the catalog on the entity's own rig for now — a
    // documented degradation (never a black/vanished body) until the creation-stamp render path lands.
    private static int LookRig(WorldLook look) => (look.Source is WorldLookSource.Catalog { Index: { } pinned }) ? pinned : -1;
}
