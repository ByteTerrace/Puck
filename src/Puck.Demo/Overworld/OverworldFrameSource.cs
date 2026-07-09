using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Puck.Assets;
using Puck.Compositing;
using Puck.Demo.Configuration;
using Puck.Demo.Creator;
using Puck.Demo.World;
using Puck.HumbleGamingBrick;
using Puck.Input.Devices;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// Bridges the deterministic <see cref="OverworldWorld"/> to the SDF renderer. It builds the static room program (the
/// floor, four walls, the console stands with their diegetic screen surfaces, the cartridge shelf, and one box per
/// FIXED player/cartridge/control slot at a dynamic-transform slot) and, each frame, emits the slots' current
/// transforms (which the renderer uploads into the dynamic-transform buffer) plus the screen director's view list.
/// Everything is rendered relative to the room's SPAWN ANCHOR — the room is bounded (±8 world units), so a FIXED
/// anchor keeps every float small AND immovable; an anchor that followed the players (the unbounded-world pattern)
/// flipped across its snap grid at the room's center lines and made the smoothed camera race the jump. The program
/// rebuilds when a console boots (its screen material lights up) or the creator scene's program content changes;
/// rebuilt programs may vary in size below the WORST-CASE envelope this source probes at render assembly
/// (<see cref="MeasureWorstCaseEnvelope"/> — the engine's buffers reserve that ceiling up front). A player,
/// cartridge, or control moves purely by changing the per-frame dynamic-transform buffer (a free player slot's box
/// rides a hidden position) and the per-frame view regions.
/// </summary>
public sealed partial class OverworldFrameSource : ISdfFrameSource, IOverworldControlHost {
    // The default per-console accent palette, by console index — matching the default document's dmg/cgb/agb
    // console order. Presentation only; see ConsoleAccentPalette for the shared source.
    private static readonly Vector3[] DefaultAccents = [
        ConsoleAccentPalette.Dmg,
        ConsoleAccentPalette.Cgb,
        ConsoleAccentPalette.Agb,
    ];

    // The GB screen's native aspect (160×144 = 10:9) — the diegetic screen slab's visible face is sized close to it
    // so sampled pixels aren't grossly stretched.
    private const float ScreenAspect = (160f / 144f);
    // Per-console control-cluster piece count and their slot offsets within a console's 3-slot block (d-pad, A, B).
    private const int ControlsPerConsole = 3;
    private const int DPadControlOffset = 0;
    private const int AButtonControlOffset = 1;
    private const int BButtonControlOffset = 2;
    // How far a pressed control depresses into its pedestal (world units) — small enough to read as a press, not a
    // collapse.
    private const float ControlDepress = 0.03f;
    // The d-pad's tilt when a direction is held (radians) — cheap per-direction feedback without separate arms.
    private const float DPadTiltRadians = (12f * (MathF.PI / 180f));
    // The overworld MOOD: the room runs dim so each booted cabinet's diegetic screen glow becomes the dominant light —
    // the whole point of the CRT room. Presentation-only per-frame scales on the world path's ambient + sun terms.
    private const float OverworldAmbientScale = 0.42f;
    private const float OverworldSunScale = 0.5f;
    // THE STUDIO backdrop (the --scenario review harness, ScenarioBackdrop.Studio): the workpiece alone against the
    // renderer's dark neutral sky, lit FLAT and BRIGHT so the palette reads true — the whole point of a review shot.
    // The ambient (hemisphere) term is lifted well past its room value for even fill on every face; the sun stays near
    // its natural weight so form still reads and the orbit's eight angles remain visibly distinct, but never dramatic.
    // Constant, wall-clock-free scales (byte-identical every run) that DON'T ride roomLight/daylight — the room mood
    // dampers are exactly what studio replaces.
    private const float StudioAmbientScale = 1.7f;
    private const float StudioSunScale = 0.6f;
    // The studio CYCLORAMA: a large neutral mid-gray shell centered on the workbench that the orbit camera sits well
    // inside — every ray that misses the workpiece lands on its uniform inner wall, so the backdrop reads as a
    // deliberate flat-gray field (with the hemisphere ambient's gentle top-to-bottom falloff) at all eight angles
    // instead of the near-black sky void. Radius is far past the ~6.5-unit orbit so the wall never crowds the subject;
    // the mid-gray albedo separates a light-gray creature without the blowout a near-white field would cause.
    // A neutral mid-gray floor sweep sits just below the workpiece — a half-space PLANE always renders for a camera
    // above it (the room's own floor uses exactly this primitive), so it is robust from every orbit angle where the
    // enclosing-shell trick fought the tile cull. The gray fills the lower frame and the pitch-down orbit keeps the
    // subject reading against it; the dark sky remains above, giving the deliberate flat-lit studio sweep. The albedo
    // is a true mid-gray so a light creature still separates without the blowout a near-white field would cause.
    private static readonly Vector3 StudioBackdropAlbedo = new(0.55f, 0.56f, 0.58f);
    // How far below the workbench floor the sweep plane sits (a hair under, so a creature resting near the floor never
    // z-fights the sweep).
    private const float StudioFloorDrop = 0.05f;
    // CREATOR MODE (the in-engine SDF authoring surface): the model + emission live in Puck.Demo.Creator
    // (CreatorScene/CreatorSceneRenderer); this source keeps only the slot layout and delegates. The pool's
    // dynamic-transform slots are present from frame 0 (hidden below the floor when unused), and the engine's
    // program/instance buffers are sized against the renderer's probed WORST CASE (MeasureWorstCaseEnvelope), so an
    // authored rebuild never has to grow a GPU buffer.
    // The horizontal half-extent of the authoring workbench around the room's center — authoring stays inside this
    // region (also the workpiece camera's orbit target and the composition groups' static bound source).
    private const float WorkbenchHalfExtent = 4f;

    // The diegetic link cable (whimsy): two thin dark capsules sagging from each linked cabinet's screen-top to a
    // low midpoint, plus a small sphere marking the sag — SmoothUnion INSIDE this one static instance only (the
    // cull-safety contract: never smooth-blend across an instance boundary you want maskable).
    private const float CableRadius = 0.025f;
    private const float CableSagRadius = 0.05f;
    private const float CableSagDrop = 0.6f;
    private const float CableSmooth = 0.04f;

    // THE DIEGETIC WORKBENCH (Stage 3 of the self-editing arc): a room-only prop — a desk with a terminal screen panel
    // — that reveals the world is EDITABLE. DARK/powered-off by default; when the editor reveal fires (the meta-victory
    // "complete X games", or the `reveal editor` verb) it POWERS ON — its panel lights with an emissive CRT glow eased
    // in over a transition, so it reads as "the workshop opens." Room-only because the four view slots are all spoken
    // for (room + 3 cabinet panes) — the workbench is never a paned cabinet, only an SDF prop in the room scene.
    private const float WorkbenchDeskHalfWidth = 0.7f;   // the desk slab's half-width (X)
    private const float WorkbenchDeskHalfDepth = 0.4f;   // half-depth (Z) — the player faces its −X front from the room
    private const float WorkbenchDeskHalfHeight = 0.06f; // a thin slab desktop
    private const float WorkbenchDeskTopY = 0.85f;       // the desktop height above the floor
    private const float WorkbenchLegHalfHeight = 0.4f;   // the two support legs' half-height (floor → under the desktop)
    private const float WorkbenchPanelHalfWidth = 0.5f;  // the terminal screen panel's half-width
    private const float WorkbenchPanelHalfHeight = 0.36f;// its half-height (a stout CRT terminal, ~10:7)
    private const float WorkbenchPanelHalfDepth = 0.06f; // the panel's thickness
    private const float WorkbenchPanelRiseY = 0.5f;      // how far the panel center sits above the desktop
    // The monitor tilts BACK (face up and toward the room) so its glowing face is caught both by a player approaching
    // at floor level AND by the high isometric reveal camera — a screen laid back like a drafting terminal.
    private const float WorkbenchPanelTiltRadians = (26f * (MathF.PI / 180f));
    private const float WorkbenchInstanceRadius = 1.5f;  // covers desk + legs + panel (the farthest member ~1.4 up-left)
    // The lit panel's peak emissive strength (albedo * emissive adds self-illumination, town-lamp style) — sized so a
    // fully-powered workbench reads clearly as the one lit object in the dim room, its CRT teal still showing (a higher
    // value blows the face to flat white).
    private const float WorkbenchLitEmissive = 1.5f;
    // How fast the power-on glow eases in once EditorRevealed latches (per second) — ~0.7 s to full, matching the
    // reveal's transition feel. The program rebuilds while the glow ramps (a handful of rebuilds, exactly a boot's
    // cost profile); once at 0 or 1 it is settled and stops rebuilding. Quantized into buckets for the rebuild key so
    // the ramp rebuilds a bounded number of times, never once per frame indefinitely.
    private const float WorkbenchGlowRate = 1.45f;
    private const int WorkbenchGlowBuckets = 12;

    /// <summary>The engine's screen-surface slot capacity (mirrors <see cref="SdfProgramBuilder.MaxScreenSurfaces"/>
    /// as a primitive) — exposed so the render node, at its analyzer coupling ceiling, never needs to name
    /// <see cref="SdfProgramBuilder"/> itself just to bound its generic headroom loop.</summary>
    public const int MaxScreenSurfaceCount = SdfProgramBuilder.MaxScreenSurfaces;

    private readonly OverworldWorld m_world;
    // Mutable (not readonly): a committed world load swaps the RENDERED room in the same consume that hands the sim
    // its new collision (TryConsumePendingWorldLoad) — the walls the player sees and the walls the sim enforces
    // change together. m_baseRoom keeps the code-built original (console count/layout) FromWorld layers onto.
    private OverworldRoom m_room;
    private readonly OverworldRoom m_baseRoom;
    private readonly ScreenLayoutDirector m_director;
    // The last committed world document this source applied (reference identity — records are immutable).
    private WorldDocument? m_appliedWorldCommit;
    // The creating slot's rendered position this frame (the world-sculpt camera's anchor).
    private Vector3 m_primaryPlayerRenderPosition;
    // The companion pool (presentation-only creatures) — composed here like the creator/world trios.
    private readonly CompanionRoster m_companions = new();
    private readonly CompanionRenderer m_companionRenderer;
    private readonly int m_companionSlotBase;
    // The roster size the current program was built for (add/del = a structural rebuild; the slot topology is
    // constant, only the reserved slots' content changes).
    private int m_builtCompanionCount;
    private readonly IReadOnlyList<Vector3> m_consoleAccents;
    // Reads the buttons currently applied to a console's machine this frame (GamingBrickChildNode.CurrentButtons) —
    // the SAME per-frame joypad state the machine consumes, never re-derived from raw input. Null (bare-room mode,
    // no consoles) means every control cluster stays in its neutral pose.
    private readonly Func<int, JoypadButtons>? m_controlsSource;
    // Reused render-relative position buffer for the screen director — Cleared+refilled each frame (no per-frame alloc).
    private readonly List<Vector3> m_activePositions = new(capacity: OverworldWorld.MaxPlayers);
    // The unified dynamic-transform layout: players first (fixed MaxPlayers slots, OverworldWorld's own numbering),
    // then one slot per unified cartridge index, then ControlsPerConsole slots per console stand. Computed once
    // (cartridge/console counts never change after construction) and reused every frame — no per-frame allocation.
    private readonly int m_controlSlotBase;
    // The creator pool's first dynamic-transform slot: the GHOST rides m_creatorSlotBase, and placed shape i rides
    // m_creatorSlotBase + 1 + i (see the creator-mode section below).
    private readonly int m_creatorSlotBase;
    private readonly int m_dynamicTransformCount;
    private readonly DynamicTransform[] m_dynamicTransforms;
    private SdfProgram? m_program;
    // The boot mask the current program's screen materials were chosen under; a boot rebuilds the program.
    private uint m_programBootedMask;
    // The set of OCCUPIED player slots the current program parked/unparked against (bit s = slot s has a player). An
    // empty slot's player box is PARKED so the beam cull skips it; a join/leave flips a bit and rebuilds the program
    // (one envelope-safe rebuild, exactly like a boot). -1 forces the first build.
    private uint m_builtActivePlayerMask = 0xFFFFFFFFu;
    // The scene's ProgramRevision the current program was built from; an authoring edit that changes the program's
    // content moves it, and CaptureFrame rebuilds (a creator MOVE never does — it rides the dynamic transforms).
    private int m_builtProgramRevision = -1;
    private float m_time;
    // THE WORKBENCH power-on glow (Stage 3): 0 = dark/locked, 1 = fully lit. It eases toward the target the editor
    // unlock implies (EditorRevealed → 1) on the render clock in CaptureFrame — presentation only, never hashed. The
    // program's workbench panel bakes an emissive proportional to this, so the ramp rebuilds the program (like a boot)
    // as it crosses quantized buckets; m_builtWorkbenchBucket is the bucket the current program baked, so a settled
    // glow (bucket unchanged) never rebuilds. The FALLBACK bucket (-1) forces the first build.
    private float m_workbenchGlow;
    private int m_builtWorkbenchBucket = -1;
    // CREATOR-MODE presentation state (never hashed — the deterministic sim knows nothing of it): the authored scene
    // model, its program/transform emitter, and the pad state machine that edits it. This source is the composition
    // point for the creator objects — the render node drives them through thin forwarders so its own type coupling
    // stays flat while the editor grows.
    //
    // W-SEAM note: BuildProgram reads these composed sub-objects (m_creator, m_worldScene, m_director) as direct
    // fields, NOT through the ScreenSlotLedger token-claimant seam — deliberately. The claimant seam is for a
    // subsystem OUTSIDE this source attaching screen content without editing it; these ARE this source's own owned
    // composition, read to build its own program. Routing them through a token registry would be indirection for its
    // own sake (a field this source owns, re-fetched by an opaque key it also owns), not decoupling. The screen SLABS
    // these emit (the easel's preview, a world.wire'd cabinet) still arbitrate through the ledger; only their content,
    // owned right here, is read directly.
    private readonly CreatorScene m_creator;
    private readonly CreatorSceneRenderer m_creatorRenderer;
    private readonly CreatorController m_creatorController;
    // The live bake-preview seam the easel's screen slab samples — NULL until the bake pipeline's service replaces it
    // (ConnectBakePreview); a null seam reads as a powered-off easel (handle 0 / zero glow), same as the former
    // NullCreatorBakePreview null-object. Kept nullable (not a null-object) so this source names one fewer type — it
    // sits at its exact analyzer coupling ceiling, and the SDF-debug facade needs the room.
    private ICreatorBakePreview? m_bakePreview;
    // The owned live bake service, when installed (InstallBakePreview) — this source composes it so the render
    // node's coupling stays flat.
    private Forge.Bake.BakePreviewService? m_bakePreviewService;
    // The scenario capture driver, when a --scenario is active (InstallScenario) — this source composes it (like the
    // bake preview) so the render node, at its class-coupling ceiling, never names the scenario types. The node drives
    // it through ScenarioTick each produced frame (settle-then-capture); the director holds the verbatim shot pose and
    // returns a path when a settled shot should arm its one-shot capture (see ScenarioTick / ScenarioComplete).
    private CaptureSequencer? m_captureSequencer;
    // The installed scenario's backdrop (InstallScenario). Studio drops the room/cabinet/shelf content in BuildProgram
    // and lights the scene flat + bright in CaptureFrame; false (the default, and every non-scenario run) keeps the
    // dim arcade-room framing. Never toggled after install — a scenario's backdrop is fixed for the run.
    private bool m_scenarioStudio;
    // The probed worst-case capacity envelope (computed once on first use — see MeasureWorstCaseEnvelope).
    private (int Words, int Instances)? m_worstCase;
    // The world sculptor's composition (scene/renderer/controller/history/store) — see the ctor's trio comment.
    private readonly ContentAddressedStore m_worldStore;
    private readonly WorldScene m_worldScene;
    private readonly EditHistory<WorldScene.Snapshot> m_worldHistory;
    private readonly WorldSceneRenderer m_worldRenderer;
    private readonly WorldSculptController m_worldController;
    private readonly int m_worldSlotBase;
    private int m_builtWorldRevision = -1;
    private bool m_worldSculptActive;

    // SDF-DEBUG mode (the fullscreen single-shape debug tool): the whole mode (scene + orbit controller + emitter) is
    // composed behind ONE facade type — this source is at its exact analyzer coupling ceiling and cannot name three
    // (it sheds NullCreatorBakePreview's reference to make room; see m_bakePreview). Presentation only — the sim never
    // sees it. While the mode is up the program REPLACES the room with the debug subject (BuildProgram's early branch);
    // the capacity probe folds the subject's worst case in (EmitProbe), so a live op push can never outgrow the buffers.
    private readonly Puck.Demo.SdfDebug.SdfDebugMode m_sdfDebug = new();
    private int m_builtSdfDebugRevision = -1;

    // The diegetic-feed director (the camera-feed pool + the procedural face feed + the named-feed registry) — this
    // source owns and drives it (InstallFeeds composes it once the render assembly's envelope is known); null until
    // then (a bare-room source with no feed wiring pays nothing). It is the WS-12 wiring of the landed camera primitive
    // into the live render path.
    private CameraFeedPool? m_feeds;
    // The program-affecting feed state the LAST rebuild was built for: the SET of world+creation camera-feed NAMES the
    // wiring wants live. It does not change the program's instruction stream (feeds render into offscreen slabs the
    // program already declares), so it is NOT a rebuild trigger — this is stashed only so PlanFeeds runs once per frame.
    private int m_builtFeedRevision = -1;
    // A reusable owner token for the world/creation camera-feed claimants' ledger participation (one token covers ALL
    // camera feeds this source publishes — they share the headroom band, and the render node reads their handles by
    // name, not by which slot each landed on). Reference-stable (boxed once).
    private readonly object m_cameraFeedClaimToken = new();
    // The screen indices a world.wire / creation-face wired to a camera feed or named feed this frame (screen index ->
    // feed name). Recomputed each CaptureFrame; the render node's per-slot override consults it (a wired slot samples
    // the named-feed registry instead of its cabinet brick / flat material).
    private readonly Dictionary<int, string> m_wiredFeedByScreen = [];
    // Reused scratch for PlanDiegeticFeeds — cleared and refilled each frame instead of allocating fresh collections
    // (the plan reruns every produced frame because a companion's live shape pose feeds each request, but these two
    // CONTAINERS need not churn the GC). WiredScreenSet still returns a fresh per-request set — those are retained a
    // frame inside each PlannedFeed, so they cannot share one scratch instance.
    private readonly List<CameraFeedRequest> m_feedRequestScratch = new(capacity: CameraFeedPool.MaxCameraFeeds);
    private readonly Dictionary<string, int> m_feedByNameScratch = new(comparer: StringComparer.Ordinal);

    // ---- The screen mux (ledger arbitration + the link cable) ---------------------------------------------------
    // The 8-slot allocator: cabinets (0-3, preferred slots) and the creator easel's borrow (slot 3, preferred) claim
    // every frame exactly as before (see ResolveScreenMux). Slots 4-7 are headroom for future claimants registered
    // through RegisterScreenClaimant — no concrete claimant of this source's own lives there today.
    private readonly ScreenSlotLedger m_screenLedger = new();
    // Reference-stable owner tokens for the ledger's per-pass claims (boxed once, compared by reference every
    // Resolve — see ScreenSlotLedger.Claim's contract). One per console (by index), plus one for the easel.
    private readonly object[] m_cabinetClaimTokens;
    private static readonly object EaselClaimToken = new();
    // The program-affecting mux state the LAST rebuild was built for: the linked pair changing whether the cable
    // instance emits at all changes the PROGRAM's instruction stream, so it must join the rebuild trigger (mirrors
    // bootedMask/companion count above) — a mux change alone (same pair) never rebuilds.
    private (int A, int B)? m_builtLinkedPair;
    // This frame's VALIDATED linked pair (see ResolveScreenMux) — BuildProgram's EmitLinkCable reads this directly
    // rather than taking it as a parameter, mirroring how BuildProgram already reads m_creator.Active/m_worldScene.
    private (int A, int B)? m_currentLinkedPair;

    /// <summary>An optional primitive-typed reader of ONE end of the currently linked cabinet pair (a console index,
    /// or -1 for none) — set by the render node (a lambda over its own already-coupled link bookkeeping adds NO new
    /// type coupling to it). Two single-int properties instead of one tuple-returning one so the node's own
    /// delegate signature never spells a tuple type. Paired with <see cref="LinkedConsoleBSource"/>; the diegetic
    /// sagging cable emits only when BOTH resolve to distinct, valid console indices this frame.
    /// <para>This is a callback drill, but NOT a screen-attach one (see <see cref="ScreenSlotLedger"/>'s seam
    /// contract): it reports which cabinets are LINKED so the cable geometry emits — it puts no content on a screen
    /// surface — so the ledger/claimant seam is the wrong shape for it and it stays a direct <see cref="Func{T}"/>.</para></summary>
    public Func<int>? LinkedConsoleASource { get; set; }

    /// <summary>The link pair's other end (see <see cref="LinkedConsoleASource"/>).</summary>
    public Func<int>? LinkedConsoleBSource { get; set; }

    /// <summary>Initializes the frame source over a world, its room, and the screen director that lays out the views.</summary>
    /// <param name="world">The deterministic simulation to present.</param>
    /// <param name="room">The authored room (the same instance the world's collision derived from).</param>
    /// <param name="director">The screen director that lays out the room view + console panes.</param>
    /// <param name="consoleAccents">An optional per-console accent color (albedo), by console index — the host passes
    /// the document consoles' costume colors; null falls back to the built-in dmg/cgb/agb palette.</param>
    /// <param name="controlsSource">An optional per-console reader of the joypad buttons currently applied to that
    /// console's machine this frame (see <see cref="Demo.GamingBrickChildNode.CurrentButtons"/>) — drives the
    /// control-cluster press animation. Null keeps every cluster in its neutral pose (the bare-room path).</param>
    public OverworldFrameSource(OverworldWorld world, OverworldRoom room, ScreenLayoutDirector director, IReadOnlyList<Vector3>? consoleAccents = null, Func<int, JoypadButtons>? controlsSource = null) {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(director);

        m_world = world;
        m_room = room;
        m_baseRoom = room;
        m_director = director;
        m_consoleAccents = (consoleAccents ?? DefaultAccents);
        m_controlsSource = controlsSource;

        // No physical cartridge instances — the cart choice lives at the cabinet; the control clusters follow
        // the player slots directly.
        m_controlSlotBase = OverworldWorld.MaxPlayers;
        // The creator pool sits after the control slots: one ghost slot, then the placed-shape slots.
        m_creatorSlotBase = (m_controlSlotBase + (m_room.Consoles.Count * ControlsPerConsole));
        // The world sculptor's two slots (ghost stamp + selected drag) sit after the creator pool; the companion
        // pool (roots + shape slots) after those.
        m_worldSlotBase = (m_creatorSlotBase + CreatorSceneRenderer.DynamicSlotCount);
        m_companionSlotBase = (m_worldSlotBase + WorldSceneRenderer.DynamicSlotCount);
        m_dynamicTransformCount = (m_companionSlotBase + CompanionRenderer.DynamicSlotCount);
        m_dynamicTransforms = new DynamicTransform[m_dynamicTransformCount];

        // One reference-stable ledger claim token per console (see ScreenSlotLedger.Claim's ref-equality contract).
        m_cabinetClaimTokens = new object[m_room.Consoles.Count];
        for (var index = 0; (index < m_cabinetClaimTokens.Length); index++) {
            m_cabinetClaimTokens[index] = new object();
        }

        // The authoring workbench: a bounded region around the room's center (never near the stands along the far
        // wall), at the SAME floor height the room renders at.
        var workbench = new WorkbenchRegion(
            Center: new Vector3(
                (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
                m_room.FloorY,
                (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
            ),
            HalfExtent: WorkbenchHalfExtent,
            MaxY: (m_room.FloorY + 3.0f),
            MinY: (m_room.FloorY + 0.35f)
        );

        m_creator = new CreatorScene(workbench: workbench);
        m_creatorRenderer = new CreatorSceneRenderer(scene: m_creator, slotBase: m_creatorSlotBase);
        m_creatorController = new CreatorController(narrate: static line => Console.Error.WriteLine(value: line), scene: m_creator);
        // The workpiece camera: while creator is up, the room view leaves the player chase for the controller's
        // orbit/head-on framing; while WORLD-SCULPT is up, a lifted town-read orbit anchored on the creating slot
        // (the player IS the cursor — steep pitch, generous distance, so a street reads while stamping). Both ride
        // the director's one eased CreatorCameraSource seam (this source is the composition point).
        m_director.CreatorCameraSource = () => (AgbDebugCameraFrame() ?? m_sdfDebug.CameraFrame ?? m_creatorController.CameraFrame ?? WorldSculptCameraFrame());
        // The perf bench's pose is a MEASUREMENT pose: applied verbatim (no easing) so every configuration samples an
        // identical, fully settled framing — see SdfDebugMode.CameraSnaps.
        m_director.CreatorCameraSnapSource = () => m_sdfDebug.CameraSnaps;
        // When a world (the town) is loaded, the fourth-wall reveal frames the WHOLE lot rather than the fixed
        // default-room overview — centred on the lot, pulled back to its bounds. Null while no world is applied, so
        // the default room's reveal framing is byte-unchanged.
        m_director.RoomFramingSource = () => ((m_appliedWorldCommit is null)
            ? (ScreenLayoutDirector.RoomFraming?)null
            : new ScreenLayoutDirector.RoomFraming(
                Center: new Vector3((0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)), (m_room.FloorY + 0.5f), (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))),
                HalfDepth: (0.5f * (m_room.BoundsMax.Y - m_room.BoundsMin.Y)),
                HalfWidth: (0.5f * (m_room.BoundsMax.X - m_room.BoundsMin.X))
            ));
        // Drive each pane camera from its cabinet's diegetic-screen center + the overworld's per-console closeness. The
        // screen center is a fixed room-local position; render-relative equals local here (the render origin is the
        // spawn anchor at local zero), so it shares the space of the active player positions the director frames.
        // Assigned ONCE: the lambda resolves current instance state (m_room, PaneCloseness) at INVOKE time (each
        // Compose), so it never needs per-frame recreation — a fresh capturing delegate every frame was pure churn.
        m_director.PaneCameraSource = paneIndex => ((paneIndex >= 0) && (paneIndex < m_room.Consoles.Count)
            ? (ScreenCenterLocal(consoleIndex: paneIndex), (PaneCloseness?.Invoke(paneIndex) ?? 0f), ScreenHalfHeightLocal(consoleIndex: paneIndex))
            : ((Vector3, float, float)?)null);

        // The WORLD SCULPTOR: the scene/renderer/controller trio mirrors the creator pool's composition exactly —
        // this source is the composition point, the node only toggles the mode and forwards the creating slot's pad.
        // One shared CAS store (cwd-relative, the worlds/creations/tunes sibling) backs placement resolution and the
        // world.* verbs' saves.
        m_worldStore = new ContentAddressedStore(root: "store");
        m_worldScene = new WorldScene();
        m_worldHistory = new EditHistory<WorldScene.Snapshot>(capacity: 64, initial: m_worldScene.CaptureSnapshot());
        m_worldRenderer = new WorldSceneRenderer(scene: m_worldScene, slotBase: m_worldSlotBase, store: m_worldStore);
        m_worldController = new WorldSculptController(
            history: m_worldHistory,
            narrate: static line => Console.Error.WriteLine(value: line),
            scene: m_worldScene,
            store: () => new WorldSculptController.ContentAddressedStoreHandle(Store: m_worldStore)
        );
        // The deliberate-save bake: the walk grid is derived from the document's own content (+ the room's stands)
        // and ships INSIDE the saved bytes — see BakeWorldForSave.
        m_worldScene.PrepareForSave = BakeWorldForSave;

        m_companionRenderer = new CompanionRenderer(roster: m_companions, slotBase: m_companionSlotBase);
        // Companions roam the whole ROOM (not the workbench pedestal) — a generous band above the floor, inside
        // the walls.
        CompanionBounds = new WorkbenchRegion(
            Center: new Vector3(
                (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
                m_room.FloorY,
                (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
            ),
            HalfExtent: (0.5f * MathF.Max(x: (m_room.BoundsMax.X - m_room.BoundsMin.X), y: (m_room.BoundsMax.Y - m_room.BoundsMin.Y)) - 1.2f),
            MaxY: (m_room.FloorY + 3.5f),
            MinY: (m_room.FloorY + 0.4f)
        );

    }

    // The boot-time world load: when the run document names a world handle (OverworldNode.World), resolve it from
    // ./worlds/ (or a direct path) plus the CAS store and COMMIT it, so the first tick-boundary ConsumePendingWorldLoad
    // swaps the room, collision, and walk grid to that sculpted world BEFORE the player ever moves — the town is
    // already live at boot; the fourth-wall reveal reframes the camera OUT of the intro machines and INTO that
    // already-loaded world (rung 2 of the unification contract's reveal ladder — the world the DATA FILE defines).
    // Mirrors WorldCommands.Load's resolve-then-commit exactly, but GRACEFUL at boot: an unreadable handle or a
    // partially-resolved world (a creation missing from the store) narrates to stderr and leaves the plain room,
    // rather than crashing the boot (the bit-for-bit doctrine still forbids a PARTIAL load — it's all or nothing,
    // just non-fatal here). The live mid-session equivalent is the world.load console verb.
    private void LoadBootWorld(string? handle) {
        if (string.IsNullOrWhiteSpace(value: handle)) {
            return;
        }

        if (!WorldDocumentStore.TryLoad(nameOrPath: handle, out var bootDocument, out var loadError)) {
            Console.Error.WriteLine(value: $"[boot-world: '{handle}' is unreadable ({loadError}) — the room stays plain]");

            return;
        }

        if (bootDocument is not { } document) {
            Console.Error.WriteLine(value: $"[boot-world: nothing readable at '{handle}' — the room stays plain]");

            return;
        }

        _ = WorldDocumentStore.TryResolvePlacementSources(document: document, missing: out var missing, store: m_worldStore);

        if (missing.Count > 0) {
            Console.Error.WriteLine(value: $"[boot-world: '{handle}' refused — {missing.Count} placement(s) reference creations missing from the store (ids: {string.Join(separator: ", ", values: missing)}); the room stays plain]");

            return;
        }

        var loaded = m_worldScene.LoadDocument(document: document, store: m_worldStore);

        m_worldScene.MarkCommitted(document: document);
        Console.Error.WriteLine(value: $"[boot-world: '{document.Name}' — {loaded} placement(s) applying on the first tick]");
    }

    /// <summary>The ROOM view's normalized region as of the most recent <see cref="CaptureFrame"/> — the rect the
    /// screen director gave view 0 this frame (fullscreen before any console boots, shrinking through the staged
    /// layouts as panes light). The binding-bar overlay confines its cluster to it, so the controls hug the room
    /// view rather than painting across the console panes.</summary>
    public NormalizedRect LastRoomRegion { get; private set; } = new(X: 0f, Y: 0f, Width: 1f, Height: 1f);

    /// <summary>An optional per-console CLOSENESS for the pane cameras (0 = the wide room framing, 1 = right up on the
    /// cabinet's diegetic screen so the brick fills the pane). The overworld sets it per mode — immersed panes tight,
    /// break-out panes a medium shot, hidden panes 0. Combined with each cabinet's screen center to drive the director's
    /// per-pane cameras. Presentation-only.</summary>
    public Func<int, float>? PaneCloseness { get; set; }

    /// <summary>An optional PRESENTATION-ONLY position override per player slot: when it returns a world position for a
    /// slot, that player's avatar renders there instead of at its simulation body. The switchboard's brick→world
    /// coupling uses it to make a driving player's avatar FOLLOW their brick sprite around the room — machine state
    /// moving a presentation transform, deliberately OUT of the hashed simulation. Null (or a null result) = the sim
    /// body, as always.</summary>
    public Func<int, WorldCoord3?>? PresentationOverride { get; set; }

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_time += deltaSeconds;

        // The world anchor is the room's spawn anchor, ALWAYS: the room is bounded, so every render-relative float
        // stays within a few units (precise no matter which cell the room sits in — subtracted in FIXED POINT before
        // the float cast), and the anchor NEVER moves mid-session (a moving anchor made the smoothed camera chase the
        // jump every time the players crossed its snap grid).
        var renderOrigin = m_world.SpawnAnchor;

        // Rebuild when a console boots (its screen material lights) or the creator scene's PROGRAM content changed
        // (a place/undo/primitive/scale/material edit). Rebuilt programs may vary in size below the worst-case
        // envelope the engine's buffers were sized against (MeasureWorstCaseEnvelope) — the once-sized buffers stay
        // valid by construction. A creator MOVE never rebuilds (it rides the dynamic-transform buffer).
        // Companions steer/animate on the render clock BEFORE packing (presentation only — the sim never sees them).
        // The companion face auto-tune's "preferred remote feed is live" probe: a companion drifts to its remote face
        // feed only when the host's named-feed registry is actually producing that feed this frame — never fish/lure
        // by name, just "is this companion's last face-feed name live" (see CompanionState.FaceFeeds).
        m_companions.Tick(deltaSeconds: deltaSeconds, nearestPlayerProvider: NearestActivePlayer, remoteFeedProbe: PreferredRemoteFeedLive);

        // The screen mux: resolves the ledger (cabinets/easel/any registered dynamic claimants) — BEFORE the program
        // rebuild check, since a claimed headroom slot or the link cable's pair can change what BuildProgram emits.
        var linkedA = (LinkedConsoleASource?.Invoke() ?? -1);
        var linkedB = (LinkedConsoleBSource?.Invoke() ?? -1);
        var linkedPair = (((linkedA >= 0) && (linkedB >= 0)) ? (linkedA, linkedB) : ((int A, int B)?)null);

        // Register the companion faces' screen claims + the world/creation camera-feed claim BEFORE ResolveScreenMux,
        // so the ledger arbitrates them with the cabinets/easel in one pass; then plan the camera feeds from the
        // resolved wiring. Presentation-only — never touches sim state.
        RegisterCompanionFaceClaims();
        ResolveScreenMux(linkedPair: linkedPair);
        PlanDiegeticFeeds();

        // Ease the workbench power-on glow toward the editor unlock (revealed → 1, else 0) on the render clock; a
        // crossed quantized bucket rebuilds the program (its panel bakes an emissive proportional to the glow), so the
        // power-on reads as an eased "the workshop opens" rather than a hard cut. Presentation only — never hashed.
        AdvanceWorkbenchGlow(deltaSeconds: deltaSeconds);

        var bootedMask = m_world.BootedMask;
        var activePlayerMask = ActivePlayerMask();
        var workbenchBucket = WorkbenchGlowBucket();
        var programChanged = ((m_program is null) || (bootedMask != m_programBootedMask) || (activePlayerMask != m_builtActivePlayerMask) || (workbenchBucket != m_builtWorkbenchBucket) || (m_creator.ProgramRevision != m_builtProgramRevision) || (m_worldScene.ProgramRevision != m_builtWorldRevision) || (m_companions.Companions.Count != m_builtCompanionCount) || !EqualLinkedPair(a: linkedPair, b: m_builtLinkedPair) || (m_sdfDebug.Active && (m_sdfDebug.Revision != m_builtSdfDebugRevision)));

        if (programChanged) {
            m_program = BuildProgram(bootedMask: bootedMask);
            m_programBootedMask = bootedMask;
            m_builtActivePlayerMask = activePlayerMask;
            m_builtWorkbenchBucket = workbenchBucket;
            m_builtProgramRevision = m_creator.ProgramRevision;
            m_builtWorldRevision = m_worldScene.ProgramRevision;
            m_builtCompanionCount = m_companions.Companions.Count;
            m_builtLinkedPair = linkedPair;
            m_builtSdfDebugRevision = m_sdfDebug.Revision;
            // The camera feeds share this program object; bump the feed revision only when it actually rebuilds, so the
            // feed engine re-uploads once per real program change, not every frame (Rebuild no-ops on an unchanged rev).
            m_builtFeedRevision++;
        }

        // Render-relative, alpha-interpolated player positions for the camera director (reused list — no per-frame alloc).
        // A presentation override (a player driving their brick, whose avatar follows the game sprite) wins over the sim
        // body, so the camera frames the followed avatar.
        m_activePositions.Clear();

        for (var slot = 0; (slot < m_world.Slots.Count); slot++) {
            if (m_world.Slots[slot] is not { } player) {
                continue;
            }

            m_activePositions.Add(item: ((PresentationOverride?.Invoke(slot) is { } overridden)
                ? overridden.ToRenderRelative(origin: renderOrigin)
                : player.Body.RenderRelativePositionAt(renderOrigin: renderOrigin, alpha: interpolationAlpha)));
        }

        // The pane cameras ride m_director.PaneCameraSource, assigned once in the constructor (it resolves live
        // instance state each Compose) rather than reassigned per frame.
        //
        // The scenario harness's per-frame pose is settled by the render node's ScenarioTick BEFORE this compose (the
        // node owns the capture-arm + graceful-exit seams; see ScenarioTick), so the director already holds this
        // frame's verbatim shot pose here. Nothing to advance in the source itself.
        var views = m_director.Compose(activePositions: m_activePositions, bootOrder: m_world.BootOrder, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds);

        // View 0 is always the room; stash its live rect for the binding-bar overlay (this runs INSIDE the producer's
        // ProduceFrame, so the overlay — which wraps the producer — always reads the region of the frame it draws over).
        LastRoomRegion = views[0].Region;

        PackDynamicTransforms(renderOrigin: renderOrigin, alpha: interpolationAlpha);

        // While immersed the room is UNLIT (RoomLightFactor 0), so the letterbox margins around a contained screen
        // render BLACK — a native handheld/emulator look — easing up to the arcade mood as the reveal lights the room.
        var roomLight = m_director.RoomLightFactor;

        // The grid-lock overlay channel (grid-locking §4): the ACTIVE editor writes it as primitives (no new type
        // named here — the scenes name the GridOverlayState facade, keeping this class under its coupling ceiling).
        // Outside an editor it stays all-zero (byte-identical upload to before the channel existed).
        var gridFloorY = m_room.FloorY;
        var gridFlags = 0u;
        var gridWorldPitch = Vector2.Zero;
        var gridObjectOrigin = Vector3.Zero;
        var gridObjectFrame = Quaternion.Identity;
        var gridObjectPitch = Vector2.Zero;
        var gridObjectPatchRadius = 0f;

        if (m_worldSculptActive) {
            m_worldScene.WriteGridOverlay(floorY: gridFloorY, flags: out gridFlags, worldPitch: out gridWorldPitch, objectOrigin: out gridObjectOrigin, objectFrame: out gridObjectFrame, objectPitch: out gridObjectPitch, objectPatchRadius: out gridObjectPatchRadius);
        } else if (m_creator.Active) {
            m_creator.WriteGridOverlay(floorY: gridFloorY, flags: out gridFlags, worldPitch: out gridWorldPitch, objectOrigin: out gridObjectOrigin, objectFrame: out gridObjectFrame, objectPitch: out gridObjectPitch, objectPatchRadius: out gridObjectPatchRadius);
        }

        return new SdfFrame(
            Program: m_program!, // non-null: programChanged is true whenever m_program was null, so it was just built
            ProgramChanged: programChanged,
            Views: views,
            // A scenario run PINS the rendered content/animation clock to the active shot's deterministic time (never the
            // wall-clock accumulator), so a time-animated creation renders identically regardless of the run's fps — the
            // byte-identical proof. Every non-scenario run keeps the live render clock.
            Time: ((m_captureSequencer is { } scenario) ? scenario.PinnedContentTime : m_time),
            WarpAmount: 0f
        ) {
            // A studio scenario lights the workpiece FLAT and BRIGHT (constant scales, no roomLight/daylight damper) so
            // the palette reads true; otherwise the sculpted world's DAYLIGHT dial rides the same presentation seam the
            // reveal's room-light does — at dusk the authored lamps' emissive materials carry the room (world.dusk).
            // SDF-debug pins the room full-bright (Ambient/Sun 1) so the lit/normals views read true against the
            // replaced-room debug subject; otherwise studio's flat lighting or the dim arcade mood applies.
            AmbientScale = ((m_sdfDebug.Active || m_agbActive) ? 1f : (m_scenarioStudio ? StudioAmbientScale : (OverworldAmbientScale * roomLight * m_worldScene.Daylight))),
            // The SLICE debug view's plane channel (two floats riding the frame's screen-light env lanes — see
            // SdfFrame.DebugSliceAxis): the sdf.slice verb positions an axis-aligned slice plane; the defaults (0)
            // are the camera-locked plane, so a run that never touches the verb uploads the same zeros as before.
            DebugSliceAxis = m_sdfDebug.SliceAxis,
            DebugSliceOffset = m_sdfDebug.SliceOffset,
            // The analytic-normal A/B lever (the sdf.normals verb): default false = the analytic forward-mode gradient
            // dual; true swaps back to the 4-tap finite-difference probe. A pure frame flag (no geometry), so it rides
            // the same per-frame channel as the slice lanes above.
            UseFiniteDifferenceNormals = m_sdfDebug.UseFiniteDifferenceNormals,
            DynamicTransforms = m_dynamicTransforms,
            // The grid-lock overlay channel (grid-locking §4), threaded into SdfFrame's Grid* fields exactly like the
            // slice lanes above (the active editor wrote the locals; all-zero outside an editor).
            GridFlags = gridFlags,
            GridFloorY = gridFloorY,
            GridObjectFrame = gridObjectFrame,
            GridObjectOrigin = gridObjectOrigin,
            GridObjectPatchRadius = gridObjectPatchRadius,
            GridObjectPitch = gridObjectPitch,
            GridWorldPitch = gridWorldPitch,
            SunScale = ((m_sdfDebug.Active || m_agbActive) ? 1f : (m_scenarioStudio ? StudioSunScale : (OverworldSunScale * roomLight * m_worldScene.Daylight))),
        };
    }

    // ---- The screen mux (ScreenSlotLedger arbitration) -------------------------------------------------------------
    // Ledger priority BANDS (see ScreenSlotPriority): Anchored (0) > Overlay (10) > Ambient (20). The settled
    // cabinet/easel borrow contract is preserved EXACTLY: a cabinet claims its own preferred index (Anchored) UNLESS
    // the easel is borrowing it this session (mirrors BuildProgram's prior slotBorrowed check) — the easel then
    // claims that exact preferred slot instead (Overlay), so the two can never both hold it.
    //
    // GENERIC EXTENSION SEAM: a future claimant (e.g. a placeable diegetic camera) registers through
    // <see cref="RegisterScreenClaimant"/>/<see cref="UnregisterScreenClaimant"/> — an opaque owner token, a
    // priority band, an optional preferred slot, and its own source/light/transform providers — and this method
    // (and BuildProgram) never need to change to support it: ResolveScreenMux re-submits every currently-registered
    // claimant's ledger claim each pass (mirroring the cabinet/easel claims below) and resolves headroom slots
    // GENERICALLY by owner token, never by role.
    private void ResolveScreenMux((int A, int B)? linkedPair) {
        // Stashed for BuildProgram (the sagging cable's emission reads this — see EmitLinkCable), validated against
        // the room's actual console count so a stale/out-of-range linked pair (e.g. from the `link` console verb) can
        // never index past m_room.Consoles.
        m_currentLinkedPair = ((linkedPair is { A: >= 0, B: >= 0 } pair) && (pair.A < m_room.Consoles.Count) && (pair.B < m_room.Consoles.Count) && (pair.A != pair.B))
            ? linkedPair
            : null;

        // Studio review has no cabinets and no easel (BuildProgram skips both), so no borrow is submitted — the easel
        // slab isn't emitted, so nothing samples the preview slot.
        var borrowed = (m_creator.Active && !m_scenarioStudio);

        for (var index = 0; (index < m_room.Consoles.Count); index++) {
            if (borrowed && (index == CreatorSceneRenderer.PreviewScreenIndex)) {
                continue; // the easel claims this exact slot below instead.
            }

            m_screenLedger.Claim(ownerToken: m_cabinetClaimTokens[index], preferredSlot: index, priority: ScreenSlotPriority.Anchored);
        }

        if (borrowed) {
            m_screenLedger.Claim(ownerToken: EaselClaimToken, preferredSlot: CreatorSceneRenderer.PreviewScreenIndex, priority: ScreenSlotPriority.Overlay);
        }

        // Any GENERICALLY registered claimant (see RegisterScreenClaimant) re-submits its claim every pass, exactly
        // like the cabinets/easel above — the ledger neither knows nor cares who they are.
        foreach (var (ownerToken, registration) in m_dynamicScreenClaimants) {
            m_screenLedger.Claim(ownerToken: ownerToken, preferredSlot: registration.PreferredSlot, priority: registration.Priority);
        }

        m_resolvedDynamicSlots.Clear();

        foreach (var (ownerToken, claim) in m_screenLedger.Resolve()) {
            if (claim.HasSlot && m_dynamicScreenClaimants.ContainsKey(key: ownerToken)) {
                m_resolvedDynamicSlots[claim.Slot] = ownerToken;
            }
        }

        foreach (var narration in m_screenLedger.LastNarrations) {
            Console.Error.WriteLine(value: narration);
        }
    }

    private static bool EqualLinkedPair((int A, int B)? a, (int A, int B)? b) =>
        (a.HasValue == b.HasValue) && (!a.HasValue || ((a.Value.A == b!.Value.A) && (a.Value.B == b.Value.B)));

    // ---- Companion faces + the diegetic-feed director (WS-12) ------------------------------------------------------
    // A screen-faced companion (its behavior manifest declares a face) registers a floating Ambient screen claim
    // through the SAME generic seam a placeable diegetic camera would (RegisterScreenClaimant); the ledger grants it a
    // headroom slot, its face slab emits there, and the render node's headroom source/light providers resolve the
    // companion's CURRENT face feed (the auto-tune's resolved name) through the named-feed registry. Nothing here
    // touches sim state. One reference-stable owner token per companion, held across frames while it stays loaded.
    private readonly Dictionary<CompanionState, object> m_companionFaceTokens = [];
    // Reused scratch for the stale-companion sweep below (cleared, not reallocated) — the sweep runs every CaptureFrame
    // whenever any face token is held, but the list itself need not churn the GC.
    private readonly List<CompanionState> m_staleCompanionScratch = [];

    // Registers (or refreshes) each screen-faced companion's ledger claim + its named-feed source/light providers, and
    // releases the claim of any companion that left the roster. Called every CaptureFrame BEFORE ResolveScreenMux.
    private void RegisterCompanionFaceClaims() {
        var live = m_companions.Companions;

        // Drop tokens for companions that left the roster (del/clear) so the ledger stops seating a ghost face.
        if (m_companionFaceTokens.Count > 0) {
            var stale = m_staleCompanionScratch;

            stale.Clear();

            foreach (var tracked in m_companionFaceTokens.Keys) {
                var stillLive = false;

                for (var index = 0; (index < live.Count); index++) {
                    if (ReferenceEquals(objA: live[index], objB: tracked)) {
                        stillLive = true;

                        break;
                    }
                }

                if (!stillLive) {
                    stale.Add(item: tracked);
                }
            }

            foreach (var companion in stale) {
                UnregisterScreenClaimant(ownerToken: m_companionFaceTokens[companion]);
                _ = m_companionFaceTokens.Remove(key: companion);
            }
        }

        foreach (var companion in live) {
            if (!companion.HasFace) {
                continue;
            }

            if (!m_companionFaceTokens.TryGetValue(key: companion, value: out var token)) {
                token = new object();
                m_companionFaceTokens[companion] = token;
            }

            var pinned = companion; // capture for the closures.

            // A floating (preferredSlot -1) Ambient claim: it never evicts a cabinet, taking the lowest free headroom
            // slot. Its source/light resolve the companion's CURRENTLY-tuned face feed by NAME through the registry.
            RegisterScreenClaimant(
                light: () => (m_feeds?.ResolveNamedFeedLight(name: pinned.CurrentFaceFeed) ?? Vector3.Zero),
                ownerToken: token,
                priority: ScreenSlotPriority.Ambient,
                source: () => (m_feeds?.ResolveNamedFeedHandle(name: pinned.CurrentFaceFeed) ?? 0)
            );
        }
    }

    // The ledger-granted screen-surface slot a screen-faced companion holds this pass, or -1 when it declared no face
    // or the ledger seated it nowhere. The ScreenSlotLedger is the SOLE owner of this resolved slot (F-STATE-2): the
    // companion renderer reads it through this resolver during emission rather than off a mirror field on
    // CompanionState. Valid only AFTER ResolveScreenMux this pass (the slots are known then) — which is exactly when
    // BuildProgram (and so EmitCompanions) runs.
    private int CompanionFaceSlot(CompanionState companion) =>
        (m_companionFaceTokens.TryGetValue(key: companion, value: out var token) ? ResolvedSlotFor(ownerToken: token) : -1);

    // The headroom slot the ledger granted a given owner token this pass, or -1 when it seated none.
    private int ResolvedSlotFor(object ownerToken) {
        foreach (var (slot, owner) in m_resolvedDynamicSlots) {
            if (ReferenceEquals(objA: owner, objB: ownerToken)) {
                return slot;
            }
        }

        return -1;
    }

    // Whether a companion's PREFERRED remote face feed (the last entry in its face-feed list, e.g. its own camera feed)
    // is producing pixels this frame — the auto-tune's "is there something to drift to" probe. Never names a specific
    // feed: it asks the registry whether the companion's own last-listed feed name is live.
    private bool PreferredRemoteFeedLive(CompanionState companion) {
        if ((m_feeds is not { } feeds) || (companion.FaceFeeds.Count == 0)) {
            return false;
        }

        return feeds.IsNamedFeedLive(name: companion.FaceFeeds[^1]);
    }

    /// <summary>Composes the diegetic-feed director over the main engine's worst-case envelope (idempotent) — the
    /// render node calls this once at resource build so its own type coupling stays flat; this source is the feed
    /// objects' composition point (mirrors <see cref="InstallBakePreview"/>). Without it the source runs feed-free (a
    /// bare-room run pays nothing).</summary>
    /// <param name="services">The application services (the GPU compute seam).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the feed kernels).</param>
    public void InstallFeeds(IServiceProvider services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(services);

        m_feeds ??= new CameraFeedPool(
            dynamicTransformCapacity: m_dynamicTransformCount,
            hostsOnDirectX: hostsOnDirectX,
            instanceCapacity: WorstCaseInstanceCapacity,
            programWordCapacity: WorstCaseProgramWordCapacity,
            services: services
        );
    }

    /// <summary>Renders this frame's planned camera feeds + ticks the procedural face feed (the render-thread half of
    /// the diegetic-feed director). The render node calls this each produced frame beside its bake-preview tick (a
    /// live GPU device); a no-op until <see cref="InstallFeeds"/> ran. Reads the PREVIOUS <c>CaptureFrame</c>'s plan —
    /// a deliberate one-frame lag matching the diegetic-CRT read the primitive's self-reference rule expects.</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device).</param>
    public void TickFeeds(in Puck.Hosting.FrameContext context) {
        if ((m_feeds is not { } feeds) || (m_program is null)) {
            return;
        }

        feeds.TickFeeds(
            context: in context,
            dynamicTransforms: m_dynamicTransforms,
            faceFeedNeeded: FaceFeedNeeded(),
            program: m_program,
            resolveScreenSource: ResolveFeedScreenSource,
            revision: m_builtFeedRevision,
            time: ((m_captureSequencer is { } scenario) ? scenario.PinnedContentTime : m_time)
        );
    }

    // Whether the default (procedural) face feed is wanted this frame: a screen-faced companion exists, or a
    // world.wire routed named:emotes onto a screen. A plain room with no companions never uploads the face feed.
    private bool FaceFeedNeeded() {
        foreach (var companion in m_companions.Companions) {
            if (companion.Document.Behavior?.Faces is { Count: > 0 }) {
                return true;
            }
        }

        foreach (var name in m_wiredFeedByScreen.Values) {
            if (string.Equals(a: name, b: CompanionState.DefaultFaceFeed, comparisonType: StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Disposes the diegetic-feed director's GPU resources (the render node's teardown path).</summary>
    public void DisposeFeeds() {
        m_feeds?.Dispose();
        m_feeds = null;
    }

    /// <summary>The wired-feed image handle a <c>world.wire</c> routed onto screen <paramref name="screenIndex"/> this
    /// frame, or 0 when nothing wired a feed there — the render node's per-slot cabinet source override consults this
    /// so a screen wired to a camera/named feed samples the feed INSTEAD of its cabinet brick / flat material (the
    /// fish's lure onto a cabinet screen). Primitive-typed (<see langword="nint"/>) on purpose — the node stays
    /// coupling-flat.</summary>
    /// <param name="screenIndex">The screen-surface slot index (a cabinet index 0-3, or a headroom slot).</param>
    /// <returns>The wired feed's handle, or 0 for no wire (the caller keeps its default source).</returns>
    public nint ResolveWiredFeedOverride(int screenIndex) =>
        ((m_wiredFeedByScreen.TryGetValue(key: screenIndex, value: out var feedName) && (m_feeds is { } feeds))
            ? feeds.ResolveNamedFeedHandle(name: feedName)
            : 0);

    // What a screen index binds INSIDE a camera feed's own render: the same wiring the room shows (a wired feed's
    // named handle, else the cabinet/flat fallback via the base screen-source resolution). The feed engine itself
    // enforces the self-reference rule (a screen wired to the feed being rendered binds 0), so this need not.
    private nint ResolveFeedScreenSource(int screenIndex) {
        if (m_wiredFeedByScreen.TryGetValue(key: screenIndex, value: out var feedName) && (m_feeds is { } feeds)) {
            return feeds.ResolveNamedFeedHandle(name: feedName);
        }

        // A feed sees the room's OTHER diegetic screens through the same dynamic-claimant source the room uses (a
        // booted cabinet's framebuffer lives on the render node's side, not here — a feed showing a cabinet is a
        // future refinement; today a feed renders the world geometry + any wired feed screens, which is the proof).
        return ResolveDynamicSource(slot: screenIndex);
    }

    // Plans the camera feeds the wiring wants live this frame: every companion creation-camera whose named feed a face
    // references (the fish's lure feed the face auto-tunes to), plus every world-scene camera a world.wire routes onto
    // a screen. Builds the CameraFeedRequest list (resolving each eye's live anchor pose), registers the camera-feed
    // screen claim, records screen->feed-name wiring for the render node's per-slot override, and hands the plan to the
    // director. Presentation-only; the plan is consumed one frame later by TickFeeds (the diegetic lag).
    private void PlanDiegeticFeeds() {
        m_wiredFeedByScreen.Clear();

        if (m_feeds is not { } feeds) {
            return;
        }

        // Collect distinct requested feeds by NAME (a feed named by both a face and a world.wire is one pool slot).
        // Reused scratch — cleared, not reallocated (see the field remarks). The plan itself must rerun each frame (live
        // companion poses), but the containers do not need to churn the GC.
        var requests = m_feedRequestScratch;
        var byName = m_feedByNameScratch;

        requests.Clear();
        byName.Clear();

        // 1) Companion creation cameras backing a face's referenced feed name (the fish's lure lens). A companion's
        //    creation camera rides one of the companion's OWN shapes; resolve that shape's live world pose.
        CollectCompanionFeeds(byName: byName, requests: requests);

        // 2) World-scene cameras a world.wire routed onto a screen (feed:N -> eye #N). Records the screen->feed-name
        //    wiring so the render node's per-slot override samples the feed.
        CollectWorldWiredFeeds(byName: byName, requests: requests);

        feeds.PlanFeeds(requestedFeeds: requests);
    }

    // Companion creation cameras whose named feed a face references: the feed is requested (so it renders live), and
    // its eye rides the anchored shape's CURRENT world pose (the shape's dynamic-transform slot the companion renderer
    // packed this frame). The face's own auto-tune decides WHETHER to show it; the feed renders regardless of tune so
    // the auto-tune's "is the feed live" probe has a real answer.
    private void CollectCompanionFeeds(Dictionary<string, int> byName, List<CameraFeedRequest> requests) {
        var companions = m_companions.Companions;

        for (var companionIndex = 0; (companionIndex < companions.Count); companionIndex++) {
            var companion = companions[companionIndex];
            var cameras = companion.Document.Cameras;

            if (cameras is not { Count: > 0 }) {
                continue;
            }

            var shapes = (companion.Document.Shapes ?? []);
            var rootSlot = (m_companionSlotBase + (companionIndex * CompanionRenderer.SlotsPerCompanion));

            foreach (var camera in cameras) {
                if (requests.Count >= CameraFeedPool.MaxCameraFeeds) {
                    break;
                }

                var feedName = (camera.Feed ?? camera.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

                if (byName.ContainsKey(key: feedName)) {
                    continue; // already requested (a shared name).
                }

                // The anchored shape's live pose: its index in the document's shapes is the companion renderer's
                // shape-slot offset (rootSlot + 1 + shapeIndex — see CompanionRenderer).
                var shapeIndex = IndexOfShape(shapes: shapes, shapeId: camera.ShapeId);

                if (shapeIndex < 0) {
                    continue; // a camera naming a missing shape (normalization should have dropped it).
                }

                var shapeSlot = (rootSlot + 1 + shapeIndex);
                var shapeTransform = m_dynamicTransforms[shapeSlot];

                byName[feedName] = requests.Count;
                requests.Add(item: BuildCreationCameraRequest(camera: camera, feedName: feedName, shapeTransform: shapeTransform));
            }
        }
    }

    // World-scene cameras a world.wire routed onto a screen: for each Feed-kind wire, the eye is the world camera whose
    // Id equals the wire's feed index; its pose is world/placement-anchored (world anchors pose directly; a placement
    // anchor rides its stamp — resolved through the world scene). Also records Named-kind wires so a screen wired to a
    // creation/host feed name samples it (the fish's lure onto a cabinet screen).
    private void CollectWorldWiredFeeds(Dictionary<string, int> byName, List<CameraFeedRequest> requests) {
        foreach (var (screenIndex, source) in m_worldScene.Wiring) {
            switch (source.Kind) {
                case ScreenWireKind.Named when (source.Name is { Length: > 0 } namedFeed):
                    // A screen wired directly to a named feed (a creation camera's feed, the emote face, …). The
                    // named feed itself is requested by whatever OWNS it (a companion camera, above); here we only
                    // record the screen->name binding so the render node's per-slot override samples it.
                    m_wiredFeedByScreen[screenIndex] = namedFeed;

                    break;
                case ScreenWireKind.Feed:
                    RecordWorldCameraFeed(byName: byName, feedIndex: source.Index, requests: requests, screenIndex: screenIndex);

                    break;
                default:
                    break;
            }
        }
    }

    private void RecordWorldCameraFeed(Dictionary<string, int> byName, int feedIndex, List<CameraFeedRequest> requests, int screenIndex) {
        var feedName = $"world:{feedIndex}";

        m_wiredFeedByScreen[screenIndex] = feedName;

        if (byName.ContainsKey(key: feedName) || (requests.Count >= CameraFeedPool.MaxCameraFeeds)) {
            return;
        }

        foreach (var eye in m_worldScene.Cameras) {
            if (eye.Id != feedIndex) {
                continue;
            }

            // A Placement-anchored eye rides its prop's LIVE pose (so a camera on a dragged building follows it); a
            // World-anchored eye poses straight from its own Position/Yaw (CameraEye.Resolve ignores the anchor pose),
            // so the zero default is correct there.
            var anchorPosition = Vector3.Zero;
            var anchorYaw = 0f;

            if (eye.Anchor == CameraAnchorKind.Placement) {
                _ = m_worldScene.TryResolvePlacementPose(placementId: eye.AnchorId, out anchorPosition, out anchorYaw);
            }

            byName[feedName] = requests.Count;
            requests.Add(item: new CameraFeedRequest(
                AnchorPosition: anchorPosition,
                AnchorYaw: anchorYaw,
                Eye: eye,
                Name: feedName,
                WiredScreens: WiredScreenSet(feedName: feedName)
            ));

            break;
        }
    }

    // A creation camera → a CameraFeedRequest: the eye is a Shape-anchored eye whose stored pose is the camera's
    // offset from the anchored shape's frame; the anchor position/yaw come from the shape's live dynamic transform
    // (the companion renderer packed it this frame). Degrees → radians for the offset yaw/pitch.
    private CameraFeedRequest BuildCreationCameraRequest(CreationCameraDocument camera, string feedName, DynamicTransform shapeTransform) {
        var offsetYaw = ((camera.Yaw ?? 0f) * (MathF.PI / 180f));
        var offsetPitch = ((camera.Pitch ?? 0f) * (MathF.PI / 180f));
        var eye = new CameraEye(
            Anchor: CameraAnchorKind.Shape,
            AnchorId: camera.ShapeId,
            FieldOfViewRadians: ((camera.Fov is { } fov) ? (float?)(fov * (MathF.PI / 180f)) : null),
            FocusDistance: camera.Focus,
            Id: camera.Id,
            Pitch: offsetPitch,
            Position: camera.Position,
            Yaw: offsetYaw
        );
        // The anchored shape's live world heading (yaw about +Y) from its packed orientation.
        var anchorYaw = YawOf(orientation: shapeTransform.Orientation);

        return new CameraFeedRequest(
            AnchorPosition: shapeTransform.Position,
            AnchorYaw: anchorYaw,
            Eye: eye,
            Name: feedName,
            WiredScreens: WiredScreenSet(feedName: feedName)
        );
    }

    // The screen indices wired to a given feed name this frame (the self-reference set for that feed) — a screen shows
    // this feed either through a face's tune (companion faces are dynamic-claimant slots) or a world.wire.
    private IReadOnlySet<int> WiredScreenSet(string feedName) {
        var set = new HashSet<int>();

        foreach (var (screenIndex, name) in m_wiredFeedByScreen) {
            if (string.Equals(a: name, b: feedName, comparisonType: StringComparison.Ordinal)) {
                _ = set.Add(item: screenIndex);
            }
        }

        return set;
    }

    private static int IndexOfShape(IReadOnlyList<ShapeDocument> shapes, int shapeId) {
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == shapeId) {
                return index;
            }
        }

        return -1;
    }

    // The heading (yaw about +Y) a quaternion orientation faces — the one axis a companion's upright frame turns
    // around (matching CameraEye.Resolve's yaw-only anchor composition).
    private static float YawOf(Quaternion orientation) {
        var forward = Vector3.Transform(value: Vector3.UnitZ, rotation: orientation);

        return MathF.Atan2(y: forward.X, x: forward.Z);
    }

    // ---- The generic dynamic screen-claimant seam --------------------------------------------------------------
    // A future caller (outside this file) wires a NEW diegetic screen source through this seam alone: no ledger
    // internals, no ResolveScreenMux/BuildProgram edits. One registration per owner token holds its ledger priority/
    // preferred slot plus its own source/light/transform providers; resolution (which slot it landed on this frame,
    // if any) is looked up GENERICALLY by owner token — the render node's screen-source/light/transform dictionaries
    // consult ResolveDynamicSource/ResolveDynamicLight/ResolveDynamicTransform per headroom index without ever
    // knowing which registered owner (if any) currently holds it.
    private readonly record struct DynamicScreenClaimRegistration(ScreenSlotPriority Priority, int PreferredSlot, Func<nint>? Source, Func<Vector3>? Light, Func<SdfScreenSurfaceTransform?>? Transform);

    private readonly Dictionary<object, DynamicScreenClaimRegistration> m_dynamicScreenClaimants = [];
    private readonly Dictionary<int, object> m_resolvedDynamicSlots = [];

    /// <summary>THE attach seam for a diegetic screen source that lives OUTSIDE this frame source — the one blessed way
    /// to put content on a screen surface (see <see cref="ScreenSlotLedger"/> for the full seam contract: what a
    /// claimant is, the band semantics, token identity, the per-pass re-claim convention). A caller wires a new screen
    /// through THIS method alone — an opaque owner token, a <see cref="ScreenSlotPriority"/> band, an optional preferred
    /// slot, and its own source/light/transform providers — without touching <see cref="ScreenSlotLedger"/> internals,
    /// this source's mux (<c>ResolveScreenMux</c>), or the render node. Do NOT add a new raw callback drill or a direct
    /// field poll for a screen source; ride this instead. Registration persists across passes (register once per
    /// activation, not every frame — UNLIKE <see cref="ScreenSlotLedger.Claim"/>'s bare per-pass contract, which this
    /// re-submits on the caller's behalf each <see cref="CaptureFrame"/>) until <see cref="UnregisterScreenClaimant"/>
    /// withdraws it.</summary>
    /// <param name="ownerToken">An opaque, reference-stable identity for the claimant (compared by reference).</param>
    /// <param name="priority">The claim's <see cref="ScreenSlotPriority"/> band.</param>
    /// <param name="preferredSlot">A specific slot this claim wants, or -1 for a floating claim (see
    /// <see cref="ScreenSlotLedger.Claim"/>).</param>
    /// <param name="source">An optional provider of the claimed slot's same-device image-view handle (see
    /// <see cref="SdfEngineNode"/>'s <c>screenSources</c>); null leaves the slot unbound (the flat/procedural
    /// fallback) even while the claim holds it.</param>
    /// <param name="light">An optional provider of the claimed slot's room-glow color.</param>
    /// <param name="transform">An optional provider of the claimed slot's world-space sampling frame (for a screen
    /// riding a dynamic entity); omit for a screen on static geometry.</param>
    public void RegisterScreenClaimant(object ownerToken, ScreenSlotPriority priority, int preferredSlot = -1, Func<nint>? source = null, Func<Vector3>? light = null, Func<SdfScreenSurfaceTransform?>? transform = null) {
        ArgumentNullException.ThrowIfNull(ownerToken);

        m_dynamicScreenClaimants[ownerToken] = new DynamicScreenClaimRegistration(Priority: priority, PreferredSlot: preferredSlot, Source: source, Light: light, Transform: transform);
    }

    /// <summary>Withdraws a dynamic screen claimant registered through <see cref="RegisterScreenClaimant"/> — a
    /// no-op if it never registered, or already withdrew.</summary>
    /// <param name="ownerToken">The owner token previously passed to <see cref="RegisterScreenClaimant"/>.</param>
    public void UnregisterScreenClaimant(object ownerToken) {
        m_dynamicScreenClaimants.Remove(key: ownerToken);
        m_screenLedger.Release(ownerToken: ownerToken);
    }

    /// <summary>The image-view handle a dynamic claimant registered for headroom slot <paramref name="slot"/> this
    /// frame, or 0 when no registered claimant currently holds it (or it registered no source provider) — the render
    /// node's generic headroom screen-source entries call this per slot.</summary>
    /// <param name="slot">The screen-surface slot index.</param>
    public nint ResolveDynamicSource(int slot) =>
        ((m_resolvedDynamicSlots.TryGetValue(key: slot, value: out var ownerToken) && m_dynamicScreenClaimants.TryGetValue(key: ownerToken, value: out var registration))
            ? (registration.Source?.Invoke() ?? 0)
            : 0);

    /// <summary>The room-glow color a dynamic claimant registered for headroom slot <paramref name="slot"/> this
    /// frame, or zero when none holds it (or it registered no light provider).</summary>
    /// <param name="slot">The screen-surface slot index.</param>
    public Vector3 ResolveDynamicLight(int slot) =>
        ((m_resolvedDynamicSlots.TryGetValue(key: slot, value: out var ownerToken) && m_dynamicScreenClaimants.TryGetValue(key: ownerToken, value: out var registration))
            ? (registration.Light?.Invoke() ?? Vector3.Zero)
            : Vector3.Zero);

    /// <summary>The world-space sampling frame a dynamic claimant registered for headroom slot
    /// <paramref name="slot"/> this frame, or null when none holds it (or it registered no transform provider).</summary>
    /// <param name="slot">The screen-surface slot index.</param>
    public SdfScreenSurfaceTransform? ResolveDynamicTransform(int slot) =>
        ((m_resolvedDynamicSlots.TryGetValue(key: slot, value: out var ownerToken) && m_dynamicScreenClaimants.TryGetValue(key: ownerToken, value: out var registration))
            ? registration.Transform?.Invoke()
            : null);

    // Every possible dynamic-claimant slot's transform provider, built ONCE (the closures read m_resolvedDynamicSlots
    // fresh each call — no per-frame rebuild needed). Cabinets/the easel sit on static geometry and never appear
    // here (they never register a transform provider in the first place).
    private IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? m_screenSurfaceTransformProviders;

    /// <inheritdoc/>
    public IReadOnlyDictionary<int, Func<SdfScreenSurfaceTransform?>>? ScreenSurfaceTransforms {
        get {
            if (m_screenSurfaceTransformProviders is null) {
                var providers = new Dictionary<int, Func<SdfScreenSurfaceTransform?>>(capacity: SdfProgramBuilder.MaxScreenSurfaces);

                for (var index = 0; (index < SdfProgramBuilder.MaxScreenSurfaces); index++) {
                    var slot = index;

                    providers[slot] = () => ResolveDynamicTransform(slot: slot);
                }

                m_screenSurfaceTransformProviders = providers;
            }

            return m_screenSurfaceTransformProviders;
        }
    }

    // The room-local center of a console's diegetic screen — the SAME placement BuildProgram gives the screen slab (top
    // of the pedestal, sized to the GB aspect). Render-relative equals this here (the render origin is the spawn anchor
    // at local zero), so the camera can push in toward it.
    private Vector3 ScreenCenterLocal(int consoleIndex) {
        var stand = m_room.Consoles[consoleIndex];
        var screenHalfHeight = ((stand.HalfExtents.X * 0.8f) / ScreenAspect);

        return new Vector3(stand.Center.X, (m_room.FloorY + (2f * stand.HalfExtents.Y) + screenHalfHeight), stand.Center.Y);
    }

    // A console's diegetic-screen half-height (the SAME value BuildProgram/ScreenCenterLocal derive) — the pane camera
    // uses it to sit at exactly the distance that makes the screen fill the viewport height (the native-panel look).
    private float ScreenHalfHeightLocal(int consoleIndex) {
        var stand = m_room.Consoles[consoleIndex];

        return ((stand.HalfExtents.X * 0.8f) / ScreenAspect);
    }

    // Fills the unified dynamic-transform buffer for this frame: players (OverworldWorld's own slots, unchanged)
    // followed by the per-console control clusters (neutral pose, or depressed/tilted by the console's current
    // joypad state). REUSED buffer, no per-frame allocation. No cartridge slots — the cart choice lives at the
    // cabinet (see the constructor), so there are no physical cartridge instances to place.
    private void PackDynamicTransforms(WorldCoord3 renderOrigin, float alpha) {
        var players = m_world.DynamicTransforms(renderOrigin: renderOrigin, alpha: alpha);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            // A driving player's avatar follows their brick sprite (presentation override); everyone else renders at
            // their simulation body.
            m_dynamicTransforms[slot] = ((PresentationOverride?.Invoke(slot) is { } overridden)
                ? (players[slot] with { Position = overridden.ToRenderRelative(origin: renderOrigin) })
                : players[slot]);
        }

        // The world-sculpt camera anchors on the CREATING slot's rendered position (the player is the cursor).
        m_primaryPlayerRenderPosition = m_dynamicTransforms[0].Position;

        PackControlTransforms();
        PackCreatorTransforms();
    }

    // The creator pool's per-frame transforms — delegated to the renderer (the ghost + placed shapes at their live
    // transforms, unused slots hidden below the floor).
    private void PackCreatorTransforms() {
        m_creatorRenderer.PackTransforms(hiddenPosition: HiddenPosition(), transforms: m_dynamicTransforms);
        m_worldRenderer.PackTransforms(hiddenPosition: HiddenPosition(), timeSeconds: m_time, transforms: m_dynamicTransforms);
        m_companionRenderer.PackTransforms(hiddenPosition: HiddenPosition(), transforms: m_dynamicTransforms);
    }

    /// <summary>The live companion roster (the companion verbs + the screen mux read it here).</summary>
    public CompanionRoster Companions => m_companions;

    /// <summary>The room-wide region companions wander inside (see the constructor).</summary>
    public WorkbenchRegion CompanionBounds { get; }

    // The companions' steering target: the nearest RENDERED player this frame (hidden/free slots park far below the
    // floor, so they lose every distance contest naturally).
    private (Vector3 Position, float Distance)? NearestActivePlayer(Vector3 from) {
        var best = ((Vector3 Position, float Distance)?)null;

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var position = m_dynamicTransforms[slot].Position;
            var distance = Vector3.Distance(value1: from, value2: position);

            if ((position.Y > (m_room.FloorY - 2f)) && ((best is not { } current) || (distance < current.Distance))) {
                best = (position, distance);
            }
        }

        return best;
    }

    /// <summary>A fresh companion's spawn point — beside the workbench, fanned by roster index.</summary>
    /// <param name="rosterIndex">The joining companion's roster index.</param>
    public Vector3 CompanionSpawnPosition(int rosterIndex) {
        var center = new Vector3(
            (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
            (m_room.FloorY + 0.9f),
            (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
        );

        return (center + new Vector3((1.6f + (0.9f * rosterIndex)), 0f, (1.2f - (0.8f * rosterIndex))));
    }

    // ---- Creator mode (the in-engine SDF authoring surface) ------------------------------------------------------
    // The model lives in Puck.Demo.Creator.CreatorScene (shared with the render node's CreatorController and the
    // console verbs); these thin forwarders keep the node/forge seams stable. All of it is presentation only — the
    // deterministic world/hash never sees a creator shape.

    /// <summary>The authored creator scene — the editor model the controller and the console verbs mutate.</summary>
    public CreatorScene Creator => m_creator;
    /// <summary>Whether creator mode is active (the mode's ghost is visible and the player edits shapes).</summary>
    public bool CreatorActive => m_creator.Active;
    /// <summary>The TARGET primitive's name (for the console/HUD readout).</summary>
    public string CreatorShapeName => m_creator.TargetShapeName;
    /// <summary>How many shapes have been placed so far.</summary>
    public int CreatorPlacedCount => m_creator.PlacedCount;

    // ---- World sculpt mode (the town authoring surface) ----------------------------------------------------------
    // The model trio lives in Puck.Demo.World (shared with the world.* console verbs through the render node's
    // IWorldSculptModeHost); these thin seams keep the node's coupling flat. Presentation only until a deliberate
    // world.save/world.load crosses the one legal authoring→sim seam (OverworldWorld.LoadWorld).

    /// <summary>The authored world scene — the sculptor model the controller and the console verbs mutate.</summary>
    public WorldScene WorldScene => m_worldScene;
    /// <summary>The sculptor's shared undo ring (pad + console verbs push to the same history).</summary>
    internal EditHistory<WorldScene.Snapshot> WorldHistory => m_worldHistory;
    /// <summary>The shared content-addressed store (placement resolution + deliberate saves).</summary>
    public ContentAddressedStore WorldContentStore => m_worldStore;
    /// <summary>Whether world-sculpt mode is active (the creating slot's pad drives the sculptor).</summary>
    public bool WorldSculptActive => m_worldSculptActive;
    /// <summary>The sculptor's active chord page (for the binding-bar publish), as a primitive — the render node is
    /// at its coupling ceiling and may not name the controller's enum.</summary>
    public int WorldSculptBarPage => (int)m_worldController.ActivePage;
    /// <summary>The creating pad's live button mask while world-sculpt is active (the bar's pressed highlights).</summary>
    public GamepadButtons WorldSculptHeldButtons { get; private set; }

    /// <summary>Enters or leaves world-sculpt mode; entering resets the controller's edge tracking so a held button
    /// never fires a stale edge into the mode.</summary>
    /// <param name="active">Whether the mode should be active.</param>
    public void SetWorldSculptActive(bool active) {
        if (m_worldSculptActive == active) {
            return;
        }

        m_worldSculptActive = active;

        if (active) {
            m_worldController.Reset();
        }
    }

    /// <summary>Forwards the creating slot's raw pad state to the sculptor (only while the mode is active).</summary>
    /// <param name="raw">The pad state.</param>
    /// <param name="deltaSeconds">The render-clock delta driving analog sweeps.</param>
    public void AdvanceWorldSculptInput(in GamepadState raw, float deltaSeconds) {
        if (m_worldSculptActive) {
            WorldSculptHeldButtons = raw.Buttons;
            m_worldController.Advance(deltaSeconds: deltaSeconds, raw: in raw);
        }
    }

    /// <summary>Applies a pending committed world (a deliberate <c>world.save</c>/<c>world.load</c>), once per
    /// commit: rebuilds the room over the code-built base, decodes the walk grid, parses the movement lock, applies
    /// all three to the SIM wholesale (<see cref="OverworldWorld.LoadWorld"/> — the one legal authoring→sim seam),
    /// and swaps this source's own RENDERED room in the same breath, so the walls the player sees and the walls the
    /// sim enforces change together. The host calls this ON A TICK BOUNDARY (before the frame's first
    /// <see cref="OverworldWorld.Advance"/>); primitive-typed on purpose — the render node is at its analyzer
    /// coupling ceiling.</summary>
    /// <returns>The narration line when a commit was applied; null otherwise.</returns>
    public string? ConsumePendingWorldLoad() {
        // Reference-compare against the last applied commit: WorldDocument records are immutable, so a new save/load
        // is exactly a new reference (poll-based — catches BOTH world.save and world.load without an event per path).
        var committed = m_worldScene.CommittedDocument;

        if (ReferenceEquals(objA: committed, objB: m_appliedWorldCommit) || (committed is null)) {
            return null;
        }

        m_appliedWorldCommit = committed;

        var room = OverworldRoom.FromWorld(baseRoom: m_baseRoom, document: committed);
        var walkGrid = ((committed.WalkGrid is { } gridDocument) ? FixedWalkGrid.FromDocument(document: gridDocument) : null);
        var movementLock = OverworldWorld.ParseMovementLock(value: committed.MovementLock);

        m_world.LoadWorld(movementLock: movementLock, room: room, walkGrid: walkGrid);
        // The rendered walls follow the sim's walls: swap the room this source emits and force a program rebuild.
        m_room = room;
        m_program = null;

        return $"[world] applied — bounds ({room.BoundsMin.X:F0},{room.BoundsMin.Y:F0})..({room.BoundsMax.X:F0},{room.BoundsMax.Y:F0}), walk grid {(walkGrid is null ? "walls-only" : "baked")}, movement {movementLock}";
    }

    // The world-sculpt town-read camera: a lifted orbit behind/above the creating slot (steep pitch, generous
    // distance — streets and lamp rows read as rows, not walls of geometry). Yields only while the mode is up; the
    // director eases in and out through the same machinery the creator's workpiece camera uses.
    private (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? WorldSculptCameraFrame() {
        if (!m_worldSculptActive) {
            return null;
        }

        return (Target: m_primaryPlayerRenderPosition, Yaw: 0f, Pitch: 0.92f, Distance: 13.5f, Sprite: false);
    }

    // The deliberate-save transformation (installed as WorldScene.PrepareForSave): bake the walk grid from the
    // document's placements (resolved from the store), terrain patches, and the room's own console stands, honoring
    // the scene's tessellation knob — so the saved bytes carry the EXACT collision a reload walks. Deterministic:
    // bake-twice from the same document is byte-identical (the WS-3 battery's proof).
    private WorldDocument BakeWorldForSave(WorldDocument document) =>
        WorldWalkGridBake.Bake(document: document, room: m_room, store: m_worldStore, walkGridKind: m_worldScene.WalkGridKind);

    /// <summary>Enters or leaves creator mode (see <see cref="CreatorScene.SetActive"/>); toggling also clears the
    /// pad state machine's edge tracking so a held button never fires a stale edge into the other mode.</summary>
    /// <param name="active">The desired state.</param>
    public void SetCreatorActive(bool active) {
        m_creator.SetActive(active: active);
        m_creatorController.Reset();
    }

    /// <summary>Advances one frame of creator-mode pad input for the creating slot (delegates to the
    /// <see cref="CreatorController"/> this source composes).</summary>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void AdvanceCreatorInput(in GamepadState raw, float deltaSeconds) {
        m_creatorController.Advance(raw: in raw, deltaSeconds: deltaSeconds);
    }

    /// <summary>Whether the creator EXIT verb fired since the last consume (clears it) — the node leaves the mode
    /// and restores the view when this reports true.</summary>
    public bool ConsumeCreatorExitRequest() =>
        m_creatorController.ConsumeExitRequest();

    /// <summary>The active creator verb page as a bar-layout index (see <see cref="CreatorPage"/>) — the binding-bar
    /// overlay switches its slot table on it.</summary>
    public int CreatorBarPage => (int)m_creatorController.Page;

    /// <summary>The screen-surface slot the preview easel borrows while creator mode is up (the render node's
    /// provider mux keys on it — see <see cref="CreatorSceneRenderer.PreviewScreenIndex"/>).</summary>
    public int CreatorPreviewScreenIndex => CreatorSceneRenderer.PreviewScreenIndex;

    /// <summary>The bake preview's live image handle for the easel slab (0 until the first bake lands, and always 0
    /// while the mode is down — the borrowed cabinet's own source resumes then).</summary>
    public nint CreatorPreviewHandle => ((m_creator.Active ? m_bakePreview?.CurrentImageViewHandle : 0) ?? 0);

    /// <summary>The bake preview's screen-light color (the workbench glows with the creation; zero when dark).</summary>
    public Vector3 CreatorPreviewLight => ((m_creator.Active ? m_bakePreview?.PreviewAverageColor : Vector3.Zero) ?? Vector3.Zero);

    /// <summary>Replaces the bake-preview seam (the bake pipeline's live service plugs in here; the null stand-in
    /// keeps the easel dark until then).</summary>
    /// <param name="preview">The preview implementation.</param>
    public void ConnectBakePreview(ICreatorBakePreview preview) {
        ArgumentNullException.ThrowIfNull(preview);

        m_bakePreview = preview;
    }

    /// <summary>Builds and connects the LIVE bake-preview service over this source's creator scene (idempotent) —
    /// the render node calls this once at resource build so its own type coupling stays flat; this source is the
    /// creator objects' composition point.</summary>
    /// <param name="services">The application services (the GPU compute seam + the optional dev console).</param>
    public void InstallBakePreview(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        if (m_bakePreviewService is not null) {
            return;
        }

        m_bakePreviewService = new Forge.Bake.BakePreviewService(scene: m_creator, services: services);
        ConnectBakePreview(preview: m_bakePreviewService);
    }

    /// <summary>Loads the document's boot world and composes the <c>--scenario</c> capture driver — the render node
    /// calls this once at resource build so its own type coupling stays flat (this source is the composition point for
    /// the scenario objects). The former companion / wire / face / world-roundtrip boot-time capture aids are gone; the
    /// live in-session paths (<c>companion.add</c> / <c>world.wire</c> / <c>companion.face</c> / <c>world.verify</c>
    /// console verbs) are the only way to reach them now.</summary>
    /// <param name="services">The application services (the bound scenario options).</param>
    /// <param name="bootWorld">The document's world handle (<see cref="Puck.Scene.OverworldNode.World"/>) resolved +
    /// committed at boot, or null for the plain room. The render node threads it from the run document (it is a
    /// durable document field, not an env var). The first tick-boundary ConsumePendingWorldLoad swaps the
    /// room/collision/walk-grid to it.</param>
    public void InstallScenario(IServiceProvider services, string? bootWorld = null) {
        ArgumentNullException.ThrowIfNull(services);

        LoadBootWorld(handle: bootWorld);

        if ((services.GetService<IOptions<ScenarioOptions>>()?.Value is { Active: true } scenario) && (m_captureSequencer is null)) {
            m_captureSequencer = new CaptureSequencer(director: m_director, options: scenario, defaultTarget: m_creator.Workbench.SpawnPosition);
            m_scenarioStudio = (scenario.Backdrop == ScenarioBackdrop.Studio);
        }
    }

    /// <summary>Whether a <c>--scenario</c> capture plan is active on this source.</summary>
    public bool ScenarioActive => (m_captureSequencer is not null);

    /// <summary>Advances the scenario's SETTLE-THEN-CAPTURE state machine one produced frame (settle timing only —
    /// wall-clock never reaches a rendered value) and holds this frame's verbatim shot pose. Returns the output PNG
    /// path to arm a one-shot capture for THIS frame once the active shot has settled, or null on frames that arm
    /// nothing. The render node calls this BEFORE the frame renders (it owns the producer's capture-arm), so the pose
    /// this sets is the pose the capture reads back. A no-op returning null when no <c>--scenario</c> is installed.</summary>
    /// <param name="deltaSeconds">The wall-clock delta since the previous produced frame.</param>
    /// <returns>A path to arm a capture for this frame, or null.</returns>
    public string? ScenarioTick(float deltaSeconds) =>
        m_captureSequencer?.Advance(deltaSeconds: deltaSeconds);

    /// <summary>Whether every scenario shot's capture has been written — the completion-driven exit condition the
    /// render node polls to request a graceful shutdown. Never true when no scenario is installed.</summary>
    public bool ScenarioComplete => (m_captureSequencer?.IsComplete ?? false);

    /// <summary>How many scenario shots have had their capture armed so far (the safety-net accounting).</summary>
    public int ScenarioCapturedCount => (m_captureSequencer?.CapturedCount ?? 0);

    /// <summary>How many scenario shots the plan contains (the safety-net accounting).</summary>
    public int ScenarioShotCount => (m_captureSequencer?.ShotCount ?? 0);

    /// <summary>Advances the live bake preview one produced frame (render thread; the frame's host resolves the
    /// live GPU device). A no-op until <see cref="InstallBakePreview"/> ran.</summary>
    /// <param name="context">The frame context.</param>
    public void TickBakePreview(in Puck.Hosting.FrameContext context) {
        m_bakePreviewService?.Tick(context: in context);
    }

    /// <summary>Disposes the owned bake-preview service's GPU resources (the render node's teardown path) and
    /// restores the null stand-in so the easel degrades dark rather than sampling a destroyed image.</summary>
    public void DisposeBakePreview() {
        m_bakePreviewService?.Dispose();
        m_bakePreviewService = null;
        m_bakePreview = null;
    }

    /// <summary>Loads a saved creation into the scene by save handle or file path (the <c>--scenario</c> review harness
    /// rides this to open its creation; the console's <c>creator.load</c> uses the store directly).</summary>
    /// <param name="nameOrPath">The save handle or file path.</param>
    /// <returns>A one-line status for the console.</returns>
    public string LoadCreationFile(string nameOrPath) {
        if (CreationStore.Load(nameOrPath: nameOrPath) is not { } document) {
            return $"[creator.load: nothing readable at '{nameOrPath}']";
        }

        // Loading is legal while the mode is down (the shapes persist; entering creator reveals them).
        var loaded = m_creator.LoadDocument(document: document);

        return $"[creator.load: {loaded} shape(s) from '{document.Name}' (style {document.BakeStyle}, intent {document.Intent})]";
    }

    /// <summary>The packed-word floor the engine's program buffer must reserve — the WORST-CASE program this source
    /// can ever build (every console's diegetic screen lit, the creator pool in its largest emission form), probed
    /// once and cached. The probe program is only measured, never rendered.</summary>
    public int WorstCaseProgramWordCapacity => (m_worstCase ??= MeasureWorstCaseEnvelope()).Words;

    /// <summary>The instance-count floor the engine's mask buffer must reserve (see
    /// <see cref="WorstCaseProgramWordCapacity"/>).</summary>
    public int WorstCaseInstanceCapacity => (m_worstCase ??= MeasureWorstCaseEnvelope()).Instances;

    private (int Words, int Instances) MeasureWorstCaseEnvelope() {
        var fullBootMask = ((m_room.Consoles.Count >= 32) ? uint.MaxValue : ((1u << m_room.Consoles.Count) - 1u));
        var probe = BuildProgram(bootedMask: fullBootMask, probeWorstCase: true);

        // The SDF-debug mode's takeover is EITHER the room's own debug subject OR a bench workload (up to 4096
        // instances) — never both, and 4096 bench instances cannot pile onto the room's instances in one program (past
        // the cap). So the bench worst case is a SEPARATE probe and the envelope is the MAX, not the sum. This reserves
        // 4096 instance slots always (the parked-slot machinery keeps reserved-but-inactive slots cheap per frame; the
        // one-time cost is the mask/bounds buffer sizing, reported by sdf.info / the bench header).
        var benchBuilder = new Puck.SdfVm.SdfProgramBuilder();

        m_sdfDebug.EmitBenchProbe(builder: benchBuilder);

        var benchProbe = benchBuilder.Build();

        return (Math.Max(probe.Words.Length, benchProbe.Words.Length), Math.Max(probe.Instances.Count, benchProbe.Instances.Count));
    }

    // Each console's control cluster: a d-pad cross (tilts toward the held direction) and two round buttons (A/B,
    // depress a few centimeters when held). Reads the SAME per-frame joypad state the console's machine consumes —
    // never re-derived from raw input — so an unbooted/unassigned console (whose provider is absent from
    // m_controlsSource's backing dictionary, or simply never pressed) stays in its neutral pose.
    private void PackControlTransforms() {
        for (var consoleIndex = 0; (consoleIndex < m_room.Consoles.Count); consoleIndex++) {
            var stand = m_room.Consoles[consoleIndex];
            var buttons = (m_controlsSource?.Invoke(consoleIndex) ?? JoypadButtons.None);
            var anchor = ControlClusterAnchor(stand: stand);
            var dPadPressed = (buttons & (JoypadButtons.Left | JoypadButtons.Right | JoypadButtons.Up | JoypadButtons.Down));
            // Composed per-axis (not a single switch on the exact combo) so a diagonal press (Left+Up, a real GB
            // joypad state) tilts both axes at once instead of falling through to a neutral pose.
            var tiltZ = (((dPadPressed & JoypadButtons.Right) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Left) != 0) ? -DPadTiltRadians : 0f));
            var tiltX = (((dPadPressed & JoypadButtons.Up) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Down) != 0) ? -DPadTiltRadians : 0f));
            var dPadTilt = (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: tiltX) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: tiltZ));

            var baseSlot = (m_controlSlotBase + (consoleIndex * ControlsPerConsole));

            m_dynamicTransforms[baseSlot + DPadControlOffset] = new DynamicTransform(
                Position: (dPadPressed != JoypadButtons.None ? DepressedInward(position: anchor.DPad) : anchor.DPad),
                Orientation: dPadTilt
            );
            m_dynamicTransforms[baseSlot + AButtonControlOffset] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.A) != 0) ? DepressedInward(position: anchor.A) : anchor.A),
                Orientation: Quaternion.Identity
            );
            m_dynamicTransforms[baseSlot + BButtonControlOffset] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.B) != 0) ? DepressedInward(position: anchor.B) : anchor.B),
                Orientation: Quaternion.Identity
            );
        }
    }

    // A pressed control moves a few centimeters INTO the pedestal (−Z, against the front-face normal) — a uniform
    // depress, the cheap fallback the mission allows when a per-shape tilt isn't warranted (the buttons are round,
    // not directional).
    private static Vector3 DepressedInward(Vector3 position) =>
        (position - new Vector3(0f, 0f, ControlDepress));

    private (Vector3 DPad, Vector3 A, Vector3 B) ControlClusterAnchor(ConsoleStand stand) {
        // Centered-low on the pedestal's front face (+Z, the same face the screen and the stand's cartridge slot
        // share); the d-pad sits left of center, A/B sit right of center — a minimal cluster sized to read at room
        // scale without crowding the cartridge slot (offset to the LEFT of stand center; the cluster favors the
        // right half).
        var faceZ = (stand.Center.Y + stand.HalfExtents.Z);
        var faceY = (m_room.FloorY + (stand.HalfExtents.Y * 0.65f));
        var dPad = new Vector3((stand.Center.X - (stand.HalfExtents.X * 0.15f)), faceY, faceZ);
        var buttonA = new Vector3((stand.Center.X + (stand.HalfExtents.X * 0.45f)), (faceY + 0.06f), faceZ);
        var buttonB = new Vector3((stand.Center.X + (stand.HalfExtents.X * 0.25f)), (faceY - 0.06f), faceZ);

        return (dPad, buttonA, buttonB);
    }

    private Vector3 HiddenPosition() =>
        new(0f, (m_room.FloorY - 1000f), 0f);

    // Ease the workbench power-on glow toward the editor unlock (1 when EditorRevealed, else 0). A linear ramp on the
    // render clock — enough that the panel's emissive fades in smoothly; the discrete bucket quantization below keeps the
    // rebuild count bounded (~WorkbenchGlowBuckets rebuilds over the whole ramp, a boot's cost profile). Presentation
    // only; the sim never sees the glow.
    private void AdvanceWorkbenchGlow(float deltaSeconds) {
        var target = (m_editorRevealed ? 1f : 0f);
        var step = (WorkbenchGlowRate * MathF.Max(deltaSeconds, 0f));

        m_workbenchGlow = ((m_workbenchGlow < target)
            ? MathF.Min(target, (m_workbenchGlow + step))
            : MathF.Max(target, (m_workbenchGlow - step)));
    }

    // The current glow quantized into a bucket 0..WorkbenchGlowBuckets — the program-rebuild key for the workbench
    // panel's emissive (so the ramp rebuilds a bounded number of times, never once per frame at rest). 0 = dark, the
    // top bucket = fully lit.
    private int WorkbenchGlowBucket() =>
        (int)MathF.Round(Math.Clamp(value: m_workbenchGlow, min: 0f, max: 1f) * WorkbenchGlowBuckets);

    // Instance bounding-sphere radii (world units), each the worst-case reach of its wrapped geometry from the chosen
    // center plus a generous rounding/margin — a fat bound only costs a rare extra evaluation, a tight one clips
    // geometry at the tile boundary (the Post world-instanced stage's proven caller contract).
    // Stand: pedestal (half-extents length ~0.95) < screen slab (the farthest member, center offset + half-extents
    // length ~1.84) < housing/control cluster (~1.0) — 2.2 covers the worst member with ~20% headroom.
    private const float StandInstanceRadius = 2.2f;
    // Shelf bracket: half-extents length ~0.46 (anchor half-extents (0.3, 0.175, 0.3)) + round + margin.
    private const float ShelfInstanceRadius = 0.55f;
    // Player: half-extents length ~0.70 (PlayerHalfExtents) + round + margin.
    private const float PlayerInstanceRadius = 0.85f;

    // Bit s set = player slot s is OCCUPIED this frame — the parked/unparked state BuildProgram bakes each player box
    // against (a free slot's box is parked so the beam cull skips it). A change flips the rebuild trigger, exactly like
    // a boot mask change.
    private uint ActivePlayerMask() {
        var mask = 0u;

        for (var slot = 0; (slot < m_world.Slots.Count); slot++) {
            if (m_world.Slots[slot] is not null) {
                mask |= (1u << slot);
            }
        }

        return mask;
    }
    private SdfProgram BuildProgram(uint bootedMask, bool probeWorstCase = false) {
        // The render anchor IS the spawn anchor, so the room is authored directly in the spawn cell's local frame
        // (origin delta identically zero); the per-slot player boxes ride the dynamic-transform buffer, which the
        // frame source already feeds render-relative to the same anchor.
        var origin = Vector3.Zero;
        var builder = new SdfProgramBuilder();

        // SDF-DEBUG takeover: while the mode is up (and not probing), the program is ONLY the debug subject (+ optional
        // floor) at the world origin — the room is replaced. The probe below still emits the full room AND folds the
        // debug subject's worst case in (EmitProbe), so the frozen envelope covers both; a live debug program is a
        // strict subset of that sum.
        if (!probeWorstCase && m_sdfDebug.Active) {
            m_sdfDebug.Emit(builder: builder);

            // The sdf.grid verb's live A/B lever: OFF packs a DISABLED grid so the beam takes the flat per-instance
            // fallback over the same instances — grid-vs-flat beam cost measurable in one session, no rebuild.
            return builder.Build(buildInstanceGrid: m_sdfDebug.GridCull);
        }

        // AGB-DEBUG takeover: the native GBA scene is ONE fullscreen diegetic slab sampling the ARM7TDMI framebuffer
        // (see the OverworldFrameSource.AgbDebug partial). Like the SDF-debug branch it replaces the room; the probe
        // path never takes it (the room worst case strictly dominates one slab, so the frozen envelope already covers it).
        if (!probeWorstCase && m_agbActive) {
            return BuildAgbDebugProgram();
        }

        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.34f, 0.36f, 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.50f, 0.46f, 0.58f)));
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.52f, 0.18f)));
        var screenOffMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.08f, 0.09f, 0.10f)));
        var shelfMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.40f, 0.38f, 0.35f)));
        var controlMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.15f, 0.15f, 0.17f)));

        // THE ROOM CONTENT — floor, perimeter walls, console stands, and the shelf brackets. A studio scenario
        // (ScenarioBackdrop.Studio) SKIPS all of it: the workpiece is reviewed alone against the renderer's neutral
        // sky. Skipping is probe-safe — the worst-case probe (probeWorstCase) always emits the full room, so the
        // engine's buffers still reserve the room-framed ceiling; a studio program is a strict subset that fits. No
        // backdrop shape is added, so the worst-case envelope needs no new member.
        if (probeWorstCase || !m_scenarioStudio) {
        // Floor: a plane whose surface sits at world y = FloorY. In render-relative space (p = world − origin) the plane
        // equation dot(p, n) + offset must still vanish there, so offset = origin.Y − FloorY (= −FloorY when origin = 0).
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: (origin.Y - m_room.FloorY), material: floorMaterial);

        // Four perimeter walls as thin tall boxes. The half-thickness is the room's single source of truth, so these
        // visual walls and the collision clamp planes (FixedRoom.From) stay locked — a body rests flush against the
        // inner face drawn here.
        var wallHeight = 1.5f;
        var wallThickness = m_room.WallThickness;
        var minX = m_room.BoundsMin.X;
        var maxX = m_room.BoundsMax.X;
        var minZ = m_room.BoundsMin.Y;
        var maxZ = m_room.BoundsMax.Y;
        var midX = (0.5f * (minX + maxX));
        var midZ = (0.5f * (minZ + maxZ));
        var halfSpanX = (0.5f * (maxX - minX));
        var halfSpanZ = (0.5f * (maxZ - minZ));
        var wallCenterY = (m_room.FloorY + wallHeight);

        // Wall centers are rebased into render-relative space (− origin); the half-extents are differences, hence anchor-invariant.
        AddWall(builder: builder, center: (new Vector3(maxX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(minX, wallCenterY, midZ) - origin), halfExtents: new Vector3(wallThickness, wallHeight, halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, maxZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(midX, wallCenterY, minZ) - origin), halfExtents: new Vector3(halfSpanX, wallHeight, wallThickness), material: wallMaterial);

        // The console stands: each is a pedestal (its accent color, boot target + obstacle) with a screen slab
        // sitting on top facing the room (+Z, toward the player area — the stands sit near the −Z wall). An UNBOOTED
        // stand's screen is the powered-off dark box; a boot swaps that one instruction for a diegetic screen-surface
        // slab (identical instruction count, constant material table) whose world frame MATCHES the geometry:
        // worldOrigin sits on the slab's front face (center + halfExtents.Z along +Z, the face normal),
        // worldRight/worldUp are the slab's local +X/+Y in world space (no rotation is applied to the point before
        // the Box, so they are simply +X/+Y). The screen-surface table therefore changes ONLY on boot rebuilds —
        // exactly when the program re-uploads anyway.
        //
        // Each stand is ONE static per-object instance: pedestal, screen, cartridge-slot patch, control housing, and
        // the stand's 3 control-cluster pieces (which ride their own dynamic-transform slots but only travel a few
        // centimeters around the stand) all fold into a single bound centered on the pedestal — StandInstanceRadius
        // is sized generously past the farthest member (the screen slab) so the beam prepass's tile cull never clips
        // any of them. The control pieces are emitted HERE (not in the trailing loop the flat layout used) so the
        // whole stand is one CONTIGUOUS instruction range — BeginInstance/EndInstance cannot straddle a gap.
        for (var index = 0; (index < m_room.Consoles.Count); index++) {
            var stand = m_room.Consoles[index];
            var accent = m_consoleAccents[index % m_consoleAccents.Count];
            var bodyMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: accent));
            // The lit material is ALWAYS added (constant material table across rebuilds — the program buffer is sized
            // once); the boot bit only selects which index the screen references.
            var screenLitMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ((accent * 0.35f) + new Vector3(0.55f, 0.62f, 0.45f))));
            var screenMaterial = ((0u != (bootedMask & (1u << index))) ? screenLitMaterial : screenOffMaterial);
            var pedestalCenter = new Vector3(stand.Center.X, (m_room.FloorY + stand.HalfExtents.Y), stand.Center.Y);
            // Sized close to the GB's 10:9 aspect (width : height) so a bound screen source isn't grossly stretched.
            var screenHalfWidth = (stand.HalfExtents.X * 0.8f);
            var screenHalfExtents = new Vector3(screenHalfWidth, (screenHalfWidth / ScreenAspect), 0.08f);
            var screenCenter = new Vector3(stand.Center.X, (m_room.FloorY + (2f * stand.HalfExtents.Y) + screenHalfExtents.Y), stand.Center.Y);
            var screenFaceOrigin = (screenCenter + new Vector3(0f, 0f, screenHalfExtents.Z) - origin);
            var buttonMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.85f, 0.15f, 0.20f)));

            _ = builder.BeginInstance(boundCenter: pedestalCenter, boundRadius: StandInstanceRadius);
            _ = builder.ResetPoint().Translate(offset: (pedestalCenter - origin)).Box(halfExtents: stand.HalfExtents, round: 0.05f, material: bodyMaterial);

            // While creator mode is up (or probing), the preview easel BORROWS this cabinet's screen-surface slot
            // (CreatorSceneRenderer.PreviewScreenIndex) — the cabinet's screen degrades to its lit flat material for
            // the session and relights on exit. Same rebuild, same flag as the provider mux, so the surface table
            // and the sources can never disagree.
            var slotBorrowed = ((probeWorstCase || m_creator.Active) && (index == CreatorSceneRenderer.PreviewScreenIndex));

            if ((index < SdfProgramBuilder.MaxScreenSurfaces) && (0u != (bootedMask & (1u << index))) && !slotBorrowed) {
                _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).ScreenSlab(
                    halfExtents: screenHalfExtents,
                    round: 0.03f,
                    worldOrigin: screenFaceOrigin,
                    worldRight: Vector3.UnitX,
                    worldUp: Vector3.UnitY,
                    screenIndex: index
                );
            } else {
                // Unbooted (or past the engine's 4-surface cap — MaxConsoles is exactly that cap, a full house, so
                // the index guard is defense in depth): the powered-off dark screen, exactly the pre-diegetic look —
                // the boot rebuild swaps this one instruction for the screen-surface slab above (identical
                // instruction count either way).
                _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).Box(halfExtents: screenHalfExtents, round: 0.03f, material: screenMaterial);
            }

            // The stand's cartridge slot: a shallow notch-colored patch on the pedestal's front face (a visual seat,
            // not an obstacle — cartridges resolve against the stand box itself), so an inserted-but-unbooted
            // cartridge has a place to visibly sit even before the screen lights.
            var slotCenter = new Vector3((stand.Center.X - (stand.HalfExtents.X * 0.55f)), (m_room.FloorY + (stand.HalfExtents.Y * 1.5f)), (stand.Center.Y + stand.HalfExtents.Z - 0.02f));

            _ = builder.ResetPoint().Translate(offset: (slotCenter - origin)).Box(halfExtents: new Vector3(0.16f, 0.20f, 0.02f), round: 0.01f, material: bodyMaterial);

            // The control cluster: a static housing bump behind the animated pieces (the d-pad/button shapes ride
            // their own dynamic-transform slots below), so the cluster reads as recessed into the pedestal.
            var anchor = ControlClusterAnchor(stand: stand);
            var housingCenter = new Vector3(((anchor.DPad.X + anchor.A.X) * 0.5f), anchor.DPad.Y, (stand.Center.Y + stand.HalfExtents.Z - 0.01f));

            _ = builder.ResetPoint().Translate(offset: (housingCenter - origin)).Box(halfExtents: new Vector3((stand.HalfExtents.X * 0.7f), 0.12f, 0.01f), round: 0.02f, material: controlMaterial);

            // The stand's animated control cluster: a d-pad cross (two crossed boxes) and two round buttons (A/B),
            // one dynamic-transform slot per piece — folded into THIS stand's instance (they ride a dynamic slot but
            // only travel a few centimeters, well inside StandInstanceRadius's margin). PackControlTransforms
            // depresses/tilts pressed pieces every frame; the instruction count here never changes.
            var baseSlot = (m_controlSlotBase + (index * ControlsPerConsole));

            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(0.09f, 0.03f, 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(0.03f, 0.09f, 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + AButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + BButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.EndInstance();
        }

        // The cartridge shelf: one static wall-mounted bracket per shelf slot (a simple slab, always present). The
        // brackets are inert wall furniture — the cart choice lives at the cabinet, so no cartridge boxes rest on
        // them. One static instance PER SLOT (not one for the whole strip): a full 8-slot shelf spans ~15 world
        // units, so a single enclosing sphere would cover most of the room and defeat the tile cull; per-slot bounds
        // stay tight (ShelfInstanceRadius).
        for (var index = 0; (index < m_room.Shelf.Count); index++) {
            var anchor = m_room.Shelf[index];
            var bracketCenter = new Vector3(anchor.Center.X, (m_room.FloorY + (anchor.HalfExtents.Y * 0.5f)), anchor.Center.Y);

            _ = builder.BeginInstance(boundCenter: bracketCenter, boundRadius: ShelfInstanceRadius);
            _ = builder.ResetPoint().Translate(offset: (bracketCenter - origin)).Box(halfExtents: new Vector3(anchor.HalfExtents.X, (anchor.HalfExtents.Y * 0.5f), anchor.HalfExtents.Z), round: 0.03f, material: shelfMaterial);
            _ = builder.EndInstance();
        }

        // One player box per FIXED slot, placed by its per-frame dynamic transform (active slots at the player, free
        // slots hidden below the floor). One dynamic instance per slot: the bound tracks the slot's own position
        // (boundOffset zero — the box is centered on the slot). A FREE slot's box is PARKED (Active=false) so the beam
        // cull skips it with one branch instead of testing its hidden sphere per tile; a join/leave flips the active
        // mask and rebuilds (ActivePlayerMask), so the reserved slot count is unchanged — the once-sized buffers stay
        // valid. The probe keeps every slot active (worst case). Room content too — a studio review shows the workpiece
        // alone, never a player avatar.
        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var occupied = (probeWorstCase || ((slot < m_world.Slots.Count) && (m_world.Slots[slot] is not null)));

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: PlayerInstanceRadius, active: occupied);
            _ = builder.ResetPoint().TransformDynamic(slot: slot).Box(halfExtents: m_room.PlayerHalfExtents, round: 0.06f, material: playerMaterial);
            _ = builder.EndInstance();
        }

        // THE DIEGETIC WORKBENCH (Stage 3): the room-only editor prop — a desk + a terminal panel that lights up when
        // the editor reveal fires. Emitted with the room content; the probe always emits it in its LIT (brightest)
        // form, so its one static instance + words join the worst-case envelope — MeasureWorstCaseEnvelope's binding
        // rule for any new emission. Its panel's emissive is proportional to the eased glow (0 dark → peak lit).
        EmitWorkbench(builder: builder, origin: origin, probeWorstCase: probeWorstCase);
        }

        // The CREATOR pool: the scene's palette + ghost + one instance per placed-shape slot, emitted by the
        // renderer (Puck.Demo.Creator.CreatorSceneRenderer). Slot/material counts are constant across rebuilds; the
        // per-slot instruction count may vary below the probed worst case (MeasureWorstCaseEnvelope), which the
        // engine's buffers were sized against.
        // Studio suppresses the preview EASEL (the post + bake-preview screen slab) AND every creator-mode ADORNMENT —
        // the placement ghost, the RIG's goal markers, and the selection highlight — so a review shows the CREATURE
        // alone, never a floating cursor/marker photobombing the shot. Probe-safe — the worst-case probe still emits
        // all of it (probeWorstCase wins inside EmitPool), so the reserved buffer ceiling is unchanged; studio's program
        // is a strict subset. Suppression is by emission, not scene-state mutation (sticky ghost/selection fields stay).
        m_creatorRenderer.EmitPool(builder: builder, probeWorstCase: probeWorstCase, suppressEasel: m_scenarioStudio, suppressAdornments: m_scenarioStudio);

        // The WORLD SCULPTOR's authored content: terrain, lights, override ghosts, every stamped placement (each a
        // static instance), and its two dynamic slots — emitted by Puck.Demo.World.WorldSceneRenderer under the same
        // worst-case-probe discipline as the creator pool (the synthetic probe covers the densest legal scene).
        m_worldRenderer.EmitWorld(builder: builder, probeWorstCase: probeWorstCase);

        // The COMPANION pool: presentation-only sculpted creatures (roots + shape slots reserved for the full
        // roster regardless of how many are loaded — constant slot topology, same probe discipline). A screen-faced
        // companion's face slab lands on the slot the ScreenSlotLedger granted it this pass, read through
        // CompanionFaceSlot (the ledger owns that resolved slot — F-STATE-2, no mirror on CompanionState). The probe
        // needs no resolver (it emits its own placeholder-index face slab unconditionally).
        m_companionRenderer.EmitCompanions(builder: builder, faceSlotResolver: CompanionFaceSlot, probeWorstCase: probeWorstCase);

        // THE DIEGETIC LINK CABLE (whimsy): a static instance emitted ONLY while two cabinets are linked (or under
        // the worst-case probe, which always emits it — see EmitLinkCable's remarks).
        EmitLinkCable(builder: builder, origin: origin, probeWorstCase: probeWorstCase);

        // THE STUDIO CYCLORAMA: the neutral gray backdrop shell, emitted only for a studio review (or under the probe,
        // which always emits it so the worst-case envelope covers it — MeasureWorstCaseEnvelope's binding rule for any
        // optional emission). One static instance; its bound intentionally spans the scene (it IS the background every
        // missed-ray tile needs — there is nothing else large to cull against in a studio review).
        EmitStudioBackdrop(builder: builder, origin: origin, probeWorstCase: probeWorstCase);

        // THE SDF-DEBUG subject's worst case joins the probe (a full MaxOps stack + the wordiest shape + floor), so the
        // frozen envelope covers a live debug program — MeasureWorstCaseEnvelope's binding rule for any new emission.
        // Only the probe path reaches here (a live debug frame returned at the early branch above).
        if (probeWorstCase) {
            m_sdfDebug.EmitProbe(builder: builder);
        }

        return builder.Build();
    }

    // The diegetic WORKBENCH prop (Stage 3): a desk (a thin slab on two legs) with a stout terminal SCREEN PANEL, one
    // static instance bounded on the desk center (WorkbenchInstanceRadius). Room-only — it is never a paned cabinet
    // (the four view slots are all spoken for). The panel is DARK (a matte off-screen box) by default and lights with
    // an EMISSIVE CRT glow as the eased glow ramps up on the editor reveal — so it reads as "the workshop opens." The
    // WORD/INSTANCE COUNT IS CONSTANT regardless of the glow (only the panel material's emissive scalar changes), so
    // the eased rebuilds never resize a buffer; the probe bakes the LIT peak (worst case is identical in size, but the
    // probe form is unconditional to satisfy MeasureWorstCaseEnvelope's binding rule for any new emission).
    private void EmitWorkbench(SdfProgramBuilder builder, Vector3 origin, bool probeWorstCase) {
        var (centerX, centerZ) = m_world.WorkbenchCenterLocal;
        // The panel's emissive: the probe uses the LIT peak (envelope worst case); a live build scales the peak by the
        // eased glow, so the powered-off panel is a plain matte box (emissive 0) and a fully-revealed one glows.
        var glow = (probeWorstCase ? 1f : Math.Clamp(value: m_workbenchGlow, min: 0f, max: 1f));
        var deskMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.30f, 0.26f, 0.22f)));
        var frameMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.12f, 0.13f, 0.15f)));
        // The lit panel: a cool CRT teal that brightens from near-black (dark) to a self-illuminated glow. The albedo
        // itself lifts a touch with the glow so the OFF panel reads as a dead dark screen, not a dim colored one.
        var panelAlbedo = Vector3.Lerp(value1: new Vector3(0.05f, 0.06f, 0.07f), value2: new Vector3(0.45f, 0.85f, 0.70f), amount: glow);
        var panelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: panelAlbedo, Emissive: (WorkbenchLitEmissive * glow)));

        var floorY = m_room.FloorY;
        var deskCenter = new Vector3(centerX, (floorY + WorkbenchDeskTopY), centerZ);
        var legY = (floorY + WorkbenchLegHalfHeight);
        var legFrontZ = (centerZ - (WorkbenchDeskHalfDepth * 0.7f));
        var legBackZ = (centerZ + (WorkbenchDeskHalfDepth * 0.7f));
        var legLeftX = (centerX - (WorkbenchDeskHalfWidth * 0.8f));
        var legRightX = (centerX + (WorkbenchDeskHalfWidth * 0.8f));
        // The monitor rides above the desk, tilted slightly back. Its dark backing/hood sits BEHIND (+Z) the glowing
        // panel; the emissive panel is pushed clearly IN FRONT (−Z, the room-facing side the player approaches) so the
        // lit face is the surface every ray hits first — never occluded by the hood (the sliver-behind-a-frame bug).
        var monitorCenter = new Vector3(centerX, (deskCenter.Y + WorkbenchPanelRiseY), centerZ);
        var panelTilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: WorkbenchPanelTiltRadians);

        _ = builder.BeginInstance(boundCenter: deskCenter, boundRadius: WorkbenchInstanceRadius);
        // Desktop slab.
        _ = builder.ResetPoint().Translate(offset: (deskCenter - origin)).Box(halfExtents: new Vector3(WorkbenchDeskHalfWidth, WorkbenchDeskHalfHeight, WorkbenchDeskHalfDepth), round: 0.03f, material: deskMaterial);
        // Two legs (front-left, back-right — a diagonal pair reads as support without four separate members).
        _ = builder.ResetPoint().Translate(offset: (new Vector3(legLeftX, legY, legFrontZ) - origin)).Box(halfExtents: new Vector3(0.05f, WorkbenchLegHalfHeight, 0.05f), round: 0.01f, material: deskMaterial);
        _ = builder.ResetPoint().Translate(offset: (new Vector3(legRightX, legY, legBackZ) - origin)).Box(halfExtents: new Vector3(0.05f, WorkbenchLegHalfHeight, 0.05f), round: 0.01f, material: deskMaterial);
        // The monitor's dark matte backing plate: sits directly BEHIND the panel along the tilted local +Z (the
        // laid-back screen's underside), a hair thinner and the same footprint, so it gives the terminal a solid body
        // without ever occluding the up-facing glowing face the camera and player both see.
        _ = builder.ResetPoint().Translate(offset: (monitorCenter - origin)).Rotate(rotation: panelTilt).Translate(offset: new Vector3(0f, 0f, (WorkbenchPanelHalfDepth * 1.6f))).Box(halfExtents: new Vector3((WorkbenchPanelHalfWidth + 0.04f), (WorkbenchPanelHalfHeight + 0.04f), (WorkbenchPanelHalfDepth * 0.6f)), round: 0.03f, material: frameMaterial);
        // The screen panel: dark box when locked, an emissive CRT glow when revealed. Tilted back so its lit face
        // points up-and-toward the room — visible from a floor-level approach AND the high iso reveal camera. Emissive
        // lifts every visible face uniformly, so the whole panel reads as powered-on.
        _ = builder.ResetPoint().Translate(offset: (monitorCenter - origin)).Rotate(rotation: panelTilt).Box(halfExtents: new Vector3(WorkbenchPanelHalfWidth, WorkbenchPanelHalfHeight, WorkbenchPanelHalfDepth), round: 0.02f, material: panelMaterial);
        _ = builder.EndInstance();
    }

    // Two thin dark capsules sagging from each linked cabinet's screen-top to a shared low midpoint, plus a small
    // sphere marking the sag — ONE static instance (SmoothUnion between the two capsules + the sag sphere, INSIDE
    // this instance only: the cull-safety contract forbids smooth-blending across an instance boundary you want
    // maskable, but blending members WITHIN one instance is exactly what the contract allows). The probe emits it
    // unconditionally at a synthetic (0,1) pair so the worst-case word/instance count always covers a real cable —
    // MeasureWorstCaseEnvelope's binding rule (any new optional emission joins the probe in the same change).
    private void EmitLinkCable(SdfProgramBuilder builder, Vector3 origin, bool probeWorstCase) {
        if (!probeWorstCase && (m_currentLinkedPair is null)) {
            return;
        }

        var (indexA, indexB) = (probeWorstCase ? (0, Math.Min(1, m_room.Consoles.Count - 1)) : m_currentLinkedPair!.Value);

        if ((indexA < 0) || (indexB < 0) || (indexA >= m_room.Consoles.Count) || (indexB >= m_room.Consoles.Count)) {
            return; // fewer than two consoles exist (a bare-room probe) — nothing to swag a cable between.
        }

        // World-space (== render-relative here; the render anchor IS the spawn anchor, so origin is always zero —
        // see BuildProgram's own comment) screen-top positions, exactly matching ScreenCenterLocal's placement.
        var cableMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.05f, 0.05f, 0.06f)));
        var topA = ScreenCenterLocal(consoleIndex: indexA);
        var topB = ScreenCenterLocal(consoleIndex: indexB);
        var sag = (Vector3.Lerp(value1: topA, value2: topB, amount: 0.5f) - new Vector3(0f, CableSagDrop, 0f));
        var boundCenter = Vector3.Lerp(value1: topA, value2: topB, amount: 0.5f);
        var boundRadius = (Vector3.Distance(value1: topA, value2: topB) * 0.5f + CableSagDrop + CableSagRadius + 0.2f);

        _ = builder.BeginInstance(boundCenter: boundCenter, boundRadius: boundRadius);
        _ = builder.ResetPoint().Translate(offset: (topA - origin)).Capsule(endpoint: (sag - topA), radius: CableRadius, material: cableMaterial, blend: SdfBlendOp.Union);
        _ = builder.ResetPoint().Translate(offset: (topB - origin)).Capsule(endpoint: (sag - topB), radius: CableRadius, material: cableMaterial, blend: SdfBlendOp.SmoothUnion, smooth: CableSmooth);
        _ = builder.ResetPoint().Translate(offset: (sag - origin)).Sphere(radius: CableSagRadius, material: cableMaterial, blend: SdfBlendOp.SmoothUnion, smooth: CableSmooth);
        _ = builder.EndInstance();
    }
    // The studio cyclorama: a large neutral-gray SHELL centered on the workbench (the orbit target) that the review
    // camera sits inside — every ray missing the workpiece lands on its uniform inner wall, so the backdrop reads as a
    // deliberate flat gray field at all eight angles instead of the near-black sky. ONE static instance; Onion shells
    // the sphere so the inside-out camera hits a real surface (a solid sphere is negative from within and never marches
    // to a hit). The probe emits it unconditionally so the worst-case word/instance count always covers it —
    // MeasureWorstCaseEnvelope's binding rule (any optional emission joins the probe in the same change).
    private void EmitStudioBackdrop(SdfProgramBuilder builder, Vector3 origin, bool probeWorstCase) {
        if (!probeWorstCase && !m_scenarioStudio) {
            return;
        }

        var backdropMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: StudioBackdropAlbedo));
        var floorY = (m_creator.Workbench.MinY - StudioFloorDrop);

        // The sweep floor: an unbounded half-space plane (like the room floor), so it renders for the camera above it
        // from every orbit angle without the enclosing-shell trick that fought the tile cull. A plane is a field
        // primitive (no instance bound needed — the room's floor is emitted the same way).
        _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: (origin.Y - floorY), material: backdropMaterial);
    }
    private static void AddWall(SdfProgramBuilder builder, Vector3 center, Vector3 halfExtents, int material) {
        _ = builder.ResetPoint().Translate(offset: center).Box(halfExtents: halfExtents, round: 0f, material: material);
    }
}
