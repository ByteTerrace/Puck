using System.Numerics;
using Puck.Assets;
using Puck.Compositing;
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
public sealed class OverworldFrameSource : ISdfFrameSource {
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
    private readonly ScreenDirector m_director;
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
    // The scene's ProgramRevision the current program was built from; an authoring edit that changes the program's
    // content moves it, and CaptureFrame rebuilds (a creator MOVE never does — it rides the dynamic transforms).
    private int m_builtProgramRevision = -1;
    private float m_time;
    // CREATOR-MODE presentation state (never hashed — the deterministic sim knows nothing of it): the authored scene
    // model, its program/transform emitter, and the pad state machine that edits it. This source is the composition
    // point for the creator objects — the render node drives them through thin forwarders so its own type coupling
    // stays flat while the editor grows.
    private readonly CreatorScene m_creator;
    private readonly CreatorSceneRenderer m_creatorRenderer;
    private readonly CreatorController m_creatorController;
    // The live bake-preview seam the easel's screen slab samples — the null stand-in until the bake pipeline's
    // service replaces it (ConnectBakePreview).
    private ICreatorBakePreview m_bakePreview = new NullCreatorBakePreview();
    // The owned live bake service, when installed (InstallBakePreview) — this source composes it so the render
    // node's coupling stays flat.
    private Forge.Bake.BakePreviewService? m_bakePreviewService;
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
    /// sagging cable emits only when BOTH resolve to distinct, valid console indices this frame.</summary>
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
    public OverworldFrameSource(OverworldWorld world, OverworldRoom room, ScreenDirector director, IReadOnlyList<Vector3>? consoleAccents = null, Func<int, JoypadButtons>? controlsSource = null) {
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
        m_director.CreatorCameraSource = () => (m_creatorController.CameraFrame ?? WorldSculptCameraFrame());

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

        // PUCK_COMPANION_LOAD: comma-separated creation names/hashes spawn as companions at boot (the headless
        // capture hook, mirroring PUCK_CREATOR_LOAD).
        if (Environment.GetEnvironmentVariable(variable: "PUCK_COMPANION_LOAD") is { Length: > 0 } companionNames) {
            foreach (var companionName in companionNames.Split(separator: ',', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))) {
                if (CompanionState.ResolveDocument(nameOrHash: companionName, store: m_worldStore) is { } companionDocument) {
                    _ = m_companions.Add(companion: new CompanionState(bounds: CompanionBounds, document: companionDocument, spawnPosition: CompanionSpawnPosition(rosterIndex: m_companions.Companions.Count)));
                    Console.Error.WriteLine(value: $"[companion: '{companionName}' joined the room]");
                }
                else {
                    Console.Error.WriteLine(value: $"[companion: '{companionName}' did not resolve — creator.save it first]");
                }
            }
        }

        // PUCK_WORLD_ROUNDTRIP=1: the hands-off bit-for-bit proof, observable in an --exit-after-seconds run — save
        // the (possibly empty) live world through the full bake, reload the bytes from disk, and byte-compare. Never
        // a gate; a boot-time stderr verdict for eyeballs and captures.
        if (Environment.GetEnvironmentVariable(variable: "PUCK_WORLD_ROUNDTRIP") == "1") {
            var (savedPath, savedHash) = m_worldScene.Save(store: m_worldStore);
            var reloaded = WorldStore.Load(nameOrPath: savedPath);
            var committedJson = ((m_worldScene.CommittedDocument is { } committedDocument) ? WorldStore.ToJson(document: committedDocument) : "");
            var reloadedJson = ((reloaded is { } reloadedDocument) ? WorldStore.ToJson(document: reloadedDocument) : null);

            Console.Error.WriteLine(value: (string.Equals(a: committedJson, b: reloadedJson, comparisonType: StringComparison.Ordinal)
                ? $"[world-roundtrip] MATCH — save→reload byte-identical ({savedHash ?? savedPath})"
                : $"[world-roundtrip] MISMATCH — the reloaded bytes differ from the committed save ({savedPath}); the bit-for-bit doctrine is broken"));
        }
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
        m_companions.Tick(deltaSeconds: deltaSeconds, nearestPlayerProvider: NearestActivePlayer);

        // The screen mux: resolves the ledger (cabinets/easel/any registered dynamic claimants) — BEFORE the program
        // rebuild check, since a claimed headroom slot or the link cable's pair can change what BuildProgram emits.
        var linkedA = (LinkedConsoleASource?.Invoke() ?? -1);
        var linkedB = (LinkedConsoleBSource?.Invoke() ?? -1);
        var linkedPair = (((linkedA >= 0) && (linkedB >= 0)) ? (linkedA, linkedB) : ((int A, int B)?)null);

        ResolveScreenMux(linkedPair: linkedPair);

        var bootedMask = m_world.BootedMask;
        var programChanged = ((m_program is null) || (bootedMask != m_programBootedMask) || (m_creator.ProgramRevision != m_builtProgramRevision) || (m_worldScene.ProgramRevision != m_builtWorldRevision) || (m_companions.Companions.Count != m_builtCompanionCount) || !EqualLinkedPair(a: linkedPair, b: m_builtLinkedPair));

        if (programChanged) {
            m_program = BuildProgram(bootedMask: bootedMask);
            m_programBootedMask = bootedMask;
            m_builtProgramRevision = m_creator.ProgramRevision;
            m_builtWorldRevision = m_worldScene.ProgramRevision;
            m_builtCompanionCount = m_companions.Companions.Count;
            m_builtLinkedPair = linkedPair;
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

        // Drive each pane camera from its cabinet's diegetic-screen center + the overworld's per-console closeness. The
        // screen center is a fixed room-local position; render-relative equals local here (the render origin is the
        // spawn anchor at local zero), so it lives in the same space as the active player positions the camera frames.
        m_director.PaneCameraSource = paneIndex => ((paneIndex >= 0) && (paneIndex < m_room.Consoles.Count)
            ? (ScreenCenterLocal(consoleIndex: paneIndex), (PaneCloseness?.Invoke(paneIndex) ?? 0f), ScreenHalfHeightLocal(consoleIndex: paneIndex))
            : ((Vector3, float, float)?)null);

        var views = m_director.Compose(activePositions: m_activePositions, bootOrder: m_world.BootOrder, imageWidth: width, imageHeight: height, deltaSeconds: deltaSeconds);

        // View 0 is always the room; stash its live rect for the binding-bar overlay (this runs INSIDE the producer's
        // ProduceFrame, so the overlay — which wraps the producer — always reads the region of the frame it draws over).
        LastRoomRegion = views[0].Region;

        PackDynamicTransforms(renderOrigin: renderOrigin, alpha: interpolationAlpha);

        // While immersed the room is UNLIT (RoomLightFactor 0), so the letterbox margins around a contained screen
        // render BLACK — a native handheld/emulator look — easing up to the arcade mood as the reveal lights the room.
        var roomLight = m_director.RoomLightFactor;

        return new SdfFrame(
            Program: m_program!, // non-null: programChanged is true whenever m_program was null, so it was just built
            ProgramChanged: programChanged,
            Views: views,
            Time: m_time,
            WarpAmount: 0f
        ) {
            // The sculpted world's DAYLIGHT dial rides the same presentation seam the reveal's room-light does —
            // at dusk the authored lamps' emissive materials carry the room (world.dusk).
            AmbientScale = (OverworldAmbientScale * roomLight * m_worldScene.Daylight),
            DynamicTransforms = m_dynamicTransforms,
            SunScale = (OverworldSunScale * roomLight * m_worldScene.Daylight),
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
        // the room's actual console count so a stale/out-of-range PUCK_LINK_CABLE_PROBE pair can never index past
        // m_room.Consoles.
        m_currentLinkedPair = ((linkedPair is { A: >= 0, B: >= 0 } pair) && (pair.A < m_room.Consoles.Count) && (pair.B < m_room.Consoles.Count) && (pair.A != pair.B))
            ? linkedPair
            : null;

        var borrowed = m_creator.Active;

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

    /// <summary>Registers (or updates) a dynamic screen claimant for every future <see cref="CaptureFrame"/> pass —
    /// the generic extension seam a future diegetic-camera/screen source wires through, without touching
    /// <see cref="ScreenSlotLedger"/> or this source's own mux logic. Mirrors <see cref="ScreenSlotLedger.Claim"/>'s
    /// per-pass re-claim contract, except registration here persists across passes (the caller only needs to call
    /// this once per activation, not every frame) until <see cref="UnregisterScreenClaimant"/> withdraws it.</summary>
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

    /// <summary>Lifts the currently PLACED shapes into a self-contained, recentered <see cref="Forge.AvatarDefinition"/>
    /// — the seam the forge consumes to bake a spritesheet and a playable ROM from the player's creation.</summary>
    /// <returns>The player's avatar in its own local frame.</returns>
    public Forge.AvatarDefinition ExportAvatar() =>
        m_creator.ExportAvatar();

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
    private WorldDocument BakeWorldForSave(WorldDocument document) {
        var bounds = (document.Bounds ?? new WorldBoundsDocument(FloorY: m_baseRoom.FloorY, MaxX: m_baseRoom.BoundsMax.X, MaxZ: m_baseRoom.BoundsMax.Y, MinX: m_baseRoom.BoundsMin.X, MinZ: m_baseRoom.BoundsMin.Y));
        var footprints = new List<WorldFootprint>();

        foreach (var placement in (document.Placements ?? [])) {
            if ((placement.Source is { Length: > 0 } source) && m_worldStore.TryGet(content: out var bytes, hash: source) && (CreationDocumentBytes.Deserialize(bytes: bytes) is { } creation)) {
                footprints.AddRange(collection: WorldFootprintDerivation.ForPlacement(creation: creation, placement: placement));
            }
        }
        foreach (var patch in (document.Terrain ?? [])) {
            footprints.Add(item: WorldFootprintDerivation.ForTerrainPatch(patch: patch));
        }
        // The room's own stands block exactly as the sim's FixedConsole boxes do (full walk-band height). Room
        // planar coordinates are Vector2 XZ (X = world X, Y = world Z — the room's own convention).
        foreach (var stand in m_room.Consoles) {
            footprints.Add(item: new WorldFootprint(
                MaxX: (stand.Center.X + stand.HalfExtents.X),
                MaxY: (m_room.FloorY + (2f * m_room.PlayerHalfExtents.Y)),
                MaxZ: (stand.Center.Y + stand.HalfExtents.Y),
                MinX: (stand.Center.X - stand.HalfExtents.X),
                MinY: m_room.FloorY,
                MinZ: (stand.Center.Y - stand.HalfExtents.Y)
            ));
        }

        var overrides = (document.WalkOverrides ?? []).Select(selector: static entry => WalkOverrideInput.FromDocument(document: entry));
        var kind = (string.Equals(a: m_worldScene.WalkGridKind, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase) ? WalkGridKind.Hex : WalkGridKind.Square);
        var grid = WalkGridBaker.Bake(
            bounds: bounds,
            footprints: footprints,
            kind: kind,
            overrides: overrides,
            playerHalfExtentX: m_room.PlayerHalfExtents.X,
            playerHalfExtentZ: m_room.PlayerHalfExtents.Z,
            walkBandFloorY: bounds.FloorY,
            walkBandHeight: (2f * m_room.PlayerHalfExtents.Y)
        );

        return (document with { WalkGrid = grid });
    }

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
    public nint CreatorPreviewHandle => (m_creator.Active ? m_bakePreview.CurrentImageViewHandle : 0);

    /// <summary>The bake preview's screen-light color (the workbench glows with the creation; zero when dark).</summary>
    public Vector3 CreatorPreviewLight => (m_creator.Active ? m_bakePreview.PreviewAverageColor : Vector3.Zero);

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
        m_bakePreview = new NullCreatorBakePreview();
    }

    /// <summary>Loads a saved creation into the scene by save handle or file path (the <c>PUCK_CREATOR_LOAD</c>
    /// headless hook rides this; the console's <c>creator.load</c> uses the store directly).</summary>
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

        return (probe.Words.Length, probe.Instances.Count);
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

    private SdfProgram BuildProgram(uint bootedMask, bool probeWorstCase = false) {
        // The render anchor IS the spawn anchor, so the room is authored directly in the spawn cell's local frame
        // (origin delta identically zero); the per-slot player boxes ride the dynamic-transform buffer, which the
        // frame source already feeds render-relative to the same anchor.
        var origin = Vector3.Zero;
        var builder = new SdfProgramBuilder();
        var floorMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.34f, 0.36f, 0.42f)));
        var wallMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.50f, 0.46f, 0.58f)));
        var playerMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.93f, 0.52f, 0.18f)));
        var screenOffMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.08f, 0.09f, 0.10f)));
        var shelfMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.40f, 0.38f, 0.35f)));
        var controlMaterial = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(0.15f, 0.15f, 0.17f)));

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
        // slots hidden below the floor). Built once — never rebuilt as players join or leave. One dynamic instance
        // per slot: the bound tracks the slot's own position (boundOffset zero — the box is centered on the slot),
        // so a free/hidden slot's instance simply culls to nothing productive rather than needing special-casing.
        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: PlayerInstanceRadius);
            _ = builder.ResetPoint().TransformDynamic(slot: slot).Box(halfExtents: m_room.PlayerHalfExtents, round: 0.06f, material: playerMaterial);
            _ = builder.EndInstance();
        }

        // The CREATOR pool: the scene's palette + ghost + one instance per placed-shape slot, emitted by the
        // renderer (Puck.Demo.Creator.CreatorSceneRenderer). Slot/material counts are constant across rebuilds; the
        // per-slot instruction count may vary below the probed worst case (MeasureWorstCaseEnvelope), which the
        // engine's buffers were sized against.
        m_creatorRenderer.EmitPool(builder: builder, probeWorstCase: probeWorstCase);

        // The WORLD SCULPTOR's authored content: terrain, lights, override ghosts, every stamped placement (each a
        // static instance), and its two dynamic slots — emitted by Puck.Demo.World.WorldSceneRenderer under the same
        // worst-case-probe discipline as the creator pool (the synthetic probe covers the densest legal scene).
        m_worldRenderer.EmitWorld(builder: builder, probeWorstCase: probeWorstCase);

        // The COMPANION pool: presentation-only sculpted creatures (roots + shape slots reserved for the full
        // roster regardless of how many are loaded — constant slot topology, same probe discipline).
        m_companionRenderer.EmitCompanions(builder: builder, probeWorstCase: probeWorstCase);

        // THE DIEGETIC LINK CABLE (whimsy): a static instance emitted ONLY while two cabinets are linked (or under
        // the worst-case probe, which always emits it — see EmitLinkCable's remarks).
        EmitLinkCable(builder: builder, origin: origin, probeWorstCase: probeWorstCase);

        return builder.Build();
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
    private static void AddWall(SdfProgramBuilder builder, Vector3 center, Vector3 halfExtents, int material) {
        _ = builder.ResetPoint().Translate(offset: center).Box(halfExtents: halfExtents, round: 0f, material: material);
    }
}
