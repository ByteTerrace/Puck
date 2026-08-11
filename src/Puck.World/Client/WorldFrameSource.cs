using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SdfVm.Queries;
using Puck.SdfVm.Views;
using Puck.World.Client.Sdf;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Client;

/// <summary>
/// The client's per-frame source: the engine-facing half of the overworld's presentation. It composes exactly one
/// content source — <see cref="WorldSceneEmitter"/>, the room's geometry — through
/// <see cref="SdfCompositionFrameSource"/>, and DRESSES the program that host hands back
/// (<see cref="ISdfFrameDresser"/>): the four local seats' chased over-the-shoulder viewports (fullscreen →
/// side-by-side → big-top/two-bottom → 2×2 quad as players join), the named authored cameras, the editor speaker
/// gizmos, the audio listener/emitter snapshot, and the frame's render-quality flags. When no local seat is
/// active (every seat departed via a portal/transfer with none yet rejoined) it presents ONE fixed spectator view
/// instead of zero — an interim safety net, not a designed spectator mode (see <c>Dress</c>'s tail remarks).
/// </summary>
/// <remarks>
/// <para>
/// The split is content versus presentation: the emitter owns WHAT geometry exists (and with it the capacity probe,
/// candidate admission, and the rebuild revision), this type owns HOW a frame presents it. It keeps its
/// <see cref="ISdfFrameSource"/> face so the engine seam does not move.
/// </para>
/// <para>
/// A rebuild happens when any composed revision COMPONENT moves — the composition host's own componentwise predicate
/// over the emitter's <see cref="WorldSceneEmitter.WriteRevision"/>. This type contributes the DELIVERY side of that: it reconciles the screen
/// binder, the seat rigs, and the shimmer baseline whenever the definition revision moves, before the host captures.
/// </para>
/// </remarks>
internal sealed class WorldFrameSource : ISdfFrameSource, ISdfFrameDresser {
    // BOOT-CONSUMED: the reserved derived-face screen count (WorldAuthoringDefaults.DerivedFaceScreens) — the binder's
    // frozen derived-face slot range, re-pointed live at each delivery.
    private readonly int m_derivedFaceScreens;
    private readonly FrameRateMonitor m_frameRate;
    private readonly PlayerRoster m_roster;
    private readonly WorldClient m_client;
    // The per-seat perception anchor: every seat-relative derivation in this type (the camera anchor pose, the
    // seat-join cue site) resolves its body index through it — one resolution point, so a possession anchor swap
    // moves every derivation together.
    private readonly WorldPerceptionAnchor m_anchor;
    // The traveler-follow router (stage 1) — the per-seat roster-bookkeeping pass in Dress reads it to keep an
    // away-routed seat OUT of the local viewport layout (its own body left boot's simulation with it, so nothing
    // here can frame a camera against it) rather than reading stale/reused boot-scoped body data.
    private readonly WorldSeatInstanceRouter m_seatRouter;
    // The routed-definition registry supplies the structure half of each seat's live look policy while the
    // presentation clock integrates its latched stick Y. A traveling seat therefore uses the destination's clamp,
    // exactly like pointer drag and world.view.camera, rather than silently retaining the boot world's structure.
    // The traveler-follow away-render pool (stage 1, item 5/6) — tracks a WorldSessionMirror/AwaySeatSceneEmitter/
    // WorldSessionView per FOLLOWED INSTANCE (refcounted, never per seat), reconciled once per produced frame
    // against the router's current away locations, and rendered alongside the jumbotron pool in RenderViews.
    private readonly WorldAwaySeatViews m_awaySeatViews;
    // Every local seat's live camera-orbit yaw/pitch — armed and shaped per that seat's own control feel, composed
    // into the resolved orbit rig at ResolveCamera. WorldClient also reads yaw to rotate an authored world-frame
    // movement pair before submission. Pointer drag and the look stick share its one policy nudge door.
    // Each local seat's live control feel — the preference half WorldSeatCameraResolver.ResolveSeatLook merges with
    // the boot document's own structure each frame; only the merged WorldAxes is consumed here. Per seat, so two
    // seats can differ in the same frame.
    // Each local seat's live-orbit rig cache (the seat rig must author an Orbit motion — the ONLY live camera
    // mechanism this type carries; any other authored motion renders untouched, with no live composition at all):
    // rebuilt only when the authored orbit motion instance or that seat's composed yaw/pitch actually changed since
    // the last frame, so an unmoved drag on a stationary body costs no per-frame rig recompile — see
    // WorldSeatCameraResolver.ResolveChase, the same shared path AwaySeatSceneEmitter reuses for a traveling seat.
    // Slot-indexed, PlayerRoster.MaxSlots entries — one live rig per seat, never shared.
    private readonly WorldSimulation m_simulation;
    // This produced frame's dressed SdfFrame, kept from Dress so the LATER RenderViews call can hand it to every
    // offscreen view as the base each derives its own submission from. Null before the first Dress.
    private SdfFrame? m_dressedFrame;
    // The editor's client-side seams this half still reads: the targeting selection (the gizmo highlight tier), the
    // drag channel (its per-frame retirement pass and the composed speaker rows), and the sculpt workbench and
    // animated-placement pool (their presentation clocks). Their PROGRAM-side effects belong to the emitter.
    private readonly WorldEditorTargeting m_targeting;
    private readonly WorldEditorDrag m_drag;
    private readonly WorldWorkbench m_workbench;
    private readonly WorldStampPool m_animator;
    // The audio director: its emitter derivation reconciles at the delivery boundary (AFTER the screen binder —
    // the chiasmus ordering, speakers consume screen slots) and its snapshot publishes at the end of every dress.
    private readonly WorldAudioDirector m_audio;
    // The editor-gizmo feed: geometry-less rows (speakers) projected into each EDITING seat's viewport as
    // overlay chips — published every produced frame (leaving editor mode clears the chips), consumed by the
    // unified overlay's gizmo writer the same frame (CaptureFrame runs before the overlay's FeedTick/writers).
    private readonly EditorGizmoStore m_gizmos;
    private readonly OverlayGizmoSeat[] m_gizmoSeats = new OverlayGizmoSeat[PlayerRoster.MaxSlots];
    private readonly OverlayGizmoChip[][] m_gizmoChips = new OverlayGizmoChip[PlayerRoster.MaxSlots][];
    // Per-frame scratch for the listener policy: each joined seat's resolved view-camera pose, slot-indexed.
    private readonly WorldSeatCameraPose[] m_seatCameraPoses = new WorldSeatCameraPose[PlayerRoster.MaxSlots];
    // The per-seat viewport + camera publication (the cursor feed's unproject seam): republished every dressed
    // frame from the SAME resolved region/camera each seat view renders with.
    private readonly WorldSeatViewports m_viewports;
    // The seat.join cue's edge detector: a slot's roster presence last frame.
    private readonly bool[] m_seatWasJoined = new bool[PlayerRoster.MaxSlots];
    // One rig slot per local seat, chase (OrientedFollowRig) by default: its defaults (eye up-and-back along the
    // anchor's +Z, target lifted a touch) frame that seat's avatar from behind, tracking its heading. The editor
    // session swaps its own rig in per frame while a seat edits. Only local seats get cameras/views.
    // The seat-chase smoothing gap: the authored WorldCameraRig.SmoothRate the current seat rig carries (0 = off,
    // the unsmoothed snap every shipped world used before this field existed) and, per slot, the previously resolved
    // eye/target the exponential ease lags toward — see WorldSeatCameraResolver.Smooth (the same shared ease
    // AwaySeatSceneEmitter reuses for a traveling seat), which mirrors the "seed un-smoothed on first resolve, alpha
    // = 1 - e^(-rate * dt) after" shape WorldGroupAnchors already establishes for the establishing-shot centroid.
    // Presentation-only: never read by anything that feeds back into the sim. Rig-level (not motion-level): every
    // motion kind shares one smoothing knob, so Orbit/Static/Track ease too.
    // The window composer (layout selection + eased transitions; a shared singleton the world.view.state read also
    // observes), the group-anchor resolver (smoothed centroids for establishing shots), and the shared live
    // composition-override store (view.override layout/view.override camera). All presentation-only.
    private readonly WorldViewComposer m_composer;
    private readonly WorldGroupAnchors m_groupAnchors = new();
    private readonly WorldCompositionState m_composition;
    // The per-seat editor mode: camera rig swap + the sole-editor layout policy, both read during dress.
    private readonly WorldEditorSession m_editor;
    private readonly List<SdfViewSnapshot> m_views = new(capacity: PlayerRoster.MaxSlots);
    private readonly WorldRenderSettings m_settings;
    // The binder that owns the diegetic screens' CPU-fed GPU sources. The scene (ground + boulders) and the screens are
    // read LIVE from the client's delivered definition each rebuild, so a mutation's new geometry lands on the next
    // program rebuild; the binder's runtime source machinery is reconciled when the definition revision moves.
    private readonly WorldScreenBinder m_binder;
    // The room's content sources, and the host that composes them. The host owns the capacity probe, the
    // dynamic-transform buffer and its slot assignment, and the rebuild-on-revision-change predicate.
    private readonly WorldSceneEmitter m_emitter;
    // The first-party puck.sdf.v1 document emitter (world.sdf.load) — a SECOND tenant of the same live composition
    // seam m_emitter already exercises, never a parallel composition point (see WorldSdfDocumentEmitter's remarks).
    private readonly WorldSdfDocumentEmitter m_sdfDocuments;
    // The border-margin strip's render half — the neighbour's own solid geometry composed within each mapped portal
    // facet's authored margin, through the SAME isometry the arrival math and the collision wrapper both use. Static
    // content only (DynamicSlotCount 0), so it needs no dynamic-transform slot budgeting of its own.
    private readonly WorldBorderMarginSceneEmitter m_borderMargin;
    private readonly SdfCompositionFrameSource m_composed;
    // This frame's composed program + packed transforms, stashed by Dress so the post-capture jumbotron pass
    // (RenderViews) films the SAME program the room renders. Null/empty until the first captured frame.
    private SdfProgram? m_program;
    private DynamicTransform[] m_transforms = [];
    private SdfProgram? m_lastProgram;
    private SdfFieldEvaluator? m_cameraClearanceField;
    // Advances exactly when the composed program is a NEW instance — the jumbotron engines' re-upload trigger.
    private int m_programRevision;
    private int m_builtDefinitionRevision;
    private float m_elapsedSeconds;
    // The no-local-seats fallback's own narration edge (see Dress's tail): true once the "presenting the world
    // camera" line has fired for the CURRENT empty stretch, cleared the instant a seat's view fills m_views again,
    // so a later departure re-narrates instead of staying silent forever.
    private bool m_noLocalSeatsNarrated;

    /// <summary>Initializes a new instance of the <see cref="WorldFrameSource"/> class, composing the world scene
    /// emitter over the snapshot-fed client view (the primer snapshot must already be delivered, so the capacity probe
    /// and the first program declare the boot seats and census active).</summary>
    /// <param name="frameRate">The frame-rate witness sampled once per captured frame (the <c>world.fps</c> verb reads it).</param>
    /// <param name="client">The snapshot-fed entity view every pose, color, and active flag is read from.</param>
    /// <param name="simulation">The host-ticked simulation whose completed tick drives presentation sources.</param>
    /// <param name="settings">The live render settings read every captured frame (console-mutated in real time).</param>
    /// <param name="binder">The screen binder owning the declared screens' CPU-fed GPU sources, published each frame.</param>
    /// <param name="envelope">The render-capacity oracle configured here with the probed floors and the emitter's
    /// candidate measurer, so the server can reject an over-envelope scene/screen mutation at apply time.</param>
    /// <param name="editor">The per-seat editor mode (camera rig swap + the sole-editor layout policy).</param>
    /// <param name="targeting">The editor selection state (the render highlight + rebuild watch).</param>
    /// <param name="drag">The editor drag channel (the pending-row overlay + rebuild watch).</param>
    /// <param name="animator">The animated-placement replay pool.</param>
    /// <param name="workbench">The sculpt workbench (the preview creation/placement overlay + rebuild watch).</param>
    /// <param name="audio">The audio director — the emitter derivation reconciled at the delivery boundary and the
    /// per-frame snapshot publisher.</param>
    /// <param name="gizmos">The editor-gizmo store the per-frame speaker-chip projections publish into.</param>
    /// <param name="anchor">The per-seat perception anchor — the one body index every seat-relative derivation here
    /// (camera anchor pose, seat-join cue site, crowd soft-shadow centers) resolves through.</param>
    /// <param name="composition">The shared live composition-override store (view.override layout/view.override camera) the composer reads.</param>
    /// <param name="composer">The shared window composer (layout selection + eased transitions) the world.view.state read observes.</param>
    /// <param name="viewports">The per-seat viewport + camera publication each dressed frame fills (the cursor
    /// feed's unproject seam).</param>
    /// <param name="sdfDocuments">The first-party puck.sdf.v1 document emitter (world.sdf.load composes into it) —
    /// configured here with the SAME probed floors and the reciprocal composed measurer, so a document load is
    /// checked against the live world definition exactly as a scene mutation is checked against the live document.</param>
    /// <param name="seatRouter">The traveler-follow router (stage 1) — the per-seat roster-bookkeeping pass reads
    /// it to exclude an away-routed seat from the local viewport layout.</param>
    /// <param name="awaySeatViews">The traveler-follow away-render pool (stage 1) — reconciled once per produced
    /// frame and rendered alongside the jumbotron pool.</param>
    /// <param name="borderMargin">The injected neighbour resolver behind the border-margin strip's render half — the
    /// SAME wire-shaped seam <see cref="Server.WorldServer.BorderMargin"/> reads for collision.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldFrameSource(FrameRateMonitor frameRate, WorldClient client, WorldSimulation simulation, WorldRenderSettings settings, WorldScreenBinder binder, WorldRenderEnvelope envelope, WorldEditorSession editor, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldStampPool animator, WorldWorkbench workbench, WorldAudioDirector audio, EditorGizmoStore gizmos, WorldPerceptionAnchor anchor, WorldCompositionState composition, WorldViewComposer composer, WorldSdfDocumentEmitter sdfDocuments, WorldSeatViewports viewports, WorldSeatInstanceRouter seatRouter, WorldAwaySeatViews awaySeatViews, IWorldBorderMarginSource borderMargin) {
        ArgumentNullException.ThrowIfNull(argument: frameRate);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: anchor);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);
        ArgumentNullException.ThrowIfNull(argument: awaySeatViews);
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
        ArgumentNullException.ThrowIfNull(argument: sdfDocuments);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: borderMargin);

        m_viewports = viewports;
        m_composition = composition;
        m_composer = composer;
        m_gizmos = gizmos;
        m_seatRouter = seatRouter;
        m_awaySeatViews = awaySeatViews;

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
        m_anchor = anchor;
        m_roster = client.Roster;
        m_simulation = simulation;
        m_settings = settings;
        m_binder = binder;
        m_editor = editor;
        m_targeting = targeting;
        m_drag = drag;
        m_animator = animator;
        m_workbench = workbench;
        m_sdfDocuments = sdfDocuments;

        // Resolve the primer snapshot's render poses once so the capacity probe and the camera anchors are live before
        // the first frame. Alpha 0 is immaterial — a freshly spawned entity has previous == current pose.
        m_client.UpdateRenderPoses(alpha: 0f);

        var definition = m_client.Definition;

        m_derivedFaceScreens = definition.Authoring.DerivedFaceScreens;
        // The emitter freezes the boot authoring policy, seeds the stamp pool, and takes the shimmer baseline; the
        // audio director's boot derivation follows (a booted world may already author speakers/facets/sounds).
        m_emitter = new WorldSceneEmitter(client: client, settings: settings, targeting: targeting, drag: drag, workbench: workbench, animator: animator, audio: audio, anchor: anchor);
        m_borderMargin = new WorldBorderMarginSceneEmitter(client: client, source: borderMargin);
        m_audio.ReconcileSpeakers(definition: definition);
        // Composing the emitter runs the ONE capacity probe (its worst-case branch: all 128 avatars, the reserved
        // placement instances, the worst-case animated pool, and the authoring headroom), freezing the word, instance,
        // and dynamic-transform envelopes every live rebuild fits inside by construction.
        m_composed = new SdfCompositionFrameSource(emitters: [m_emitter, m_sdfDocuments, m_borderMargin], dresser: this) {
            // Park unused slots exactly where a hidden avatar and an unused pool slot already sit — below the floor,
            // outside the camera and tile-cull reach.
            ParkPosition = WorldSceneEmitter.HiddenAvatar,
        };
        ProgramWordCapacity = m_composed.WorstCaseProgramWordCapacity;
        InstanceCapacity = m_composed.WorstCaseInstanceCapacity;
        DynamicTransformCapacity = m_composed.WorstCaseDynamicTransformCapacity;

        // Publish the probed envelope + a JOINT candidate measurer so a scene/screen/placement mutation is
        // capacity-checked at apply time against the SAME worst-case build (avatars and the animated pool are always at
        // worst case; scene/screens/static placements measure AS AUTHORED, so authoring consumes the reserved room
        // before the loud rejection) — and, composition-safely, against whatever puck.sdf.v1 document is CURRENTLY
        // loaded (see MeasureComposed: measuring the world emitter alone would let a mutation spend capacity
        // the loaded document already holds, since the packed tables the two share are computed over the COMPOSED
        // program and are not additive).
        _ = envelope.Configure(
            programWordCapacity: ProgramWordCapacity,
            instanceCapacity: InstanceCapacity,
            measure: candidate => MeasureComposed(worldDefinition: candidate, documentProgram: m_sdfDocuments.CurrentProgram)
        );

        // THE RECIPROCAL HALF (the asymmetric-join fix): a puck.sdf.v1 document load (world.sdf.load) commits OUTSIDE
        // WorldRenderEnvelope's queued-mutation path entirely — it is a client-local Immediate door (see
        // WorldSdfCommandModule), never a WorldMutation the server drains — so it needs its OWN composed-admission
        // check against the SAME frozen floors, reusing the SAME MeasureComposed method with the roles swapped: the
        // CANDIDATE is the incoming document, the CURRENT side is the live world definition (m_client.Definition, read
        // fresh at call time so a document loaded after a scene mutation is checked against what that mutation left
        // behind, never a stale snapshot). Without this, a scene mutation could spend capacity a document isn't
        // currently using, and a subsequently loaded — individually valid — document would commit unchecked and
        // overflow the composed program at the next rebuild.
        m_sdfDocuments.Configure(
            programWordCapacity: ProgramWordCapacity,
            instanceCapacity: InstanceCapacity,
            measureComposed: candidateProgram => MeasureComposed(worldDefinition: m_client.Definition, documentProgram: candidateProgram)
        );

        // NEVER m_client.DefinitionRevision here: the client's primer delivery (LoopbackTransport.Bind, run
        // synchronously inside the client's OWN DI factory, before this type is ever constructed) already bumped the
        // revision once, so capturing it as the baseline would make ReconcileDelivery's very first call see "nothing
        // moved" and skip forever absent a LATER live mutation — a fresh boot with zero mutations would leave every
        // derived face (session/view/testPattern/camera/capture/qr) frozen at its reserved None placeholder forever.
        // A sentinel outside the revision's real range (which only ever counts up from 0) guarantees the first
        // ReconcileDelivery call always reconciles once, exactly like every later delivery.
        m_builtDefinitionRevision = int.MinValue;
    }

    // THE COMPOSITION-SAFE ADMISSION MEASURE, GENERALIZED to take EITHER side as the candidate (both Configure calls
    // above reuse this ONE method rather than each hand-rolling their own builder walk):
    //   - the scene-mutation check passes the candidate WorldDefinition and the CURRENTLY loaded document (read via
    //     m_sdfDocuments.CurrentProgram);
    //   - the document-load check passes the CURRENT live WorldDefinition and the CANDIDATE document (not yet
    //     committed).
    // WorldRenderEnvelope's ceiling was probed from BOTH composed emitters together (m_composed's construction-time
    // probe walks [m_emitter, m_sdfDocuments] into ONE program — see SdfCompositionFrameSource.BuildProgram), so
    // either direction must be measured the same way: composing both sides into one shared builder, then measuring
    // that ONE composed program. Measuring either side alone would let a mutation on ONE side spend program-word/
    // instance capacity the OTHER side already holds, since
    // the packed tables a program carries (the instance grid, segment directory, world-segment list, rigid plan) are
    // computed over the WHOLE composed program and are not additive across emitters — a later commit on the
    // unchecked side then adds its content on top and SdfWorldEngine.UploadProgram throws on the frozen buffer.
    //
    // Emission order and slot base mirror SdfCompositionFrameSource.BuildProgram exactly: the world side first (its
    // own material scope, matching ComposeCandidate's internal wrap), then the document side at the slot base the
    // world emitter's FIXED (candidate-independent) DynamicSlotCount reserves — the document side declares no dynamic
    // slots of its own (see WorldSdfDocumentEmitter's type remarks), so the base only has to line up for parity with
    // the live composition, not correctness. A null documentProgram (no document loaded) composes the world side alone.
    private (int Words, int Instances) MeasureComposed(WorldDefinition worldDefinition, SdfDocumentProgram? documentProgram) {
        var builder = new SdfProgramBuilder();

        m_emitter.ComposeCandidate(builder: builder, candidate: worldDefinition);

        if (documentProgram is { } program) {
            using (builder.BeginMaterialScope()) {
                SdfDocumentDecoder.Replay(builder: builder, program: program);
            }
        }

        WorldPlacementStamper.EmitProbe(
            builder: builder,
            reservedCount: (WorldBorderMarginBands.CollectFrom(definition: worldDefinition).Count * WorldBorderMarginGeometry.MaximumPlacementsPerBand)
        );

        var measured = builder.Build();

        return (Words: measured.Words.Length, Instances: measured.Instances.Count);
    }

    /// <summary>The worst-case (all avatars active) program word count — the spec's <c>ProgramWordCapacity</c> floor.</summary>
    public int ProgramWordCapacity { get; }

    /// <summary>The worst-case (all avatars active) instance count — the spec's <c>InstanceCapacity</c> floor.</summary>
    public int InstanceCapacity { get; }

    /// <summary>The frozen transform-slot count: every leaf in the all-128 avatar catalog plus the reserved
    /// animated-placement replay pool.</summary>
    public int DynamicTransformCapacity { get; }

    /// <inheritdoc/>
    public void NotifyDeviceLost() {
        m_binder.NotifyDeviceLost();
        m_awaySeatViews.NotifyDeviceLost();
    }

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
        // missing-response deadline — see WorldEditorDrag), so the revision the host reads below already reflects any
        // retirement.
        m_drag.Reconcile();

        // Advance the animated-placement replay cursors on the render clock (hold-style — transforms move; the
        // program itself never rebuilds for a timeline step).
        m_animator.Tick(deltaSeconds: deltaSeconds);

        // Advance the sculpt workbench: playback ticks, drag-coalescer frame boundary, and its model revisions fold
        // into the emitter's monotonic rebuild watch.
        m_workbench.Tick(deltaSeconds: deltaSeconds);

        // Hand the emitter this frame's CONTENT clock: while a change-shimmer pulse is live it bumps its own revision
        // from that number, so the pulse's decay animates through the host's ordinary revision predicate instead of
        // through a clock the host would have to query.
        m_emitter.AdvanceContentClock(seconds: m_elapsedSeconds);
        ReconcileDelivery();
        // Traveler-follow stage 1: track/untrack away instances against the router's CURRENT locations before Dress
        // resolves any periscope handle this frame — see WorldAwaySeatViews.Reconcile's own remarks.
        m_awaySeatViews.Reconcile();
        return m_composed.CaptureFrame(width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
    }

    /// <inheritdoc/>
    public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        m_program = program;
        m_transforms = transforms;

        // A rebuilt program is a NEW instance (the host hands back the previous one whenever the composed revision
        // held), so reference inequality IS the re-upload signal — and it covers the first frame, whose program has
        // never been on the GPU.
        var programChanged = !ReferenceEquals(objA: program, objB: m_lastProgram);

        if (programChanged) {
            m_lastProgram = program;
            m_programRevision++;
            RebuildCameraClearanceField();
        }

        // Emit one view per joined local seat (a 1..MaxSlots count up to the ViewportCapacity floor, so players can join
        // later without freezing the count at the first frame's). Views ride slot order; the layout ladder places each
        // by its position among the joined players. The procedural catalog's dynamic transforms are separate and always
        // supplied in full; the active-only program addresses only its avatars' stable leaf ranges.
        var joinedCount = m_roster.Count;

        // Self-heal a seat that left the roster while editing (its mode layer and camera drop), then resolve this
        // frame's layout policy: a SOLE editing seat among 2+ players takes the dominant workbench region.
        m_editor.PruneDeparted();

        var soleEditorViewIndex = m_editor.SoleEditorViewIndex();

        m_views.Clear();
        Array.Clear(array: m_seatCameraPoses);
        m_viewports.BeginFrame();

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
                m_audio.SubmitCue(eventToken: WorldAudioCue.SeatJoin, site: m_client.Position(index: m_anchor.PerceivedBody(slot: slot)));
            }

            // TRAVELER-FOLLOW STAGE 1: a followed seat stays a roster participant while away (WorldInstanceHost.
            // ApplyTransfer's commit loop skips VacateSeat for it — see that loop's own remarks), so m_roster.Seat
            // above no longer excludes it the way an ordinary departure would — it keeps its own composed layout
            // slot exactly like a boot-bound seat; the per-composed-slot loop below is what tells its region apart
            // (periscope camera + away-view fill instead of the ordinary boot-scoped chase). The seat-join cue
            // above still runs once on arrival/return either way.
            joinedRosterSlots[joinedRosterCount++] = slot;
        }

        // Compose the window: layout selection + eased transition. An empty authored layout list falls through to
        // the built-in seat ladder.
        m_composer.Compose(
            joinedCount: joinedCount,
            soleEditorIndex: soleEditorViewIndex,
            workbenchFraction: m_client.Definition.Authoring.WorkbenchFraction,
            views: m_client.Definition.Views,
            layoutOverride: m_composition.ActiveLayout,
            cameraOverride: m_composition.SelectedCamera,
            elapsedSeconds: m_elapsedSeconds
        );

        var transitionScale = m_composer.CurrentRenderScale;

        foreach (var composed in m_composer.Slots) {
            var region = composed.Region;

            if (composed.Camera is { } cameraName) {
                // A single followed traveler still owns the window's composition context. Its pixels come from the
                // away render, so both its destination-authored default rig and a live camera override must show
                // through the reserved periscope quad; resolving cameraName against m_client.Definition here would
                // keep filming the boot scene after the crossing. Multi-seat camera slots remain process-global —
                // there is no unique routed instance to choose when several seats occupy different worlds.
                if (joinedRosterCount == 1) {
                    var routedSlot = joinedRosterSlots[0];

                    if (!string.Equals(a: m_seatRouter.Location(slot: routedSlot).InstanceName, b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                        var routedPixelWidth = (uint)(region.Width * width);
                        var routedPixelHeight = (uint)(region.Height * height);
                        var periscope = WorldAwaySeatQuad.PeriscopeCamera(slot: routedSlot, width: routedPixelWidth, height: routedPixelHeight);

                        m_awaySeatViews.ReportSeatViewportSize(slot: routedSlot, width: routedPixelWidth, height: routedPixelHeight);
                        m_views.Add(item: new SdfViewSnapshot(Camera: periscope, Region: region) {
                            RenderScale = (m_settings.RenderScale * transitionScale),
                            UpscaleSharpness = m_settings.UpscaleSharpness,
                        });

                        continue;
                    }
                }

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
            var seatPixelWidth = (uint)(region.Width * width);
            var seatPixelHeight = (uint)(region.Height * height);

            // Keep the seat's composed extent warm while it is still boot-bound. A seamless crossing has no
            // presentation-side arrival callback: the away projection must be able to allocate its first target
            // from the preceding frame's exact viewport, before this frame reaches the routed branch below.
            m_awaySeatViews.ReportSeatViewportSize(slot: slot, width: seatPixelWidth, height: seatPixelHeight);

            // TRAVELER-FOLLOW STAGE 1, item (c): an away-routed seat's region renders its own reserved periscope
            // quad (WorldAwaySeatQuad) instead of the ordinary boot-scoped chase — the quad's bound screen source
            // (WorldAwaySeatViews.ResolveHandle, merged into the engine's screen-source providers) fills it with
            // the away view's last rendered image, the SAME textured-quad technique a jumbotron session screen
            // composites with. No seat-camera-pose/gizmo/viewport-unproject publication for an away seat: none of
            // those are meaningful against a periscope framing a flat quad rather than the room.
            if (!string.Equals(a: m_seatRouter.Location(slot: slot).InstanceName, b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                var periscope = WorldAwaySeatQuad.PeriscopeCamera(slot: slot, width: seatPixelWidth, height: seatPixelHeight);

                // This IS the one place the seat's actual composed viewport pixel size is known — hand it to the
                // away-render pool so the followed instance's own offscreen render targets it instead of the fixed
                // native brick-panel size (WorldSessionView.DefaultWidth/DefaultHeight) stretched over a viewport it
                // was never sized for. WorldAwaySeatViews.ReconcileViewportSizes reads every seat's report once per
                // produced frame and re-targets the SHARED render at the largest requesting seat's own size when two
                // or more seats follow the same instance.
                m_views.Add(item: new SdfViewSnapshot(Camera: periscope, Region: region) {
                    RenderScale = (m_settings.RenderScale * transitionScale),
                    UpscaleSharpness = m_settings.UpscaleSharpness,
                });

                continue;
            }

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
            // The cursor feed's unproject seam: the SAME region + camera this view renders with, so a cursor ray
            // aims exactly where the pixel under it was drawn from.
            m_viewports.Publish(slot: slot, region: region, camera: in camera, width: width, height: height);

            // The speaker gizmos: EDITOR-MODE-ONLY chips at each speaker's resolved pose, projected through
            // the SAME camera this seat renders with. Pending drag rows compose over the delivered truth, so a
            // dragged chip tracks its snapped position live.
            if (m_editor.IsEditing(slot: slot)) {
                gizmoSpeakers ??= m_drag.ComposeSpeakers(live: m_client.Definition.Speakers);
                m_gizmoSeats[gizmoSeatCount++] = ComposeGizmoSeat(slot: slot, region: region, camera: in camera, width: width, height: height, speakers: gizmoSpeakers);
            }
        }

        // SAFETY NET — no local seat (and no authored seatCount:0 catch-all layout) resolved a view: PrepareFrame
        // refuses 0 viewports outright ("composites 1 to N viewports"), so an empty m_views here would fail-fast
        // the whole process on the very next present rather than degrade gracefully — the last local occupant
        // departing via a portal/world.transfer is exactly this shape once the roster drops the departed seat
        // (see WorldFrameSource's Dress remarks above). Present ONE fixed spectator view of the world instead,
        // deterministically derived from the world's OWN authored seat-spawn positions — no wall clock, no RNG;
        // float is fine, this is presentation only, nothing here feeds the sim. Narrated once on stderr when it
        // engages, and cleared the instant a rejoined seat fills m_views again so the ordinary composer path
        // resumes with no trace of the fallback ever having run.
        if (m_views.Count == 0) {
            if (!m_noLocalSeatsNarrated) {
                m_noLocalSeatsNarrated = true;

                Console.Error.WriteLine(value: "[frame] no local seats — presenting the world camera");
            }

            m_views.Add(item: new SdfViewSnapshot(Camera: ResolveSpectatorCamera(width: width, height: height), Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f)) {
                RenderScale = m_settings.RenderScale,
                UpscaleSharpness = m_settings.UpscaleSharpness,
            });
        } else {
            m_noLocalSeatsNarrated = false;
        }

        // Published EVERY frame: an empty frame clears the chips the moment no seat edits.
        m_gizmos.Publish(frame: new OverlayGizmoFrame(Seats: m_gizmoSeats.AsMemory(start: 0, length: gizmoSeatCount)));

        // Publish this frame's audio snapshot AFTER the transforms are packed and the view rigs resolved: emitter
        // poses read the packed leaf transforms; the listener reads the seat cameras once per produced
        // frame, from the produce path where render poses are already resolved. The presentation delta ages the
        // transient cue pool (visual-only clock use — audio is presentation).
        _ = m_audio.Publish(transforms: transforms, seats: m_seatCameraPoses, deltaSeconds: deltaSeconds);

        // Stashed on the way out (see m_dressedFrame): RenderViews runs LATER in the same produced frame and hands
        // this exact instance to every offscreen view, which derives its own submission from it. Returning it without
        // keeping it is what left the views building their own.
        return m_dressedFrame = new SdfFrame(
            Program: program,
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
            DisableShadowEscapeExit = !m_settings.ShadowFarExit,
            // Temporal accumulation ships ON — the raw two-sample estimate is a three-level quantity and reads as
            // stipple on its own. The flag is the negated "disable" side, exactly like the far-field isolators.
            DisableShadowAccumulation = !m_settings.ShadowAccumulation,
            DynamicTransforms = transforms,
            // The area-light shadow estimator's net index, taken from the DETERMINISTIC 240 Hz tick counter and never
            // from m_elapsedSeconds — the sampler is seekable so that a replay at tick N draws the identical sun-disc
            // directions, which a wall-clock accumulation would destroy.
            SampleIndex = (uint)m_simulation.ElapsedTicks,
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
        // The frame context's target extent IS the launcher's live client area (window.Width/Height at this frame's
        // BeginFrame) — the one place the World side can learn it. Published for the cursor feed's client→frame
        // mapping (see WorldCursorFeed.Decide); the per-seat views above carry the FIXED frame extent instead.
        m_viewports.PublishClientExtent(width: context.TargetWidth, height: context.TargetHeight);

        // Render this frame's jumbotron views (the View screens) against the live device, feeding each the SAME world
        // program / dynamic transforms / content clock the room renders with, so a jumbotron shows this world from its
        // placeable camera. Called AFTER PrepareScreenSources (the CPU-fed screens the views sample are already this
        // frame's) and BEFORE the source poll (so a View screen's provider returns this frame's offscreen render).
        // The dressed frame is required, not optional: an offscreen view DERIVES its submission from it, so a frame
        // this produced frame never dressed has nothing to derive from and the views hold their last resolved image
        // for one frame rather than rendering off a fabricated one.
        if ((m_program is not { } program) || (m_dressedFrame is not { } hostFrame)) {
            return;
        }

        m_binder.RenderViews(context: in context, program: program, revision: m_programRevision, transforms: m_transforms, time: m_elapsedSeconds, authoritativeTick: m_simulation.Tick, hostFrame: hostFrame);
        // The away-seat render pool, rendered the SAME way and at the SAME point the jumbotron pool is — before the
        // screen-source provider poll, so a followed instance's periscope quad samples THIS frame's offscreen image.
        m_awaySeatViews.RenderViews(context: in context, program: program, revision: m_programRevision, transforms: m_transforms, time: m_elapsedSeconds, authoritativeTick: m_simulation.Tick, hostFrame: hostFrame);
    }

    // The delivery boundary: a definition delivery (scene/screen mutation, swap, or undo) landed since the last frame,
    // so the binder's runtime source machinery reconciles to the new definition BEFORE the host rebuilds the program
    // off the live geometry — cameras FIRST, so a same-delivery View source change resolves the new camera rows.
    private void ReconcileDelivery() {
        var definitionRevision = m_client.DefinitionRevision;

        if (definitionRevision == m_builtDefinitionRevision) {
            return;
        }

        // A views-section edit recompiles each seat's chase rig from the delivered seat rig (world.row.set views.seatRig live).
        // Derive the creation facets: a creation's eyes become cameras on WorldAnchor.Placement, its faces
        // become screens at the reserved derived range. Cameras concatenate onto the document rows; the reserved
        // face range re-points the boot-registered slots. Never written to the document — recomputed each delivery.
        var facets = WorldCreationFacets.Derive(definition: m_client.Definition, derivedFaceBase: WorldCreationFacets.DerivedFaceBase, derivedFaceScreens: m_derivedFaceScreens);

        m_binder.ReconcileCameras(cameras: Concat(first: m_client.Definition.Cameras, second: facets.Cameras));
        m_binder.ReconcileScreens(screens: Concat(first: m_client.Definition.Screens, second: facets.Faces));
        // Cable links reconcile SERVER-SIDE (Server.WorldMachineHost.ReconcileLinks, called from
        // WorldServer.Install), not here.
        // ReconcileSpeakers runs AFTER ReconcileScreens (the chiasmus: speakers consume screen slots) and before the
        // emitter's own delivery pass (whose stamp-pool reconcile runs at the rebuild the host is about to make).
        m_audio.ReconcileSpeakers(definition: m_client.Definition);
        // The SAME facets.Faces the binder just reconciled its sources against, threaded to the emitter so the
        // ScreenSlab geometry it composes and the binder's bound sources never disagree about which face maps to
        // which placement — one WorldCreationFacets.Derive call per delivery, never two.
        m_emitter.ObserveDelivery(definition: m_client.Definition, derivedFaces: facets.Faces);
        m_builtDefinitionRevision = definitionRevision;
    }

    // The per-editing-seat gizmo budget: the projected speaker chips one seat contributes to the
    // gizmo channel's own reservation are capped here, nearest-to-camera first, so an author who declares a large
    // speaker field admits its nearest rows rather than clipping arbitrary ones at the channel boundary. A dropped
    // chip is off-screen priority (the farthest speakers), never a nearer one. Starving another surface is no longer
    // possible in either direction: every channel writes against its own reservation
    // (Puck.Overlays.OverlayChannelLeases), and the gizmo reservation is exactly PlayerRoster.MaxSlots seats × this
    // budget × 2 records (ring + icon). THE cap is the writer's — this admission mirrors it, never widens it.
    private const int MaxGizmoChipsPerSeat = EditorGizmoWriter.MaxChipsPerSeat;

    // One editing seat's gizmo set: every composed speaker row resolved to a world pose (the director's own anchor
    // resolution — leaf/placement anchors track exactly what the audio hears), projected into the seat's viewport, then
    // culled to MaxGizmoChipsPerSeat nearest the camera (bounded admission into the shared overlay
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
                Pulse: (m_emitter.SpeakerPulse(name: speaker.Name) > 0f)
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

    // Frames the slot's view at the region's pixel size (region × window dims), so each split keeps its own aspect.
    // The rig is the seat's chase rig by default; while the seat edits, the editor session's rig (advanced by this
    // frame's presentation delta) frames it instead. The anchor is the render pose (interpolated and error-eased,
    // resolved by the client view this frame) of the seat's PERCEIVED body — the perception anchor's resolution,
    // the seat's bound body or, while possessing, the routed body — so the chase camera tracks the pose the avatar
    // is drawn at and the orbit pivot rides it live. The audio listener follows by construction: the
    // WorldSeatCameraPose this camera fills is the listener policy's per-seat candidate.
    private CameraSnapshot ResolveCamera(int slot, NormalizedRect region, uint width, uint height, float deltaSeconds, out Vector3 eye, out Vector3 target) {
        var body = m_anchor.PerceivedBody(slot: slot);
        var bodyOrientation = m_client.Orientation(index: body);
        var views = m_client.Definition.Views;

        // The one live-orbit mechanism: an authored Orbit seat rig feeds this seat's live pointer/stick offset into the
        // document's own orbit vocabulary (authored yaw/pitch + the live offset) via WorldSeatCameraResolver — the
        // same shared path AwaySeatSceneEmitter reuses for a traveling seat, so a destination frames identically
        // whether the seat sits at its boot or arrived through a portal. The merged seat-look's WorldAxes selects
        // what the composed yaw rides on top of — false (today's shipped default) adds the body's own yaw so the
        // orbit rides the body's heading (turn, and the camera swings with you); true drops it, an absolute orbit
        // independent of facing. WorldAxes is rig STRUCTURE (the boot world's own playerDefaults.seatLook, never a
        // joined profile's — see WorldSeatCameraResolver.ResolveSeatLook), so it agrees with the away path's own
        // structure-only read of a destination's WorldAxes. Any other authored motion renders through the plain
        // compiled chase untouched. m_client.Orientation (the sim body orientation) is never written — everything
        // here is a local presentation-only derivation.
        var view = m_roster.Seat(slot: slot)?.View ?? throw new InvalidOperationException(message: "joined view has no seat controller");
        var chase = view.ResolveChase(views: views, bodyOrientation: bodyOrientation);

        var anchor = new SdfAnchor(Position: m_client.Position(index: body), Orientation: bodyOrientation);
        var rig = m_editor.ResolveRig(slot: slot, chase: chase, anchor: in anchor, time: m_elapsedSeconds, deltaSeconds: deltaSeconds);
        var fieldOfView = 0f;

        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_simulation.Tick);

        (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, clock: in clock);

        // The seat-chase smoothing gap: applied only to the plain (non-editing) chase rig — ReferenceEquals catches
        // exactly that, since ResolveRig above returns the same chase instance unchanged while the seat is not
        // editing (see its own remarks) and a different rig (drag/orbit/workbench) otherwise. See
        // WorldSeatCameraResolver.Smooth: a zero rate (the default, and every world/camera authored before this
        // field existed) skips the ease entirely — eye/target pass through raw, byte-for-byte.
        view.Smooth(rate: views.SeatRig.SmoothRate, enabled: ReferenceEquals(objA: rig, objB: chase),
            deltaSeconds: deltaSeconds, eye: ref eye, target: ref target);

        if (ReferenceEquals(objA: rig, objB: chase)) {
            eye = WorldCameraClearance.Resolve(field: m_cameraClearanceField, desiredEye: eye, target: target);
        }

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );
    }

    // Build the camera query from the same static scene layers the composed frame renders. The live program cannot
    // be queried directly because its avatar catalog carries TransformDynamic instructions; reconstructing only the
    // static placements/screens/margin strip keeps the camera from treating the avatar it follows as an obstacle.
    private void RebuildCameraClearanceField() {
        m_cameraClearanceField = null;

        try {
            var definition = m_client.Definition;
            var builder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(builder: builder, creations: definition.Creations, placements: definition.Placements);

            foreach (var screen in definition.Screens) {
                WorldScreenStamper.Emit(builder: builder, screen: screen);
            }

            var facets = WorldCreationFacets.Derive(
                definition: definition,
                derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
                derivedFaceScreens: definition.Authoring.DerivedFaceScreens
            );

            foreach (var screen in facets.Faces) {
                WorldScreenStamper.Emit(builder: builder, screen: screen);
            }

            m_borderMargin.EmitCurrent(builder: builder);
            m_cameraClearanceField = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        } catch (ArgumentException) {
            // Render-only warp/texture operations have no fixed-point query twin. Those worlds retain the authored
            // eye; query-compatible worlds still receive the presentation-only clearance correction.
        }
    }


    // Resolves a named authored camera into a CameraSnapshot framed in `region`: its anchor pose (entity/part/placement/
    // group, or null = world), motion, aim, lens, and group spread. Returns
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
        var rig = WorldCameraRigCompiler.Compile(rig: cameraRow.Rig, spread: spread);
        var anchor = new SdfAnchor(Position: basePosition, Orientation: baseOrientation);
        var clock = new SdfCameraClock(PresentationSeconds: m_elapsedSeconds, AuthoritativeTick: m_simulation.Tick);

        var (eye, target, fieldOfView) = rig.Resolve(anchor: in anchor, clock: in clock);

        camera = CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(val1: 1u, val2: (uint)(region.Width * width)),
            viewportHeight: Math.Max(val1: 1u, val2: (uint)(region.Height * height))
        );

        return true;
    }

    // The no-local-seats fallback's own camera (see Dress's tail) — a fixed overview anchored to the plaza's own
    // bounds: the centroid of the world's authored local-seat spawn positions, which every world declares
    // (population.seatSpawns names exactly LocalSeatCount rows in SpawnPoints), so no per-world authoring is owed
    // to make this safe. A pure function of the boot-frozen document, recomputed on each engaged frame rather than
    // cached — the spawn table never changes size and the loop is four iterations.
    private CameraSnapshot ResolveSpectatorCamera(uint width, uint height) {
        var definition = m_client.Definition;
        var centroid = Vector3.Zero;
        var resolved = 0;

        foreach (var name in definition.Population.SeatSpawns) {
            if (WorldDefinitionRows.FindSpawnPoint(spawnPoints: definition.SpawnPoints, id: name) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        // A plain elevated pull-back — an establishing shot over the plaza, not a chase rig; the exact offset is
        // an arbitrary but fixed presentation choice, not a value anything downstream depends on.
        var target = (centroid + new Vector3(x: 0f, y: 1f, z: 0f));
        var eye = (centroid + new Vector3(x: 0f, y: 14f, z: 18f));

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(val1: 1u, val2: width),
            viewportHeight: Math.Max(val1: 1u, val2: height)
        );
    }

    // The one shared anchor→pose resolver the camera path reads: entity/part ride the live snapshot pose, a
    // placement rides its stamped transform (WorldAnchorGeometry, the same math speakers read), a group rides its
    // smoothed centroid + spread, and a null anchor is the world origin. A group has no facing → identity orientation.
    private (Vector3 Position, Quaternion Orientation, float Spread) ResolveCameraAnchorPose(WorldCamera row, float deltaSeconds) {
        switch (row.Anchor) {
            case WorldAnchor.Entity entity:
                return (m_client.Position(index: entity.Index), m_client.Orientation(index: entity.Index), 0f);
            case WorldAnchor.EntityPart part: {
                    return (WorldEntityPartResolver.TryPackedPose(client: m_client, stamps: m_animator, entityIndex: part.Index, partId: part.PartId, transforms: m_transforms, pose: out var pose)
                        ? (pose.Position, pose.Orientation, 0f)
                        : (Vector3.Zero, Quaternion.Identity, 0f));
                }
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(definition: m_client.Definition, placementId: placement.PlacementId, shapeId: placement.ShapeId), Quaternion.Identity, 0f);
            case WorldAnchor.Group group: {
                    var (centroid, spread) = m_groupAnchors.Resolve(key: row.Name, group: group, client: m_client, maxPopulation: m_client.Definition.Population.Capacity, deltaSeconds: deltaSeconds);

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

    // Concatenate two row lists into one (document rows + derived rows) for the binder reconcile — a small allocation at
    // the delivery boundary only, never per-frame.
    private static IReadOnlyList<T> Concat<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) {
        if (second.Count == 0) {
            return first;
        }

        var combined = new List<T>(capacity: (first.Count + second.Count));

        combined.AddRange(collection: first);
        combined.AddRange(collection: second);

        return combined;
    }
}
