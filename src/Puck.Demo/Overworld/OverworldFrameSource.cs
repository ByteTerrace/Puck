using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Puck.Abstractions.Gpu;
using Puck.Assets;
using Puck.Authoring;
using Puck.Cameras;
using Puck.Compositing;
using Puck.Demo.Configuration;
using Puck.Demo.Creator;
using Puck.Demo.DevConsole;
using Puck.Demo.Garden;
using Puck.Demo.Museum;
using Puck.Demo.Rts;
using Puck.Demo.World;
using Puck.HumbleGamingBrick;
using Puck.Input.Devices;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.Text;

namespace Puck.Demo.Overworld;

/// <summary>
/// Bridges the deterministic <see cref="OverworldWorld"/> to the SDF renderer. Its program content is composed from
/// named <see cref="ISdfSceneEmitter"/>s (the room shell, the console stands, the player boxes, the workbench, the
/// terminal, the diegetic bar, the creator/world/companion pools, the link cable, the studio backdrop — see
/// <c>OverworldFrameSource.Emitters.cs</c>) over a <see cref="SdfCompositionFrameSource"/>, so a console boot or a
/// creator/world edit rebuilds exactly like any other composed content change; the SDF-debug takeover
/// (<see cref="Puck.SdfVm.Debug.SdfDebugMode"/>) is a SEPARATE, alternate composition (never mixed into the room
/// list — see <see cref="ISdfSceneEmitter"/>'s takeover remarks), and the native AGB takeover is a single static
/// program (its worst case is strictly dominated by the room's, so it needs no probe of its own — see
/// <see cref="BuildAgbDebugProgram"/>). This source implements <see cref="ISdfFrameDresser"/> itself: the composed
/// program/transforms are "dressed" into a full <see cref="SdfFrame"/> by <see cref="Dress"/>, which owns the
/// screen-director view layout, the room mood (ambient/sun/grid-overlay), and every debug frame-channel flag —
/// exactly the presentation concerns that have nothing to do with WHAT geometry exists.
/// <para>
/// Everything is rendered relative to the room's SPAWN ANCHOR — the room is bounded (±8 world units), so a FIXED
/// anchor keeps every float small AND immovable; an anchor that followed the players (the unbounded-world pattern)
/// flipped across its snap grid at the room's center lines and made the smoothed camera race the jump. A player,
/// cartridge, or control moves purely by changing the per-frame dynamic-transform buffer (a free player slot's box
/// rides a hidden position) and the per-frame view regions.
/// </para></summary>
public sealed partial class OverworldFrameSource : ISdfFrameSource, IOverworldControlHost, ISdfFrameDresser {
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
    private const int AButtonControlOffset = 1;
    private const int BButtonControlOffset = 2;
    private const int DPadControlOffset = 0;
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
    private static readonly Vector3 StudioBackdropAlbedo = new(x: 0.55f, y: 0.56f, z: 0.58f);
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
    private const float CableSagDrop = 0.6f;
    private const float CableSagRadius = 0.05f;
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
    // cost profile); once at 0 or 1 it is settled and stops rebuilding. The linear ramp is SmoothStep-shaped (in BOTH
    // the bucket key and EmitWorkbench's read — one easing, so the quantized rebuilds and the baked emissive always
    // agree) so the power-on accelerates then settles rather than ramping flatly. Quantized into buckets for the
    // rebuild key so the ramp rebuilds a bounded number of times, never once per frame indefinitely; SmoothStep's slow
    // tails mean the near-0/near-1 buckets rarely flip, so the extra buckets buy finer mid-ramp steps (where the ease
    // is steepest) without adding rebuilds at the ends.
    // NOTE: this is Q33's easing fallback, not the glow-as-frame-channel ideal — the emissive is still BAKED per
    // material (SdfMaterialData in sdf-vm.hlsli), so a per-frame channel would need shared-engine shader surgery.
    private const float WorkbenchGlowRate = 1.45f;
    private const int WorkbenchGlowBuckets = 16;
    // The editor-reveal beat (Q35): a one-shot swell as the workshop opens. Duration ~0.9 s; the bell (sin over the
    // span) peaks mid-beat then returns to rest, so BOTH the light pulse and the camera nudge start and end at zero
    // (no snap). The gain lifts the room light ~35% at the peak (a warm bloom); the target lift nudges the room
    // camera's look-point up a touch (a subtle "glance" as the light comes up). Presentation-only tunables.
    private const float EditorRevealBeatSeconds = 0.9f;
    private const float EditorRevealBeatLightGain = 0.35f;
    private const float EditorRevealBeatTargetLift = 0.35f;

    // ---- The diegetic console terminal (diegetic-UI Tier 0) ---------------------------------------------------------
    // A physical terminal object whose CRT screen live-mirrors the developer console (the same lines the overlay shows /
    // stdout echoes) — the control plane made flesh. Its screen SOURCE rides the generic dynamic-claimant seam
    // (RegisterScreenClaimant) at one HEADROOM slot, resolving the console named feed the diegetic-feed director
    // publishes; the geometry is a modest desk + a room-facing CRT slab emitted with the room (EmitTerminal). Display-
    // only this tier: input stays pad + overlay console + stdin.
    //
    // Placed against the +Z (near) wall — the only wall free of cabinets (−Z), the shelf (−X), and the workbench (+X) —
    // offset toward −X so it sits off-center, its CRT facing −Z back into the room (across from the cabinet row). The
    // face uses the +Z cabinet convention mirrored 180° about Y (worldRight −X, worldUp +Y, front face on −Z), so the
    // console image reads un-mirrored exactly as a cabinet's does from its own front. Visual-only (no collision entry —
    // the sim's FixedRoom never learns it exists), like the workbench.
    internal const int TerminalScreenSlot = 5;            // a free ScreenSlotLedger headroom slot (4-7; 4 is the AGB debug slot); internal so the render assembly can bind the terminal's glyph decal to this slot

    private const float TerminalInsetX = 3.0f;            // the terminal center's inset from the −X wall (clear of a full shelf)
    private const float TerminalInsetZ = 1.15f;           // its inset from the +Z (near) wall
    private const float TerminalPedestalHalfDepth = 0.34f;
    private const float TerminalPedestalHalfHeight = 0.46f;
    private const float TerminalPedestalHalfWidth = 0.42f;
    private const float TerminalScreenHalfWidth = 0.44f;  // the CRT slab half-width; height derives from the 4:3 CRT aspect
    private const float TerminalScreenHalfDepth = 0.06f;  // the slab thickness
    private const float TerminalScreenGap = 0.06f;        // the gap between the pedestal top and the slab's bottom edge
    private const float TerminalInstanceRadius = 1.3f;    // covers the pedestal + hood + CRT slab in one bound
    private const float TerminalCrtAspect = (4f / 3f);    // matches ConsoleFeed's 256x192 (4:3) CRT so the slab samples undistorted

    // ---- The diegetic UI bar (Tier 2) worst-case envelope --------------------------------------------------------
    // The bar is ONE dynamic instance; these bound its worst case so MeasureWorstCaseEnvelope reserves it BEFORE the
    // director installs (the probe runs at spec construction, ahead of Build's InstallDiegeticUi — so this source
    // reserves the envelope self-containedly, mirroring EmitTerminal's unconditional-lit probe form). The real emit
    // (DiegeticUiDirector.Emit) is a strict subset of the synthetic probe below. KEEP IN SYNC with the director's
    // MaxChips / MaxLabelChars.
    private const int DiegeticBarMaxChips = 12;           // the primary bar's twelve physical slots
    private const int DiegeticBarMaxLabelChars = 2;       // the longest button label (e.g. "LB"/"RT")
    private const int DiegeticUiSlotCount = 1;            // the whole bar rides one dynamic-transform slot (its rig pose)
    private const float DiegeticBarBoundRadius = 1.20f;   // KEEP IN SYNC with DiegeticUiDirector.BoundRadius
    private const int DiegeticNameplateMaxChars = 6;      // "PUCK/1" — KEEP IN SYNC with DiegeticUiDirector.NameplateText
    private const int DiegeticTitleMaxChars = 11;         // "HUB TRACKER" — the hub readout; KEEP IN SYNC with DiegeticUiDirector.TitleEmHeight's remarks

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
    // The creating slot's rendered position this frame (the world-sculpt camera's anchor) — mirrors
    // m_playerRenderTransforms[0].Position, kept as its own field for the existing WorldSculptCameraFrame reader.
    private Vector3 m_primaryPlayerRenderPosition;
    // The companion pool (presentation-only creatures) — composed here like the creator/world trios.
    private readonly CompanionRoster m_companions = new();
    private readonly Creator.CompanionEmitter m_companionEmitter;
    // The roster size the current program was built for (add/del = a structural rebuild; the slot topology is
    // constant, only the reserved slots' content changes) — folded into m_roomRevision (see AdvanceRoomRevision).
    private int m_builtCompanionCount;
    private readonly IReadOnlyList<Vector3> m_consoleAccents;
    // Reads the buttons currently applied to a console's machine this frame (GamingBrickChildNode.CurrentButtons) —
    // the SAME per-frame joypad state the machine consumes, never re-derived from raw input. Null (bare-room mode,
    // no consoles) means every control cluster stays in its neutral pose.
    private readonly Func<int, JoypadButtons>? m_controlsSource;
    // Reused render-relative position buffer for the screen director — Cleared+refilled each frame (no per-frame alloc).
    private readonly List<Vector3> m_activePositions = new(capacity: OverworldWorld.MaxPlayers);
    // Every FIXED player slot's last-packed render-relative transform (see PackPlayerRenderTransforms) — computed
    // FRESH each CaptureFrame BEFORE the active composition source packs the room's dynamic-transform buffer (so
    // PlayerBoxEmitter bakes THIS frame's positions), which makes it exactly ONE FRAME STALE for anything that reads
    // it EARLIER in the same CaptureFrame call (NearestActivePlayer, the world-sculpt camera target via
    // m_primaryPlayerRenderPosition) — the same lag every other diegetic-feed anchor in this source already carries.
    private readonly DynamicTransform[] m_playerRenderTransforms = new DynamicTransform[OverworldWorld.MaxPlayers];
    // This frame's screen-director view layout, computed once in CaptureFrame (BEFORE the active composition source
    // packs) so the diegetic UI bar's rig-mount transform (DiegeticBarEmitter.PackDynamicTransforms) tracks the room
    // camera with NO added lag, and so Dress can hand it straight to the returned SdfFrame.
    private IReadOnlyList<SdfViewSnapshot> m_currentViews = [];
    // ---- The diegetic UI (Tier 2): the overlay action bar mirrored as camera-rig-mounted SDF geometry -------------
    // The whole bar rides ONE dynamic-transform slot (its rig-facing pose, repositioned each frame from the room
    // camera); the geometry is baked in bar-local space and REBUILT only when the bar's content signature changes. The
    // Tier-2 composition (the glyph atlas + the bar emission) lives in the composed DiegeticUiDirector, wired here
    // through a pure DELEGATE seam (InstallDiegeticUi) so this source — at its exact CA1506 coupling ceiling — names no
    // new type to host it (the delegate arities are already in its coupling set; the atlas rides an ISdfFrameSource
    // decorator the render assembly wraps, never this source's own GlyphAtlas override).
    // The bar's per-frame rig transform (from the director), the content signature that triggers a bar rebuild, and the
    // emit callback that lays the bar's panels + embossed labels. Null until InstallDiegeticUi runs (a run with no
    // binding-bar store — e.g. a bare validation harness — leaves the bar absent, its reserved slot simply unused).
    private Func<IReadOnlyList<SdfViewSnapshot>, DynamicTransform>? m_diegeticMount;
    private Func<int>? m_diegeticSignature;
    private Action<SdfProgramBuilder>? m_diegeticEmit;
    // The bar's own emitter (see the room-composition list in the constructor) — kept typed so InstallDiegeticUi can
    // read the ACTUAL dynamic-transform slot the composition host assigned it (see DiegeticBarEmitter.SlotBase).
    private readonly DiegeticBarEmitter m_diegeticBarEmitter;
    // The diegetic bar's visibility latch (the `ui.diegetic on|off` verb) — DEFAULT ON so the feature shows the moment
    // a revealed room appears; the overlay bar stays regardless (they coexist by design this tier). A toggle rebuilds
    // the program (the bar geometry appears/vanishes), exactly like a cabinet boot/unboot. m_builtDiegeticVisible /
    // m_builtDiegeticSignature are what the current program baked, so a settled bar never rebuilds.
    private bool m_diegeticUiVisible = true;
    private bool m_builtDiegeticVisible = true;
    private int m_builtDiegeticSignature = -1;
    // The most recently DRESSED program/transforms (see Dress) — read by TickFeeds for the camera-feed pool's own
    // render pass, regardless of which composition (room, SDF-debug, or the AGB takeover) produced them.
    private SdfProgram? m_program;
    private IReadOnlyList<DynamicTransform> m_lastTransforms = [];
    // The last program object Dress actually saw — reference-diffed against the incoming one every call to detect a
    // real rebuild (SdfCompositionFrameSource always hands back the SAME instance across frames it did not rebuild,
    // and a rebuild always constructs a fresh one — see SdfCompositionFrameSource.CaptureFrame).
    private SdfProgram? m_lastDressedProgram;
    // The AGB takeover's ONE static program (see BuildAgbDebugProgram) — built once and reused forever (the framebuffer
    // it samples changes every frame, but the PROGRAM never does).
    private SdfProgram? m_agbProgram;
    // The room composition's shared "did the combined room content change" trigger (see AdvanceRoomRevision) — every
    // studio-suppressible room emitter without its own independent Revision (see Emitters.cs) shares this ONE counter,
    // exactly mirroring the old single-program-single-trigger discipline: the whole room rebuilds together, as it
    // always has, just expressed as a monotonic counter instead of a hand-rolled bool.
    private int m_roomRevision;
    // The boot mask the current program's screen materials were chosen under; a boot rebuilds the program.
    private uint m_programBootedMask;
    // The set of OCCUPIED player slots the current program parked/unparked against (bit s = slot s has a player). An
    // empty slot's player box is PARKED so the beam cull skips it; a join/leave flips a bit and rebuilds the program
    // (one envelope-safe rebuild, exactly like a boot). -1 forces the first build.
    private uint m_builtActivePlayerMask = 0xFFFFFFFFu;
    private float m_time;
    // The engine-side anchor registry is republished every CaptureFrame via PublishAnchors: the
    // room's real player bodies, its spawn anchor, and each console's screen face, all under stable names a future
    // rig/consumer resolves by id (ISdfAnchorSource) rather than reaching into this class's private fields. This
    // per-frame pose data flows through SdfEmitContext.InterpolationAlpha.
    private readonly SdfAnchorTable m_anchorTable = new();
    // The two alternate composition sources (see the class remarks): the room (this source's default content) and the
    // SDF-debug takeover. Both share this source as their ISdfFrameDresser. Built once, at the end of the constructor
    // (after every field an emitter's worst-case probe might read is already assigned).
    private readonly SdfCompositionFrameSource m_roomComposition;
    private readonly SdfCompositionFrameSource m_sdfDebugComposition;
    // THE WORKBENCH power-on glow (Stage 3): 0 = dark/locked, 1 = fully lit. It eases toward the target the editor
    // unlock implies (EditorRevealed → 1) on the render clock in CaptureFrame — presentation only, never hashed. The
    // program's workbench panel bakes an emissive proportional to this, so the ramp rebuilds the program (like a boot)
    // as it crosses quantized buckets; m_builtWorkbenchBucket is the bucket the current program baked, so a settled
    // glow (bucket unchanged) never rebuilds. The FALLBACK bucket (-1) forces the first build.
    private float m_workbenchGlow;
    private int m_builtWorkbenchBucket = -1;
    // THE DETERMINISTIC GARDEN's own room-revision trigger: an occupied-count + per-slot growth-stage-bucket
    // signature (see GardenSignature) — a plant/clear or a stage advance rebuilds the room exactly like a boot does.
    // -1 forces the first build.
    private int m_builtGardenSignature = -1;
    // THE EDITOR-REVEAL BEAT (Q35): a one-shot choreographed flourish armed on the EditorRevealed false→true edge (see
    // the setter in OverworldFrameSource.Control). It rides the render clock in CaptureFrame — presentation only, never
    // hashed — driving a bell-shaped room-light pulse (a transient lift on the AmbientScale/SunScale room-light seam)
    // and a small room-camera target nudge (through the director's RoomTargetNudge hook, riding its existing easing) so
    // "the workshop opens" reads as a warm swell + glance rather than a bare latch. Starts ELAPSED (= duration) so no
    // beat plays until armed; advances to the duration then rests.
    private float m_editorRevealBeatTime = EditorRevealBeatSeconds;
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
    private readonly CreatorSceneEmitter m_creatorEmitter;
    private readonly CreatorController m_creatorController;
    // The live bake-preview seam the easel's screen slab samples — NULL until the bake pipeline's service replaces it
    // (ConnectBakePreview); a null seam reads as a powered-off easel (handle 0 / zero glow). Kept nullable so this
    // source names one fewer type — it
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
    // The world sculptor's composition (scene/renderer/controller/history/store) — see the ctor's trio comment.
    private readonly ContentAddressedStore m_worldStore;
    private readonly WorldScene m_worldScene;
    private readonly EditHistory<WorldScene.Snapshot> m_worldHistory;
    private readonly WorldSceneEmitter m_worldEmitter;

    /// <summary>Binds the SHARED world-glyph atlas onto the world-scene emitter, AFTER the atlas-owning
    /// <c>DiegeticUiDirector</c> is composed (the town's façade signage lays out against that one atlas) — forwards to
    /// <see cref="WorldSceneEmitter.SetGlyphAtlas"/> so this source stays at its CA1506 ceiling and never names the
    /// atlas type itself.</summary>
    /// <param name="font">The shared font atlas.</param>
    internal void SetWorldGlyphAtlas(FontAtlas? font) => m_worldEmitter.SetGlyphAtlas(font: font);

    private readonly WorldSculptController m_worldController;
    private bool m_worldSculptActive;

    // The workbench authoring hub is a one-shot mode picker the lit workbench prop opens. Bare
    // primitives (bool + int) so this source — at its exact analyzer coupling ceiling — takes on no new type; the
    // registry that gives the selection meaning is reached only through the node's ForgeCommands forwarders.
    private bool m_hubActive;
    private int m_hubSelection;

    // SDF-DEBUG mode (the fullscreen single-shape debug tool): the whole mode (scene + orbit controller + emitter) is
    // composed behind ONE facade type — this source is at its exact analyzer coupling ceiling and cannot name three
    // (it sheds NullCreatorBakePreview's reference to make room; see m_bakePreview). Presentation only — the sim never
    // sees it. While the mode is up, m_sdfDebugComposition (an alternate composition — see the ctor) REPLACES the room
    // with the debug subject; its own aggregate Revision (SdfDebugEmitter.Revision => m_sdfDebug.Revision) drives its
    // rebuilds, so this source tracks no mirror of it.
    private readonly Puck.SdfVm.Debug.SdfDebugMode m_sdfDebug = new();

    // The REVEALED-ROOM fixed-camera perf-bench channel (room.bench) — a diagnostic distinct from m_sdfDebug's own
    // SdfBenchScene (which swaps the program to a synthetic gallery workload): this one changes nothing about what is
    // rendered, it only pins the camera over the LIVE room content via the director's existing ScenarioCameraPose
    // seam (the --scenario harness's own verbatim-pose primitive) and samples SdfEngineNode's per-pass GPU timings.
    // See RoomBenchScene + OverworldFrameSource.RoomBench.cs.
    private readonly RoomBenchScene m_roomBench = new();
    // Whether the pin is currently asserted on the director — tracked so the release-to-player edge (Running
    // false after having been true) fires exactly once (ApplyRoomBenchCameraPose).
    private bool m_roomBenchHeld;

    // The diegetic view stack contains every offscreen camera render, procedural face feed, and console-mirror CRT;
    // register against this ONE pool. InstallViews composes it once the render assembly's envelope is known; null
    // until then (a bare-room source with no view wiring pays nothing).
    private ViewStack? m_views;
    // The application services InstallFeeds was given — retained only so TickFeeds can resolve the GPU compute seam
    // for the face and console feeds' own ticking.
    private IServiceProvider? m_services;
    // Whether the resolved host backend is Direct3D 12 — retained from InstallFeeds so a camera view registered later
    // (RegisterCameraView, lazily on first request) selects the right kernel bytecode.
    private bool m_viewsHostOnDirectX;
    // The program-affecting revision state the view stack's content was LAST rebuilt for: bumped only when Dress sees
    // a genuinely new SdfProgram instance (see Dress's programChanged), so a view's own re-upload happens once per
    // real program change, never every frame (SdfCameraView/NestedWorldView both no-op an unchanged revision).
    private int m_viewProgramRevision;
    // The procedural face feed + the diegetic console terminal's CRT — presentation-only CPU-drawn producers this
    // source still owns and ticks directly (their own upload cadence, gated on "is anything showing me this frame"),
    // registered into m_views as unbudgeted GuestSurfaceView content so every consumer resolves them through the same
    // named-view vocabulary as a real camera. See PlanViews/TickViews.
    private readonly ProceduralFeed m_faceFeed = new();
    private ConsoleFeed? m_consoleFeed;
    // Persistent SdfCameraView instances by name — a camera view owns a real GPU resource (an offscreen engine), so
    // PlanViews REBINDS the same instance's Rig/AnchorSource each frame rather than constructing a fresh one (which
    // would rebuild the engine for nothing; see SdfCameraView's remarks). Never removed mid-run — a camera feed that
    // stops being wired simply is not rendered.
    private readonly Dictionary<string, SdfCameraView> m_cameraViews = new(comparer: StringComparer.Ordinal);
    // A reusable owner token for the world/creation camera-feed claimants' ledger participation (one token covers ALL
    // camera feeds this source publishes — they share the headroom band, and the render node reads their handles by
    // name, not by which slot each landed on). Reference-stable (boxed once).
    private readonly object m_cameraFeedClaimToken = new();
    // The diegetic console terminal's ledger owner token (one reference-stable claim on its headroom screen slot) and
    // its visibility latch (default ON — a permanent revealed-room fixture; the `terminal on|off` verb flips it as a dev
    // assist). m_builtTerminalVisible joins the rebuild trigger: hiding/showing swaps the CRT slab for a dark box (a
    // program-instruction change), exactly like a cabinet boot.
    private readonly object m_terminalClaimToken = new();
    private bool m_terminalVisible = true;
    private bool m_builtTerminalVisible = true;
    // THE REPLAY MUSEUM + THE DROSTE DOOR's ledger owner tokens: one per reserved headroom slot (the museum's four
    // screens, then the door's one), registered ONCE in InstallFeeds — permanent Anchored reservations with no
    // source of their own (content arrives only through `world.wire`, see MuseumRenderer's type remarks).
    private static readonly object[] MuseumClaimTokens = [new(), new(), new(), new(), new()];
    // The screen indices a world.wire / creation-face wired to a camera view or named view this frame (screen index ->
    // view name). Recomputed each CaptureFrame; the render node's per-slot override consults it (a wired slot samples
    // the named-view registry instead of its cabinet brick / flat material).
    private readonly Dictionary<int, string> m_wiredFeedByScreen = [];
    // Reused scratch for PlanViews — the SET of view names requested this frame (dedupes a name shared by both a face
    // and a world.wire) and, per name, the screen indices wired to it. Cleared and refilled each frame rather than
    // reallocated (the plan reruns every produced frame because a companion's live shape pose feeds each request).
    private readonly HashSet<string> m_requestedViewScratch = new(comparer: StringComparer.Ordinal);

    // The reveal transition state (see BeginRevealTransition's remarks).
    private const string RevealTransitionConsoleView = "reveal.console";
    private const string RevealTransitionRoomView = "reveal.room";
    private const float RevealTransitionSeconds = 0.6f;

    private ViewTransition? m_revealTransition;
    private float m_revealTransitionElapsed;

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

    /// <summary>An optional 0..1 GLOW intensity for the diegetic link cable, set by the render node from the linked
    /// ports' live serial-transfer state (SC bit 7, decayed) — the cable lights while a pair is exchanging bytes and
    /// fades when idle. Presentation-only whimsy: a pure read of emulated state, never fed back into it, and null (the
    /// default) leaves the cable its plain dark rubber. Read by <see cref="EmitLinkCable"/>.</summary>
    public Func<float>? LinkCableGlowSource { get; set; }

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

        // One reference-stable ledger claim token per console (see ScreenSlotLedger.Claim's ref-equality contract).
        m_cabinetClaimTokens = new object[m_room.Consoles.Count];
        for (var index = 0; (index < m_cabinetClaimTokens.Length); index++) {
            m_cabinetClaimTokens[index] = new object();
        }

        // The authoring workbench: a bounded region around the room's center (never near the stands along the far
        // wall), at the SAME floor height the room renders at.
        var workbench = new WorkbenchRegion(
            Center: new Vector3(
                x: (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
                y: m_room.FloorY,
                z: (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
            ),
            HalfExtent: WorkbenchHalfExtent,
            MaxY: (m_room.FloorY + 3.0f),
            MinY: (m_room.FloorY + 0.35f)
        );

        m_creator = new CreatorScene(workbench: workbench);
        m_creatorEmitter = new CreatorSceneEmitter(scene: m_creator);
        m_creatorController = new CreatorController(narrate: static line => Console.Error.WriteLine(value: line), scene: m_creator);
        // The workpiece camera: while creator is up, the room view leaves the player chase for the controller's
        // orbit/head-on framing; while WORLD-SCULPT is up, a lifted town-read orbit anchored on the creating slot
        // (the player IS the cursor — steep pitch, generous distance, so a street reads while stamping). Both ride
        // the director's one eased CreatorCameraSource seam (this source is the composition point).
        m_director.CreatorCameraSource = () => (AgbDebugCameraFrame() ?? (m_sdfDebug.CameraFrame ?? (m_creatorController.CameraFrame ?? WorldSculptCameraFrame())));
        // The perf bench's pose is a MEASUREMENT pose: applied verbatim (no easing) so every configuration samples an
        // identical, fully settled framing — see SdfDebugMode.CameraSnaps.
        m_director.CreatorCameraSnapSource = () => m_sdfDebug.CameraSnaps;
        // The editor-reveal beat's room-camera nudge (Q35): a transient look-point lift while the one-shot beat plays,
        // resolved at INVOKE time (each Compose) so it rides the director's own eased room framing rather than cutting.
        // Zero at rest, so a session that never reveals the editor frames byte-identically.
        m_director.RoomTargetNudge = EditorRevealBeatTargetNudge;
        // When a world (the town) is loaded, the fourth-wall reveal frames the WHOLE lot rather than the fixed
        // default-room overview — centred on the lot, pulled back to its bounds. Null while no world is applied, so
        // the default room's reveal framing is byte-unchanged.
        m_director.RoomFramingSource = () => ((m_appliedWorldCommit is null)
            ? (ScreenLayoutDirector.RoomFraming?)null
            : new ScreenLayoutDirector.RoomFraming(
                Center: new Vector3(x: (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)), y: (m_room.FloorY + 0.5f), z: (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))),
                HalfDepth: (0.5f * (m_room.BoundsMax.Y - m_room.BoundsMin.Y)),
                HalfWidth: (0.5f * (m_room.BoundsMax.X - m_room.BoundsMin.X))
            ));
        // Drive each pane camera from its cabinet's diegetic-screen center + the overworld's per-console closeness. The
        // screen center is a fixed room-local position; render-relative equals local here (the render origin is the
        // spawn anchor at local zero), so it shares the space of the active player positions the director frames.
        // Assigned ONCE: the lambda resolves current instance state (m_room, PaneCloseness) at INVOKE time (each
        // Compose), so it never needs per-frame recreation — a fresh capturing delegate every frame was pure churn.
        m_director.PaneCameraSource = paneIndex => (((paneIndex >= 0) && (paneIndex < m_room.Consoles.Count))
            ? (ScreenCenterLocal(consoleIndex: paneIndex), (PaneCloseness?.Invoke(paneIndex) ?? 0f), ScreenHalfHeightLocal(consoleIndex: paneIndex))
            : ((Vector3, float, float)?)null);

        // The WORLD SCULPTOR: the scene/renderer/controller trio mirrors the creator pool's composition exactly —
        // this source is the composition point, the node only toggles the mode and forwards the creating slot's pad.
        // One shared CAS store (cwd-relative, the worlds/creations/tunes sibling) backs placement resolution and the
        // world.* verbs' saves.
        m_worldStore = new ContentAddressedStore(root: "store");
        m_worldScene = new WorldScene();
        m_worldHistory = new EditHistory<WorldScene.Snapshot>(capacity: 64, initial: m_worldScene.CaptureSnapshot());
        m_worldEmitter = new WorldSceneEmitter(scene: m_worldScene, store: m_worldStore);
        m_worldController = new WorldSculptController(
            history: m_worldHistory,
            narrate: static line => Console.Error.WriteLine(value: line),
            scene: m_worldScene,
            store: () => new WorldSculptController.ContentAddressedStoreHandle(Store: m_worldStore)
        );
        // The deliberate-save bake: the walk grid is derived from the document's own content (+ the room's stands)
        // and ships INSIDE the saved bytes — see BakeWorldForSave.
        m_worldScene.PrepareForSave = BakeWorldForSave;

        m_companionEmitter = new Creator.CompanionEmitter(roster: m_companions);
        m_companionEmitter.SetFaceSlotResolver(resolver: CompanionFaceSlot);
        // Companions roam the whole ROOM (not the workbench pedestal) — a generous band above the floor, inside
        // the walls. Recomputed (not just set once here) whenever m_room changes — see ComputeCompanionBounds.
        m_companionBounds = ComputeCompanionBounds();

        // The room composition contains every studio-suppressible content block in emission order (order is otherwise
        // free — see the sdf-world skill; nothing
        // outside this source depends on a specific numeric dynamic-transform slot). PlayerBoxEmitter/
        // ConsoleStandEmitter's dynamic slots and the creator/world/companion/diegetic-bar emitters' own slots are all
        // assigned contiguously by SdfCompositionFrameSource — see the type remarks for the slot-base story.
        // The RTS scenario bakes the arena's deterministic query provider once and binds it onto
        // the sim — the sim's ground-snap/spawn checks consult ONLY this interface (see OverworldWorld.ConfigureRtsQuery
        // /RtsScenario's remarks), never the render program. Configured unconditionally (like the garden pool, always
        // present regardless of which document loaded) — cheap, deterministic, in-memory only this wave.
        m_world.ConfigureRtsQuery(query: RtsScenario.BuildQuery());

        m_diegeticBarEmitter = new DiegeticBarEmitter(owner: this);
        m_roomComposition = new SdfCompositionFrameSource(
            dresser: this,
            emitters: [
                new PlayerBoxEmitter(owner: this),
                new ConsoleStandEmitter(owner: this),
                m_creatorEmitter,
                m_worldEmitter,
                m_companionEmitter,
                m_diegeticBarEmitter,
                new RoomEmitter(owner: this),
                new WorkbenchEmitter(owner: this),
                new TerminalEmitter(owner: this),
                new LinkCableEmitter(owner: this),
                new StudioBackdropEmitter(owner: this),
                new GardenEmitter(owner: this),
                new RtsTerrainEmitter(),
                new RtsUnitInstanceEmitter(world: m_world),
                new MuseumEmitter(owner: this),
            ]
        );
        // THE SDF-DEBUG TAKEOVER: an ALTERNATE composition (never mixed into the room list — see
        // ISdfSceneEmitter's takeover remarks) sharing this source as its dresser, so entering/leaving the mode needs
        // no manual "force a rebuild" — the composed PROGRAM OBJECT simply differs from whichever the room last built,
        // which Dress's reference-diff already treats as a real change.
        m_sdfDebugComposition = new SdfCompositionFrameSource(emitters: [new Puck.SdfVm.Debug.SdfDebugEmitter(mode: m_sdfDebug)], dresser: this);
        // The gravity scenario takeover (see OverworldFrameSource.Gravity.cs) binds the one field
        // evaluator + builds its own alternate composition, mirroring the SDF-debug wiring immediately above.
        InitializeGravity();
    }

    // Companions roam the whole ROOM (not the workbench pedestal) — a generous band above the floor, inside the
    // walls, derived from the CURRENT m_room's bounds/floor. Factored out of the constructor so
    // ConsumePendingWorldLoad can recompute it after swapping m_room to a loaded world's bounds — the constructor
    // originally captured this ONCE against the default room, which left companions confined to the stale default
    // bounds after a world.load into a differently-sized lot (the rank-19 staleness bug).
    private WorkbenchRegion ComputeCompanionBounds() {
        return new WorkbenchRegion(
            Center: new Vector3(
                x: (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
                y: m_room.FloorY,
                z: (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
            ),
            HalfExtent: ((0.5f * MathF.Max(x: (m_room.BoundsMax.X - m_room.BoundsMin.X), y: (m_room.BoundsMax.Y - m_room.BoundsMin.Y))) - 1.2f),
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

        // Companions steer/animate on the render clock BEFORE packing (presentation only — the sim never sees them).
        // The companion face auto-tune's "preferred remote feed is live" probe: a companion drifts to its remote face
        // feed only when the host's named-feed registry is actually producing that feed this frame — never fish/lure
        // by name, just "is this companion's last face-feed name live" (see CompanionState.FaceFeeds).
        m_companions.Tick(deltaSeconds: deltaSeconds, nearestPlayerProvider: NearestActivePlayer, remoteFeedProbe: PreferredRemoteFeedLive);

        // The screen mux: resolves the ledger (cabinets/easel/any registered dynamic claimants) — BEFORE the room
        // composition packs, since a claimed headroom slot or the link cable's pair can change what it emits.
        var linkedA = (LinkedConsoleASource?.Invoke() ?? -1);
        var linkedB = (LinkedConsoleBSource?.Invoke() ?? -1);
        var linkedPair = (((linkedA >= 0) && (linkedB >= 0)) ? (linkedA, linkedB) : ((int A, int B)?)null);

        // Register the companion faces' screen claims + the world/creation camera-feed claim BEFORE ResolveScreenMux,
        // so the ledger arbitrates them with the cabinets/easel in one pass. Presentation-only — never touches sim
        // state. Reads LAST frame's packed companion/player transforms (NearestActivePlayer) — one frame stale by
        // construction, same as before. PlanViews itself moves BELOW PublishAnchors (see there) — it binds each
        // camera view to an anchor name PublishAnchors must have already republished THIS tick.
        RegisterCompanionFaceClaims();
        ResolveScreenMux(linkedPair: linkedPair);

        // Ease the workbench power-on glow toward the editor unlock (revealed → 1, else 0) on the render clock; a
        // crossed quantized bucket rebuilds the program (its panel bakes an emissive proportional to the glow), so the
        // power-on reads as an eased "the workshop opens" rather than a hard cut. Presentation only — never hashed.
        AdvanceWorkbenchGlow(deltaSeconds: deltaSeconds);
        // Advance the one-shot editor-reveal beat (armed on the EditorRevealed edge): a bell-shaped swell over
        // EditorRevealBeatSeconds, then it rests. Presentation only — never hashed, like the workbench glow above.
        if (m_editorRevealBeatTime < EditorRevealBeatSeconds) {
            m_editorRevealBeatTime = MathF.Min(x: EditorRevealBeatSeconds, y: (m_editorRevealBeatTime + MathF.Max(x: deltaSeconds, y: 0f)));
        }

        // The shared room-content trigger (see AdvanceRoomRevision) — runs every frame regardless of which
        // composition ends up active, so a change while a takeover is up (e.g. a scripted `boot` mid-debug-session)
        // is still waiting for the room composition the moment it becomes active again.
        AdvanceRoomRevision(linkedPair: linkedPair);

        // FRESH per-frame player positions + the screen-director view layout — computed BEFORE any composition source
        // packs its dynamic-transform buffer, so PlayerBoxEmitter (players) and DiegeticBarEmitter (the bar's
        // camera-rig mount) bake THIS frame's values; every read of these two ABOVE this point in the method (the
        // companion tick/plan above) already saw LAST frame's, the documented one-frame lag.
        PackPlayerRenderTransforms(renderOrigin: renderOrigin, alpha: interpolationAlpha);
        PublishAnchors();

        // Plans this frame's diegetic views (camera views + named-view wiring) now that every anchor a view might
        // bind (a companion shape, a world placement) is freshly republished — see PublishAnchors.
        PlanViews();
        AdvanceRevealTransition(deltaSeconds: deltaSeconds);

        m_activePositions.Clear();

        for (var slot = 0; (slot < m_world.Slots.Count); slot++) {
            if (m_world.Slots[slot] is not null) {
                m_activePositions.Add(item: m_playerRenderTransforms[slot].Position);
            }
        }

        // The pane cameras ride m_director.PaneCameraSource, assigned once in the constructor (it resolves live
        // instance state each Compose) rather than reassigned per frame.
        //
        // The scenario harness's per-frame pose is settled by the render node's ScenarioTick BEFORE this compose (the
        // node owns the capture-arm + graceful-exit seams; see ScenarioTick), so the director already holds this
        // frame's verbatim shot pose here. Nothing to advance in the source itself. The room-bench pin (below) rides
        // the SAME director seam; the two are not expected to run together (one is a headless capture run, the other
        // an interactive console verb), but ApplyRoomBenchCameraPose only asserts while Running, so a bench started
        // mid-scenario would win outright rather than corrupt either state.
        ApplyRoomBenchCameraPose();
        // The engine-benchmark harness's per-frame camera and workload pins are consumed after room.bench's pin
        // (the two never overlap) and BEFORE Compose reads ScenarioCameraPose, so a bench scene frames view 0.
        ApplyBenchStage();

        m_currentViews = m_director.Compose(activePositions: m_activePositions, bootOrder: m_world.BootOrder, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds);

        // View 0 is always the room; stash its live rect for the binding-bar overlay (this runs INSIDE the producer's
        // ProduceFrame, so the overlay — which wraps the producer — always reads the region of the frame it draws over).
        LastRoomRegion = m_currentViews[0].Region;

        // Exactly ONE of the takeovers is active: the native AGB scene (a single static program — no capacity probe
        // needed, see BuildAgbDebugProgram), the SDF-debug mode (its OWN alternate composition), the GRAVITY ARC's
        // planetoid takeover (also its OWN alternate composition — see OverworldFrameSource.Gravity.cs; it uses its
        // OWN dresser, not this file's shared Dress, so it never reads m_currentViews at all), or the room (the
        // default). Each composition's CaptureFrame rebuilds (if its aggregate Revision changed), packs its own
        // dynamic transforms, and calls its dresser — this method never builds an SdfFrame itself.
        // The engine-benchmark synthetic-workload takeover renders a single cached workload program
        // fullscreen in place of the room, framed by the pinned bench pose — the same single-cached-program takeover
        // shape as the AGB scene below, so Dress's reference-diff treats entering/leaving it as a real change. Checked
        // FIRST so a bench sdf.* scene wins over any other takeover.
        if (m_benchWorkloadActive && (m_benchWorkloadProgram is { } benchProgram)) {
            return Dress(program: benchProgram, transforms: m_benchWorkloadTransforms, width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
        }

        if (m_agbActive) {
            m_agbProgram ??= BuildAgbDebugProgram();

            return Dress(program: m_agbProgram, transforms: [], width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
        }

        if (m_gravityActive) {
            return m_gravityComposition.CaptureFrame(width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);
        }

        return (m_sdfDebug.Active
            ? m_sdfDebugComposition.CaptureFrame(width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha)
            : m_roomComposition.CaptureFrame(width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha));
    }

    // The shared "did the combined room content change" trigger (see the m_roomRevision field remarks): every
    // rebuild condition is expressed as a monotonic bump. The creator and world scenes' own
    // ProgramRevision and the SDF-debug mode's OWN Revision are read directly by their respective emitters/composition
    // (CreatorSceneEmitter/WorldSceneEmitter/SdfDebugEmitter), so they are NOT folded in here.
    private void AdvanceRoomRevision((int A, int B)? linkedPair) {
        var bootedMask = m_world.BootedMask;
        var activePlayerMask = ActivePlayerMask();
        var workbenchBucket = WorkbenchGlowBucket();
        // The diegetic bar's content signature (page/binding/family) joins the trigger, exactly like the terminal's
        // on/off and the workbench glow: the CAMERA-rig pose rides the bar's dynamic slot (never a rebuild), only its
        // CONTENT changing re-bakes the panels + labels.
        var diegeticSignature = (m_diegeticSignature?.Invoke() ?? 0);
        var gardenSignature = GardenSignature();
        var changed = ((bootedMask != m_programBootedMask) || (activePlayerMask != m_builtActivePlayerMask) || (workbenchBucket != m_builtWorkbenchBucket) || (m_terminalVisible != m_builtTerminalVisible) || (m_diegeticUiVisible != m_builtDiegeticVisible) || (diegeticSignature != m_builtDiegeticSignature) || (m_companions.Companions.Count != m_builtCompanionCount) || !EqualLinkedPair(a: linkedPair, b: m_builtLinkedPair) || (gardenSignature != m_builtGardenSignature));

        if (!changed) {
            return;
        }

        m_roomRevision++;
        m_programBootedMask = bootedMask;
        m_builtActivePlayerMask = activePlayerMask;
        m_builtWorkbenchBucket = workbenchBucket;
        m_builtTerminalVisible = m_terminalVisible;
        m_builtDiegeticVisible = m_diegeticUiVisible;
        m_builtDiegeticSignature = diegeticSignature;
        m_builtCompanionCount = m_companions.Companions.Count;
        m_builtLinkedPair = linkedPair;
        m_builtGardenSignature = gardenSignature;
    }

    // The deterministic garden's rebuild key: occupied-slot count in the low bits, then each occupied slot's growth
    // stage (clamped to a small fixed width so six slots always pack into one int) — changes exactly when a plant/
    // clear happens or a stage advances, never on every tick (a settled tree stops rebuilding once fully grown).
    private int GardenSignature() {
        var gardens = m_world.Gardens;
        var signature = 0;

        for (var slot = 0; (slot < gardens.Count); slot++) {
            if (gardens[slot] is not { } planted) {
                continue;
            }

            var ticksSincePlanting = (m_world.CurrentTick - planted.PlantedTick);
            var stageBucket = Math.Min(val1: 7UL, val2: (ticksSincePlanting / GardenTreeGenerator.TicksPerStage));

            signature |= ((1 + (int)stageBucket) << (slot * 4));
        }

        return signature;
    }

    // Every FIXED player slot's FRESH render-relative transform for this frame (see the m_playerRenderTransforms field
    // remarks) — a presentation override (a player driving their brick, whose avatar follows the game sprite) wins
    // over the sim body, so the camera and the baked player box both follow the followed avatar.
    private void PackPlayerRenderTransforms(WorldCoord3 renderOrigin, float alpha) {
        var players = m_world.DynamicTransforms(renderOrigin: renderOrigin, alpha: alpha);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            m_playerRenderTransforms[slot] = ((PresentationOverride?.Invoke(slot) is { } overridden)
                ? (players[slot] with { Position = overridden.ToRenderRelative(origin: renderOrigin) })
                : players[slot]);
        }

        // The world-sculpt camera anchors on the CREATING slot's rendered position (the player is the cursor).
        m_primaryPlayerRenderPosition = m_playerRenderTransforms[0].Position;
    }

    // Republishes this frame's real anchors into m_anchorTable: every player body, the room's
    // spawn anchor, and each console's screen face — the room's own honest answer to "what can a camera rig ride
    // here." BeginTick marks every previously-published id not-live FIRST, so a slot/console that stops existing
    // (a player leaves, the room's console count shrinks on a world reload) correctly stops resolving rather than
    // leaving a stale pose behind (see SdfAnchorTable's remarks). Called once per CaptureFrame, right after
    // PackPlayerRenderTransforms (whose fresh values this publishes) and before anything reads the table.
    private void PublishAnchors() {
        m_anchorTable.BeginTick();

        // The spawn anchor: render-relative space is ALREADY anchored at the room's spawn point (renderOrigin, above
        // — see CaptureFrame's remarks), so its own render-relative position is always the origin.
        _ = m_anchorTable.Publish(name: "world.spawn", pose: new SdfAnchor(Position: Vector3.Zero, Orientation: Quaternion.Identity));

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (m_world.Slots[slot] is null) {
                continue; // An empty slot publishes nothing — a rig resolving "player.N" for a free slot correctly
                          // sees it as not-live rather than parked-below-the-floor.
            }

            var transform = m_playerRenderTransforms[slot];

            _ = m_anchorTable.Publish(name: $"player.{slot}", pose: new SdfAnchor(Position: transform.Position, Orientation: transform.Orientation));
        }

        for (var index = 0; (index < m_room.Consoles.Count); index++) {
            // Static (a console stand never moves once placed) — republished every tick like every other anchor, per
            // the table's own per-tick-republish contract (see SdfAnchorTable's remarks), rather than published once
            // and left to linger.
            _ = m_anchorTable.Publish(name: $"console.{index}", pose: new SdfAnchor(Position: ScreenCenterLocal(consoleIndex: index), Orientation: Quaternion.Identity));
        }

        // Every companion creation camera's anchored shape and every world-scene camera's anchored placement are rows
        // in this table, so SdfCameraView reads them through the same ISdfAnchorSource
        // vocabulary as a player body or a console face (see RegisterCameraView/PlanViews). Published for EVERY
        // declared camera (not only a currently-wired/requested one) — cheap (a dictionary write), and it means a
        // camera view registered later in the same tick always finds a fresh anchor if one exists.
        PublishCompanionShapeAnchors();
        PublishWorldPlacementAnchors();
    }

    // Publishes "shape.{companionIndex}.{shapeId}" for every companion creation camera's anchored shape, from the
    // companion emitter's own last-packed transform cache (one frame stale, like every other diegetic-view anchor).
    // A shape not yet packed (or missing — normalization should have dropped its camera) simply is not published this
    // tick, so SdfCameraView.Resolve correctly reports no signal for it (see the anchor table's own remarks).
    private void PublishCompanionShapeAnchors() {
        var companions = m_companions.Companions;

        for (var companionIndex = 0; (companionIndex < companions.Count); companionIndex++) {
            var cameras = companions[companionIndex].Document.Cameras;

            if (cameras is not { Count: > 0 }) {
                continue;
            }

            var shapes = (companions[companionIndex].Document.Shapes ?? []);

            foreach (var camera in cameras) {
                var shapeIndex = IndexOfShape(shapes: shapes, shapeId: camera.ShapeId);

                if ((shapeIndex < 0) || !m_companionEmitter.TryGetShapeTransform(companionIndex: companionIndex, shapeIndex: shapeIndex, transform: out var shapeTransform)) {
                    continue;
                }

                _ = m_anchorTable.Publish(name: ShapeAnchorName(companionIndex: companionIndex, shapeId: camera.ShapeId), pose: new SdfAnchor(Position: shapeTransform.Position, Orientation: shapeTransform.Orientation));
            }
        }
    }

    // Publishes "placement.{id}" for every world-scene camera anchored to a placement (SdfAnchorKind.Instance), from
    // the world scene's own live placement pose. A World-anchored eye needs no such anchor (CameraEye ignores it).
    private void PublishWorldPlacementAnchors() {
        foreach (var eye in m_worldScene.Cameras) {
            if (eye.Anchor != SdfAnchorKind.Instance) {
                continue;
            }

            if (!m_worldScene.TryResolvePlacementPose(placementId: eye.AnchorId, out var position, out var yawRadians)) {
                continue;
            }

            _ = m_anchorTable.Publish(name: PlacementAnchorName(placementId: eye.AnchorId), pose: new SdfAnchor(Position: position, Orientation: Quaternion.CreateFromYawPitchRoll(yaw: yawRadians, pitch: 0f, roll: 0f)));
        }
    }
    private static int IndexOfShape(IReadOnlyList<ShapeDocument> shapes, int shapeId) {
        for (var index = 0; (index < shapes.Count); index++) {
            if (shapes[index].Id == shapeId) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Turns the composed program/transforms into this frame's <see cref="SdfFrame"/> (see
    /// <see cref="ISdfFrameDresser"/>) — the room mood, the debug frame-channel flags, and the grid-lock overlay; the
    /// SAME dressing applies regardless of which of the three takeovers produced <paramref name="program"/> (a
    /// fullscreen debug/AGB subject still sits inside the room's view layout — see <see cref="m_currentViews"/>).</summary>
    SdfFrame ISdfFrameDresser.Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
        Dress(program: program, transforms: transforms, width: width, height: height, deltaSeconds: deltaSeconds, interpolationAlpha: interpolationAlpha);

    private SdfFrame Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        _ = width;
        _ = height;
        _ = deltaSeconds;
        _ = interpolationAlpha;

        // A real rebuild always hands back a FRESH SdfProgram object (every composition source's BuildProgram
        // constructs a new SdfProgramBuilder every time); an unchanged program is the SAME instance across calls — so
        // reference identity is exactly the old `programChanged` bool, generalized across all three takeovers (a
        // switch between them is itself always a "change": the two composition sources' m_program fields are
        // independent objects, and the AGB program is a third, separately cached one).
        var programChanged = !ReferenceEquals(objA: program, objB: m_lastDressedProgram);

        m_lastDressedProgram = program;
        m_program = program;
        m_lastTransforms = transforms;

        if (programChanged) {
            // The view stack's content shares this program object; bump the revision only when it actually rebuilds, so
            // a view's offscreen engine re-uploads once per real program change, not every frame.
            m_viewProgramRevision++;
        }

        // The diegetic bar's rig mount is packed by DiegeticBarEmitter.PackDynamicTransforms directly (it reads
        // m_currentViews, computed fresh in CaptureFrame before any composition source packs) — nothing to do here.

        // While immersed the room is UNLIT (RoomLightFactor 0), so the letterbox margins around a contained screen
        // render BLACK — a native handheld/emulator look — easing up to the arcade mood as the reveal lights the room.
        // The editor-reveal beat (Q35) rides this same seam: a transient bell-shaped lift as the workshop opens, so the
        // room blooms warm then settles. Zero at rest (and while immersed, RoomLightFactor 0 zeroes it out anyway).
        var roomLight = (m_director.RoomLightFactor * (1f + (EditorRevealBeatLightGain * EditorRevealBeat())));

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
            Program: program,
            ProgramChanged: programChanged,
            Views: m_currentViews,
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
            AmbientScale = ((m_sdfDebug.Active || m_agbActive || m_benchWorkloadActive) ? 1f : (m_scenarioStudio ? StudioAmbientScale : ((OverworldAmbientScale * roomLight) * m_worldScene.Daylight))),
            // The SLICE debug view's plane channel (two floats riding the frame's screen-light env lanes — see
            // SdfFrame.DebugSliceAxis): the sdf.slice verb positions an axis-aligned slice plane; the defaults (0)
            // are the camera-locked plane, so a run that never touches the verb uploads the same zeros as before.
            DebugSliceAxis = m_sdfDebug.SliceAxis,
            DebugSliceOffset = m_sdfDebug.SliceOffset,
            // The analytic-normal A/B lever (the sdf.normals verb): default false = the analytic forward-mode gradient
            // dual; true swaps back to the 4-tap finite-difference probe. A pure frame flag (no geometry), so it rides
            // the same per-frame channel as the slice lanes above.
            DisableAmbientOcclusion = m_benchDisableAmbientOcclusion,
            // The soft-shadow grid-cull A/B lever (the sdf.shadowcull verb): default ON (DisableShadowCull false), so the
            // shadow march gathers each lit pixel's shadow-ray grid neighbourhood; OFF forces the flat all-instances
            // reference. A pure frame flag, same per-frame channel as the normals lever above.
            // The F1/F2 far-field isolators (sdf.far-bound / sdf.shadow-far-exit): both ship ON, so the frame DISABLES
            // only when the backing state clears the feature — the "off" sides of the owner's paired A/B.
            DisableFarBound = !m_benchFarBound,
            DisableScreenLights = m_benchDisableScreenLights,
            DisableShadowCull = !m_sdfDebug.ShadowCull,
            DisableShadowFarExit = !m_benchShadowFarExit,
            // The grid-lock overlay channel (grid-locking §4), threaded into SdfFrame's Grid* fields exactly like the
            // slice lanes above (the active editor wrote the locals; all-zero outside an editor).
            DisableSoftShadows = m_benchDisableSoftShadows,
            DynamicTransforms = transforms,
            // The cadence gate (sdf.cadence-gate): default OFF (m_benchEnableCadenceGate false) = every frame renders
            // fully, byte-identical. A presentation-only engine policy, not a shader lever, so it rides SdfFrame as a
            // plain flag the engine reads (never the screen-light buffer).
            EnableCadenceGate = m_benchEnableCadenceGate,
            EnableShadowProxy = m_benchEnableShadowProxy,
            GridFlags = gridFlags,
            GridFloorY = gridFloorY,
            GridObjectFrame = gridObjectFrame,
            GridObjectOrigin = gridObjectOrigin,
            GridObjectPatchRadius = gridObjectPatchRadius,
            // The engine-benchmark shader-level levers are four per-frame lanes driven by the demo-side
            // live state the feature-switch registry writes (BenchDisable*/BenchShadowDistanceScale). Default all-off/0 =
            // every effect ON, so the shipped demo uploads the same zeros as before the levers existed.
            GridObjectPitch = gridObjectPitch,
            GridWorldPitch = gridWorldPitch,
            ShadowDistanceScale = m_benchShadowDistanceScale,
            SunScale = ((m_sdfDebug.Active || m_agbActive || m_benchWorkloadActive) ? 1f : (m_scenarioStudio ? StudioSunScale : ((OverworldSunScale * roomLight) * m_worldScene.Daylight))),
            // PATH B — the shadow-proxy lever (sdf.shadow-proxy): default OFF (m_benchEnableShadowProxy false) uploads 0
            // = the full shadow occluder set, byte-identical to before the lever existed.
            UseFiniteDifferenceNormals = m_sdfDebug.UseFiniteDifferenceNormals,
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
        m_currentLinkedPair = (((linkedPair is { A: >= 0, B: >= 0 } pair) && (pair.A < m_room.Consoles.Count) && (pair.B < m_room.Consoles.Count) && (pair.A != pair.B))
            ? linkedPair
            : null);

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
        ((a.HasValue == b.HasValue) && (!a.HasValue || ((a.Value.A == b!.Value.A) && (a.Value.B == b.Value.B))));

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
            // slot. Its source/light resolve the companion's CURRENTLY-tuned face feed by NAME through the view stack.
            RegisterScreenClaimant(
                light: () => (m_views?.ResolveGlow(name: pinned.CurrentFaceFeed) ?? Vector3.Zero),
                ownerToken: token,
                priority: ScreenSlotPriority.Ambient,
                source: () => (m_views?.Resolve(name: pinned.CurrentFaceFeed) ?? 0)
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
    // feed: it asks the view stack whether the companion's own last-listed view name is live.
    private bool PreferredRemoteFeedLive(CompanionState companion) {
        if ((m_views is not { } views) || (companion.FaceFeeds.Count == 0)) {
            return false;
        }

        return views.IsLive(name: companion.FaceFeeds[^1]);
    }

    /// <summary>Composes the diegetic view stack over the main engine's worst-case envelope (idempotent) — the render
    /// node calls this once at resource build so its own type coupling stays flat; this source is the view stack's
    /// composition point (mirrors <see cref="InstallBakePreview"/>). Without it the source runs view-free (a bare-room
    /// run pays nothing). Named <c>InstallFeeds</c> still (not renamed to match <see cref="ViewStack"/>'s own
    /// vocabulary) — the render node already names this method, and the swap underneath it is meant to be invisible.</summary>
    /// <param name="services">The application services (the GPU compute seam).</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (selects the view kernels).</param>
    public void InstallFeeds(IServiceProvider services, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(services);

        m_services = services;
        m_viewsHostOnDirectX = hostsOnDirectX;
        m_views ??= new ViewStack();
        m_consoleFeed ??= new ConsoleFeed(source: (services.GetService(serviceType: typeof(ConsoleTextStore)) as IConsoleTextSource));

        // The procedural face feed + the console CRT are cheap producers some OTHER path (TickFeeds, below) already
        // keeps current every frame — unbudgeted GuestSurfaceView content, so ViewStack.RenderFrame always serves
        // their FRESHEST handle rather than a round-robin-stale one.
        _ = m_views.Register(band: ScreenSlotPriority.Ambient, content: new GuestSurfaceView(roomGlow: FaceFeedGlow, source: () => m_faceFeed.CurrentImageViewHandle), name: CompanionState.DefaultFaceFeed);
        _ = m_views.Register(band: ScreenSlotPriority.Anchored, content: new GuestSurfaceView(roomGlow: ConsoleFeedGlow, source: () => (m_consoleFeed?.CurrentImageViewHandle ?? 0)), name: ConsoleFeed.FeedName);

        // The diegetic console terminal claims its headroom screen slot through the SAME generic seam a placeable
        // diegetic camera would — an Anchored preferred-slot claim (a permanent fixture, like a cabinet, so it always
        // seats its own slot); its source/light resolve the console named view the stack publishes. No transform
        // provider: the CRT slab is STATIC geometry (EmitTerminal bakes its world frame directly). Registered once here
        // (persistent across passes); the `terminal on|off` verb gates only its EMISSION, never this claim.
        RegisterScreenClaimant(
            light: () => (m_views?.ResolveGlow(name: ConsoleFeed.FeedName) ?? Vector3.Zero),
            ownerToken: m_terminalClaimToken,
            preferredSlot: TerminalScreenSlot,
            priority: ScreenSlotPriority.Anchored,
            source: () => (m_views?.Resolve(name: ConsoleFeed.FeedName) ?? 0)
        );

        // THE REPLAY MUSEUM + THE DROSTE DOOR: reserve their five headroom slots permanently (Anchored, like the
        // terminal), so a floating claimant never seats where MuseumRenderer's static geometry already declared a
        // ScreenSlab. No source/light provider here — content arrives only through `world.wire named:<name> <screen>`
        // (the render node's per-slot wired-feed override wins over the ledger's own empty source; see
        // OverworldRenderNode.BuildScreenSources).
        for (var index = 0; (index < MuseumRenderer.ScreenCount); index++) {
            RegisterScreenClaimant(ownerToken: MuseumClaimTokens[index], preferredSlot: (MuseumRenderer.ScreenSlotBase + index), priority: ScreenSlotPriority.Anchored);
        }

        RegisterScreenClaimant(ownerToken: MuseumClaimTokens[^1], preferredSlot: MuseumRenderer.DoorScreenSlot, priority: ScreenSlotPriority.Anchored);
    }

    /// <summary>Registers a booted cabinet's raw framebuffer as a named, resolvable <see cref="ViewStack"/> entry
    /// for the immersed-emulator guest view: <c>world.wire named:guest:N &lt;screen&gt;</c>
    /// already resolves any name registered here through the EXISTING <c>ScreenSourceKind.Named</c> wiring grammar
    /// (<c>CollectWorldWiredViews</c>) — no wiring-grammar change was needed, only the registration itself. Zero
    /// emulator reference here: <paramref name="source"/> is the SAME <c>Func&lt;nint&gt;</c> closure shape
    /// <c>OverworldRenderNode.BuildScreenSources</c> already wraps a <c>GamingBrickChildNode</c> in directly — this
    /// call ADDS a second, named path to that same handle for the view stack's benefit, alongside (never replacing)
    /// the cabinet's existing direct screen-source wiring (the documented "W-SEAM decision" — a cabinet's screen
    /// slab still samples the direct provider; this name is for a DIFFERENT screen wiring it onto, e.g. the reveal's
    /// guest→room transition or a TV-in-TV proof). Idempotent per name (<see cref="ViewStack.Register"/> updates in
    /// place) — safe to call once at boot, which is all this wave does.</summary>
    /// <param name="consoleIndex">The cabinet's console index — the registered name is <c>"guest:{consoleIndex}"</c>.</param>
    /// <param name="source">Resolves the cabinet's current native framebuffer handle (0 while unbooted/unassigned).</param>
    /// <returns>The registered view id, or <see cref="ViewId.None"/> when the view stack isn't installed yet.</returns>
    public ViewId RegisterGuestView(int consoleIndex, Func<nint> source) {
        ArgumentNullException.ThrowIfNull(source);

        if (m_views is not { } views) {
            return ViewId.None;
        }

        return views.Register(band: ScreenSlotPriority.Ambient, content: new GuestSurfaceView(source: source), name: $"guest:{consoleIndex}");
    }

    // A soft neutral glow for the default face feed's room-light contribution (the CRT face casts a faint light).
    private static readonly Vector3 FaceFeedGlow = new(x: 0.10f, y: 0.16f, z: 0.12f);
    // A soft green phosphor glow for the console terminal's room-light contribution, same spirit as the face glow.
    private static readonly Vector3 ConsoleFeedGlow = new(x: 0.08f, y: 0.15f, z: 0.10f);

    /// <summary>Installs the diegetic-UI Tier-2 seam (the camera-rig-mounted action bar) as a set of DELEGATES, so this
    /// source hosts it without naming the <c>DiegeticUiDirector</c> type — it sits at its exact CA1506 coupling ceiling,
    /// and every delegate arity here is already in its coupling set. The render assembly composes the director and calls
    /// this; the bar's atlas reaches the engine through the director's own <c>ISdfFrameSource</c> decorator, not this
    /// source's <c>GlyphAtlas</c> override. Idempotent enough to call once at assembly.</summary>
    /// <param name="bindSlotBase">Receives the reserved dynamic-transform slot the bar rides (given synchronously here).</param>
    /// <param name="emit">Emits the bar geometry into the program being built (invoked from <see cref="EmitDiegeticBar"/>
    /// when the bar is visible; the worst-case probe reserves the envelope self-containedly instead — see the field
    /// remarks).</param>
    /// <param name="mount">Computes the bar's per-frame camera-rig transform from the composed views.</param>
    /// <param name="signature">The bar's content signature; a change rebuilds the program (page/binding/family swap).</param>
    public void InstallDiegeticUi(Action<int> bindSlotBase, Action<SdfProgramBuilder> emit, Func<IReadOnlyList<SdfViewSnapshot>, DynamicTransform> mount, Func<int> signature) {
        ArgumentNullException.ThrowIfNull(bindSlotBase);
        ArgumentNullException.ThrowIfNull(emit);
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(signature);

        m_diegeticEmit = emit;
        m_diegeticMount = mount;
        m_diegeticSignature = signature;
        // The bar's actual slot: captured by DiegeticBarEmitter on its first Emit call, which the room composition's
        // construction-time worst-case probe already forced (see the ctor) — so it is always known by the time this
        // install runs (the render assembly composes this AFTER the frame source is fully constructed).
        bindSlotBase(obj: m_diegeticBarEmitter.SlotBase);
    }

    /// <summary>Renders this frame's planned views + ticks the procedural face/console feeds (the render-thread half
    /// of the diegetic view stack). The render node calls this each produced frame beside its bake-preview tick (a
    /// live GPU device); a no-op until <see cref="InstallFeeds"/> ran. Reads the PREVIOUS <c>CaptureFrame</c>'s plan —
    /// a deliberate one-frame lag matching the diegetic-CRT read <see cref="ViewStack"/>'s self-reference rule expects.</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device).</param>
    public void TickFeeds(in Puck.Hosting.FrameContext context) {
        if ((m_views is not { } views) || (m_program is null) || (m_services is not { } services)) {
            return;
        }

        var faceFeedNeeded = FaceFeedNeeded();
        var consoleFeedNeeded = m_terminalVisible;

        if (
            (faceFeedNeeded || consoleFeedNeeded) &&
            (services.GetService(serviceType: typeof(IGpuComputeServices)) is IGpuComputeServices gpu) &&
            context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)
        ) {
            if (faceFeedNeeded) {
                m_faceFeed.Tick(deltaSeconds: (float)context.DeltaSeconds, device: device, gpu: gpu);
            }

            if (consoleFeedNeeded) {
                m_consoleFeed?.Tick(device: device, gpu: gpu);
            }
        }

        var renderContext = new ViewRenderContext(
            DynamicTransforms: m_lastTransforms,
            Host: context,
            Program: m_program,
            ProgramRevision: m_viewProgramRevision,
            ResolveScreenSource: ResolveFeedScreenSource,
            Time: ((m_captureSequencer is { } scenario) ? scenario.PinnedContentTime : m_time)
        );

        views.RenderFrame(context: in renderContext);
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

    /// <summary>Disposes the diegetic view stack's GPU resources + the face/console feeds' own (the render node's
    /// teardown path).</summary>
    public void DisposeFeeds() {
        m_views?.Dispose();
        m_views = null;
        m_faceFeed.Dispose();
        m_consoleFeed?.Dispose();
        m_consoleFeed = null;
        m_cameraViews.Clear();
    }

    /// <summary>The wired-view image handle a <c>world.wire</c> routed onto screen <paramref name="screenIndex"/> this
    /// frame, or 0 when nothing wired a view there — the render node's per-slot cabinet source override consults this
    /// so a screen wired to a camera/named view samples the view INSTEAD of its cabinet brick / flat material (the
    /// fish's lure onto a cabinet screen). Primitive-typed (<see langword="nint"/>) on purpose — the node stays
    /// coupling-flat.</summary>
    /// <param name="screenIndex">The screen-surface slot index (a cabinet index 0-3, or a headroom slot).</param>
    /// <returns>The wired view's handle, or 0 for no wire (the caller keeps its default source).</returns>
    public nint ResolveWiredFeedOverride(int screenIndex) =>
        ((m_wiredFeedByScreen.TryGetValue(key: screenIndex, value: out var viewName) && (m_views is { } views))
            ? views.Resolve(name: viewName)
            : 0);

    // What a screen index binds INSIDE a camera/nested view's own render: the same wiring the room shows (a wired
    // view's named handle, else the cabinet/flat fallback via the base screen-source resolution). ViewStack itself
    // enforces the self-reference rule (a screen wired to the view being rendered binds 0), so this need not.
    private nint ResolveFeedScreenSource(int screenIndex) {
        if (m_wiredFeedByScreen.TryGetValue(key: screenIndex, value: out var viewName) && (m_views is { } views)) {
            return views.Resolve(name: viewName);
        }

        // A view sees the room's OTHER diegetic screens through the same dynamic-claimant source the room uses (a
        // booted cabinet's framebuffer lives on the render node's side, not here — a view showing a cabinet is a
        // future refinement; today a view renders the world geometry + any wired view screens, which is the proof).
        return ResolveDynamicSource(slot: screenIndex);
    }

    // Plans the views the wiring wants live this frame: every companion creation-camera whose named view a face
    // references (the fish's lure lens), plus every world-scene camera a world.wire routes onto a screen. Registers
    // (or rebinds) each as a persistent SdfCameraView, records screen->view-name wiring for the render node's per-slot
    // override, and sets each view's self-reference screen set. Presentation-only; the views render one frame later
    // via TickFeeds (the diegetic lag) — called AFTER PublishAnchors so every anchor a view might bind is fresh.
    private void PlanViews() {
        m_wiredFeedByScreen.Clear();

        if (m_views is not { } views) {
            return;
        }

        var requested = m_requestedViewScratch;

        requested.Clear();

        // 1) Companion creation cameras backing a face's referenced view name (the fish's lure lens). A companion's
        //    creation camera rides one of the companion's OWN shapes — its anchor was just published by PublishAnchors.
        CollectCompanionViews(views: views, requested: requested);

        // 2) World-scene cameras a world.wire routed onto a screen (camera:N -> eye #N). Records the screen->view-name
        //    wiring so the render node's per-slot override samples the view, and Named-kind wires too.
        CollectWorldWiredViews(views: views, requested: requested);

        // 2.5) The reveal-transition views (BeginRevealTransition) are directly-registered, never wire-requested —
        //    fold them into the SAME requested-set so the structural release below (ReleaseUnwiredNestedViews)
        //    reclaims them too, not only AdvanceRevealTransition's one-shot completion edge. "Still wanted" for a
        //    reveal view means the transition is still running.
        if (m_revealTransition is not null) {
            _ = requested.Add(item: RevealTransitionConsoleView);
            _ = requested.Add(item: RevealTransitionRoomView);
        }

        // 3) Withdraw any lazily-registered nested-world exhibit no wire references anymore THIS frame — the
        //    counterpart to EnsureNestedWorldViewRegistered/EnsureMuseumNestedViewsRegistered above, which only ever
        //    register (see ReleaseUnwiredNestedViews's remarks for why this altitude, not ViewStack.RenderFrame,
        //    owns the withdrawal).
        ReleaseUnwiredNestedViews(views: views, requested: requested);

        // Every requested view's self-reference set is only fully known once BOTH passes above have recorded every
        // wire — apply it last.
        foreach (var name in requested) {
            views.SetWiredScreens(name: name, screenIndices: WiredScreenSet(viewName: name));
        }
    }

    // Releases lazily registered nested-world exhibits when no `world.wire` references them this frame, and releases
    // reveal-transition views when their transition is no longer running. ViewStack cannot infer demand from screen
    // wiring alone because transitions can sample an unwired view. This owner has the complete requested set, so it
    // releases unused views, disposes their offscreen engines, and clears the cache for lazy recreation on demand.
    private void ReleaseUnwiredNestedViews(ViewStack views, HashSet<string> requested) {
        if ((m_nestedWorldView is not null) && !requested.Contains(item: NestedWorldViewName)) {
            views.Release(name: NestedWorldViewName);
            m_nestedWorldView = null;
        }

        if ((m_museumWallpaperView is not null) && !requested.Contains(item: MuseumRenderer.WallpaperViewName)) {
            views.Release(name: MuseumRenderer.WallpaperViewName);
            m_museumWallpaperView = null;
        }

        if ((m_drosteDoorView is not null) && !requested.Contains(item: MuseumRenderer.DoorViewName)) {
            views.Release(name: MuseumRenderer.DoorViewName);
            m_drosteDoorView = null;
        }

        // The reveal-transition pair: AdvanceRevealTransition's completion edge already releases these immediately,
        // so by the time this runs on a later frame m_cameraViews normally no longer holds either key — this is the
        // structural backstop (a future call site that arms/settles a transition without going through
        // AdvanceRevealTransition still gets reclaimed here).
        if (m_cameraViews.ContainsKey(key: RevealTransitionConsoleView) && !requested.Contains(item: RevealTransitionConsoleView)) {
            views.Release(name: RevealTransitionConsoleView);
            _ = m_cameraViews.Remove(key: RevealTransitionConsoleView);
        }

        if (m_cameraViews.ContainsKey(key: RevealTransitionRoomView) && !requested.Contains(item: RevealTransitionRoomView)) {
            views.Release(name: RevealTransitionRoomView);
            _ = m_cameraViews.Remove(key: RevealTransitionRoomView);
        }
    }

    // Companion creation cameras whose named view a face references: the view is requested (so it renders live), its
    // rig is a CameraEye (Body-anchored) bound to the shape's anchor PublishAnchors just published. The face's own
    // auto-tune decides WHETHER to show it; the view renders regardless of tune so the auto-tune's "is it live" probe
    // has a real answer.
    private void CollectCompanionViews(ViewStack views, HashSet<string> requested) {
        var companions = m_companions.Companions;

        for (var companionIndex = 0; (companionIndex < companions.Count); companionIndex++) {
            var companion = companions[companionIndex];
            var cameras = companion.Document.Cameras;

            if (cameras is not { Count: > 0 }) {
                continue;
            }

            foreach (var camera in cameras) {
                if (requested.Count >= ViewStack.MaxRegisteredViews) {
                    break;
                }

                var viewName = (camera.Feed ?? camera.Id.ToString(provider: System.Globalization.CultureInfo.InvariantCulture));

                if (!requested.Add(item: viewName)) {
                    continue; // already requested (a shared name).
                }

                var anchorName = ShapeAnchorName(companionIndex: companionIndex, shapeId: camera.ShapeId);

                RegisterCameraView(views: views, viewName: viewName, eye: BuildCreationCameraEye(camera: camera), anchorName: anchorName);
            }
        }
    }

    // World-scene cameras a world.wire routed onto a screen: for each Camera-kind wire, the eye is the world camera
    // whose Id equals the wire's index; its rig binds the placement anchor PublishAnchors published (or no anchor at
    // all for a World-anchored eye — CameraEye ignores it). Also records Named-kind wires so a screen wired to a
    // creation/host view name samples it (the fish's lure onto a cabinet screen).
    private void CollectWorldWiredViews(ViewStack views, HashSet<string> requested) {
        foreach (var (screenIndex, source) in m_worldScene.Wiring) {
            switch (source.Kind) {
                case ScreenSourceKind.Named when (source.Name is { Length: > 0 } namedView):
                    // A screen wired directly to a named view (a creation camera's view, the emote face, a nested
                    // world, …). The named view itself is requested by whatever OWNS it (a companion camera, above,
                    // or a host registration like a NestedWorldView — EnsureNestedWorldViewRegistered lazily
                    // registers the one this demo ships, the FIRST time something actually wires to it); here we only
                    // record the screen->name binding so the render node's per-slot override samples it.
                    m_wiredFeedByScreen[screenIndex] = namedView;
                    _ = requested.Add(item: namedView);
                    EnsureNestedWorldViewRegistered(views: views, name: namedView);
                    EnsureMuseumNestedViewsRegistered(views: views, name: namedView);

                    break;
                case ScreenSourceKind.Camera:
                    RecordWorldCameraView(views: views, feedIndex: source.Index, requested: requested, screenIndex: screenIndex);

                    break;
                default:
                    break;
            }
        }
    }
    private void RecordWorldCameraView(ViewStack views, int feedIndex, HashSet<string> requested, int screenIndex) {
        var viewName = $"world:{feedIndex}";

        m_wiredFeedByScreen[screenIndex] = viewName;

        if (!requested.Add(item: viewName) || (requested.Count > ViewStack.MaxRegisteredViews)) {
            return;
        }

        foreach (var eye in m_worldScene.Cameras) {
            if (eye.Id != feedIndex) {
                continue;
            }

            // A Placement-anchored eye rides its prop's LIVE pose (so a camera on a dragged building follows it) via
            // the "placement.{id}" anchor PublishAnchors published this tick; a World-anchored eye poses straight from
            // its own Position/Yaw (CameraEye.Resolve ignores the anchor), so no anchor binding is correct there.
            var anchorName = ((eye.Anchor == SdfAnchorKind.Instance) ? PlacementAnchorName(placementId: eye.AnchorId) : null);

            // Unlike a nested-world/museum exhibit (ReleaseUnwiredNestedViews), a world-wired camera feed is never
            // Released when a screen re-wires away from it — re-registering a fresh SdfCameraView on every re-wire
            // would lose its persistent offscreen engine for no reason. So it stays registered forever once any
            // screen has wired it once, which used to mean it round-robins forever even after every screen moves on.
            // The ViewStack liveness predicate closes that gap WITHOUT releasing: m_wiredFeedByScreen (rebuilt this
            // same PlanViews pass, just above) already says whether any screen still wires this exact name — reading
            // it live means the round-robin cursor skips this view's turn the instant nothing wires it, and resumes
            // spending budget on it the instant something does, with its last frame served in between.
            RegisterCameraView(views: views, viewName: viewName, eye: eye, anchorName: anchorName, isLive: () => m_wiredFeedByScreen.ContainsValue(value: viewName));

            break;
        }
    }

    // A creation camera → a live CameraEye (a Body-anchored eye whose stored pose is the camera's offset from the
    // anchored shape's frame). Degrees → radians for the offset yaw/pitch.
    private static CameraEye BuildCreationCameraEye(CreationCameraDocument camera) {
        var offsetYaw = ((camera.Yaw ?? 0f) * (MathF.PI / 180f));
        var offsetPitch = ((camera.Pitch ?? 0f) * (MathF.PI / 180f));

        return new CameraEye(
            Anchor: SdfAnchorKind.Body,
            AnchorId: camera.ShapeId,
            FieldOfViewRadians: ((camera.Fov is { } fov) ? (float?)(fov * (MathF.PI / 180f)) : null),
            FocusDistance: camera.Focus,
            Id: camera.Id,
            Pitch: offsetPitch,
            Position: camera.Position,
            Yaw: offsetYaw
        );
    }

    // Gets-or-creates the persistent SdfCameraView for `viewName` and rebinds its rig/anchor for THIS frame, then
    // (re-)registers it with the stack, returning its ViewId. CameraEye itself IS the rig (see
    // CameraEye.Resolve(in SdfAnchor, float)); a null anchorName leaves the view unanchored (a World-anchored eye,
    // which ignores the anchor parameter anyway). `isLive` is the ViewStack round-robin gate (null = always live,
    // the companion-camera default — see CollectCompanionViews's remarks on why those must stay unconditional).
    private ViewId RegisterCameraView(ViewStack views, string viewName, CameraEye eye, string? anchorName, Func<bool>? isLive = null) {
        if (!m_cameraViews.TryGetValue(key: viewName, value: out var view)) {
            view = new SdfCameraView(
                dynamicTransformCapacity: WorstCaseDynamicTransformCapacity,
                hostsOnDirectX: m_viewsHostOnDirectX,
                instanceCapacity: WorstCaseInstanceCapacity,
                programWordCapacity: WorstCaseProgramWordCapacity,
                services: m_services!
            );
            m_cameraViews[viewName] = view;
        }

        view.Rig = eye;
        view.AnchorSource = ((anchorName is not null) ? m_anchorTable : null);
        view.AnchorIdSource = ((anchorName is not null) ? (() => (m_anchorTable.TryResolveId(name: anchorName, anchorId: out var id) ? id : -1)) : null);

        return views.Register(band: ScreenSlotPriority.Ambient, content: view, isLive: isLive, name: viewName);
    }

    /// <summary>
    /// Arms the reveal transition: the hypervisor-identity primitive's
    /// defining moment, expressed on <see cref="ViewTransition"/>: a tight, console-framed <see cref="SdfCameraView"/>
    /// (<see cref="RevealTransitionConsoleView"/>, anchored on <c>console.0</c> — the fourth-wall's "the player was
    /// INSIDE that machine" read) eases into a pulled-back, room-framed one (<see cref="RevealTransitionRoomView"/>,
    /// anchored on <c>world.spawn</c>) over <see cref="RevealTransitionSeconds"/>. Called from the SAME
    /// <c>reveal world</c> event <see cref="IOverworldControlHost.RequestRevealNow"/> latches for
    /// <see cref="ScreenLayoutDirector"/>'s own (unchanged) camera/rect easing — this is ADDITIVE: it registers real
    /// content into the view stack and samples it every frame (<see cref="AdvanceRevealTransition"/>, narrated to
    /// stderr on start/settle), proving the primitive end to end, but its sampled <see cref="ViewLayout"/> is not YET
    /// spliced into the live multi-viewport compositor (<see cref="ScreenLayoutDirector"/> still owns those pixels) —
    /// that splice is the same scope as the full staged-layout-walk migration this wave deliberately left for a later
    /// pass (see the wave's handoff notes). A no-op if the view stack was never installed (a headless/no-GPU run).
    /// </summary>
    private void BeginRevealTransition() {
        if ((m_views is not { } views) || (m_revealTransition is not null)) {
            return; // already armed this session — the reveal is a one-shot latch, like the camera easing it mirrors.
        }

        var consoleEye = new CameraEye(
            Anchor: SdfAnchorKind.Instance,
            AnchorId: 0,
            FieldOfViewRadians: null,
            FocusDistance: 2.5f,
            Id: -1,
            Pitch: 0f,
            Position: new Vector3(x: 0f, y: 0f, z: 2.5f),
            Yaw: MathF.PI
        );
        var roomEye = new CameraEye(
            Anchor: SdfAnchorKind.Instance,
            AnchorId: 0,
            FieldOfViewRadians: null,
            FocusDistance: 10f,
            Id: -2,
            Pitch: -0.5f,
            Position: new Vector3(x: 0f, y: 10f, z: 14f),
            Yaw: MathF.PI
        );

        var consoleId = RegisterCameraView(views: views, viewName: RevealTransitionConsoleView, eye: consoleEye, anchorName: "console.0");
        var roomId = RegisterCameraView(views: views, viewName: RevealTransitionRoomView, eye: roomEye, anchorName: "world.spawn");
        var fullscreen = new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f);

        m_revealTransition = new ViewTransition(
            durationSeconds: RevealTransitionSeconds,
            from: new ViewLayout(Bindings: [new ViewBinding(View: consoleId, Region: fullscreen)]),
            to: new ViewLayout(Bindings: [new ViewBinding(View: roomId, Region: fullscreen)])
        );
        m_revealTransitionElapsed = 0f;

        Console.Error.WriteLine(value: $"[view-reveal] transition armed: {RevealTransitionConsoleView} -> {RevealTransitionRoomView} over {RevealTransitionSeconds:0.0}s");
    }

    // Samples the armed reveal transition on the render clock (deterministic — never wall time), narrating exactly
    // once when it settles. A no-op once nothing is armed (the common case — most runs never fire `reveal`).
    private void AdvanceRevealTransition(float deltaSeconds) {
        if (m_revealTransition is not { } transition) {
            return;
        }

        m_revealTransitionElapsed += MathF.Max(x: deltaSeconds, y: 0f);

        var layout = transition.Sample(elapsedSeconds: m_revealTransitionElapsed, complete: out var complete);

        _ = layout; // sampled for its side effect (proving Sample() runs every frame) — see BeginRevealTransition's
                    // remarks for why the result is not yet consumed by the compositor.

        if (!complete) {
            return;
        }

        Console.Error.WriteLine(value: $"[view-reveal] transition complete: {RevealTransitionRoomView} settled");
        m_revealTransition = null; // one-shot — done narrating, stop sampling every frame for nothing.

        // The same leak class ReleaseUnwiredNestedViews closes for the nested/museum exhibits (see its remarks): a
        // Register that's never paired with a Release keeps the view in the stack's round-robin forever. Release
        // immediately here (the one-shot completion edge) rather than waiting for next frame's PlanViews pass, AND
        // remove both from the m_cameraViews cache — the cache must never serve a disposed instance back to
        // RegisterCameraView's lazy-init check, which is exactly what makes a SECOND `reveal` re-arm cleanly.
        if (m_views is { } views) {
            views.Release(name: RevealTransitionConsoleView);
            views.Release(name: RevealTransitionRoomView);
        }

        _ = m_cameraViews.Remove(key: RevealTransitionConsoleView);
        _ = m_cameraViews.Remove(key: RevealTransitionRoomView);
    }

    /// <summary>The demonstrative NestedWorldView's wiring name. A
    /// screen <c>world.wire</c>d to <c>named:nested:0</c> shows a TRULY SEPARATE world (its own
    /// <see cref="SdfCompositionFrameSource"/>, unrelated to the room's program/anchors), not just a differently-posed
    /// camera on this one. See <see cref="EnsureNestedWorldViewRegistered"/>.</summary>
    private const string NestedWorldViewName = "nested:0";

    private NestedWorldView? m_nestedWorldView;

    // Lazily builds + registers the demonstrative NestedWorldView the FIRST time something actually wires to
    // NestedWorldViewName — a no-op for any other name, and a no-op once already registered (the nested world is
    // entirely self-contained, so once registered and still wired it never needs touching again). Its counterpart is
    // ReleaseUnwiredNestedViews (called every PlanViews, after this), which nulls m_nestedWorldView back out once no
    // wire references this name anymore — this method's `is not null` guard is what then rebuilds a fresh instance
    // on the next re-wire.
    private void EnsureNestedWorldViewRegistered(ViewStack views, string name) {
        if (!string.Equals(a: name, b: NestedWorldViewName, comparisonType: StringComparison.Ordinal) || (m_nestedWorldView is not null)) {
            return;
        }

        var nestedFrameSource = new SdfCompositionFrameSource(dresser: new NestedWorldDresser(), emitters: [new NestedWorldEmitter()]);

        m_nestedWorldView = new NestedWorldView(services: m_services!, hostsOnDirectX: m_viewsHostOnDirectX, frameSource: nestedFrameSource);

        _ = views.Register(band: ScreenSlotPriority.Ambient, content: m_nestedWorldView, name: NestedWorldViewName);
    }

    // The nested world's own content: the shared drift-monolith exhibit (Puck.SdfVm.Debug.SdfDriftMonolith — the same
    // program the gallery/Post ride) rendered ALONE in its own program, entirely independent of the room. Its Emit
    // owns the whole material palette (positional strides), so this emitter must be the ONLY member of its composed
    // list — exactly the intended standalone usage its own remarks document.
    private sealed class NestedWorldEmitter : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
            Puck.SdfVm.Debug.SdfDriftMonolith.Emit(builder: builder);

        public bool OwnsMaterialScope => true;
    }

    // The nested world's own dresser: one fixed camera framing the monolith — static (the exhibit itself animates
    // nothing sim-side), so ProgramChanged is always false after the engine's own initial upload.
    private sealed class NestedWorldDresser : ISdfFrameDresser {
        private static readonly Vector3 Eye = new(x: 0f, y: 2f, z: 5f);

        public SdfFrame Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var camera = CameraSnapshot.LookAt(fieldOfViewRadians: CameraEye.DefaultFieldOfViewRadians, position: Eye, target: Vector3.Zero, viewportHeight: height, viewportWidth: width);

            return new SdfFrame(
                Program: program,
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
                WarpAmount: 0f
            ) {
                DynamicTransforms = transforms,
            };
        }
    }

    // THE REPLAY MUSEUM's second exhibit + THE DROSTE DOOR's interior: two more standalone nested worlds, same
    // lazy-register-on-first-wire discipline as EnsureNestedWorldViewRegistered above (a new, independent pair — the
    // existing nested:0 machinery is left untouched). Both reuse Puck.SdfVm.Debug.SdfDebugRenderer's gallery exhibit
    // emission (already a dependency of this file via SdfDriftMonolith above) rather than hand-authoring a second
    // Droste/wallpaper scene. Same release counterpart as nested:0 too — ReleaseUnwiredNestedViews nulls these
    // fields back out once their name drops out of a frame's wire-driven want-set, so the `is null` guards below
    // rebuild fresh instances on the next re-wire.
    private NestedWorldView? m_museumWallpaperView;
    private NestedWorldView? m_drosteDoorView;

    private void EnsureMuseumNestedViewsRegistered(ViewStack views, string name) {
        if (string.Equals(a: name, b: MuseumRenderer.WallpaperViewName, comparisonType: StringComparison.Ordinal) && (m_museumWallpaperView is null)) {
            // Camera pose mirrors SdfGalleryScene's own WallpaperP4G entry (Target (0,-1,0), Yaw 0.5, Pitch 0.9,
            // Distance 7) — that table is private to the gallery's OWN cycling tour, so the pose is copied here as a
            // literal rather than threading a new public seam through it for one reader.
            var wallpaperSource = new SdfCompositionFrameSource(
                dresser: new GalleryExhibitDresser(target: new Vector3(x: 0f, y: -1f, z: 0f), yaw: 0.5f, pitch: 0.9f, distance: 7.0f),
                emitters: [new GalleryExhibitEmitter(exhibit: Puck.SdfVm.Debug.SdfGalleryExhibit.WallpaperP4G)]
            );

            m_museumWallpaperView = new NestedWorldView(services: m_services!, hostsOnDirectX: m_viewsHostOnDirectX, frameSource: wallpaperSource);

            _ = views.Register(band: ScreenSlotPriority.Ambient, content: m_museumWallpaperView, name: MuseumRenderer.WallpaperViewName);

            return;
        }

        if (string.Equals(a: name, b: MuseumRenderer.DoorViewName, comparisonType: StringComparison.Ordinal) && (m_drosteDoorView is null)) {
            // See DoorInteriorEmitter's remarks: a P6M wallpaper fold, not LogSphere (the earlier LogSphere attempts
            // read flat black at THIS content's natural scale under an ad hoc pose — diagnosed as a camera-framing
            // artifact of the fold's own shell-boundary residual, not a NestedWorldView/composition defect; see
            // docs/sdf-backlog.md item 29). Pose mirrors the museum's own WallpaperP4G framing (steep pitch onto the
            // tiled plane).
            var doorSource = new SdfCompositionFrameSource(
                dresser: new GalleryExhibitDresser(target: Vector3.Zero, yaw: 0.5f, pitch: 0.6f, distance: 7f),
                emitters: [new DoorInteriorEmitter()]
            );

            m_drosteDoorView = new NestedWorldView(services: m_services!, hostsOnDirectX: m_viewsHostOnDirectX, frameSource: doorSource);

            _ = views.Register(band: ScreenSlotPriority.Ambient, content: m_drosteDoorView, name: MuseumRenderer.DoorViewName);
        }
    }

    // Wraps one Puck.SdfVm.Debug.SdfDebugRenderer gallery exhibit as a standalone nested world's sole content — the
    // museum/door's own generalization of NestedWorldEmitter above (that one is fixed to the drift monolith; this one
    // is parameterized by exhibit, since both new nested worlds want a DIFFERENT already-authored gallery scene, not
    // a third hand-rolled one).
    private sealed class GalleryExhibitEmitter(Puck.SdfVm.Debug.SdfGalleryExhibit exhibit) : ISdfSceneEmitter {
        private readonly Puck.SdfVm.Debug.SdfDebugRenderer m_renderer = new();

        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) =>
            m_renderer.EmitGallery(builder: builder, exhibit: exhibit);

        public bool OwnsMaterialScope => true;
    }

    // THE DROSTE DOOR's interior: a hand-authored P6M wallpaper fold (hexagonal kaleidoscope symmetry — the SAME
    // op family the museum's own WallpaperP4G exhibit already proved renders reliably in this standalone-nested-
    // world context). The LogSphere family was tried first and read flat black here at an ad hoc pose — the
    // logsphere-hunt (docs/sdf-backlog.md item 29) proved this is NOT a NestedWorldView/composition gap: a
    // structural bisection (bare direct build vs. this exact composition+material-scope+WorstCase-capacity shape
    // vs. two more variants) rendered byte-identical pixels for both LogSphere gallery exhibits at every pose
    // tested, and SdfFrame.AmbientScale/SunScale default to 1.0 in every path. The actual driver is LogSphere's own
    // fold-safe-step-bound residual (item 24): a camera EYE near a shell-BOUNDARY radius reads near-total black,
    // and the fold is inherently under-lit up close from most vantage points — a camera-framing constraint on
    // whoever authors LogSphere content here, not an engine defect. P6M sidesteps it because a wallpaper fold has
    // no such boundary-radius sensitivity. A field of tori tiled across the XZ plane in P6M's 6-fold rotational
    // symmetry — an endless identical ring repeating in every direction reads as "ordinary doorway, impossible
    // other side" on its own, no LogSphere needed (the brief's own "p4g/p6m-folded OR Droste-spiraled" either/or).
    private sealed class DoorInteriorEmitter : ISdfSceneEmitter {
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.55f, y: 0.30f, z: 0.70f), Specular: 0.4f, Shininess: 48f));
            var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.14f, y: 0.10f, z: 0.18f)));

            _ = builder.ResetPoint().Plane(normal: Vector3.UnitY, offset: 1.1f, material: floorMaterial);
            _ = builder.ResetPoint()
                .WallpaperFold(group: SdfWallpaperGroup.P6M, cell: new Vector2(x: 1.6f, y: 1.6f), limit: new Vector2(x: 4f, y: 4f), plane: SdfWallpaperPlane.XZ)
                .Torus(majorRadius: 0.5f, minorRadius: 0.12f, material: material);
        }

        public bool OwnsMaterialScope => true;
    }

    // A fixed-pose dresser parameterized by the same (Target, Yaw, Pitch, Distance) orbit convention
    // ScreenLayoutDirector's workpiece/scenario cameras use (eye = target + distance * (sin(yaw)cos(pitch),
    // sin(pitch), cos(yaw)cos(pitch))) — the museum/door's own generalization of NestedWorldDresser above.
    private sealed class GalleryExhibitDresser(Vector3 target, float yaw, float pitch, float distance) : ISdfFrameDresser {
        public SdfFrame Dress(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var eye = (target + (new Vector3(
                x: (MathF.Sin(x: yaw) * MathF.Cos(x: pitch)),
                y: MathF.Sin(x: pitch),
                z: (MathF.Cos(x: yaw) * MathF.Cos(x: pitch))
            ) * distance));
            var camera = CameraSnapshot.LookAt(fieldOfViewRadians: CameraEye.DefaultFieldOfViewRadians, position: eye, target: target, viewportHeight: height, viewportWidth: width);

            return new SdfFrame(
                Program: program,
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f))],
                WarpAmount: 0f
            ) {
                DynamicTransforms = transforms,
            };
        }
    }

    // The stable anchor name a companion shape's creation camera binds — PublishAnchors publishes it every tick from
    // the companion emitter's live pack.
    private static string ShapeAnchorName(int companionIndex, int shapeId) => $"shape.{companionIndex}.{shapeId}";

    // The stable anchor name a world placement's camera binds — PublishAnchors publishes it every tick from the world
    // scene's live placement pose.
    private static string PlacementAnchorName(int placementId) => $"placement.{placementId}";

    // The screen indices wired to a given view name this frame (the self-reference set for that view) — a screen
    // shows this view either through a face's tune (companion faces are dynamic-claimant slots) or a world.wire.
    private IReadOnlySet<int> WiredScreenSet(string viewName) {
        var set = new HashSet<int>();

        foreach (var (screenIndex, name) in m_wiredFeedByScreen) {
            if (string.Equals(a: name, b: viewName, comparisonType: StringComparison.Ordinal)) {
                _ = set.Add(item: screenIndex);
            }
        }

        return set;
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

        return new Vector3(x: stand.Center.X, y: ((m_room.FloorY + (2f * stand.HalfExtents.Y)) + screenHalfHeight), z: stand.Center.Y);
    }

    // A console's diegetic-screen half-height (the SAME value BuildProgram/ScreenCenterLocal derive) — the pane camera
    // uses it to sit at exactly the distance that makes the screen fill the viewport height (the native-panel look).
    private float ScreenHalfHeightLocal(int consoleIndex) {
        var stand = m_room.Consoles[consoleIndex];

        return ((stand.HalfExtents.X * 0.8f) / ScreenAspect);
    }

    /// <summary>The live companion roster (the companion verbs + the screen mux read it here).</summary>
    public CompanionRoster Companions => m_companions;

    /// <summary>The room-wide region companions wander inside — recomputed against the CURRENT room, not just the
    /// one live at construction (see <see cref="ComputeCompanionBounds"/> and <see cref="ConsumePendingWorldLoad"/>).</summary>
    public WorkbenchRegion CompanionBounds => m_companionBounds;

    private WorkbenchRegion m_companionBounds;

    // The companions' steering target: the nearest RENDERED player this frame (hidden/free slots park far below the
    // floor, so they lose every distance contest naturally).
    private (Vector3 Position, float Distance)? NearestActivePlayer(Vector3 from) {
        var best = ((Vector3 Position, float Distance)?)null;

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var position = m_playerRenderTransforms[slot].Position;
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
            x: (0.5f * (m_room.BoundsMin.X + m_room.BoundsMax.X)),
            y: (m_room.FloorY + 0.9f),
            z: (0.5f * (m_room.BoundsMin.Y + m_room.BoundsMax.Y))
        );

        return (center + new Vector3(x: (1.6f + (0.9f * rosterIndex)), y: 0f, z: (1.2f - (0.8f * rosterIndex))));
    }

    // ---- The deterministic garden -----------------------------------------------------------------------------
    // Thin forwarders onto OverworldWorld (the garden's actual sim state — see its own remarks) — GardenCommandModule
    // reaches them the same way every other authoring surface reaches this source's composition point.

    /// <summary>Plants a garden near player slot 0 (or a fixed workbench-adjacent spot when it's empty). Forwards to
    /// <see cref="OverworldWorld.PlantGardenNearPlayer"/>.</summary>
    /// <param name="seed">The seed to plant, or <see langword="null"/> to derive one from the current tick.</param>
    /// <returns>The slot index, or -1 when the garden is full.</returns>
    public int PlantGarden(uint? seed) =>
        m_world.PlantGardenNearPlayer(seed: seed);

    /// <summary>Uproots every planted garden. Forwards to <see cref="OverworldWorld.ClearGardens"/>.</summary>
    public void ClearGardens() =>
        m_world.ClearGardens();

    /// <summary>The planted-garden slots. Forwards to <see cref="OverworldWorld.Gardens"/>.</summary>
    public IReadOnlyList<OverworldWorld.GardenPlant?> Gardens => m_world.Gardens;

    // ---- The RTS scenario ------------------------------------------------------------------------------------------
    // Thin forwarders onto OverworldWorld (the RTS unit pool's actual sim state) — RtsCommandModule reaches them the
    // same way GardenCommandModule reaches the garden pool. Console-facing coordinates arrive as float/double text;
    // the FixedQ4816 boundary conversion happens HERE (mirrors OverworldWorld.QuantizeMove's own float→fixed seam),
    // so OverworldWorld's own RTS API stays purely fixed-point.

    /// <summary>Spawns an RTS unit at the given room-local XZ. Forwards to <see cref="OverworldWorld.SpawnRtsUnit"/>.</summary>
    /// <param name="x">The room-local X to spawn at.</param>
    /// <param name="z">The room-local Z to spawn at.</param>
    /// <returns>The slot index, or -1 when full or blocked.</returns>
    public int SpawnRtsUnit(double x, double z) =>
        m_world.SpawnRtsUnit(x: FixedQ4816.FromDouble(value: x), z: FixedQ4816.FromDouble(value: z));

    /// <summary>Selects every active unit inside the given box. Forwards to <see cref="OverworldWorld.SelectRtsUnitsInBox"/>.</summary>
    public int SelectRtsUnitsInBox(double minX, double minZ, double maxX, double maxZ) =>
        m_world.SelectRtsUnitsInBox(minX: FixedQ4816.FromDouble(value: minX), minZ: FixedQ4816.FromDouble(value: minZ), maxX: FixedQ4816.FromDouble(value: maxX), maxZ: FixedQ4816.FromDouble(value: maxZ));

    /// <summary>Orders every selected unit to move to the given room-local XZ. Forwards to
    /// <see cref="OverworldWorld.MoveSelectedRtsUnits"/>.</summary>
    public int MoveSelectedRtsUnits(double x, double z) =>
        m_world.MoveSelectedRtsUnits(targetX: FixedQ4816.FromDouble(value: x), targetZ: FixedQ4816.FromDouble(value: z));

    /// <summary>Despawns every RTS unit. Forwards to <see cref="OverworldWorld.ClearRtsUnits"/>.</summary>
    public void ClearRtsUnits() =>
        m_world.ClearRtsUnits();

    /// <summary>The RTS unit pool. Forwards to <see cref="OverworldWorld.RtsUnits"/>.</summary>
    public IReadOnlyList<OverworldWorld.RtsUnit> RtsUnits => m_world.RtsUnits;

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

    /// <summary>Whether the workbench authoring HUB (the mode picker) is up. Bare primitive — the render node owns the
    /// picker logic through this + the registry forwarders, this source only holds the two picker cells.</summary>
    public bool HubActive => m_hubActive;
    /// <summary>The hub's highlighted mode index (into <c>AuthoringModeRegistry</c>). Bare primitive.</summary>
    public int HubSelection => m_hubSelection;

    /// <summary>Opens or closes the workbench authoring hub; closing resets the highlight so it always reopens on the
    /// first mode (WORLD — the reveal-ladder default).</summary>
    /// <param name="active">Whether the hub should be up.</param>
    public void SetHubActive(bool active) {
        m_hubActive = active;

        if (!active) {
            m_hubSelection = 0;
        }
    }

    /// <summary>Cycles the hub's highlighted mode by <paramref name="delta"/>, wrapping across <paramref name="count"/>
    /// entries (the count comes from the registry, which this source may not name).</summary>
    /// <param name="delta">The signed step (d-pad left = -1, right = +1).</param>
    /// <param name="count">The number of authoring modes.</param>
    public void AdvanceHubSelection(int delta, int count) {
        if (count <= 0) {
            return;
        }

        m_hubSelection = ((((m_hubSelection + delta) % count) + count) % count);
    }

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
        // The rendered walls follow the sim's walls: swap the room this source emits and force a room-composition
        // rebuild (a room swap changes NO tracked AdvanceRoomRevision condition on its own, so this bump is the only
        // way the emitters — which all read m_room live — get a fresh Emit call with the new walls/bounds).
        m_room = room;
        m_roomRevision++;
        // The companion wander bounds are derived from m_room (see ComputeCompanionBounds) — recompute them NOW that
        // it changed, or a loaded world's companions stay confined to the room this source started with (rank-19).
        m_companionBounds = ComputeCompanionBounds();
        // Inhabitants as data: any `companion` placement in the just-applied document joins the live roster (fresh
        // ones only — CompanionRoster.SpawnFromWorld dedupes), so a world.load (or the boot-time commit) can
        // populate the room without a companion.add per resident.
        m_companions.SpawnFromWorld(bounds: m_companionBounds, document: committed, store: m_worldStore);
        // PERSISTENCE: apply any re-forged cabinet condition the document carries (see WorldScene.FindCabinet*
        // Condition's remarks — a null field means "nothing re-forged", so this only ADDS onto the boot-authored
        // condition, never erases one) through the SAME queued edit the live condition.set verb uses, so the node's
        // next DrainControlRequests writes it onto the running brick + console-source record exactly like a live
        // re-forge would.
        for (var cabinetIndex = 0; (cabinetIndex < room.Consoles.Count); cabinetIndex++) {
            if (WorldScene.FindCabinetExitCondition(cabinetIndex: cabinetIndex, document: committed) is { } exit) {
                m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: exit, ExitSet: true, Index: cabinetIndex, Victory: null, VictorySet: false));
            }

            if (WorldScene.FindCabinetVictoryCondition(cabinetIndex: cabinetIndex, document: committed) is { } victory) {
                m_pendingConditionEdits.Enqueue(item: new PendingConditionEdit(Exit: null, ExitSet: false, Index: cabinetIndex, Victory: victory, VictorySet: true));
            }
        }

        return $"[world] applied — bounds ({room.BoundsMin.X:F0},{room.BoundsMin.Y:F0})..({room.BoundsMax.X:F0},{room.BoundsMax.Y:F0}), walk grid {((walkGrid is null) ? "walls-only" : "baked")}, movement {movementLock}";
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
    /// the scenario objects). The live in-session paths (<c>companion.add</c> / <c>world.wire</c> /
    /// <c>companion.face</c> / <c>world.verify</c>
    /// console verbs) are the only way to reach them now.</summary>
    /// <param name="services">The application services (the bound scenario options).</param>
    /// <param name="bootWorld">The document's world handle (<see cref="Puck.Scene.OverworldNode.World"/>) resolved +
    /// committed at boot, or null for the plain room. The render node threads it from the run document. The first
    /// tick-boundary ConsumePendingWorldLoad swaps the
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
        if (CreationStore.Load(nameOrPath: nameOrPath, creationsRoot: CreationStore.DefaultFolder) is not { } document) {
            return $"[creator.load: nothing readable at '{nameOrPath}']";
        }

        // Loading is legal while the mode is down (the shapes persist; entering creator reveals them).
        var loaded = m_creator.LoadDocument(document: document);

        return $"[creator.load: {loaded} shape(s) from '{document.Name}' (style {document.BakeStyle}, intent {document.Intent})]";
    }

    /// <summary>The packed-word floor the engine's program buffer must reserve — the MAX across every non-composing
    /// takeover's own worst-case probe (see <see cref="SdfCompositionFrameSource.WorstCaseProgramWordCapacity"/>): the
    /// room, the SDF-debug composition, and the GRAVITY ARC's planetoid composition never compose together in one
    /// program (each REPLACES the others), so their envelopes need only the larger, not the sum — the generalization
    /// of the old hand-rolled <c>MeasureWorstCaseEnvelope</c>. The AGB takeover needs no term here (a single
    /// fullscreen slab is strictly dominated by the room's own worst case — see <see cref="BuildAgbDebugProgram"/>'s
    /// remarks).</summary>
    public int WorstCaseProgramWordCapacity => Math.Max(Math.Max(m_roomComposition.WorstCaseProgramWordCapacity, m_sdfDebugComposition.WorstCaseProgramWordCapacity), m_gravityComposition.WorstCaseProgramWordCapacity);

    /// <summary>The instance-count floor the engine's mask buffer must reserve (see
    /// <see cref="WorstCaseProgramWordCapacity"/>).</summary>
    public int WorstCaseInstanceCapacity => Math.Max(Math.Max(m_roomComposition.WorstCaseInstanceCapacity, m_sdfDebugComposition.WorstCaseInstanceCapacity), m_gravityComposition.WorstCaseInstanceCapacity);

    /// <summary>The dynamic-transform slot FLOOR the render assembly reserves — the max of the room composition's own
    /// moving-slot population, the SDF-debug composition's storm-bench ceiling, and the GRAVITY ARC's one walker slot.
    /// The engine sizes its per-frame dynamic-transform buffer ONCE at construction, so a storm MOTION program (up to
    /// that many moving instances) can only upload if this floor was passed to the assembly — the room's small
    /// population never approaches it, so this is entirely the storm reservation (a one-time buffer sizing; the
    /// reserved-but-unused slots cost no per-frame work outside a storm run).</summary>
    public int WorstCaseDynamicTransformCapacity => Math.Max(Math.Max(m_roomComposition.WorstCaseDynamicTransformCapacity, m_sdfDebugComposition.WorstCaseDynamicTransformCapacity), m_gravityComposition.WorstCaseDynamicTransformCapacity);

    // Each console's control cluster: a d-pad cross (tilts toward the held direction) and two round buttons (A/B,
    // depress a few centimeters when held). Reads the SAME per-frame joypad state the console's machine consumes —
    // never re-derived from raw input — so an unbooted/unassigned console (whose provider is absent from
    // m_controlsSource's backing dictionary, or simply never pressed) stays in its neutral pose.
    private void PackControlTransforms(Span<DynamicTransform> slots, int slotBase) {
        for (var consoleIndex = 0; (consoleIndex < m_room.Consoles.Count); consoleIndex++) {
            var stand = m_room.Consoles[consoleIndex];
            var buttons = (m_controlsSource?.Invoke(consoleIndex) ?? JoypadButtons.None);
            var anchor = ControlClusterAnchor(stand: stand);
            var dPadPressed = buttons & (JoypadButtons.Left | JoypadButtons.Right | JoypadButtons.Up | JoypadButtons.Down);
            // Composed per-axis (not a single switch on the exact combo) so a diagonal press (Left+Up, a real GB
            // joypad state) tilts both axes at once instead of falling through to a neutral pose.
            var tiltZ = (((dPadPressed & JoypadButtons.Right) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Left) != 0) ? -DPadTiltRadians : 0f));
            var tiltX = (((dPadPressed & JoypadButtons.Up) != 0) ? DPadTiltRadians : (((dPadPressed & JoypadButtons.Down) != 0) ? -DPadTiltRadians : 0f));
            var dPadTilt = (Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: tiltX) * Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: tiltZ));

            var baseSlot = (slotBase + (consoleIndex * ControlsPerConsole));

            slots[(baseSlot + DPadControlOffset)] = new DynamicTransform(
                Position: ((dPadPressed != JoypadButtons.None) ? DepressedInward(position: anchor.DPad) : anchor.DPad),
                Orientation: dPadTilt
            );
            slots[(baseSlot + AButtonControlOffset)] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.A) != 0) ? DepressedInward(position: anchor.A) : anchor.A),
                Orientation: Quaternion.Identity
            );
            slots[(baseSlot + BButtonControlOffset)] = new DynamicTransform(
                Position: (((buttons & JoypadButtons.B) != 0) ? DepressedInward(position: anchor.B) : anchor.B),
                Orientation: Quaternion.Identity
            );
        }
    }

    // A pressed control moves a few centimeters INTO the pedestal (−Z, against the front-face normal) — a uniform
    // depress, the cheap fallback the mission allows when a per-shape tilt isn't warranted (the buttons are round,
    // not directional).
    private static Vector3 DepressedInward(Vector3 position) =>
        (position - new Vector3(x: 0f, y: 0f, z: ControlDepress));
    private (Vector3 DPad, Vector3 A, Vector3 B) ControlClusterAnchor(ConsoleStand stand) {
        // Centered-low on the pedestal's front face (+Z, the same face the screen and the stand's cartridge slot
        // share); the d-pad sits left of center, A/B sit right of center — a minimal cluster sized to read at room
        // scale without crowding the cartridge slot (offset to the LEFT of stand center; the cluster favors the
        // right half).
        var faceZ = (stand.Center.Y + stand.HalfExtents.Z);
        var faceY = (m_room.FloorY + (stand.HalfExtents.Y * 0.65f));
        var dPad = new Vector3(x: (stand.Center.X - (stand.HalfExtents.X * 0.15f)), y: faceY, z: faceZ);
        var buttonA = new Vector3(x: (stand.Center.X + (stand.HalfExtents.X * 0.45f)), y: (faceY + 0.06f), z: faceZ);
        var buttonB = new Vector3(x: (stand.Center.X + (stand.HalfExtents.X * 0.25f)), y: (faceY - 0.06f), z: faceZ);

        return (dPad, buttonA, buttonB);
    }
    private Vector3 HiddenPosition() =>
        new(x: 0f, y: (m_room.FloorY - 1000f), z: 0f);

    // Ease the workbench power-on glow toward the editor unlock (1 when EditorRevealed, else 0). A linear ramp on the
    // render clock — enough that the panel's emissive fades in smoothly; the discrete bucket quantization below keeps the
    // rebuild count bounded (~WorkbenchGlowBuckets rebuilds over the whole ramp, a boot's cost profile). Presentation
    // only; the sim never sees the glow.
    private void AdvanceWorkbenchGlow(float deltaSeconds) {
        var target = (m_editorRevealed ? 1f : 0f);
        var step = (WorkbenchGlowRate * MathF.Max(x: deltaSeconds, y: 0f));

        m_workbenchGlow = ((m_workbenchGlow < target)
            ? MathF.Min(x: target, y: (m_workbenchGlow + step))
            : MathF.Max(x: target, y: (m_workbenchGlow - step)));
    }

    // The current glow quantized into a bucket 0..WorkbenchGlowBuckets — the program-rebuild key for the workbench
    // panel's emissive (so the ramp rebuilds a bounded number of times, never once per frame at rest). 0 = dark, the
    // top bucket = fully lit. Keys on the SmoothStep-EASED glow (WorkbenchGlowEased), the exact value EmitWorkbench
    // bakes, so the rebuild fires precisely when the baked panel would visibly change — never a bucket/emissive skew.
    private int WorkbenchGlowBucket() =>
        (int)MathF.Round(x: (WorkbenchGlowEased() * WorkbenchGlowBuckets));

    // The eased power-on glow: the linear ramp shaped by SmoothStep (0..1, clamped), so the panel accelerates on then
    // settles — the ONE easing shared by the rebuild-bucket key and EmitWorkbench's baked emissive/albedo read.
    private float WorkbenchGlowEased() {
        var t = Math.Clamp(value: m_workbenchGlow, min: 0f, max: 1f);

        return ((t * t) * (3f - (2f * t)));
    }

    // The editor-reveal beat's bell (Q35): 0 at the ends, 1 at the mid-beat peak (sin over the span), so both the light
    // pulse and the camera nudge swell in and out with no snap. Zero once the beat has elapsed (or before it is armed).
    private float EditorRevealBeat() {
        if (m_editorRevealBeatTime >= EditorRevealBeatSeconds) {
            return 0f;
        }

        return MathF.Sin(x: ((MathF.PI * m_editorRevealBeatTime) / EditorRevealBeatSeconds));
    }

    // The beat's room-camera target nudge (Q35): a small look-point lift scaled by the bell, fed to the director's
    // RoomTargetNudge hook so it rides the existing eased room framing. Zero at rest → the room frames unchanged.
    private Vector3 EditorRevealBeatTargetNudge() =>
        new(x: 0f, y: (EditorRevealBeatTargetLift * EditorRevealBeat()), z: 0f);

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
    // Whether the room's studio-suppressible content should emit this call — PROBE always emits everything (the
    // worst-case envelope must cover every legal program); a live studio review (--scenario Studio) drops it (the
    // workpiece alone, no room/cabinets/players/workbench/terminal/bar). Shared by every room emitter that block
    // gated in the old BuildProgram (see OverworldFrameSource.Emitters.cs).
    private bool StudioSuppressed(in SdfEmitContext context) => (!context.Probe && m_scenarioStudio);

    // THE ROOM SHELL: the floor plane, the four perimeter walls, and the cartridge-shelf brackets (RoomEmitter). The
    // render anchor IS the spawn anchor, so the room is authored directly in the spawn cell's local frame (origin
    // delta identically zero).
    private void EmitFloorWallsShelf(SdfProgramBuilder builder) {
        var origin = Vector3.Zero;
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.34f, y: 0.36f, z: 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.50f, y: 0.46f, z: 0.58f)));
        var shelfMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.40f, y: 0.38f, z: 0.35f)));

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
        AddWall(builder: builder, center: (new Vector3(x: maxX, y: wallCenterY, z: midZ) - origin), halfExtents: new Vector3(x: wallThickness, y: wallHeight, z: halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(x: minX, y: wallCenterY, z: midZ) - origin), halfExtents: new Vector3(x: wallThickness, y: wallHeight, z: halfSpanZ), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(x: midX, y: wallCenterY, z: maxZ) - origin), halfExtents: new Vector3(x: halfSpanX, y: wallHeight, z: wallThickness), material: wallMaterial);
        AddWall(builder: builder, center: (new Vector3(x: midX, y: wallCenterY, z: minZ) - origin), halfExtents: new Vector3(x: halfSpanX, y: wallHeight, z: wallThickness), material: wallMaterial);

        // The cartridge shelf: one static wall-mounted bracket per shelf slot (a simple slab, always present). The
        // brackets are inert wall furniture — the cart choice lives at the cabinet, so no cartridge boxes rest on
        // them. One static instance PER SLOT (not one for the whole strip): a full 8-slot shelf spans ~15 world
        // units, so a single enclosing sphere would cover most of the room and defeat the tile cull; per-slot bounds
        // stay tight (ShelfInstanceRadius).
        for (var index = 0; (index < m_room.Shelf.Count); index++) {
            var anchor = m_room.Shelf[index];
            var bracketCenter = new Vector3(x: anchor.Center.X, y: (m_room.FloorY + (anchor.HalfExtents.Y * 0.5f)), z: anchor.Center.Y);

            _ = builder.BeginInstance(boundCenter: bracketCenter, boundRadius: ShelfInstanceRadius);
            _ = builder.ResetPoint().Translate(offset: (bracketCenter - origin)).Box(halfExtents: new Vector3(x: anchor.HalfExtents.X, y: (anchor.HalfExtents.Y * 0.5f), z: anchor.HalfExtents.Z), round: 0.03f, material: shelfMaterial);
            _ = builder.EndInstance();
        }
    }

    // THE CONSOLE STANDS (ConsoleStandEmitter): each is a pedestal (its accent color, boot target + obstacle) with a
    // screen slab sitting on top facing the room (+Z, toward the player area — the stands sit near the −Z wall). An
    // UNBOOTED stand's screen is the powered-off dark box; a boot swaps that one instruction for a diegetic
    // screen-surface slab (identical instruction count, constant material table) whose world frame MATCHES the
    // geometry: worldOrigin sits on the slab's front face (center + halfExtents.Z along +Z, the face normal),
    // worldRight/worldUp are the slab's local +X/+Y in world space (no rotation is applied to the point before the
    // Box, so they are simply +X/+Y). The screen-surface table therefore changes ONLY on boot rebuilds — exactly when
    // the program re-uploads anyway.
    //
    // Each stand is ONE static per-object instance: pedestal, screen, cartridge-slot patch, control housing, and
    // the stand's 3 control-cluster pieces (which ride their own dynamic-transform slots — controlSlotBase, this
    // emitter's own SdfEmitContext.SlotBase — but only travel a few centimeters around the stand) all fold into a
    // single bound centered on the pedestal — StandInstanceRadius is sized generously past the farthest member (the
    // screen slab) so the beam prepass's tile cull never clips any of them. The control pieces are emitted HERE (not
    // in a trailing loop) so the whole stand is one CONTIGUOUS instruction range — BeginInstance/EndInstance cannot
    // straddle a gap.
    private void EmitConsoleStands(SdfProgramBuilder builder, uint bootedMask, bool probeWorstCase, int controlSlotBase) {
        var origin = Vector3.Zero;
        var screenOffMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.08f, y: 0.09f, z: 0.10f)));
        var controlMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.15f, y: 0.15f, z: 0.17f)));

        for (var index = 0; (index < m_room.Consoles.Count); index++) {
            var stand = m_room.Consoles[index];
            var accent = m_consoleAccents[(index % m_consoleAccents.Count)];
            var bodyMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: accent));
            // The lit material is ALWAYS added (constant material table across rebuilds — the program buffer is sized
            // once); the boot bit only selects which index the screen references.
            var screenLitMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: ((accent * 0.35f) + new Vector3(x: 0.55f, y: 0.62f, z: 0.45f))));
            var screenMaterial = ((0u != (bootedMask & (1u << index))) ? screenLitMaterial : screenOffMaterial);
            var pedestalCenter = new Vector3(x: stand.Center.X, y: (m_room.FloorY + stand.HalfExtents.Y), z: stand.Center.Y);
            // Sized close to the GB's 10:9 aspect (width : height) so a bound screen source isn't grossly stretched.
            var screenHalfWidth = (stand.HalfExtents.X * 0.8f);
            var screenHalfExtents = new Vector3(x: screenHalfWidth, y: (screenHalfWidth / ScreenAspect), z: 0.08f);
            var screenCenter = new Vector3(x: stand.Center.X, y: ((m_room.FloorY + (2f * stand.HalfExtents.Y)) + screenHalfExtents.Y), z: stand.Center.Y);
            var screenFaceOrigin = ((screenCenter + new Vector3(x: 0f, y: 0f, z: screenHalfExtents.Z)) - origin);
            var buttonMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.85f, y: 0.15f, z: 0.20f)));

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
            var slotCenter = new Vector3(x: (stand.Center.X - (stand.HalfExtents.X * 0.55f)), y: (m_room.FloorY + (stand.HalfExtents.Y * 1.5f)), z: ((stand.Center.Y + stand.HalfExtents.Z) - 0.02f));

            _ = builder.ResetPoint().Translate(offset: (slotCenter - origin)).Box(halfExtents: new Vector3(x: 0.16f, y: 0.20f, z: 0.02f), round: 0.01f, material: bodyMaterial);

            // The control cluster: a static housing bump behind the animated pieces (the d-pad/button shapes ride
            // their own dynamic-transform slots below), so the cluster reads as recessed into the pedestal.
            var anchor = ControlClusterAnchor(stand: stand);
            var housingCenter = new Vector3(x: ((anchor.DPad.X + anchor.A.X) * 0.5f), y: anchor.DPad.Y, z: ((stand.Center.Y + stand.HalfExtents.Z) - 0.01f));

            _ = builder.ResetPoint().Translate(offset: (housingCenter - origin)).Box(halfExtents: new Vector3(x: (stand.HalfExtents.X * 0.7f), y: 0.12f, z: 0.01f), round: 0.02f, material: controlMaterial);

            // The stand's animated control cluster: a d-pad cross (two crossed boxes) and two round buttons (A/B),
            // one dynamic-transform slot per piece — folded into THIS stand's instance (they ride a dynamic slot but
            // only travel a few centimeters, well inside StandInstanceRadius's margin). PackControlTransforms
            // depresses/tilts pressed pieces every frame; the instruction count here never changes.
            var baseSlot = (controlSlotBase + (index * ControlsPerConsole));

            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(x: 0.09f, y: 0.03f, z: 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + DPadControlOffset)).Box(halfExtents: new Vector3(x: 0.03f, y: 0.09f, z: 0.015f), round: 0.005f, material: controlMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + AButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.ResetPoint().TransformDynamic(slot: (baseSlot + BButtonControlOffset)).Sphere(radius: 0.035f, material: buttonMaterial);
            _ = builder.EndInstance();
        }
    }

    // ONE player box per FIXED slot (PlayerBoxEmitter), placed by its per-frame dynamic transform (active slots at
    // the player, free slots hidden below the floor). One dynamic instance per slot: the bound tracks the slot's own
    // position (boundOffset zero — the box is centered on the slot). A FREE slot's box is PARKED (Active=false) so the
    // beam cull skips it with one branch instead of testing its hidden sphere per tile; a join/leave flips the active
    // mask and rebuilds (ActivePlayerMask), so the reserved slot count is unchanged — the once-sized buffers stay
    // valid. The probe keeps every slot active (worst case).
    private void EmitPlayerBoxes(SdfProgramBuilder builder, bool probeWorstCase, int slotBase) {
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.93f, y: 0.52f, z: 0.18f)));

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var occupied = (probeWorstCase || ((slot < m_world.Slots.Count) && (m_world.Slots[slot] is not null)));
            var dynamicSlot = (slotBase + slot);

            _ = builder.BeginInstanceDynamic(slot: dynamicSlot, boundOffset: Vector3.Zero, boundRadius: PlayerInstanceRadius, active: occupied);
            _ = builder.ResetPoint().TransformDynamic(slot: dynamicSlot).Box(halfExtents: m_room.PlayerHalfExtents, round: 0.06f, material: playerMaterial);
            _ = builder.EndInstance();
        }
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
        // SmoothStep-eased glow (WorkbenchGlowEased — the SAME value the rebuild bucket keys on), so the powered-off
        // panel is a plain matte box (emissive 0) and a fully-revealed one glows, easing in on a smooth curve.
        var glow = (probeWorstCase ? 1f : WorkbenchGlowEased());
        var deskMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.30f, y: 0.26f, z: 0.22f)));
        var frameMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.12f, y: 0.13f, z: 0.15f)));
        // The lit panel: a cool CRT teal that brightens from near-black (dark) to a self-illuminated glow. The albedo
        // itself lifts a touch with the glow so the OFF panel reads as a dead dark screen, not a dim colored one.
        var panelAlbedo = Vector3.Lerp(value1: new Vector3(x: 0.05f, y: 0.06f, z: 0.07f), value2: new Vector3(x: 0.45f, y: 0.85f, z: 0.70f), amount: glow);
        var panelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: panelAlbedo, Emissive: (WorkbenchLitEmissive * glow)));

        var floorY = m_room.FloorY;
        var deskCenter = new Vector3(x: centerX, y: (floorY + WorkbenchDeskTopY), z: centerZ);
        var legY = (floorY + WorkbenchLegHalfHeight);
        var legFrontZ = (centerZ - (WorkbenchDeskHalfDepth * 0.7f));
        var legBackZ = (centerZ + (WorkbenchDeskHalfDepth * 0.7f));
        var legLeftX = (centerX - (WorkbenchDeskHalfWidth * 0.8f));
        var legRightX = (centerX + (WorkbenchDeskHalfWidth * 0.8f));
        // The monitor rides above the desk, tilted slightly back. Its dark backing/hood sits BEHIND (+Z) the glowing
        // panel; the emissive panel is pushed clearly IN FRONT (−Z, the room-facing side the player approaches) so the
        // lit face is the surface every ray hits first — never occluded by the hood (the sliver-behind-a-frame bug).
        var monitorCenter = new Vector3(x: centerX, y: (deskCenter.Y + WorkbenchPanelRiseY), z: centerZ);
        var panelTilt = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitX, angle: WorkbenchPanelTiltRadians);

        _ = builder.BeginInstance(boundCenter: deskCenter, boundRadius: WorkbenchInstanceRadius);
        // Desktop slab.
        _ = builder.ResetPoint().Translate(offset: (deskCenter - origin)).Box(halfExtents: new Vector3(x: WorkbenchDeskHalfWidth, y: WorkbenchDeskHalfHeight, z: WorkbenchDeskHalfDepth), round: 0.03f, material: deskMaterial);
        // Two legs (front-left, back-right — a diagonal pair reads as support without four separate members).
        _ = builder.ResetPoint().Translate(offset: (new Vector3(x: legLeftX, y: legY, z: legFrontZ) - origin)).Box(halfExtents: new Vector3(x: 0.05f, y: WorkbenchLegHalfHeight, z: 0.05f), round: 0.01f, material: deskMaterial);
        _ = builder.ResetPoint().Translate(offset: (new Vector3(x: legRightX, y: legY, z: legBackZ) - origin)).Box(halfExtents: new Vector3(x: 0.05f, y: WorkbenchLegHalfHeight, z: 0.05f), round: 0.01f, material: deskMaterial);
        // The monitor's dark matte backing plate: sits directly BEHIND the panel along the tilted local +Z (the
        // laid-back screen's underside), a hair thinner and the same footprint, so it gives the terminal a solid body
        // without ever occluding the up-facing glowing face the camera and player both see.
        _ = builder.ResetPoint().Translate(offset: (monitorCenter - origin)).Rotate(rotation: panelTilt).Translate(offset: new Vector3(x: 0f, y: 0f, z: (WorkbenchPanelHalfDepth * 1.6f))).Box(halfExtents: new Vector3(x: (WorkbenchPanelHalfWidth + 0.04f), y: (WorkbenchPanelHalfHeight + 0.04f), z: (WorkbenchPanelHalfDepth * 0.6f)), round: 0.03f, material: frameMaterial);
        // The screen panel: dark box when locked, an emissive CRT glow when revealed. Tilted back so its lit face
        // points up-and-toward the room — visible from a floor-level approach AND the high iso reveal camera. Emissive
        // lifts every visible face uniformly, so the whole panel reads as powered-on.
        _ = builder.ResetPoint().Translate(offset: (monitorCenter - origin)).Rotate(rotation: panelTilt).Box(halfExtents: new Vector3(x: WorkbenchPanelHalfWidth, y: WorkbenchPanelHalfHeight, z: WorkbenchPanelHalfDepth), round: 0.02f, material: panelMaterial);
        _ = builder.EndInstance();
    }

    // THE DIEGETIC CONSOLE TERMINAL (diegetic-UI Tier 0): a modest pedestal + a room-facing CRT slab that samples the
    // console named feed at the terminal's headroom screen slot — the control plane made flesh. ONE static instance
    // bounded on the pedestal (TerminalInstanceRadius). Placed against the +Z (near) wall, offset toward −X, its CRT
    // facing −Z back into the room; the sampling frame is the +Z cabinet convention mirrored 180° about Y (worldRight
    // −X, worldUp +Y, front face on −Z) so the console reads un-mirrored exactly as a cabinet does from its own front.
    // Visual-only (no collision — the sim's FixedRoom never learns it exists), like the workbench. HIDDEN (the `terminal
    // off` verb) swaps the CRT slab for a dark box — the same instruction-count-neutral boot/unboot swap the cabinets
    // use, so the eased rebuilds never resize a buffer. The probe emits the LIT (slab) form unconditionally so the
    // worst-case envelope reserves the extra screen surface — MeasureWorstCaseEnvelope's binding rule for any new
    // emission; the terminal is the eighth (last) screen surface at a full house (4 cabinets + 3 companion faces + it).
    private void EmitTerminal(SdfProgramBuilder builder, Vector3 origin, bool probeWorstCase) {
        var floorY = m_room.FloorY;
        var centerX = (m_room.BoundsMin.X + TerminalInsetX);
        var centerZ = (m_room.BoundsMax.Y - TerminalInsetZ);
        // The bezel/screen-well palette (docs/ui-design-tokens.md section 6's "CRT quote"): the pedestal body reads as
        // the OUTER bezel tone, the hood tucked behind the glass as the darker INNER bezel tone, and the powered-off
        // glass as the screen well's dark end — the same well a lit ScreenSlab's shader tints from underneath. Literal
        // floats (not a DesignTokens reference — this source is at its exact CA1506 coupling ceiling and cannot name
        // the type) transcribing BezelOuter #23282B / BezelInner #14181A / ScreenWellInner #05100C exactly.
        var bodyMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.1373f, y: 0.1569f, z: 0.1686f)));
        var hoodMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.0784f, y: 0.0941f, z: 0.1020f)));
        var screenOffMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.0196f, y: 0.0627f, z: 0.0471f)));

        var pedestalCenter = new Vector3(x: centerX, y: (floorY + TerminalPedestalHalfHeight), z: centerZ);
        var screenHalfHeight = (TerminalScreenHalfWidth / TerminalCrtAspect);
        var screenHalfExtents = new Vector3(x: TerminalScreenHalfWidth, y: screenHalfHeight, z: TerminalScreenHalfDepth);
        var screenCenter = new Vector3(x: centerX, y: (((floorY + (2f * TerminalPedestalHalfHeight)) + TerminalScreenGap) + screenHalfHeight), z: centerZ);
        // The CRT front face is on −Z (toward the room); its world-space sampling frame is the +Z cabinet frame turned
        // 180° about Y — worldRight −X, worldUp +Y — so the sampled image is un-mirrored from the −Z viewing side.
        var screenFaceOrigin = ((screenCenter + new Vector3(x: 0f, y: 0f, z: -TerminalScreenHalfDepth)) - origin);
        // The pedestal is always present (like the workbench); only the CRT face toggles between the live screen slab
        // and a powered-off dark box. The probe always takes the slab form so the worst-case envelope covers the extra
        // screen surface.
        var showScreen = (probeWorstCase || m_terminalVisible);

        _ = builder.BeginInstance(boundCenter: pedestalCenter, boundRadius: TerminalInstanceRadius);
        // The pedestal body.
        _ = builder.ResetPoint().Translate(offset: (pedestalCenter - origin)).Box(halfExtents: new Vector3(x: TerminalPedestalHalfWidth, y: TerminalPedestalHalfHeight, z: TerminalPedestalHalfDepth), round: 0.05f, material: bodyMaterial);
        // The dark hood/bezel sits directly BEHIND (+Z) the CRT face, a hair larger, so the terminal has a solid body
        // without ever occluding the −Z-facing screen the player and camera see (the sliver-behind-a-frame bug).
        _ = builder.ResetPoint().Translate(offset: ((screenCenter + new Vector3(x: 0f, y: 0f, z: (TerminalScreenHalfDepth * 0.9f))) - origin)).Box(halfExtents: new Vector3(x: (TerminalScreenHalfWidth + 0.05f), y: (screenHalfHeight + 0.05f), z: (TerminalScreenHalfDepth * 0.8f)), round: 0.03f, material: hoodMaterial);

        if (showScreen) {
            // The CRT slab: samples the console named feed at TerminalScreenSlot (0 = unbound → the flat screen material,
            // a blank dark CRT, e.g. on a host with no glyph atlas). The Anchored ledger claim (InstallFeeds) guarantees
            // the terminal seats THIS exact slot, so the baked screenIndex and the resolved source always agree.
            _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).ScreenSlab(
                halfExtents: screenHalfExtents,
                round: 0.03f,
                worldOrigin: screenFaceOrigin,
                worldRight: -Vector3.UnitX,
                worldUp: Vector3.UnitY,
                screenIndex: TerminalScreenSlot
            );
        } else {
            // Hidden (the `terminal off` dev toggle): the powered-off dark CRT box, an instruction-count-neutral swap for
            // the slab above (mirrors a cabinet's unbooted screen).
            _ = builder.ResetPoint().Translate(offset: (screenCenter - origin)).Box(halfExtents: screenHalfExtents, round: 0.03f, material: screenOffMaterial);
        }

        _ = builder.EndInstance();
    }

    // THE DIEGETIC UI BAR (Tier 2): the live path delegates to the installed director (which lays the real panels +
    // embossed labels from the current binding-bar frame), gated on the visibility latch; the probe path emits a
    // synthetic MAX-size bar directly so MeasureWorstCaseEnvelope reserves the envelope BEFORE the director installs
    // (the probe runs at spec construction, ahead of the render assembly's InstallDiegeticUi). The real emit is a
    // strict subset of the probe (fewer visible chips, shorter labels), so the once-sized buffers always fit —
    // MeasureWorstCaseEnvelope's binding rule for any new optional emission, mirroring EmitTerminal's lit-form probe.
    private void EmitDiegeticBar(SdfProgramBuilder builder, bool probeWorstCase, int slotBase) {
        if (probeWorstCase) {
            EmitDiegeticBarProbe(builder: builder, slotBase: slotBase);

            return;
        }

        if (m_diegeticUiVisible) {
            m_diegeticEmit?.Invoke(obj: builder);
        }
    }

    // The synthetic worst-case bar: ONE dynamic instance carrying the maximum structure the director can ever emit —
    // a backing plate, DiegeticBarMaxChips panels, and DiegeticBarMaxLabelChars glyph cells per chip — plus the seven
    // materials the director adds (backing, the four chip tiers — rest/held/accent/disabled — and the two label
    // tones — embossed/accent-ink). Positions/UVs are placeholders, and the panels use Box rather than the director's
    // RoundedRectangle: every shape instruction packs to the SAME fixed word width (SdfProgram's uniform per-instruction
    // packing), so Box reserves an identical envelope while keeping this ceilinged source clear of the SdfLift type the
    // rounded-rectangle op would add. The probe measures word/instance COUNT, never appearance or material count (a
    // program's material list is NOT a frozen capacity — see SdfProgramBuilder.AddMaterial — so extra materials never
    // risk outgrowing a buffer; they are still mirrored here purely so this comment and the director's real material
    // set never drift apart). Literal floats (not a DesignTokens reference — this source is at its exact CA1506
    // coupling ceiling) transcribing PlateMid #24272B / EmbossFill #DFE6E1 / EngraveFill #14171A / Accent #FF6A2B /
    // AccentInk #160A04 exactly — see DiegeticUiDirector's own material palette for the token derivation. KEEP IN
    // SYNC with DiegeticUiDirector's Emit (its structure must never exceed this: ≤ DiegeticBarMaxChips visible chips,
    // ≤ DiegeticBarMaxLabelChars label glyphs per chip, one backing plate).
    private void EmitDiegeticBarProbe(SdfProgramBuilder builder, int slotBase) {
        var backingMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.1412f, y: 0.1529f, z: 0.1686f)));
        var chipRestMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8745f, y: 0.9020f, z: 0.8824f), Emissive: 0.06f));
        var chipHeldMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.0784f, y: 0.0902f, z: 0.1020f)));
        var chipAccentMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 1.0f, y: 0.4157f, z: 0.1686f), Emissive: 0.55f));
        var chipDisabledMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.0784f, y: 0.0902f, z: 0.1020f)));
        var labelMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8745f, y: 0.9020f, z: 0.8824f), Emissive: 0.10f));
        var labelAccentInkMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.0863f, y: 0.0392f, z: 0.0157f)));
        var chipMaterials = new[] { chipRestMaterial, chipHeldMaterial, chipAccentMaterial, chipDisabledMaterial };

        _ = builder.BeginInstanceDynamic(slot: slotBase, boundOffset: Vector3.Zero, boundRadius: DiegeticBarBoundRadius, active: true);
        _ = builder.ResetPoint().TransformDynamic(slot: slotBase).Translate(offset: new Vector3(x: 0f, y: 0f, z: -0.01f)).Box(halfExtents: new Vector3(x: 0.6f, y: 0.15f, z: 0.01f), round: 0.06f, material: backingMaterial);

        for (var chip = 0; (chip < DiegeticBarMaxChips); chip++) {
            var chipX = (-0.55f + (chip * 0.1f));
            var chipMaterial = chipMaterials[(chip % chipMaterials.Length)];
            var chipLabelMaterial = ((chipMaterial == chipAccentMaterial) ? labelAccentInkMaterial : labelMaterial);

            _ = builder.ResetPoint().TransformDynamic(slot: slotBase).Translate(offset: new Vector3(x: chipX, y: 0f, z: 0.01f)).Box(halfExtents: new Vector3(x: 0.045f, y: 0.045f, z: 0.01f), round: 0.012f, material: chipMaterial);

            for (var glyph = 0; (glyph < DiegeticBarMaxLabelChars); glyph++) {
                _ = builder.ResetPoint().TransformDynamic(slot: slotBase).Translate(offset: new Vector3(x: (chipX + (glyph * 0.02f)), y: 0f, z: 0.02f)).Glyph(uvBottomLeft: new Vector2(x: 0f, y: 0.1f), uvTopRight: new Vector2(x: 0.1f, y: 0f), halfWidth: 0.02f, halfHeight: 0.03f, extrudeHalfDepth: 0.006f, distanceScale: 0.05f, material: chipLabelMaterial);
            }
        }

        // The hub mode readout's worst case: DiegeticTitleMaxChars glyph cells riding the SAME dynamic slot + label
        // material as the chip labels, above the cluster. KEEP IN SYNC with DiegeticUiDirector.Emit's HubTitle run.
        for (var glyph = 0; (glyph < DiegeticTitleMaxChars); glyph++) {
            _ = builder.ResetPoint().TransformDynamic(slot: slotBase).Translate(offset: new Vector3(x: (glyph * 0.03f), y: 0.2f, z: 0.02f)).Glyph(uvBottomLeft: new Vector2(x: 0f, y: 0.1f), uvTopRight: new Vector2(x: 0.1f, y: 0f), halfWidth: 0.03f, halfHeight: 0.04f, extrudeHalfDepth: 0.006f, distanceScale: 0.05f, material: labelMaterial);
        }

        _ = builder.EndInstance();

        // The terminal nameplate's worst case: ONE static instance carrying DiegeticNameplateMaxChars glyph cells (the
        // director lays them with SdfProgramBuilder.Text, ResetPoint+Translate+Rotate+Glyph per glyph — matched here) and
        // its one material. KEEP IN SYNC with DiegeticUiDirector.EmitNameplate.
        // Literal EmbossFill #DFE6E1 (see EmitDiegeticBarProbe's remark on why this source cannot name DesignTokens).
        var nameplateMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(x: 0.8745f, y: 0.9020f, z: 0.8824f), Emissive: 0.12f));

        _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 0.6f);

        for (var glyph = 0; (glyph < DiegeticNameplateMaxChars); glyph++) {
            _ = builder.ResetPoint().Translate(offset: new Vector3(x: (glyph * 0.05f), y: 0f, z: 0f)).Rotate(rotation: Quaternion.Identity).Glyph(uvBottomLeft: new Vector2(x: 0f, y: 0.1f), uvTopRight: new Vector2(x: 0.1f, y: 0f), halfWidth: 0.02f, halfHeight: 0.03f, extrudeHalfDepth: 0.008f, distanceScale: 0.05f, material: nameplateMaterial);
        }

        _ = builder.EndInstance();
    }

    /// <summary>The diegetic terminal NAMEPLATE's face-centre in world space — pulled live by the diegetic-UI director
    /// each build so its embossed sign tracks the terminal (which moves when a world loads). Sits on the pedestal's
    /// upper front face (the −Z, room-facing side). Primitive-typed (Vector3) so the director wires it without this
    /// source — at its coupling ceiling — naming the director.</summary>
    public Vector3 TerminalNameplateCentre() {
        var centerX = (m_room.BoundsMin.X + TerminalInsetX);
        var centerZ = (m_room.BoundsMax.Y - TerminalInsetZ);

        return new Vector3(x: centerX, y: (m_room.FloorY + (TerminalPedestalHalfHeight * 1.35f)), z: (centerZ - TerminalPedestalHalfDepth));
    }

    /// <summary>The nameplate face's world +X (advance) axis — −X, mirroring the terminal CRT's own frame so the sign
    /// reads un-mirrored from the −Z viewing side (see <see cref="TerminalNameplateCentre"/>).</summary>
    public Vector3 TerminalNameplateRight() => -Vector3.UnitX;

    /// <summary>The nameplate face's world +Y axis (see <see cref="TerminalNameplateCentre"/>).</summary>
    public Vector3 TerminalNameplateUp() => Vector3.UnitY;

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

        var (indexA, indexB) = (probeWorstCase ? (0, Math.Min(val1: 1, val2: (m_room.Consoles.Count - 1))) : m_currentLinkedPair!.Value);

        if ((indexA < 0) || (indexB < 0) || (indexA >= m_room.Consoles.Count) || (indexB >= m_room.Consoles.Count)) {
            return; // fewer than two consoles exist (a bare-room probe) — nothing to swag a cable between.
        }

        // The presentation-only TRANSFER GLOW: the render node reports a 0..1 intensity from the linked ports' live
        // serial-transfer state (decayed), so the cable lights data-cyan while the pair exchanges bytes and settles to
        // its plain dark rubber when idle. A brighter emissive bead pools at the sag's low point — a cheap read of
        // "energy on the wire" without a traveling-pulse clock. The worst-case probe emits the plain cable (glow 0), so
        // the emissive lift never grows the program's word/instance envelope.
        var glow = (probeWorstCase ? 0f : Math.Clamp(value: (LinkCableGlowSource?.Invoke() ?? 0f), min: 0f, max: 1f));
        var cableAlbedo = Vector3.Lerp(value1: new Vector3(x: 0.05f, y: 0.05f, z: 0.06f), value2: new Vector3(x: 0.15f, y: 0.85f, z: 1f), amount: glow);
        var cableMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: cableAlbedo, Emissive: (glow * 1.6f)));
        var beadMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: cableAlbedo, Emissive: (glow * 2.6f)));

        // World-space (== render-relative here; the render anchor IS the spawn anchor, so origin is always zero —
        // see BuildProgram's own comment) screen-top positions, exactly matching ScreenCenterLocal's placement.
        var topA = ScreenCenterLocal(consoleIndex: indexA);
        var topB = ScreenCenterLocal(consoleIndex: indexB);
        var sag = (Vector3.Lerp(value1: topA, value2: topB, amount: 0.5f) - new Vector3(x: 0f, y: CableSagDrop, z: 0f));
        var boundCenter = Vector3.Lerp(value1: topA, value2: topB, amount: 0.5f);
        var boundRadius = ((((Vector3.Distance(value1: topA, value2: topB) * 0.5f) + CableSagDrop) + CableSagRadius) + 0.2f);

        _ = builder.BeginInstance(boundCenter: boundCenter, boundRadius: boundRadius);
        _ = builder.ResetPoint().Translate(offset: (topA - origin)).Capsule(endpoint: (sag - topA), radius: CableRadius, material: cableMaterial, blend: SdfBlendOp.Union);
        _ = builder.ResetPoint().Translate(offset: (topB - origin)).Capsule(endpoint: (sag - topB), radius: CableRadius, material: cableMaterial, blend: SdfBlendOp.SmoothUnion, smooth: CableSmooth);
        _ = builder.ResetPoint().Translate(offset: (sag - origin)).Sphere(radius: CableSagRadius, material: beadMaterial, blend: SdfBlendOp.SmoothUnion, smooth: CableSmooth);
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
