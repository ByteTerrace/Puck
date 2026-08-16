using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
using Puck.Text;
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
    // The per-editing-seat gizmo budget: the projected speaker chips one seat contributes to the
    // gizmo channel's own reservation are capped here, nearest-to-camera first, so an author who declares a large
    // speaker field admits its nearest rows rather than clipping arbitrary ones at the channel boundary. A dropped
    // chip is off-screen priority (the farthest speakers), never a nearer one. Starving another surface is no longer
    // possible in either direction: every channel writes against its own reservation
    // (Puck.Overlays.OverlayChannelLeases), and the gizmo reservation is exactly PlayerRoster.MaxSlots seats × this
    // budget × 2 records (ring + icon). THE cap is the writer's — this admission mirrors it, never widens it.
    private const int MaxGizmoChipsPerSeat = EditorGizmoWriter.MaxChipsPerSeat;

    // The adjacency render half — neighbour solids and delivered bodies composed through the same isometry contact
    // and handoff use, with remote avatar transforms in its own frozen slot range.
    private readonly WorldAdjacencySceneEmitter m_adjacencies;
    // The per-seat perception anchor: every seat-relative derivation in this type (the camera anchor pose, the
    // seat-join cue site) resolves its body index through it — one resolution point, so a possession anchor swap
    // moves every derivation together.
    private readonly WorldPerceptionAnchor m_anchor;
    private readonly WorldStampPool m_animator;
    // The audio director: its emitter derivation reconciles at the delivery boundary (AFTER the screen binder —
    // the chiasmus ordering, speakers consume screen slots) and its snapshot publishes at the end of every dress.
    private readonly WorldAudioDirector m_audio;
    // The binder that owns the diegetic screens' CPU-fed GPU sources. The scene (ground + boulders) and the screens are
    // read LIVE from the client's delivered definition each rebuild, so a mutation's new geometry lands on the next
    // program rebuild; the binder's runtime source machinery is reconciled when the definition revision moves.
    private readonly WorldScreenBinder m_binder;
    private readonly WorldClient m_client;
    private readonly SdfCompositionFrameSource m_composed;
    // One rig slot per local seat, chase (OrientedFollowRig) by default: its defaults (eye up-and-back along the
    // anchor's +Z, target lifted a touch) frame that seat's avatar from behind, tracking its heading. The editor
    // session swaps its own rig in per frame while a seat edits. Only local seats get cameras/views.
    // The seat-chase smoothing gap: the authored WorldCameraRig.SmoothRate the current seat rig carries (0 = off,
    // the unsmoothed snap every shipped world used before this field existed) and, per slot, the previously resolved
    // eye/target the exponential ease lags toward — see WorldSeatCameraResolver.Smooth (the same shared ease
    // every traveling seat reuses, which mirrors the "seed un-smoothed on first resolve, alpha
    // = 1 - e^(-rate * dt) after" shape WorldGroupAnchors already establishes for the establishing-shot centroid.
    // Presentation-only: never read by anything that feeds back into the sim. Rig-level (not motion-level): every
    // motion kind shares one smoothing knob, so Orbit/Static/Track ease too.
    // The window composer (layout selection + eased transitions; a shared singleton the world.view.state read also
    // observes), the group-anchor resolver (smoothed centroids for establishing shots), and the shared live
    // composition-override store (view.override layout/view.override camera). All presentation-only.
    private readonly WorldViewComposer m_composer;
    private readonly WorldCompositionState m_composition;
    // The continuum maps every routed authority pose into this frame. The ordinary seat viewport/camera path then
    // treats a local and transferred traveler identically instead of creating an away-view presentation system.
    private readonly WorldContinuum m_continuum;
    // BOOT-CONSUMED: the reserved derived-face screen count (WorldAuthoringDefaults.DerivedFaceScreens) — the binder's
    // frozen derived-face slot range, re-pointed live at each delivery.
    private readonly int m_derivedFaceScreens;
    private readonly WorldEditorDrag m_drag;
    // The per-seat editor mode: camera rig swap + the sole-editor layout policy, both read during dress.
    private readonly WorldEditorSession m_editor;
    // The room's content sources, and the host that composes them. The host owns the capacity probe, the
    // dynamic-transform buffer and its slot assignment, and the rebuild-on-revision-change predicate.
    private readonly WorldSceneEmitter m_emitter;
    private readonly FrameRateMonitor m_frameRate;
    // The editor-gizmo feed: geometry-less rows (speakers) projected into each EDITING seat's viewport as
    // overlay chips — published every produced frame (leaving editor mode clears the chips), consumed by the
    // unified overlay's gizmo writer the same frame (CaptureFrame runs before the overlay's FeedTick/writers).
    private readonly EditorGizmoStore m_gizmos;
    private readonly PlayerRoster m_roster;
    // The first-party puck.sdf.v1 document emitter (world.sdf.load) — a SECOND tenant of the same live composition
    // seam m_emitter already exercises, never a parallel composition point (see WorldSdfDocumentEmitter's remarks).
    private readonly WorldSdfDocumentEmitter m_sdfDocuments;
    private readonly WorldRenderSettings m_settings;
    // The routed-definition registry supplies the structure half of each seat's live look policy while the
    // presentation clock integrates its latched stick Y. A traveling seat therefore uses the destination's clamp,
    // exactly like pointer drag and world.view.camera, rather than silently retaining the boot world's structure.
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
    // WorldSeatCameraResolver.ResolveChase, the single shared path every traveling seat uses.
    // Slot-indexed, PlayerRoster.MaxSlots entries — one live rig per seat, never shared.
    private readonly WorldSimulation m_simulation;
    // The editor's client-side seams this half still reads: the targeting selection (the gizmo highlight tier), the
    // drag channel (its per-frame retirement pass and the composed speaker rows), and the sculpt workbench and
    // animated-placement pool (their presentation clocks). Their PROGRAM-side effects belong to the emitter.
    private readonly WorldEditorTargeting m_targeting;
    private readonly WorldTextCatalog m_text;
    // The per-seat viewport + camera publication (the cursor feed's unproject seam): republished every dressed
    // frame from the SAME resolved region/camera each seat view renders with.
    private readonly WorldSeatViewports m_viewports;
    private readonly WorldWorkbench m_workbench;

    private int m_builtDefinitionRevision;
    private SdfFieldEvaluator? m_cameraClearanceField;
    // This produced frame's dressed SdfFrame, kept from Dress so the LATER RenderViews call can hand it to every
    // offscreen view as the base each derives its own submission from. Null before the first Dress.
    private SdfFrame? m_dressedFrame;
    private float m_elapsedSeconds;
    private SdfProgram? m_lastProgram;
    // The no-local-seats fallback's own narration edge (see Dress's tail): true once the "presenting the world
    // camera" line has fired for the CURRENT empty stretch, cleared the instant a seat's view fills m_views again,
    // so a later departure re-narrates instead of staying silent forever.
    private bool m_noLocalSeatsNarrated;
    // This frame's composed program + packed transforms, stashed by Dress so the post-capture jumbotron pass
    // (RenderViews) films the SAME program the room renders. Null/empty until the first captured frame.
    private SdfProgram? m_program;
    // Advances exactly when the composed program is a NEW instance — the jumbotron engines' re-upload trigger.
    private int m_programRevision;

    private readonly OverlayGizmoSeat[] m_gizmoSeats = new OverlayGizmoSeat[PlayerRoster.MaxSlots];
    private readonly OverlayGizmoChip[][] m_gizmoChips = new OverlayGizmoChip[PlayerRoster.MaxSlots][];
    // Per-frame scratch for the listener policy: each joined seat's resolved view-camera pose, slot-indexed.
    private readonly WorldSeatCameraPose[] m_seatCameraPoses = new WorldSeatCameraPose[PlayerRoster.MaxSlots];
    private readonly Vector3[] m_lastSeatAnchorPosition = new Vector3[PlayerRoster.MaxSlots];
    private readonly Quaternion[] m_lastSeatAnchorOrientation = new Quaternion[PlayerRoster.MaxSlots];
    private readonly bool[] m_hasSeatAnchor = new bool[PlayerRoster.MaxSlots];
    private readonly bool[] m_missingSeatAnchorNarrated = new bool[PlayerRoster.MaxSlots];
    // The seat.join cue's edge detector: a slot's roster presence last frame.
    private readonly bool[] m_seatWasJoined = new bool[PlayerRoster.MaxSlots];
    private readonly WorldGroupAnchors m_groupAnchors = new();
    private readonly List<SdfViewSnapshot> m_views = new(capacity: PlayerRoster.MaxSlots);
    private DynamicTransform[] m_transforms = [];
    // One provider per ENGINE screen index (rebuilt each delivery): the closure re-reads the binder's live source every
    // produced frame, so a slot rotating away from text clears its decal the same frame, a slot whose live source
    // becomes text (a magazine selection, a live source verb) lights it without a definition revision, and removing a
    // declared text screen clears its descriptor before authoring headroom can reuse that index. The bake is cached per
    // index by (text, catalog) identity — SetScreenDecal change-detects, but the bake itself should not re-run per frame.
    private readonly Dictionary<int, Func<SdfScreenDecalFrame?>> m_screenDecals = new();
    private readonly Dictionary<int, (WorldScreenSource.Text Text, PackedFontAtlasCatalog Catalog, SdfScreenDecalFrame Frame)> m_screenDecalCache = new();

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
    /// <param name="continuum">The shared authority-to-presentation-frame pose resolver.</param>
    /// <param name="text">The world-relative font catalog and packed GPU atlas.</param>
    /// <param name="adjacencies">The injected adjacency resolver shared by rendering and collision.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldFrameSource(FrameRateMonitor frameRate, WorldClient client, WorldSimulation simulation, WorldRenderSettings settings, WorldScreenBinder binder, WorldRenderEnvelope envelope, WorldEditorSession editor, WorldEditorTargeting targeting, WorldEditorDrag drag, WorldStampPool animator, WorldWorkbench workbench, WorldAudioDirector audio, EditorGizmoStore gizmos, WorldPerceptionAnchor anchor, WorldCompositionState composition, WorldViewComposer composer, WorldSdfDocumentEmitter sdfDocuments, WorldSeatViewports viewports, WorldContinuum continuum, WorldTextCatalog text, IWorldAdjacencySource adjacencies) {
        ArgumentNullException.ThrowIfNull(argument: frameRate);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: anchor);
        ArgumentNullException.ThrowIfNull(argument: continuum);
        ArgumentNullException.ThrowIfNull(argument: text);
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
        ArgumentNullException.ThrowIfNull(argument: adjacencies);

        m_viewports = viewports;
        m_composition = composition;
        m_composer = composer;
        m_gizmos = gizmos;
        m_continuum = continuum;
        m_text = text;

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

        m_text.Reconcile(definition: definition);

        m_derivedFaceScreens = definition.Authoring.DerivedFaceScreens;
        // The emitter freezes the boot authoring policy, seeds the stamp pool, and takes the shimmer baseline; the
        // audio director's boot derivation follows (a booted world may already author speakers/facets/sounds).
        m_emitter = new WorldSceneEmitter(
            anchor: anchor,
            animator: animator,
            audio: audio,
            client: client,
            continuum: continuum,
            drag: drag,
            settings: settings,
            targeting: targeting,
            text: text,
            workbench: workbench
        );
        m_adjacencies = new WorldAdjacencySceneEmitter(
            client: client,
            source: adjacencies,
            suppressEntity: entity => continuum.IsFollowed(entity: in entity)
        );
        m_audio.ReconcileSpeakers(definition: definition);
        // Composing the emitter runs the ONE capacity probe (its worst-case branch: all 128 avatars, the reserved
        // placement instances, the worst-case animated pool, and the authoring headroom), freezing the word, instance,
        // and dynamic-transform envelopes every live rebuild fits inside by construction.
        m_composed = new SdfCompositionFrameSource(
            dresser: this,
            emitters: [m_emitter, m_sdfDocuments, m_adjacencies]
        ) {
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
            measure: candidate => MeasureComposed(
                worldDefinition: candidate,
                documentProgram: m_sdfDocuments.CurrentProgram
            )
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
            measureComposed: candidateProgram => MeasureComposed(
                worldDefinition: m_client.Definition,
                documentProgram: candidateProgram
            )
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

    // The editor-aware viewport resolver: when EXACTLY one seat edits while others play (soleEditorIndex >= 0, 2+
    // joined), the editing view takes the full-height left `workbenchFraction` (LIVE-CONSUMED —
    // WorldAuthoringDefaults.WorkbenchFraction, read fresh by the one caller each captured frame; the workbench wants
    // width and an honest aspect) and the playing seats stack in a live right rail spanning the remaining width (each
    // keeps a visible, playable view). All-playing, single-seat, and multi-editor sessions fall through to the
    // standard ladder.
    internal static NormalizedRect LayoutRegion(int count, int index, int soleEditorIndex, float workbenchFraction) {
        if (
            (soleEditorIndex >= 0) &&
            (count >= 2)
        ) {
            if (index == soleEditorIndex) {
                return new NormalizedRect(
                    Height: 1f,
                    Width: workbenchFraction,
                    X: 0f,
                    Y: 0f
                );
            }

            var railCount = (count - 1);
            var railIndex = ((index < soleEditorIndex)
                ? index
                : (index - 1)
            );
            var railWidth = (1f - workbenchFraction);

            return new NormalizedRect(
                Height: (1f / railCount),
                Width: railWidth,
                X: workbenchFraction,
                Y: (((float)railIndex) / railCount)
            );
        }

        return LayoutRegion(
            count: count,
            index: index
        );
    }
    // The viewport region for the player at slot-order position `index` of `count`. NormalizedRect convention: origin
    // top-left, Y increasing down. 1 = fullscreen; 2 = side-by-side halves; 3 = big-top (full-width, top half) over two
    // bottom quarters; 4 = the 2×2 quad (index 0=TL, 1=TR, 2=BL, 3=BR). Internal: the overlay feed scopes each seat's
    // screen-space UI (binding bar, later the editor HUD) into the SAME rect the seat renders in.
    internal static NormalizedRect LayoutRegion(int count, int index) {
        return count switch {
            1 => new NormalizedRect(
            Height: 1f,
            Width: 1f,
            X: 0f,
            Y: 0f
        ),
            2 => new NormalizedRect(
            Height: 1f,
            Width: 0.5f,
            X: (0.5f * index),
            Y: 0f
        ),
            3 => (index switch {
                0 => new NormalizedRect(
            Height: 0.5f,
            Width: 1f,
            X: 0f,
            Y: 0f
        ),
                1 => new NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: 0f,
            Y: 0.5f
        ),
                _ => new NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: 0.5f,
            Y: 0.5f
        ),
            }),
            _ => new NormalizedRect(
            Height: 0.5f,
            Width: 0.5f,
            X: (0.5f * (index % 2)),
            Y: (0.5f * (index / 2))
        ),
        };
    }

    // One editing seat's gizmo set: every composed speaker row resolved to a world pose (the director's own anchor
    // resolution — leaf/placement anchors track exactly what the audio hears), projected into the seat's viewport, then
    // culled to MaxGizmoChipsPerSeat nearest the camera (bounded admission into the shared overlay
    // table). Selection lights the ACCENT tier; a live change-shimmer pulse the HELD tier; beds carry their projected
    // support-radius ring. Reuses the per-slot chip array (grown only when the budget-bounded count does).
    private OverlayGizmoSeat ComposeGizmoSeat(int slot, NormalizedRect region, in CameraSnapshot camera, uint width, uint height, IReadOnlyList<WorldSpeaker> speakers) {
        var budget = Math.Min(
            val1: speakers.Count,
            val2: MaxGizmoChipsPerSeat
        );

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
            if (
                !m_audio.TryResolveSpeakerPose(
                position: out var world,
                speaker: speaker,
                transforms: m_transforms
            ) ||
                !TryProjectGizmo(
                camera: in camera,
                height: height,
                pixelsPerUnit: out var pixelsPerUnit,
                px: out var px,
                py: out var py,
                region: in region,
                width: width,
                world: world
            )
            ) {
                continue;
            }

            var depth = Vector3.Dot(
                vector1: (world - camera.Position),
                vector2: camera.Forward
            );
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
                RingRadiusPx: ((speaker is WorldSpeaker.Bed bed)
                ? (bed.Radius * pixelsPerUnit)
                : 0f),
                Bed: (speaker is WorldSpeaker.Bed),
                Selected: ((selection is { Section: WorldSection.Speakers } selected) && string.Equals(
                    a: selected.Id,
                    b: speaker.Name,
                    comparisonType: StringComparison.Ordinal
                )),
                Pulse: (m_emitter.SpeakerPulse(name: speaker.Name) > 0f)
            );
        }

        return new OverlayGizmoSeat(
            Viewport: region,
            Chips: chips.AsMemory(
                length: count,
                start: 0
            )
        );
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

        m_emitter.ComposeCandidate(
            builder: builder,
            candidate: worldDefinition
        );

        if (documentProgram is { } program) {
            using (builder.BeginMaterialScope()) {
                SdfDocumentDecoder.Replay(
                    builder: builder,
                    program: program
                );
            }
        }

        WorldPlacementStamper.EmitProbe(
            builder: builder,
            reservedCount: (WorldAdjacencyBands.ProjectionCapacity(definition: worldDefinition) * WorldAdjacencyGeometry.MaximumPlacementsPerBand)
        );

        var measured = builder.Build();

        return (Words: measured.Words.Length, Instances: measured.Instances.Count);
    }
    // Build the camera query from the same static scene layers the composed frame renders. The live program cannot
    // be queried directly because its avatar catalog carries TransformDynamic instructions; reconstructing only the
    // static placements/screens/adjacency geometry keeps the camera from treating the avatar it follows as an obstacle.
    private void RebuildCameraClearanceField() {
        m_cameraClearanceField = null;

        try {
            var definition = m_client.Definition;
            var builder = new SdfProgramBuilder();

            WorldPlacementStamper.EmitStatic(
                builder: builder,
                creations: definition.Creations,
                placements: definition.Placements
            );

            foreach (var screen in definition.Screens) {
                WorldScreenStamper.Emit(
                    builder: builder,
                    screen: screen
                );
            }

            var facets = WorldCreationFacets.Derive(
                definition: definition,
                derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
                derivedFaceScreens: definition.Authoring.DerivedFaceScreens
            );

            foreach (var screen in facets.Faces) {
                WorldScreenStamper.Emit(
                    builder: builder,
                    screen: screen
                );
            }

            m_adjacencies.EmitCurrent(builder: builder);
            m_cameraClearanceField = new SdfFieldEvaluator(program: builder.Build(buildInstanceGrid: false));
        } catch (ArgumentException) {
            // Render-only warp/texture operations have no fixed-point query twin. Those worlds retain the authored
            // eye; query-compatible worlds still receive the presentation-only clearance correction.
        }
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
        var facets = WorldCreationFacets.Derive(
            definition: m_client.Definition,
            derivedFaceBase: WorldCreationFacets.DerivedFaceBase,
            derivedFaceScreens: m_derivedFaceScreens
        );

        m_binder.ReconcileCameras(cameras: Concat(
            first: m_client.Definition.Cameras,
            second: facets.Cameras
        ));
        m_binder.ReconcileScreens(screens: Concat(
            first: m_client.Definition.Screens,
            second: facets.Faces
        ));
        // Cable links reconcile SERVER-SIDE (Server.WorldMachineHost.ReconcileLinks, called from
        // WorldServer.Install), not here.
        // ReconcileSpeakers runs AFTER ReconcileScreens (the chiasmus: speakers consume screen slots) and before the
        // emitter's own delivery pass (whose stamp-pool reconcile runs at the rebuild the host is about to make).
        m_audio.ReconcileSpeakers(definition: m_client.Definition);
        m_text.Reconcile(definition: m_client.Definition);

        // Keep one provider for EVERY engine slot, not only currently declared screens. A removed text screen can be
        // replaced immediately by an authoring-headroom slab at the same index; retaining the null-returning provider
        // is what clears the old decal descriptor instead of letting stale text shade the replacement surface.
        m_screenDecals.Clear();

        for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
            var capturedIndex = index;

            m_screenDecals[index] = () => ResolveScreenDecal(index: capturedIndex);
        }

        foreach (var stale in m_screenDecalCache.Keys.Where(predicate: key => (m_binder.TextSourceAt(index: key) is null)).ToArray()) {
            _ = m_screenDecalCache.Remove(key: stale);
        }
        // The SAME facets.Faces the binder just reconciled its sources against, threaded to the emitter so the
        // ScreenSlab geometry it composes and the binder's bound sources never disagree about which face maps to
        // which placement — one WorldCreationFacets.Derive call per delivery, never two.
        m_emitter.ObserveDelivery(
            definition: m_client.Definition,
            derivedFaces: facets.Faces
        );
        m_builtDefinitionRevision = definitionRevision;
    }
    // Frames the slot's view at the region's pixel size (region × window dims), so each split keeps its own aspect.
    // The rig is the seat's chase rig by default; while the seat edits, the editor session's rig (advanced by this
    // frame's presentation delta) frames it instead. The anchor is the render pose (interpolated and error-eased,
    // resolved by the client view this frame) of the seat's PERCEIVED body — the perception anchor's resolution,
    // the seat's bound body or, while possessing, the routed body — so the chase camera tracks the pose the avatar
    // is drawn at and the orbit pivot rides it live. The audio listener follows by construction: the
    // WorldSeatCameraPose this camera fills is the listener policy's per-seat candidate.
    private CameraSnapshot ResolveCamera(int slot, NormalizedRect region, uint width, uint height, float deltaSeconds, float interpolationAlpha, out Vector3 eye, out Vector3 target) {
        var route = m_continuum.Route(slot: slot);
        var views = route.Endpoint.Definition.Views;

        if (m_continuum.TryResolveSeatPose(
            interpolationAlpha: interpolationAlpha,
            orientation: out var bodyOrientation,
            position: out var bodyPosition,
            slot: slot
        )) {
            m_lastSeatAnchorPosition[slot] = bodyPosition;
            m_lastSeatAnchorOrientation[slot] = bodyOrientation;
            m_hasSeatAnchor[slot] = true;
            m_missingSeatAnchorNarrated[slot] = false;
        } else if (m_hasSeatAnchor[slot]) {
            bodyPosition = m_lastSeatAnchorPosition[slot];
            bodyOrientation = m_lastSeatAnchorOrientation[slot];

            if (!m_missingSeatAnchorNarrated[slot]) {
                m_missingSeatAnchorNarrated[slot] = true;
                Console.Error.WriteLine(value: $"[world.continuum: seat {(slot + 1)} authority '{route.Endpoint.Identity}' has not delivered body:{route.EntityIndex}; holding the last continuous camera anchor]");
            }
        } else {
            var fallbackBody = m_anchor.PerceivedBody(slot: slot);

            bodyPosition = m_client.Position(index: fallbackBody);
            bodyOrientation = m_client.Orientation(index: fallbackBody);
        }

        // The one live-orbit mechanism: an authored Orbit seat rig feeds this seat's live pointer/stick offset into the
        // document's own orbit vocabulary (authored yaw/pitch + the live offset) via WorldSeatCameraResolver — the
        // same shared path every traveling seat uses, so a destination frames identically
        // whether the seat sits at its boot or arrived through a portal. The merged seat-look's WorldAxes selects
        // what the composed yaw rides on top of — false (today's shipped default) adds the body's own yaw so the
        // orbit rides the body's heading (turn, and the camera swings with you); true drops it, an absolute orbit
        // independent of facing. WorldAxes is rig STRUCTURE (the boot world's own playerDefaults.seatLook, never a
        // joined profile's — see WorldSeatCameraResolver.ResolveSeatLook). Any other authored motion renders through the plain
        // compiled chase untouched. m_client.Orientation (the sim body orientation) is never written — everything
        // here is a local presentation-only derivation.
        var view = (m_roster.Seat(slot: slot)?.View ?? throw new InvalidOperationException(message: "joined view has no seat controller"));
        var chase = view.ResolveChase(
            bodyOrientation: bodyOrientation,
            views: views
        );

        var anchor = new SdfAnchor(
            Orientation: bodyOrientation,
            Position: bodyPosition
        );
        var rig = m_editor.ResolveRig(
            anchor: in anchor,
            chase: chase,
            deltaSeconds: deltaSeconds,
            slot: slot,
            time: m_elapsedSeconds
        );
        var fieldOfView = 0f;

        var clock = new SdfCameraClock(
            PresentationSeconds: m_elapsedSeconds,
            AuthoritativeTick: m_simulation.Tick
        );

        (eye, target, fieldOfView) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        // The seat-chase smoothing gap: applied only to the plain (non-editing) chase rig — ReferenceEquals catches
        // exactly that, since ResolveRig above returns the same chase instance unchanged while the seat is not
        // editing (see its own remarks) and a different rig (drag/orbit/workbench) otherwise. See
        // WorldSeatCameraResolver.Smooth: a zero rate (the default, and every world/camera authored before this
        // field existed) skips the ease entirely — eye/target pass through raw, byte-for-byte.
        view.Smooth(
            rate: views.SeatRig.SmoothRate,
            enabled: ReferenceEquals(
                objA: rig,
                objB: chase
            ),
            deltaSeconds: deltaSeconds,
            eye: ref eye,
            target: ref target
        );

        if (ReferenceEquals(
            objA: rig,
            objB: chase
        )) {
            eye = WorldCameraClearance.Resolve(
                desiredEye: eye,
                field: m_cameraClearanceField,
                target: target
            );
        }

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(
                val1: 1u,
                val2: ((uint)(region.Width * width))
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: ((uint)(region.Height * height))
            )
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
                    return (WorldEntityPartResolver.TryPackedPose(
                        client: m_client,
                        stamps: m_animator,
                        entityIndex: part.Index,
                        partId: part.PartId,
                        transforms: m_transforms,
                        pose: out var pose
                    )
                        ? (pose.Position, pose.Orientation, 0f)
                        : (Vector3.Zero, Quaternion.Identity, 0f)
                    );
                }
            case WorldAnchor.Placement placement:
                return (WorldAnchorGeometry.StaticPlacementPosition(
                    definition: m_client.Definition,
                    placementId: placement.PlacementId,
                    shapeId: placement.ShapeId
                ), Quaternion.Identity, 0f);
            case WorldAnchor.Group group: {
                    var (centroid, spread) = m_groupAnchors.Resolve(
                        key: row.Name,
                        group: group,
                        client: m_client,
                        maxPopulation: m_client.Definition.Population.Capacity,
                        deltaSeconds: deltaSeconds
                    );

                    return (centroid, Quaternion.Identity, spread);
                }
            default:
                return (Vector3.Zero, Quaternion.Identity, 0f);
        }
    }
    // Resolves a named authored camera into a CameraSnapshot framed in `region`: its anchor pose (entity/part/placement/
    // group, or null = world), motion, aim, lens, and group spread. Returns
    // false when the name resolves no camera row (a faulted layout slot renders nothing rather than a bogus view).
    private bool ResolveNamedCamera(string name, NormalizedRect region, uint width, uint height, float deltaSeconds, out CameraSnapshot camera) {
        camera = default;

        WorldCamera? found = null;

        foreach (var row in m_client.Definition.Cameras) {
            if (string.Equals(
                a: row.Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                found = row;

                break;
            }
        }

        if (found is not { } cameraRow) {
            return false;
        }

        var (basePosition, baseOrientation, spread) = ResolveCameraAnchorPose(
            deltaSeconds: deltaSeconds,
            row: cameraRow
        );
        var rig = WorldCameraRigCompiler.Compile(
            rig: cameraRow.Rig,
            spread: spread
        );
        var anchor = new SdfAnchor(
            Orientation: baseOrientation,
            Position: basePosition
        );
        var clock = new SdfCameraClock(
            PresentationSeconds: m_elapsedSeconds,
            AuthoritativeTick: m_simulation.Tick
        );

        var (eye, target, fieldOfView) = rig.Resolve(
            anchor: in anchor,
            clock: in clock
        );

        camera = CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: fieldOfView,
            viewportWidth: Math.Max(
                val1: 1u,
                val2: ((uint)(region.Width * width))
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: ((uint)(region.Height * height))
            )
        );

        return true;
    }
    private SdfScreenDecalFrame? ResolveScreenDecal(int index) {
        if (
            (m_binder.TextSourceAt(index: index) is not { } text) ||
            (m_text.Catalog is not { } catalog)
        ) {
            _ = m_screenDecalCache.Remove(key: index);

            return null;
        }

        if (
            m_screenDecalCache.TryGetValue(
            key: index,
            value: out var cached
        ) &&
            ReferenceEquals(
            objA: cached.Text,
            objB: text
        ) &&
            ReferenceEquals(
            objA: cached.Catalog,
            objB: catalog
        )
        ) {
            return cached.Frame;
        }

        var frame = WorldScreenTextDecal.Bake(
            catalog: catalog,
            text: text
        );

        m_screenDecalCache[index] = (Text: text, Catalog: catalog, Frame: frame);

        return frame;
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
            if (WorldDefinitionRows.FindSpawnPoint(
                spawnPoints: definition.SpawnPoints,
                id: name
            ) is { } spawn) {
                centroid += spawn.Position;
                resolved++;
            }
        }

        if (resolved > 0) {
            centroid /= resolved;
        }

        // A plain elevated pull-back — an establishing shot over the plaza, not a chase rig; the exact offset is
        // an arbitrary but fixed presentation choice, not a value anything downstream depends on.
        var target = (centroid + new Vector3(
            x: 0f,
            y: 1f,
            z: 0f
        ));
        var eye = (centroid + new Vector3(
            x: 0f,
            y: 14f,
            z: 18f
        ));

        return CameraSnapshot.LookAt(
            position: eye,
            target: target,
            fieldOfViewRadians: (MathF.PI / 3f),
            viewportWidth: Math.Max(
                val1: 1u,
                val2: width
            ),
            viewportHeight: Math.Max(
                val1: 1u,
                val2: height
            )
        );
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
        var depth = Vector3.Dot(
            vector1: delta,
            vector2: camera.Forward
        );

        if (depth < 0.05f) {
            return false;
        }

        var ndcX = (Vector3.Dot(
            vector1: delta,
            vector2: camera.Right
        ) / ((depth * camera.TanHalfFieldOfView) * camera.AspectRatio));
        var ndcY = (Vector3.Dot(
            vector1: delta,
            vector2: camera.Up
        ) / (depth * camera.TanHalfFieldOfView));

        if (
            (MathF.Abs(x: ndcX) > 1.5f) ||
            (MathF.Abs(x: ndcY) > 1.5f)
        ) {
            return false;
        }

        var regionHeight = (region.Height * height);

        px = ((region.X * width) + ((0.5f + (0.5f * ndcX)) * (region.Width * width)));
        py = ((region.Y * height) + ((0.5f - (0.5f * ndcY)) * regionHeight));
        pixelsPerUnit = ((regionHeight * 0.5f) / (depth * camera.TanHalfFieldOfView));

        return true;
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
        return m_composed.CaptureFrame(
            deltaSeconds: deltaSeconds,
            height: height,
            interpolationAlpha: interpolationAlpha,
            width: width
        );
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
        var programChanged = !ReferenceEquals(
            objA: program,
            objB: m_lastProgram
        );

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
                m_audio.SubmitCue(
                    eventToken: WorldAudioCue.SeatJoin,
                    site: m_client.Position(index: m_anchor.PerceivedBody(slot: slot))
                );
            }

            // A transferred seat remains the same roster participant and keeps the same composed layout slot.
            // The per-slot camera below resolves its routed body through WorldContinuum, so no presentation mode
            // changes at the authority boundary.
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
                // A camera-bearing slot: render the named authored camera into the rect (no seat pose / gizmo).
                if (ResolveNamedCamera(
                    camera: out var namedCamera,
                    deltaSeconds: deltaSeconds,
                    height: height,
                    name: cameraName,
                    region: region,
                    width: width
                )) {
                    m_views.Add(item: new SdfViewSnapshot(
                        Camera: namedCamera,
                        Region: region
                    ) {
                        RenderScale = transitionScale,
                        UpscaleSharpness = m_settings.UpscaleSharpness,
                    });
                }

                continue;
            }

            // A seat slot: bind the seat at this slot's order among the joined seats.
            if (((uint)composed.SeatOrder) >= ((uint)joinedRosterCount)) {
                continue;
            }

            var slot = joinedRosterSlots[composed.SeatOrder];
            var camera = ResolveCamera(
                deltaSeconds: deltaSeconds,
                eye: out var eye,
                height: height,
                interpolationAlpha: interpolationAlpha,
                region: region,
                slot: slot,
                target: out var target,
                width: width
            );

            // The live render-scale tier rides each view's own RenderScale: native = 1.0 is the bit-exact fast path,
            // any lower tier renders that view's SDF at a reduced extent and upsamples. A layout transition dips it.
            m_views.Add(item: new SdfViewSnapshot(
                Camera: camera,
                Region: region
            ) {
                RenderScale = (m_settings.RenderScale * transitionScale),
                UpscaleSharpness = m_settings.UpscaleSharpness,
            });
            // The listener-policy candidate: the SAME resolved rig the seat renders through (editor rig included),
            // so "focus" listens where the active view looks.
            m_seatCameraPoses[slot] = new WorldSeatCameraPose(
                Eye: eye,
                Forward: (target - eye),
                Joined: true
            );
            // The cursor feed's unproject seam: the SAME region + camera this view renders with, so a cursor ray
            // aims exactly where the pixel under it was drawn from.
            m_viewports.Publish(
                camera: in camera,
                height: height,
                region: region,
                slot: slot,
                width: width
            );

            // The speaker gizmos: EDITOR-MODE-ONLY chips at each speaker's resolved pose, projected through
            // the SAME camera this seat renders with. Pending drag rows compose over the delivered truth, so a
            // dragged chip tracks its snapped position live.
            if (m_editor.IsEditing(slot: slot)) {
                gizmoSpeakers ??= m_drag.ComposeSpeakers(live: m_client.Definition.Speakers);
                m_gizmoSeats[gizmoSeatCount++] = ComposeGizmoSeat(
                    camera: in camera,
                    height: height,
                    region: region,
                    slot: slot,
                    speakers: gizmoSpeakers,
                    width: width
                );
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

            m_views.Add(item: new SdfViewSnapshot(
                Camera: ResolveSpectatorCamera(
                    height: height,
                    width: width
                ),
                Region: new NormalizedRect(
                    Height: 1f,
                    Width: 1f,
                    X: 0f,
                    Y: 0f
                )
            ) {
                RenderScale = m_settings.RenderScale,
                UpscaleSharpness = m_settings.UpscaleSharpness,
            });
        } else {
            m_noLocalSeatsNarrated = false;
        }

        // Published EVERY frame: an empty frame clears the chips the moment no seat edits.
        m_gizmos.Publish(frame: new OverlayGizmoFrame(Seats: m_gizmoSeats.AsMemory(
            length: gizmoSeatCount,
            start: 0
        )));

        // Publish this frame's audio snapshot AFTER the transforms are packed and the view rigs resolved: emitter
        // poses read the packed leaf transforms; the listener reads the seat cameras once per produced
        // frame, from the produce path where render poses are already resolved. The presentation delta ages the
        // transient cue pool (visual-only clock use — audio is presentation).
        _ = m_audio.Publish(
            deltaSeconds: deltaSeconds,
            seats: m_seatCameraPoses,
            transforms: transforms
        );

        // Stashed on the way out (see m_dressedFrame): RenderViews runs LATER in the same produced frame and hands
        // this exact instance to every offscreen view, which derives its own submission from it. Returning it without
        // keeping it is what left the views building their own.
        return m_dressedFrame = new SdfFrame(
            Program: program,
            ProgramChanged: programChanged,
            Time: m_elapsedSeconds,
            Views: m_views,
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
            SampleIndex = ((uint)m_simulation.ElapsedTicks),
            ShadowDistanceScale = ((m_settings.ShadowReach >= 1f)
            ? 0f
            : m_settings.ShadowReach),
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
    public void NotifyDeviceLost() {
        m_binder.NotifyDeviceLost();
    }
    /// <inheritdoc/>
    public void PrepareScreenSources(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        // Render + upload every CPU-fed screen for this frame off the sim tick advanced during CaptureFrame, so the
        // provider polled just after this call returns a handle to THIS frame's image. The engine seam calls this
        // AFTER capture and BEFORE the source poll.
        m_binder.Publish(
            tick: m_simulation.Tick,
            elapsedTicks: m_simulation.ElapsedTicks,
            deviceContext: deviceContext,
            gpu: gpu
        );
    }
    /// <inheritdoc/>
    public void RenderViews(in Puck.Hosting.FrameContext context) {
        // The frame context's target extent IS the launcher's live client area (window.Width/Height at this frame's
        // BeginFrame) — the one place the World side can learn it. Published for the cursor feed's client→frame
        // mapping (see WorldCursorFeed.Decide); the per-seat views above carry the FIXED frame extent instead.
        m_viewports.PublishClientExtent(
            width: context.TargetWidth,
            height: context.TargetHeight
        );

        // Render this frame's jumbotron views (the View screens) against the live device, feeding each the SAME world
        // program / dynamic transforms / content clock the room renders with, so a jumbotron shows this world from its
        // placeable camera. Called AFTER PrepareScreenSources (the CPU-fed screens the views sample are already this
        // frame's) and BEFORE the source poll (so a View screen's provider returns this frame's offscreen render).
        // The dressed frame is required, not optional: an offscreen view DERIVES its submission from it, so a frame
        // this produced frame never dressed has nothing to derive from and the views hold their last resolved image
        // for one frame rather than rendering off a fabricated one.
        if (
            (m_program is not { } program) ||
            (m_dressedFrame is not { } hostFrame)
        ) {
            return;
        }

        m_binder.RenderViews(
            context: in context,
            program: program,
            revision: m_programRevision,
            transforms: m_transforms,
            time: m_elapsedSeconds,
            authoritativeTick: m_simulation.Tick,
            hostFrame: hostFrame
        );
    }

    /// <summary>The frozen transform-slot count: every leaf in the all-128 avatar catalog plus the reserved
    /// animated-placement replay pool.</summary>
    public int DynamicTransformCapacity { get; }
    /// <inheritdoc/>
    public SdfGlyphAtlas? GlyphAtlas => m_text.GlyphAtlas;
    /// <summary>The worst-case (all avatars active) instance count — the spec's <c>InstanceCapacity</c> floor.</summary>
    public int InstanceCapacity { get; }
    /// <summary>The worst-case (all avatars active) program word count — the spec's <c>ProgramWordCapacity</c> floor.</summary>
    public int ProgramWordCapacity { get; }
    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenDecalFrame?>>? ScreenDecals => ((m_screenDecals.Count > 0)
        ? m_screenDecals
        : null
    );
}
