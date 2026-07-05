using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Commands;
using Puck.Demo.BindingBar;
using Puck.Demo.DevConsole;
using Puck.Hosting;
using Puck.HumbleGamingBrick;
using Puck.Input;
using Puck.Input.Devices;
using Puck.Maths;
using Puck.Scene;
using Puck.SdfVm;
using Puck.Shaders;

namespace Puck.Demo.Overworld;

/// <summary>
/// The live, windowed root node for the overworld — the demo's opening experience. It owns the deterministic simulation
/// and, each frame, advances it by the whole fixed ticks elapsed, then renders through a <see cref="SdfEngineNode"/>
/// whose frame source reflects the players via the per-frame dynamic-transform buffer and the screen director's view
/// list. With CONSOLES declared (the document's <see cref="OverworldNode.Consoles"/>), the room seats one stand per
/// console, each hosting a dark <see cref="GamingBrickChildNode"/> pane: the player walks up and presses interact
/// (North) to boot it, the machine powers on, and the screen layout walks its staged transition (fullscreen →
/// side-by-side → big-top/two-bottom → 2×2 quad). The room player's movement mirrors into every UNOWNED booted
/// brick — the carry-forward thesis on one screen. Console mode is MULTIPLAYER: connected pads beyond the first
/// join as additional world players (pad index = player slot, up to <see cref="OverworldWorld.MaxPlayers"/>), each
/// with its own binding pages and its own bar in the overlay; a pad-count drop evicts its slots with the bare
/// room's leaver hygiene. Any player's interact at a machine performs the proximity TAKEOVER: an owned brick
/// leaves the shared timeline and is driven by its owner's pad alone (host-side input routing, NEVER sim state —
/// the deterministic hash is untouched), and a second interact releases it back to the timeline at the head.
/// Brick input otherwise rides ONE shared <see cref="OverworldBrickTimeline"/> recorded from
/// the first boot: a late-booted machine replays the stream from that epoch (fast-forwarding a few segments per
/// frame) until it converges, so same-costume machines end bit-identical no matter when their stands booted. Input in console mode flows through the shared
/// <see cref="GamingBrickPadService"/> (the run's sole gamepad drainer); with NO consoles the node is the bare
/// multi-controller room (join/leave by connecting controllers) on the deterministic router path. Render assembly
/// flows through the shared <see cref="SdfWorldRenderBuilder"/> (the overworld document still resolves to a Vulkan
/// host; the binding-bar decorator is Vulkan-only). An IMMERSED start (the document's <see cref="OverworldNode.Immersed"/>,
/// the <c>--rom</c> path) inverts the opening: each player spawns already standing at their own stand, is seated
/// host-side (booted + taken over) the frame their slot activates, and the screen director opens in its
/// <see cref="ScreenDirectorMode.Immersed"/> tiling — only the game panes, no room. The first machine whose
/// fourth-wall exit condition fires breaks the wall: the director eases to <see cref="ScreenDirectorMode.Revealed"/>
/// (the room fullscreen, the games playing on diegetically on the stands' screens) and the run continues — the
/// players keep their machines until they release with interact, exactly like any takeover.
/// Headless hooks: <c>PUCK_OVERWORLD_DEBUG_PLAYERS</c> drives N scripted players apart;
/// <c>PUCK_OVERWORLD_DEBUG_BOOT</c> (comma-separated sim ticks) boots console 0,1,2… at those ticks so every layout stage
/// can be captured without hardware.
/// </summary>
internal sealed class OverworldRenderNode : IRenderNode, IDebugViewTarget, ICreatorModeHost {
    private const float MirrorThreshold = 0.5f;

    private readonly IServiceProvider m_serviceProvider;
    private readonly IReadOnlyList<GamingBrickSource> m_consoles;
    private readonly IReadOnlyList<CartridgeSource> m_library;
    private readonly uint m_width;
    private readonly uint m_height;
    private readonly string? m_capturePath;
    // PUCK_OVERWORLD_CAPTURE_FRAME=N grabs the frame after N produced frames (the machines have booted + drawn by then),
    // not frame 0 — the plain --capture path grabs black because the emulators have not rendered yet. Inert (-1) unless
    // the env is set. A headless verification aid, like PUCK_OVERWORLD_DEBUG_BOOT.
    private readonly int m_captureFrame = DebugCaptureFrame();
    private int m_producedFrames;
    private bool m_captureFired;
    private readonly bool m_hostsOnDirectX;
    private readonly bool m_immersed;
    // The WORLD-LENS immersed mode (the reshaped default "game"): every console runs the built-in world-lens cart
    // (peripheral "world") and players WALK the room (never seated/owned) while each pane mirrors its own player;
    // reaching the goal breaks the fourth wall. Distinct from the --rom immersed boot, where players are seated and
    // drive a real game with buttons. Derived from the consoles, so no new document field is needed.
    private readonly bool m_worldLens;
    private readonly NodeDescriptor m_descriptor = new(
        Name: "overworld",
        SurfaceId: SurfaceId.New()
    );
    private OverworldWorld? m_world;
    private OverworldFrameSource? m_frameSource;
    private BindingBarAdapter? m_bindingBarAdapter;
    private ScreenDirector? m_director;
    private IPlayerIntentSource? m_intentSource;
    private PagedInputBindings? m_pagedBindings;
    private IRenderNode? m_root;
    private IRosterEventSource? m_rosterSource;
    private RouterIntentSource? m_routerSource;
    private SdfEngineNode? m_producer;
    private GamingBrickPadService? m_padService;
    private OverworldBrickTimeline? m_timeline;
    private GamingBrickChildNode[] m_bricks = [];
    private int[] m_choirLeaders = [];
    private ChoirState[] m_choirStates = [];
    // The cooperative META win watch (cabinets whose top-16 SRAM regions XOR to a shared target), built from the console
    // list at build. Null when no console declares a meta victory. Polled each frame while immersed.
    private MetaVictoryWatch? m_metaVictoryWatch;
    // The unified cartridge ROM table (see OverworldWorld's constructor doc for the index space): pre-inserted consoles'
    // ROMs first (in console order, only for consoles that HAVE one), then the library's ROMs (in library order).
    // The ROM bytes for each cart TYPE (custom / camera / showcase), indexed by OverworldWorld cart type; a null slot is a
    // cart that could not be sourced on this machine (e.g. no showcase ROM). Loaded eagerly, all pure-CPU.
    private byte[]?[] m_cartTypeRoms = [];
    // The battery-save path per cart type (only the showcase carries one), and the cart type each brick currently has
    // ASSEMBLED (-1 = empty), so the per-frame reconcile can tell insert / eject / swap apart.
    private string?[] m_cartTypeSaves = [];
    private int[] m_consoleAssembledType = [];
    // Console mode's per-player page adapters, indexed by player slot (created lazily as a slot becomes active;
    // slot 0 from frame 0). Empty outside the binding-page path.
    private OverworldPageInput?[] m_pageInputs = [];
    // The proximity-takeover ownership maps — HOST-SIDE input routing only, never hashed sim state: per console its
    // owning player slot (-1 = unowned, on the shared timeline), and per player slot the console it owns (-1 = none).
    private int[] m_consoleOwner = [];
    private int[] m_slotConsole = [];
    // The fourth-wall reveal handshake, IMMERSED mode only — host-side presentation state, never sim state: a brick's
    // exit condition fires DURING m_root.ProduceFrame (the request), and the NEXT frame's ProduceFrame applies it
    // exactly once (the director eases to the revealed layout; the run continues).
    private bool m_revealRequested;
    private bool m_revealed;
    // Which console's fourth-wall condition fired (the reveal zooms OUT of that machine's screen); first to fire wins.
    private int m_revealTriggerConsole = -1;
    private int m_debugMode;
    // The immersed seating ledger, per player slot: seated exactly once per slot ACTIVATION (cleared on eviction so a
    // rejoining pad re-seats at its stand).
    private readonly bool[] m_immersedSeated = new bool[OverworldWorld.MaxPlayers];
    private GamepadManager? m_gamepadManager;
    private int m_debugCaptureCounter;
    private bool[] m_jumpHeldLastBySlot = new bool[OverworldWorld.MaxPlayers];
    private bool[] m_cycleHeldLastBySlot = new bool[OverworldWorld.MaxPlayers];
    private bool m_interactHeldLast;
    // Creator-mode button edge tracking for the creating slot (slot 0) — creator input bypasses the page adapter's
    // command edges (it repurposes the same physical buttons), so it tracks its own previous button mask.
    private GamepadButtons m_creatorPrevButtons;
    private ulong[] m_debugBootTicks = [];
    private int m_debugBootsApplied;

    // The per-console choir lifecycle: Unparked machines step; a Parked follower mirrors its leader (one stepped
    // machine per converged identical-config group); Refused marks a failed park-time state compare — a contract
    // violation that must stay loud, never retried into silence.
    private enum ChoirState : byte {
        Unparked,
        Parked,
        Refused,
    }

    /// <summary>Initializes a new instance of the <see cref="OverworldRenderNode"/> class.</summary>
    /// <param name="serviceProvider">The application services (GPU compute, gamepads, the shared pad-routing service).</param>
    /// <param name="consoles">The document's console list (empty for the bare room).</param>
    /// <param name="library">The document's cartridge shelf (empty for no shelf — every console must then be pre-inserted).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path for a one-shot capture.</param>
    /// <param name="hostsOnDirectX">Whether the resolved host backend is Direct3D 12 (drives kernel bytecode and
    /// child-node backend selection through the shared render builder).</param>
    /// <param name="immersed">Whether the run opens IMMERSED (the document's <see cref="OverworldNode.Immersed"/>, the
    /// fourth-wall boot): players spawn seated at their own stands, the director opens on the game-pane tiling, and a
    /// machine's exit condition breaks the wall (reveals the room) instead of shutting the host down.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public OverworldRenderNode(IServiceProvider serviceProvider, IReadOnlyList<GamingBrickSource> consoles, IReadOnlyList<CartridgeSource> library, uint width, uint height, string? capturePath, bool hostsOnDirectX = false, bool immersed = false) {
        ArgumentNullException.ThrowIfNull(consoles);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        m_consoles = consoles;
        m_library = library;
        m_serviceProvider = serviceProvider;
        m_width = width;
        m_height = height;
        m_capturePath = capturePath;
        m_hostsOnDirectX = hostsOnDirectX;
        m_immersed = immersed;
        m_worldLens = (immersed && (consoles.Count > 0) && consoles.All(predicate: static c => string.Equals(a: c.Peripheral, b: "world", comparisonType: StringComparison.OrdinalIgnoreCase)));
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public int DebugMode {
        get => m_debugMode;
        set {
            m_debugMode = value;

            if (m_producer is not null) {
                m_producer.DebugMode = value;
            }
        }
    }

    /// <inheritdoc/>
    public bool CreatorModeActive => (m_frameSource?.CreatorActive ?? false);

    /// <inheritdoc/>
    public bool ToggleCreatorMode() {
        if (m_frameSource is null) {
            return false;
        }

        SetCreatorMode(active: !m_frameSource.CreatorActive);

        return m_frameSource.CreatorActive;
    }

    // Enters/leaves creator mode on the frame source and narrates it (the console command and the in-mode EXIT button
    // both route through here).
    private void SetCreatorMode(bool active) {
        if ((m_frameSource is not { } frameSource) || (frameSource.CreatorActive == active)) {
            return;
        }

        frameSource.SetCreatorActive(active: active);
        m_creatorPrevButtons = GamepadButtons.None;
        Console.Error.WriteLine(value: (active
            ? $"[creator] ENTER — left stick moves the shape, triggers raise/lower, bumpers cycle the primitive ({frameSource.CreatorShapeName}), South places, East undoes, North exits."
            : $"[creator] EXIT — {frameSource.CreatorPlacedCount} shape(s) placed."));
    }

    // Creator mode owns the creating slot's controller for the frame: the left stick slides the ghost across the
    // floor, the triggers raise/lower it, the bumpers cycle the primitive, South places, East undoes the last placed
    // shape, and North exits the mode. Presentation only — nothing here reaches the deterministic world.
    private void AdvanceCreatorInput(OverworldFrameSource creator, in GamepadState raw, float deltaSeconds) {
        var buttons = raw.Buttons;

        bool Pressed(GamepadButtons button) => ((0 != (buttons & button)) && (0 == (m_creatorPrevButtons & button)));

        // Stick-up (forward) maps to world -Z, matching the chase camera and the room mirror; the triggers nudge Y.
        creator.CreatorMove(
            deltaSeconds: deltaSeconds,
            planar: new Vector2(x: raw.LeftStick.X, y: -raw.LeftStick.Y),
            vertical: (raw.RightTrigger - raw.LeftTrigger)
        );

        if (Pressed(button: GamepadButtons.RightShoulder)) { creator.CreatorCycleShape(direction: 1); }
        if (Pressed(button: GamepadButtons.LeftShoulder)) { creator.CreatorCycleShape(direction: -1); }
        if (Pressed(button: GamepadButtons.ButtonSouth)) { creator.CreatorPlace(); }
        if (Pressed(button: GamepadButtons.ButtonEast)) { creator.CreatorUndo(); }
        if (Pressed(button: GamepadButtons.ButtonNorth)) { SetCreatorMode(active: false); }

        m_creatorPrevButtons = buttons;
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out _)) {
            return default;
        }

        EnsureResources(context: in context);

        // Delayed headless capture: once the machines have booted and drawn (PUCK_OVERWORLD_CAPTURE_FRAME frames in),
        // request the one-shot readback — the plain --capture grabs frame 0 (black, pre-boot).
        if ((m_captureFrame >= 0) && !m_captureFired && (m_capturePath is { } capturePath) && (++m_producedFrames >= m_captureFrame)) {
            m_captureFired = true;
            m_producer?.RequestCapture(path: capturePath);
        }

        // THE fourth-wall moment (immersed only): a machine's exit condition fired during a previous frame's produce.
        // Applied exactly once — the director eases to the revealed layout (the panes collapse in place while the room
        // grows in; the games play on diegetically on the stands' screens) and the run CONTINUES: every player stays
        // the owner of their machine until they release it with interact, exactly like any takeover.
        if (m_revealRequested && !m_revealed) {
            m_revealed = true;
            m_director!.SetMode(mode: ScreenDirectorMode.Revealed);
            // Zoom OUT of the triggering machine's screen (the game the player was inside) toward the iso overview.
            m_director.BeginReveal(triggerPaneIndex: Math.Max(val1: 0, val2: m_revealTriggerConsole));
            // The reveal does NOT free anyone: players stay LOCKED to their machines (engaged since they seated in) —
            // now the room is visible and they can CHOOSE to disengage (Left bumper) into free roam.
            Console.Error.WriteLine(value: "[fourth-wall] the wall breaks — welcome to the overworld. Players stay at their machines until they disengage.");
        }

        AdvanceSimulation(context: in context);

        // The machines are decoupled from the compositor slots, so THIS node steps + uploads them each frame (before
        // the SDF render reads their framebuffers for the diegetic screens). One serial pass — four machines is well
        // inside the frame; the choir park/mirror still amortizes identical ones. Their resampled output is unused now
        // (the diegetic screens sample the raw framebuffer via NativeImageViewHandle), so they produce at native size.
        ProduceMachines(context: in context);

        // The room-level meta (cooperative XOR) win, polled after the machines stepped this frame. Immersed only — the
        // reveal is the fourth-wall break, exactly like the exit/solo triggers that also only fire in immersed mode.
        if (m_immersed) {
            EvaluateMetaVictory();
        }

        return m_root!.ProduceFrame(context: in context);
    }

    // Steps + uploads each booted machine so its framebuffer is fresh for the diegetic screens this frame. Produced at
    // the native brick size (the diegetic screen samples the raw framebuffer, not a pane resample). Unbooted /
    // unassigned machines still produce (a dark frame) harmlessly.
    private void ProduceMachines(in FrameContext context) {
        if (m_bricks.Length == 0) {
            return;
        }

        var machineContext = (context with {
            TargetHeight = Framebuffer.ScreenHeight,
            TargetWidth = Framebuffer.ScreenWidth,
        });

        foreach (var brick in m_bricks) {
            _ = brick.ProduceFrame(context: in machineContext);
        }
    }

    private void EnsureResources(in FrameContext context) {
        if (m_world is not null) {
            return;
        }

        var tickSeconds = (float)EngineTicks.ToSeconds(ticks: context.StepTicks);
        var room = OverworldRoom.WithConsolesAndShelf(consoleCount: m_consoles.Count, shelfCount: m_library.Count);
        // PUCK_OVERWORLD_CELL places the whole room at a far world cell (applied to X and Z) to demonstrate the
        // planet-scale coordinate path: the sim is cell-agnostic (identical local motion) and the floating-origin render
        // seam keeps it crisp. Default 0 = the origin cell.
        var farCell = DebugSpawnCell();

        // The three cartridge TYPES a cabinet can hold, in cycle order (all sourced pure-CPU, eagerly): 0 = WORLD-LENS
        // (a real ROM that reads a work-RAM sensor page and mirrors the room it sits in — the world→machine membrane,
        // fed the room player each frame; see OverworldWorldLens), 1 = CAMERA (the Pocket Camera viewfinder, driven by the
        // PC webcam), 2 = the SHOWCASE ROM (the pre-inserted cartridge). A missing showcase leaves that
        // slot null (the cabinet refuses to load it).
        var showcaseRom = m_consoles.Select(selector: static c => c.RomPath).FirstOrDefault(predicate: static p => p is not null);

        m_cartTypeRoms = new byte[]?[OverworldWorld.CartTypeCount];
        m_cartTypeSaves = new string?[OverworldWorld.CartTypeCount];
        m_cartTypeRoms[0] = BuildWorldLensCartRom(context: in context);
        m_cartTypeRoms[1] = Puck.Demo.Forge.CameraRom.Build(title: "PUCKCAM");

        if (showcaseRom is not null) {
            m_cartTypeRoms[2] = File.ReadAllBytes(path: showcaseRom);
            m_cartTypeSaves[2] = $"{showcaseRom}.sav";
        }

        m_consoleAssembledType = new int[m_consoles.Count];
        Array.Fill(array: m_consoleAssembledType, value: -1);

        // The overworld opens with EMPTY cabinets (insert a cart to bring one alive); an immersed boot starts every cabinet
        // loaded — the WORLD-LENS default with cart type 0 (each player opens inside their own lens on the room), the
        // --rom boot with the showcase cart (each player opens inside the running game). spawnAtConsoles seats immersed
        // players at their own stand.
        // The --rom immersed boot starts every cabinet LOADED (players open inside the running game); the world-lens
        // default starts EMPTY and boots cabinet i when player i joins (seating-boot), so the number of lens panes
        // tracks the number of players — each player boots their OWN instance. startCartType 0 makes the seating-boot
        // insert the world-lens cart.
        m_world = new OverworldWorld(room: room, tuning: PlatformerTuning.Default, tickSeconds: tickSeconds, seed: 1u, spawnAtConsoles: m_immersed, spawnCellX: farCell, spawnCellZ: farCell, startLoaded: (m_immersed && !m_worldLens), startCartType: (m_worldLens ? 0 : -1));
        m_debugBootTicks = DebugBootTicks();

        var debugPlayers = DebugPlayerCount();

        if (m_consoles.Count > 0) {
            // Console mode: ONE permanent room player from the first frame (the room is never empty), driven through
            // the shared pad-routing service — the run's sole gamepad drainer, shared with the brick panes it also
            // feeds. Additional connected pads join as players 1..N each frame (UpdateConsoleRoster).
            _ = m_world.AddPlayer(playerId: RoomPlayerId());
            m_gamepadManager = (m_serviceProvider.GetService(serviceType: typeof(GamepadManager)) as GamepadManager);
            m_padService = (m_serviceProvider.GetService(serviceType: typeof(GamingBrickPadService)) as GamingBrickPadService);
            m_timeline = new OverworldBrickTimeline(consoleCount: m_consoles.Count);
            // The takeover maps start all-unowned: every brick rides the shared timeline until a player claims it.
            m_consoleOwner = new int[m_consoles.Count];
            m_slotConsole = new int[OverworldWorld.MaxPlayers];
            Array.Fill(array: m_consoleOwner, value: -1);
            Array.Fill(array: m_slotConsole, value: -1);
            // The binding-page system rides console mode too: the pad service stays the sole drainer and one page
            // adapter PER PLAYER SLOT replays that player's state into the paged resolver (jump/interact/context +
            // the debug pages). Slot 0 exists from frame 0; later slots are created as their pads join.
            m_pagedBindings = (m_serviceProvider.GetService(serviceType: typeof(PagedInputBindings)) as PagedInputBindings);

            if (m_pagedBindings is not null) {
                m_pageInputs = new OverworldPageInput?[OverworldWorld.MaxPlayers];
                m_pageInputs[0] = new OverworldPageInput(bindings: m_pagedBindings, slot: 0);
            }
        } else if (debugPlayers > 0) {
            // Headless verification: spawn N scripted players that walk to the corners — captured without any
            // controller. Uses the real roster/world path, not a bypass.
            for (var index = 0; (index < debugPlayers); index++) {
                _ = m_world.AddPlayer(playerId: DeterministicGuid(salt: (uint)index));
            }

            m_intentSource = new ScriptedIntentSource(script: DebugScript);
            m_rosterSource = new ScriptedRosterEventSource(schedule: []);
        } else if (m_serviceProvider.GetService(serviceType: typeof(GamepadManager)) is GamepadManager manager) {
            // Live bare-room: controllers join/leave at runtime; each binds to a player and drives it per-device. Input
            // flows through the engine's deterministic router (RouterIntentSource) when the capture clock is
            // available; LocalIntentSource (the direct manager drain) remains the fallback.
            var registry = new ControllerPlayerRegistry();

            if (m_serviceProvider.GetService(serviceType: typeof(IInputClock)) is IInputClock clock) {
                // The loaded binding-page profile layers over the engine default (sticks stay always-bound in
                // the fallback); without one the default table alone keeps move/jump/interact working.
                m_pagedBindings = (m_serviceProvider.GetService(serviceType: typeof(PagedInputBindings)) as PagedInputBindings);

                var bindings = ((m_pagedBindings is not null)
                    ? new LayeredInputBindings(Fallback: OverworldInput.DefaultBindings, Primary: m_pagedBindings)
                    : null);

                m_routerSource = new RouterIntentSource(bindings: bindings, clock: clock, manager: manager, registry: registry, world: m_world);
                m_intentSource = m_routerSource;
            } else {
                m_intentSource = new LocalIntentSource(manager: manager, registry: registry, world: m_world);
            }

            m_rosterSource = new LocalRosterEventSource(manager: manager, registry: registry);
        } else {
            // No gamepad service: an empty room (overview camera) until input is available.
            m_intentSource = new ScriptedIntentSource(script: static (_, _) => PlayerIntent.None);
            m_rosterSource = new ScriptedRosterEventSource(schedule: []);
        }

        // Builds m_bricks (the machines) + the choir bookkeeping. The returned slot dictionary is intentionally
        // discarded — the machines are no longer compositor children (see the Children:null note below); this node
        // produces them itself in ProduceMachines.
        _ = BuildConsoleChildren();
        // The control-cluster animation reads the SAME per-frame joypad state each console's machine consumed
        // (GamingBrickChildNode.CurrentButtons) — never re-derived from raw input. Null when the room has no
        // consoles (the bare-room path never builds children, so m_bricks stays empty).
        var controlsSource = ((m_consoles.Count > 0) ? (Func<int, JoypadButtons>)ConsoleButtons : null);
        // An immersed run OPENS in the game-pane tiling (never animating there from a misleading room frame); a
        // standard run keeps today's staged-boot layouts.
        var director = new ScreenDirector(initialMode: (m_immersed ? ScreenDirectorMode.Immersed : ScreenDirectorMode.Standard));

        if (m_immersed && !m_worldLens && (m_consoles.Count > 0)) {
            // In the --rom immersed tiling a booted console's pane shows only while it is OWNED, so a pad eviction's
            // release eases that pane out while the machine stays booted (never reset). A presentation-only read of the
            // host-side ownership map — nothing here feeds back into the simulation. The WORLD-LENS default leaves this
            // null (every booted console's pane shows): players walk the room unseated, so there is no ownership to key
            // on — each of the four lens panes is simply on.
            director.ImmersedPaneVisible = consoleIndex => (m_consoleOwner[consoleIndex] >= 0);
        }

        // After the wall breaks, a cabinet breaks out into its own pane only while it is being DRIVEN (BrickDrives) —
        // the room stays the main view and the driven panes tile with it. Presentation-only.
        if (m_consoles.Count > 0) {
            director.BreakoutPaneVisible = consoleIndex => (CouplingFor(consoleIndex: consoleIndex) == BrickCoupling.BrickDrives);
        }

        m_director = director;

        var frameSource = new OverworldFrameSource(world: m_world, room: room, director: director, consoleAccents: ConsoleAccents(), controlsSource: controlsSource);

        // Held so the console 'creator' command (ICreatorModeHost) and the per-frame creator input can drive the
        // frame source's authoring pool. Presentation only — the simulation never learns creator mode exists.
        m_frameSource = frameSource;

        // NOTE: the driving player's avatar deliberately STAYS at the cabinet it took over (its frozen sim body), so the
        // break-out camera frames "you at your machine, playing" — the moving sprite lives on the diegetic screen, not
        // the room floor. (The machine→world game-tile readback still exists — WorldLensGameTile — if an avatar-follow
        // is ever wanted; WorldLensAvatarOverride is the hook.)

        // Per-pane camera closeness: immersed/standard panes sit right on the cabinet screen (the brick fills the
        // pane — "inside the ROM"); after the reveal a broken-out (driven) pane is a medium shot of the cabinet, and a
        // non-driven pane stays on the room camera (it is hidden anyway).
        frameSource.PaneCloseness = PaneClosenessFor;

        // A locked player stays SEATED at their machine (rendered at their frozen sim body in front of the cabinet) —
        // the brick→world avatar-follow (WorldLensAvatarOverride) is deliberately NOT wired: playing the game does not
        // drag the avatar around the room; the avatar waits at the machine until the player disengages after the reveal.

        // The render assembly is DATA now (the shared builder owns every backend-specific choice — kernel bytecode,
        // decorator availability); this node keeps only the simulation and the spec's overworld-specific inputs.
        var render = SdfWorldRenderBuilder.Build(
            serviceProvider: m_serviceProvider,
            spec: new SdfWorldRenderSpec(
                FrameSource: frameSource,
                Height: m_height,
                Width: m_width
            ) {
                // The frame-0 capture is suppressed when a delayed capture is armed (PUCK_OVERWORLD_CAPTURE_FRAME), so only
                // the post-boot frame is written to the path.
                CapturePath = ((m_captureFrame >= 0) ? null : m_capturePath),
                // NO compositor children: the machines are DECOUPLED from the viewport slots (this node produces them
                // itself in ProduceMachines, feeding their framebuffers to the DIEGETIC screens), so all five slots are
                // SDF-backed — slot 0 the room, slots 1..4 per-cabinet cameras. That is what lets a pane be a 3D shot of
                // a cabinet (immersed close-up / break-out) instead of being locked to a brick child surface.
                Children = null,
                // The console overlay wraps the binding-bar overlay so the developer console draws OVER everything
                // (including the bar) when it is open; both degrade to the bare producer if their resources are absent.
                Decorate = producer => BuildConsoleOverlay(inner: BuildBindingBarOverlay(inner: producer, frameSource: frameSource)),
                HostsOnDirectX = m_hostsOnDirectX,
                ScreenSources = BuildScreenSources(),
                ScreenLights = BuildScreenLights(),
            }
        );

        m_producer = render.Producer;
        m_producer.DebugMode = m_debugMode;
        m_root = render.Root;
    }

    // Wraps the world producer with the binding-bar overlay when the paged profile is live on EITHER input path —
    // the bare room's deterministic router or console mode's page adapter; anything missing (no profile, no store,
    // no gamepad service) degrades to the bare producer.
    private IRenderNode BuildBindingBarOverlay(SdfEngineNode inner, OverworldFrameSource frameSource) {
        if ((m_pagedBindings is null) || ((m_routerSource is null) && (m_pageInputs.Length == 0))) {
            return inner;
        }

        if ((m_serviceProvider.GetService(serviceType: typeof(BindingBarStore)) is not BindingBarStore store) ||
            (m_serviceProvider.GetService(serviceType: typeof(GamepadManager)) is not GamepadManager manager) ||
            (m_serviceProvider.GetService(serviceType: typeof(IShaderModuleLoader)) is not IShaderModuleLoader shaderLoader)) {
            return inner;
        }

        m_bindingBarAdapter = new BindingBarAdapter(bindings: m_pagedBindings, manager: manager, store: store);

        // The overlay samples the producer's same-device output (already fragment-shader-readable before its
        // submit) and renders into its own presentable target on the same queue — no CPU wait. It is CONFINED to
        // the ROOM view's live rect: the frame source stashes it during the producer's ProduceFrame (which runs
        // before the overlay packs its slots), so the bar hugs the room pane through every layout transition
        // instead of painting across the console panes.
        return new BindingBarOverlayNode(
            fragmentBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.OverlayDirectory, fileName: "binding-bar-overlay.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: m_height,
            inner: inner,
            regionSource: () => frameSource.LastRoomRegion,
            services: SdfParityProducers.BuildVulkanServices(serviceProvider: m_serviceProvider, width: m_width, height: m_height),
            source: store,
            vertexBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.SdfDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: m_width
        );
    }

    // Wraps the producer with the on-screen developer console overlay (open with the backtick console, type 'creator'
    // to enter creator mode). Degrades to the bare producer if the console store, the shader loader, or the GDI glyph
    // atlas is unavailable (e.g. a non-Windows host) — the terminal console still works either way.
    private IRenderNode BuildConsoleOverlay(IRenderNode inner) {
        if ((m_serviceProvider.GetService(serviceType: typeof(ConsoleTextStore)) is not ConsoleTextStore store) ||
            (m_serviceProvider.GetService(serviceType: typeof(IShaderModuleLoader)) is not IShaderModuleLoader shaderLoader) ||
            (ConsoleGlyphFont.TryCreate() is not { } font)) {
            return inner;
        }

        return new ConsoleOverlayNode(
            font: font,
            fragmentBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.OverlayDirectory, fileName: "console-overlay.frag.spv", loader: shaderLoader, stage: ShaderStage.Fragment),
            height: m_height,
            inner: inner,
            services: SdfParityProducers.BuildVulkanServices(serviceProvider: m_serviceProvider, width: m_width, height: m_height),
            source: store,
            vertexBytecode: SdfParityProducers.LoadShader(directory: DemoShaders.SdfDirectory, fileName: "fullscreen.vert.spv", loader: shaderLoader, stage: ShaderStage.Vertex),
            width: m_width
        );
    }

    // One dark brick pane per console, at the view slot the screen director lays out for it (1 + console index). Each
    // allocates its output ONCE at the full frame extent (its pane region animates every frame of a boot transition)
    // and powers on only when the simulation says its stand booted.
    private IReadOnlyDictionary<int, IRenderNode>? BuildConsoleChildren() {
        if (m_consoles.Count == 0) {
            return null;
        }

        var world = m_world!;
        var children = new Dictionary<int, IRenderNode>(capacity: m_consoles.Count);

        m_bricks = new GamingBrickChildNode[m_consoles.Count];
        m_choirLeaders = new int[m_consoles.Count];
        m_choirStates = new ChoirState[m_consoles.Count];

        // Every cabinet starts EMPTY (no machine): its machine assembles when a cart is inserted live
        // (AssignPendingCartridges), or on the first frame for an immersed loaded start.
        for (var index = 0; (index < m_consoles.Count); index++) {
            var consoleIndex = index;
            var console = m_consoles[index];
            var brick = new GamingBrickChildNode(
                allocationHeight: m_height,
                allocationWidth: m_width,
                appServices: m_serviceProvider,
                brickOrdinal: index,
                cartridgeRom: null,
                directX: m_hostsOnDirectX,
                exitCondition: console.Exit,
                gpuServices: m_serviceProvider,
                savePath: null,
                source: console,
                sourceId: $"overworld-brick:{index}"
            ) {
                PowerSource = () => world.IsBooted(consoleIndex: consoleIndex),
                // Every brick reads the SAME recorded stream from its own cursor — a late boot replays from the epoch.
                SegmentSource = TimelineFillFor(consoleIndex: consoleIndex),
                // The world↔machine membrane: when a WORLD-LENS cart is loaded here, feed it THIS console's own player
                // each frame so its brick mirrors that player walking the room. The BATON: once the wall is broken, a
                // player who takes over this cabinet donates control to the game (its ROM reads their joypad); otherwise
                // the world drives (mirror). The player is the console's owner if one claimed it, else the player at the
                // console's own slot. Deterministic sim position → honest input; the game-auth path is presentation-only.
                WorldLensSource = () => OverworldWorldLens.LensStateFor(
                    roomFraction: world.PlayerRoomFraction(slot: WorldLensPlayerSlot(consoleIndex: consoleIndex)),
                    gameHasControl: WorldLensGameControl(consoleIndex: consoleIndex)),
            };

            // IMMERSED only: an exit condition OR a solo 128-bit win REPLACES the default clean shutdown with the
            // fourth-wall reveal request (applied once by the next ProduceFrame). A non-immersed overworld keeps the
            // default — the same shutdown path host.exitAfterSeconds uses. (Meta wins fire at the room level below.)
            if (m_immersed) {
                brick.ExitConditionMet = () => RequestReveal(consoleIndex: consoleIndex);
                brick.VictoryConditionMet = () => RequestReveal(consoleIndex: consoleIndex);
            }

            children[1 + index] = brick;
            m_bricks[index] = brick;
            // Carts are inserted live, so a machine's identity is only known at insert time — no build-time choir grouping.
            m_choirLeaders[index] = -1;
        }

        m_metaVictoryWatch = MetaVictoryWatch.Build(consoles: m_consoles);

        return children;
    }

    // Requests the fourth-wall reveal (applied exactly once by the next ProduceFrame): a machine's exit condition or a
    // 128-bit win (solo, or the room's meta XOR) fired. Shared by every reveal trigger so the cadence stays identical.
    private void RequestReveal(int consoleIndex) {
        if (m_revealTriggerConsole < 0) {
            m_revealTriggerConsole = consoleIndex;
        }

        m_revealRequested = true;
    }

    // The room-level META win, polled each frame: the watch XORs each group's cabinet regions and returns the console to
    // reveal once a whole group reaches its shared target (all cabinets present — the "no cabinet wins alone" guarantee).
    private void EvaluateMetaVictory() {
        if (m_metaVictoryWatch is not { } watch) {
            return;
        }

        var trigger = watch.Poll(bricks: m_bricks);

        if (trigger >= 0) {
            RequestReveal(consoleIndex: trigger);
        }
    }

    // The shared-timeline fill delegate for one console — construction (BuildConsoleChildren) and takeover release
    // (ReleaseConsole) assign the SAME shape through this one helper, so a released brick rejoins the stream exactly
    // as it was originally wired.
    private JoypadSegmentFiller TimelineFillFor(int consoleIndex) {
        var timeline = m_timeline!;

        return destination => timeline.Fill(consoleIndex: consoleIndex, destination: destination);
    }

    // The control-cluster animation's joypad source (OverworldFrameSource.PackControlTransforms): the buttons currently
    // applied to console index's machine — out-of-range (never happens; the frame source only ever asks for a valid
    // console index) reads as none held.
    private JoypadButtons ConsoleButtons(int consoleIndex) =>
        (((consoleIndex >= 0) && (consoleIndex < m_bricks.Length)) ? m_bricks[consoleIndex].CurrentButtons : JoypadButtons.None);

    // One screen-source provider per console (the diegetic-screen seam, prototype-arc item 1): screenIndex ==
    // console index, matching OverworldFrameSource.BuildProgram's declared SdfScreenSurface. A provider returns the
    // brick's native (unresampled) framebuffer view only while the stand is BOTH booted and assigned — an
    // unbooted/unassigned stand's screen slab falls back to the flat/procedural material exactly as before
    // (m_world is captured once; the booted mask is read fresh every poll, which SdfEngineNode does once per frame
    // AFTER children have produced this frame's fresh framebuffer).
    private IReadOnlyDictionary<int, Func<nint>>? BuildScreenSources() {
        if (m_consoles.Count == 0) {
            return null;
        }

        var world = m_world!;
        var sources = new Dictionary<int, Func<nint>>(capacity: m_bricks.Length);

        for (var index = 0; (index < m_bricks.Length); index++) {
            var brick = m_bricks[index];
            var consoleIndex = index;

            sources[consoleIndex] = () => ((world.IsBooted(consoleIndex: consoleIndex) && brick.IsAssigned)
                ? brick.NativeImageViewHandle
                : 0);
        }

        return sources;
    }

    // The colored light each booted stand's diegetic screen casts into the room (the CRT glow), PARALLEL to
    // BuildScreenSources: screenIndex == console index, the color is the brick's per-frame framebuffer average. Zero
    // (no light) while the stand is unbooted/unassigned — matching the source provider, so a dark stand neither samples
    // nor lights. The engine gates on the same booted screen mask, so this only ever adds light for a lit screen.
    private IReadOnlyDictionary<int, Func<Vector3>>? BuildScreenLights() {
        if (m_consoles.Count == 0) {
            return null;
        }

        var world = m_world!;
        var lights = new Dictionary<int, Func<Vector3>>(capacity: m_bricks.Length);

        for (var index = 0; (index < m_bricks.Length); index++) {
            var brick = m_bricks[index];
            var consoleIndex = index;

            // Scaled by the director's room-light factor (0 while immersed) so a lit screen casts NO glow into the
            // black immersed letterbox — the bars stay pure black; the glow eases in with the room on the reveal.
            lights[consoleIndex] = () => ((world.IsBooted(consoleIndex: consoleIndex) && brick.IsAssigned)
                ? (brick.AverageColor * (m_director?.RoomLightFactor ?? 1f))
                : Vector3.Zero);
        }

        return lights;
    }

    // Reconciles each cabinet's ASSEMBLED machine with the sim's loaded cart type every frame: INSERT (assemble the
    // cart's machine), EJECT (dispose it — the cabinet goes dark and its pane eases closed), or SWAP (the cycle button
    // changed the running cart). Cheap at 4 cabinets; keeps the deterministic sim a pure function of (state, intents)
    // while the host owns the machine lifecycle. Cart type 0 is the WORLD-LENS cart (binds the "world" sensor-page
    // feed); cart type 1 is the CAMERA cart (binds the webcam peripheral).
    private void AssignPendingCartridges(OverworldWorld world) {
        for (var index = 0; (index < m_bricks.Length); index++) {
            var wantType = world.InsertedCartridge(consoleIndex: index);

            if (wantType == m_consoleAssembledType[index]) {
                continue;
            }

            var brick = m_bricks[index];

            if (wantType < 0) {
                brick.Eject();
                m_consoleAssembledType[index] = -1;
            } else if ((wantType < m_cartTypeRoms.Length) && (m_cartTypeRoms[wantType] is { } rom)) {
                brick.LoadCartridge(cartridgeRom: rom, savePath: m_cartTypeSaves[wantType], peripheral: CartPeripheral(cartType: wantType));
                m_consoleAssembledType[index] = wantType;
            }
            // else: this cart type could not be sourced on this machine (e.g. no showcase ROM) — leave the cabinet dark.
        }
    }

    // The peripheral feed a cart TYPE binds: type 0 = the world-lens sensor page (world→machine membrane), type 1 = the
    // Pocket Camera webcam. Other types are self-contained ROMs with no host feed.
    private static string? CartPeripheral(int cartType) =>
        cartType switch {
            0 => "world",
            1 => "camera",
            _ => null,
        };

    // Builds the world-lens cart (type 0). The two-worlds romance: SDF-FORGE the room background on the overworld's own GPU
    // so the very room the player walks becomes the tiles their brick renders. Falls back to the CPU-authored grid
    // room if the GPU/compute services are unavailable or the forge throws (e.g. too many unique tiles), so the cabinet
    // always has a cart. The forge is a one-shot render + readback, run once at startup.
    private byte[] BuildWorldLensCartRom(in FrameContext context) {
        try {
            if (context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device) &&
                (m_serviceProvider.GetService(serviceType: typeof(IGpuComputeServices)) is IGpuComputeServices gpu)) {
                var room = Puck.Demo.Forge.SceneForge.ForgeRoom(device: device, gpu: gpu);

                return Puck.Demo.Forge.WorldLensRom.BuildFromForgedRoom(title: "PUCKLENS", room: room);
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[world-lens] SDF-forged background unavailable ({exception.Message}); using the CPU room.");
        }

        return Puck.Demo.Forge.WorldLensRom.Build(title: "PUCKLENS");
    }

    // An EMPTY stand (no pre-inserted ROM) never joins a choir group at construction time: its real cartridge identity
    // is unknown until a shelf insert happens live, well after this grouping runs once at build time. It stays its
    // own leader (-1) — the choir optimization simply does not apply to shelf-inserted machines this revision.
    private int ChoirLeaderFor(int index) {
        if (!m_consoles[index].IsPreInserted) {
            return -1;
        }

        for (var candidate = 0; (candidate < index); candidate++) {
            if (m_consoles[candidate].IsPreInserted && MachineKeyEquals(a: m_consoles[candidate], b: m_consoles[index])) {
                return candidate;
            }
        }

        return -1;
    }

    private static bool MachineKeyEquals(GamingBrickSource a, GamingBrickSource b) =>
        string.Equals(a: a.RomPath, b: b.RomPath, comparisonType: StringComparison.OrdinalIgnoreCase) &&
        (GamingBrickChildNode.ParseModel(value: (a.RunAs ?? a.Model)) == GamingBrickChildNode.ParseModel(value: (b.RunAs ?? b.Model))) &&
        (string.Equals(a: a.Speed, b: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase) == string.Equals(a: b.Speed, b: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase));

    // The debug pages' verbs (the machine-fleet plan's buff/debuff arc), dispatched DIRECTLY: console mode's input
    // deliberately does not ride the router's command dispatch, and these verbs mutate brick presentation state
    // that is not part of the deterministic world — a machine stays a pure function of (configuration, consumed
    // stream); the verbs change the configuration, and the sim's state hash never sees any of it. Dispatched PER
    // PLAYER SLOT, in slot order (the caller's loop), each verb targeting ITS player's nearest booted console —
    // two players' same-frame verbs both apply.
    private void DispatchDebugVerbs(OverworldPageInput pages, OverworldWorld world, int slot) {
        if (pages.WasPressed(command: DemoActionCommandModule.BrickSpeedToggleCommand)) {
            var target = world.NearestConsoleForSlot(slot: slot, booted: true);

            if (target < 0) {
                Console.Error.WriteLine(value: "[debug] speed pin: no booted console in range.");
            } else {
                DissolveChoirFor(console: target);

                var pinned = m_bricks[target].ToggleSpeedPolicy();

                Console.Error.WriteLine(value: $"[debug] console {target}: speed {(pinned ? "PINNED to the DMG rate (debuff)" : "restored to hardware (buff)")}.");
            }
        }

        TryModelVerb(pages: pages, world: world, command: DemoActionCommandModule.BrickModeDmgCommand, model: ConsoleModel.Dmg, slot: slot);
        TryModelVerb(pages: pages, world: world, command: DemoActionCommandModule.BrickModeCgbCommand, model: ConsoleModel.Cgb, slot: slot);
        TryModelVerb(pages: pages, world: world, command: DemoActionCommandModule.BrickModeAgbCommand, model: ConsoleModel.Agb, slot: slot);

        if (pages.WasPressed(command: DemoActionCommandModule.BrickSaveClearCommand)) {
            var saveTarget = world.NearestConsoleForSlot(slot: slot, booted: true);

            if (saveTarget < 0) {
                Console.Error.WriteLine(value: "[debug] save clear: no booted console in range.");
            } else {
                // Clearing the save changes the machine's boot inputs, so choir membership dissolves exactly as for
                // a reboot verb; the fresh machine rejoins the shared timeline at the head.
                DissolveChoirFor(console: saveTarget);
                m_bricks[saveTarget].ClearSaveData();
                m_timeline!.SkipToHead(consoleIndex: saveTarget);
                Console.Error.WriteLine(value: $"[debug] console {saveTarget}: battery save cleared; rebooted with fresh save RAM, rejoining the timeline at the head.");
            }
        }

        if (pages.WasPressed(command: DemoActionCommandModule.BrickStateHashCommand)) {
            Console.Error.WriteLine(value: $"[debug] tick {world.CurrentTick}: state hash 0x{world.StateHash():X16}");
        }

        if (pages.WasPressed(command: DemoActionCommandModule.BrickFleetStatusCommand)) {
            for (var index = 0; (index < m_bricks.Length); index++) {
                var brick = m_bricks[index];
                var leader = ((m_choirLeaders[index] >= 0) ? $" (leader {m_choirLeaders[index]})" : "");

                var presenting = ((brick.PresentationModel == brick.BootModel) ? "" : $", presenting {brick.PresentationModel}");

                Console.Error.WriteLine(value: $"[debug] console {index}: {(world.IsBooted(consoleIndex: index) ? "booted" : "dark")}, boots as {brick.BootModel}{presenting}, speed {(brick.IsSpeedPinned ? "pinned" : "hardware")}, choir {m_choirStates[index]}{leader}, cursor {(m_timeline!.IsAtHead(consoleIndex: index) ? "at head" : "behind")}");
            }
        }

        if (pages.WasPressed(command: DemoActionCommandModule.BrickCaptureCommand)) {
            var directory = Path.Combine(path1: "artifacts", path2: "overworld");

            Directory.CreateDirectory(path: directory);
            m_producer?.RequestCapture(path: Path.Combine(path1: directory, path2: $"debug-capture-{m_debugCaptureCounter++}.png"));
        }

        for (var mode = 0; (mode < DebugViewModes.Count); mode++) {
            if (pages.WasPressed(command: DebugViewModes.Command(mode: mode))) {
                DebugMode = mode;
                Console.Error.WriteLine(value: $"[debug] SDF view: {DebugViewModes.Name(mode: mode)}");
            }
        }
    }

    private void TryModelVerb(OverworldPageInput pages, OverworldWorld world, string command, ConsoleModel model, int slot) {
        if (!pages.WasPressed(command: command)) {
            return;
        }

        var target = world.NearestConsoleForSlot(slot: slot, booted: true);

        if (target < 0) {
            Console.Error.WriteLine(value: $"[debug] mode {model}: no booted console in range.");

            return;
        }

        if (m_bricks[target].PresentationModel == model) {
            Console.Error.WriteLine(value: $"[debug] console {target}: already presenting as {model}; no change.");

            return;
        }

        // With a per-ROM recipe the swap GENUINELY diverges the machine (its render model changes and its detection
        // flag is poked); without one it is presentation-only. Either way the timeline never moves (a swap is a
        // host-side per-console act, outside the shared input stream). Dissolving the choir keeps a same-model group
        // correct: a parked follower mirrors its LEADER, so a member that diverges (or is differently presented) must
        // step for itself. A no-op for the demo's dmg/cgb/agb stands (distinct boot models never choir).
        DissolveChoirFor(console: target);
        m_bricks[target].ChangeModel(model: model);
        Console.Error.WriteLine(value: $"[debug] console {target}: live device swap to {model} — game keeps running, no boot.");
    }

    // Dissolves the choir group containing a console: parked members wake first (the divergence event,
    // restore-from-leader), links sever, and the loud Refused path can never fire on a deliberate debug change. The
    // speed pin genuinely diverges the machine (its cycle budget differs); a presentation swap does not, but still
    // dissolves so a differently-presented member renders its OWN pixels instead of mirroring the leader's.
    private void DissolveChoirFor(int console) {
        if (m_choirLeaders[console] >= 0) {
            if (m_choirStates[console] == ChoirState.Parked) {
                m_bricks[console].Unpark();
                Console.Error.WriteLine(value: $"[overworld] choir: console {console} UNPARKED (divergence event) — stepping independently again.");
            }

            m_choirStates[console] = ChoirState.Unparked;
            m_choirLeaders[console] = -1;
        }

        for (var index = 0; (index < m_choirLeaders.Length); index++) {
            if (m_choirLeaders[index] == console) {
                if (m_choirStates[index] == ChoirState.Parked) {
                    m_bricks[index].Unpark();
                    Console.Error.WriteLine(value: $"[overworld] choir: console {index} UNPARKED (its leader {console} diverged).");
                }

                m_choirStates[index] = ChoirState.Unparked;
                m_choirLeaders[index] = -1;
            }
        }
    }

    // The proximity TAKEOVER (host-side ownership, never sim state): the owned machine diverges from the shared
    // stream exactly like the debug verbs do — choir membership dissolves first — then its SegmentSource clears so
    // the brick falls back to per-frame pad sampling, which the pad service's ownership override routes to the
    // OWNER's pad alone.
    private void TakeOverConsole(int console, int slot) {
        DissolveChoirFor(console: console);

        m_bricks[console].SegmentSource = null;
        m_padService?.SetBrickOwner(brickOrdinal: console, playerIndex: slot);
        m_consoleOwner[console] = slot;
        m_slotConsole[slot] = console;
        Console.Error.WriteLine(value: $"[overworld] takeover: console {console} OWNED by player {slot} — off the shared timeline, driven by that player's pad alone.");
    }

    // The takeover release: the pad override clears, the cursor seats at the head (a released machine joins the
    // stream NOW), and the original timeline-fill source is restored. A no-op for a slot that owns nothing.
    private void ReleaseConsole(int slot) {
        var console = m_slotConsole[slot];

        if (console < 0) {
            return;
        }

        m_padService?.SetBrickOwner(brickOrdinal: console, playerIndex: -1);
        m_timeline!.SkipToHead(consoleIndex: console);
        m_bricks[console].SegmentSource = TimelineFillFor(consoleIndex: console);
        m_consoleOwner[console] = -1;
        m_slotConsole[slot] = -1;
        Console.Error.WriteLine(value: $"[overworld] takeover: console {console} RELEASED by player {slot} — rejoining the shared timeline at the head.");
    }

    // Console mode's roster: player 0 (the room player) is permanent; every further connected pad gets a world
    // player at its pad index (AddPlayer is idempotent, and occupancy stays DENSE because a count drop evicts the
    // top slots below, so pad index i always lands — or already holds — slot i). Eviction mirrors the bare-room
    // leaver hygiene: any takeover releases, the slot's chord/latch state resets, and its page adapter drops so a
    // future joiner starts with fresh edges.
    private void UpdateConsoleRoster(OverworldWorld world) {
        if (m_gamepadManager is not { } manager) {
            return;
        }

        var padCount = Math.Clamp(value: manager.ConnectedDevices().Count, min: 1, max: OverworldWorld.MaxPlayers);

        for (var index = 1; (index < padCount); index++) {
            _ = world.AddPlayer(playerId: DeterministicGuid(salt: (uint)index));
        }

        for (var slot = padCount; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (world.Slots[slot] is not { } player) {
                continue;
            }

            ReleaseConsole(slot: slot);
            m_pagedBindings?.Reset(slot: slot);
            m_pageInputs[slot] = null;
            m_jumpHeldLastBySlot[slot] = false;
            // The immersed seating ledger clears with the slot, so a rejoining pad re-seats at its stand (the released
            // machine stays booted; only ownership — and its pane's immersed visibility — moved).
            m_immersedSeated[slot] = false;
            _ = world.RemovePlayer(playerId: player.Id);
        }
    }

    // Park every converged choir follower: when a follower and its leader are BOTH booted and BOTH at the shared
    // timeline's head, their machines have consumed the identical stream from identical configs and must be
    // byte-identical — TryParkBehind verifies exactly that before the follower stops stepping. A failed compare is a
    // determinism-contract violation: refuse the park permanently and say so loudly.
    private void ParkConvergedChoirMembers(OverworldWorld world) {
        for (var index = 0; (index < m_choirLeaders.Length); index++) {
            var leader = m_choirLeaders[index];

            if ((leader < 0) || (m_choirStates[index] != ChoirState.Unparked)) {
                continue;
            }

            // An OWNED console (or one whose leader is owned) is off the shared stream — its machine is fed by its
            // owner's pad, so the byte-identical precondition can never hold across ownership. (A takeover also
            // dissolves the group eagerly; this guard keeps the invariant local and obvious.)
            if ((m_consoleOwner[index] >= 0) || (m_consoleOwner[leader] >= 0)) {
                continue;
            }

            if (!world.IsBooted(consoleIndex: index) || !world.IsBooted(consoleIndex: leader)) {
                continue;
            }

            if (!m_timeline!.IsAtHead(consoleIndex: index) || !m_timeline.IsAtHead(consoleIndex: leader)) {
                continue;
            }

            if (m_bricks[index].TryParkBehind(leader: m_bricks[leader])) {
                m_choirStates[index] = ChoirState.Parked;
                Console.Error.WriteLine(value: $"[overworld] choir: console {index} parked behind console {leader} — machines byte-identical at the timeline head; one machine now steps for both.");
            } else {
                m_choirStates[index] = ChoirState.Refused;
                Console.Error.WriteLine(value: $"[overworld] choir: REFUSED to park console {index} behind console {leader} — identical configs at the timeline head produced DIFFERENT machine states. Both keep stepping; this is a determinism-contract violation worth investigating.");
            }
        }
    }

    // The per-console accent colors the frame source paints the stands with, by costume.
    private IReadOnlyList<Vector3> ConsoleAccents() {
        var accents = new Vector3[Math.Max(1, m_consoles.Count)];

        for (var index = 0; (index < accents.Length); index++) {
            accents[index] = (((index < m_consoles.Count) ? m_consoles[index].Model.ToLowerInvariant() : "cgb") switch {
                "dmg" => new Vector3(0.72f, 0.73f, 0.66f), // the DMG's grey-green shell
                "agb" => new Vector3(0.33f, 0.30f, 0.71f), // the GBA's indigo
                _ => new Vector3(0.58f, 0.26f, 0.55f),     // the CGB's berry purple
            });
        }

        return accents;
    }

    private void AdvanceSimulation(in FrameContext context) {
        if (context.StepTicks == 0UL) {
            return;
        }

        var tickCount = (int)(context.DeltaTicks / context.StepTicks);

        if (tickCount <= 0) {
            return;
        }

        if (m_consoles.Count > 0) {
            AdvanceConsoleMode(context: in context, tickCount: tickCount);
        } else {
            AdvanceBareRoom(tickCount: tickCount);
        }
    }

    // Console mode: sample the shared pad service once per frame (the same frame key the brick panes drain under),
    // update the pad-driven roster, derive each ACTIVE player's press/release edges and takeover routing, advance
    // the world with the full fixed-width intent row, then mirror the room player's movement into every unowned
    // booted brick's joypad — walking the room walks the games.
    private void AdvanceConsoleMode(in FrameContext context, int tickCount) {
        var world = m_world!;
        // Two fixed-width intent rows: press/release edges land on the frame's first tick only (the first row);
        // held state carries across the rest — the same frame-sampled discipline IPlayerIntentSource documents.
        var firstTickIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
        var heldIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
        // A boot press claims its console only once the sim has ACTUALLY booted it (the sim's nearest-target search
        // spans the shelf family too, so a press may resolve elsewhere): staged here, finalized after the advance.
        Span<int> pendingTakeover = stackalloc int[OverworldWorld.MaxPlayers];
        var roomJumpHeld = false;
        var roomMove = Vector2.Zero;

        pendingTakeover.Fill(value: -1);

        if ((m_pagedBindings is { } bindings) && (m_padService is { } pads) && (m_pageInputs.Length > 0)) {
            AdvancePagedSlots(context: in context, world: world, bindings: bindings, pads: pads, firstTickIntents: firstTickIntents, heldIntents: heldIntents, pendingTakeover: pendingTakeover, roomMove: ref roomMove, roomJumpHeld: ref roomJumpHeld);
        } else {
            // No binding system in the container: the pre-page sampler (South = jump, North = interact), room
            // player only — the multiplayer roster rides the page path.
            bool interactHeld;

            (roomMove, roomJumpHeld, interactHeld) = (m_padService?.SampleOverworld(frameKey: context.RenderTicks) ?? (Vector2.Zero, false, false));

            var interactPressed = (interactHeld && !m_interactHeldLast);

            m_interactHeldLast = interactHeld;

            var jumpPressed = (roomJumpHeld && !m_jumpHeldLastBySlot[0]);
            var jumpReleased = (!roomJumpHeld && m_jumpHeldLastBySlot[0]);

            m_jumpHeldLastBySlot[0] = roomJumpHeld;

            heldIntents[0] = new PlayerIntent(
                JumpHeld: roomJumpHeld,
                JumpPressed: false,
                JumpReleased: false,
                Move: new Vector2(x: roomMove.X, y: -roomMove.Y)
            );
            firstTickIntents[0] = (heldIntents[0] with {
                InteractPressed = interactPressed,
                JumpPressed = jumpPressed,
                JumpReleased = jumpReleased,
            });
        }

        // IMMERSED seating (pre-reveal only): every ACTIVE slot boots cabinet i as it joins, so the pane count tracks
        // the players. --rom mode also TAKES OVER (the player drives the game with buttons, seated); the WORLD-LENS
        // default only boots (players stay UNSEATED and WALK the room, their pads moving their avatars while each lens
        // pane mirrors its own player). After the reveal the seating stops.
        if (m_immersed && !m_revealed) {
            ApplyImmersedSeating(world: world);
        }

        // Mirror the room player's movement into the brick joypad image FIRST (directions + A on jump), so the
        // segment recorded below — and any other consumer this frame — already carries it; non-directional buttons
        // still pass through from the first pad, so game menus stay navigable.
        m_padService?.PublishMirror(mirror: MirrorOf(move: roomMove, jumpHeld: roomJumpHeld));

        for (var index = 0; (index < tickCount); index++) {
            ApplyDebugBoots(world: world);
            world.Advance(intentsBySlot: ((index == 0) ? firstTickIntents : heldIntents));

            // A shelf insert this tick made a stand's cartridge known: assemble that brick's machine right away, so a
            // same-tick boot (the shelf-loop's fast path — walk up, insert, immediately interact again to boot) finds
            // an assigned machine, not a stale unassigned one from before this tick.
            AssignPendingCartridges(world: world);
        }

        // Finalize the staged boot-press takeovers now the sim has resolved the presses: claim only a console that
        // actually BOOTED and is still unowned (lower slots win a same-frame race, matching the sim's own
        // in-slot-order interact resolution).
        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            var console = pendingTakeover[slot];

            if (console < 0) {
                continue;
            }

            if (world.IsBooted(consoleIndex: console)) {
                // The press inserted a cart (or the cabinet was already running): claim it so the player's pad drives it.
                if ((m_consoleOwner[console] < 0) && (m_slotConsole[slot] < 0)) {
                    TakeOverConsole(console: console, slot: slot);
                }
            } else if (m_slotConsole[slot] == console) {
                // The press ejected the cabinet the player owned: release ownership so they walk the room again.
                ReleaseConsole(slot: slot);
            }
        }

        // Park choir followers BEFORE this frame's segment is appended: cursors-at-head here means "consumed the
        // whole stream through last frame", the moment identical-config machines are provably byte-identical.
        ParkConvergedChoirMembers(world: world);

        // Record this frame onto the shared input timeline once anything is booted (the FIRST boot starts the epoch;
        // boots were applied above, so the booting frame itself is segment zero). Every powered brick consumes this
        // one stream from its own cursor — the lockstep spine. Sampled through the SHARED-stream view, so an owned
        // console 0 never leaks its owner's private input into the recording.
        if (world.BootedCount > 0) {
            var buttons = (m_padService?.SampleSharedStream(frameKey: context.RenderTicks) ?? default);

            m_timeline!.Append(buttons: buttons, ticks: context.DeltaTicks);

            // A parked follower's machine no longer consumes (its leader steps for it), and an OWNED machine
            // consumes its owner's pad instead of the stream — either way the cursor tracks the head so the trim
            // threshold and any future unpark/release stay correct.
            for (var index = 0; (index < m_choirStates.Length); index++) {
                if ((m_choirStates[index] == ChoirState.Parked) || (m_consoleOwner[index] >= 0)) {
                    m_timeline.SkipToHead(consoleIndex: index);
                }
            }
        }
    }

    // The binding-page path's per-slot half of AdvanceConsoleMode: one page adapter per active slot replays that
    // player's drained pad into the paged resolver, then reads the commands by NAME — the on-disk profile is the
    // source of truth for which button does what. Fills both intent rows, stages the boot-press takeover claims,
    // dispatches each slot's debug verbs, publishes the per-player binding bars, and hands slot 0's raw movement
    // back for the brick mirror.
    private void AdvancePagedSlots(in FrameContext context, OverworldWorld world, PagedInputBindings bindings, GamingBrickPadService pads, PlayerIntent[] firstTickIntents, PlayerIntent[] heldIntents, Span<int> pendingTakeover, ref Vector2 roomMove, ref bool roomJumpHeld) {
        UpdateConsoleRoster(world: world);

        // The command switchboard refills the per-player brick feed every frame from scratch, so a slot that went
        // inactive stops driving a brick with a stale image.
        pads.ClearPublishedJoypads();

        var activeSlots = new List<int>(capacity: OverworldWorld.MaxPlayers);
        var contextIcons = new string?[OverworldWorld.MaxPlayers];
        // The creating slot's live button mask (creator mode only) — feeds the creator bar's pressed highlights.
        var creatorButtons = default(GamepadButtons);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (world.Slots[slot] is null) {
                continue;
            }

            var pages = (m_pageInputs[slot] ??= new OverworldPageInput(bindings: bindings, slot: slot));
            var raw = pads.SamplePlayerRaw(playerIndex: slot, frameKey: context.RenderTicks);

            pages.BeginFrame(state: in raw);
            pads.SetPlayerBrickInputEnabled(playerIndex: slot, enabled: pages.AllowsBrickInput);

            // Creator mode takes over the creating slot (slot 0): its controller edits SDF shapes instead of walking
            // the room, its avatar freezes (PlayerIntent.None), and its brick feed goes silent. Other players keep
            // playing, but the overlay shows the single creator bar while the mode is up (a focused authoring mode).
            if ((slot == 0) && (m_frameSource is { CreatorActive: true } creator)) {
                AdvanceCreatorInput(creator: creator, raw: in raw, deltaSeconds: (float)EngineTicks.ToSeconds(ticks: context.DeltaTicks));
                pads.PublishPlayerJoypad(playerIndex: slot, joypad: default);
                creatorButtons = raw.Buttons;
                heldIntents[slot] = PlayerIntent.None;
                firstTickIntents[slot] = PlayerIntent.None;
                m_jumpHeldLastBySlot[slot] = false;

                continue;
            }

            var move = raw.LeftStick;

            // The sticks are always-on; the dpad moves the player only where the ACTIVE page leaves that
            // direction unbound (a debug page that claims a dpad button owns it while its chord is held).
            if (!pages.Binds(source: InputSources.Gamepad.DpadLeft) && (0 != (raw.Buttons & GamepadButtons.DpadLeft))) { move.X = -1f; }
            if (!pages.Binds(source: InputSources.Gamepad.DpadRight) && (0 != (raw.Buttons & GamepadButtons.DpadRight))) { move.X = 1f; }
            if (!pages.Binds(source: InputSources.Gamepad.DpadUp) && (0 != (raw.Buttons & GamepadButtons.DpadUp))) { move.Y = 1f; }
            if (!pages.Binds(source: InputSources.Gamepad.DpadDown) && (0 != (raw.Buttons & GamepadButtons.DpadDown))) { move.Y = -1f; }

            var jumpHeld = pages.IsHeld(command: OverworldInput.JumpCommand);

            // The command switchboard's feed: publish THIS player's command-derived joypad so their brick (owned, or
            // routed to) consumes the same commands that drive their avatar — computed even for an owned/frozen player,
            // since when they drive their cabinet (BrickDrives) their movement steers the brick, not the avatar.
            // Suppressed on a debug page (AllowsBrickInput false) so debug chords never leak into a game.
            pads.PublishPlayerJoypad(playerIndex: slot, joypad: (pages.AllowsBrickInput ? CommandJoypad(move: move, jumpHeld: jumpHeld, raw: in raw) : default));

            // The contextual cabinet target: the nearest cabinet regardless of state (empty, loaded, or running). North
            // (interact) toggles it — insert its selected cart when empty, eject when running — and ALWAYS reaches the
            // sim; the West context button also inserts when standing at a cabinet, else it is the Mario hold-to-run.
            var nearestCabinet = world.NearestCabinetForSlot(slot: slot);
            var nearCabinet = (nearestCabinet >= 0);
            var interactPressed = (pages.WasPressed(command: OverworldInput.InteractCommand) ||
                (nearCabinet && pages.WasPressed(command: DemoActionCommandModule.ContextCommand)));
            var runHeld = (!nearCabinet && pages.IsHeld(command: DemoActionCommandModule.ContextCommand));

            // Cycle (Right bumper): advance the nearest cabinet's selected cart. Read from the RAW pad edge — it is not
            // in the on-disk binding profile — so it works without a profile entry.
            var cycleHeld = (0 != (raw.Buttons & GamepadButtons.RightShoulder));
            var cyclePressed = (cycleHeld && !m_cycleHeldLastBySlot[slot]);

            m_cycleHeldLastBySlot[slot] = cycleHeld;

            // In the WORLD-LENS game the interact NEVER reaches the sim (the cabinets stay booted — pressing North must
            // not eject a player's own lens). It is purely the takeover baton, and only AFTER the wall breaks: post-
            // reveal, North at an unowned cabinet CLAIMS it (BrickDrives — the player then steers its brick, their
            // avatar following). Elsewhere (walk-in / --rom) interact reaches the sim as before (insert/eject) and the
            // finalize pass derives ownership from the outcome.
            var simInteract = (interactPressed && !m_worldLens);

            if (interactPressed && nearCabinet && (!m_worldLens || m_revealed)) {
                pendingTakeover[slot] = nearestCabinet;
            }

            // The dedicated disengage (Left bumper): a seated player leaves their console and rejoins free room
            // movement. Held off while immersed and not yet revealed (that tiling has no room to walk into yet).
            if (pages.WasPressed(command: OverworldInput.LeaveCommand) && (m_slotConsole[slot] >= 0) && (m_revealed || !m_immersed)) {
                ReleaseConsole(slot: slot);
            }

            DispatchDebugVerbs(pages: pages, world: world, slot: slot);

            if (slot == 0) {
                // The room-player mirror publishes from slot 0's RAW movement regardless of ownership — it
                // feeds the UNOWNED booted bricks (attract behavior), exactly as before.
                roomJumpHeld = jumpHeld;
                roomMove = move;
            }

            var owned = (m_slotConsole[slot] >= 0);
            var jumpPressed = (jumpHeld && !m_jumpHeldLastBySlot[slot]);
            var jumpReleased = (!jumpHeld && m_jumpHeldLastBySlot[slot]);

            m_jumpHeldLastBySlot[slot] = jumpHeld;
            activeSlots.Add(item: slot);
            contextIcons[slot] = ((owned || nearCabinet) ? "action.interact" : null);

            // An owner's avatar stands at the machine: NO movement while owned, but interact (EJECT) and cycle STILL
            // fire on their press edge — so North ejects the running cabinet and the bumper swaps its cart.
            if (owned) {
                heldIntents[slot] = PlayerIntent.None;
                firstTickIntents[slot] = (PlayerIntent.None with { CyclePressed = cyclePressed, InteractPressed = simInteract });

                continue;
            }

            heldIntents[slot] = new PlayerIntent(
                JumpHeld: jumpHeld,
                JumpPressed: false,
                JumpReleased: false,
                // The fixed chase camera looks toward -Z, so stick-up (forward) maps to world -Z — the same
                // negation LocalIntentSource applies. The brick MIRROR keeps the raw stick sense (stick-up =
                // joypad Up).
                Move: new Vector2(x: move.X, y: -move.Y),
                RunHeld: runHeld
            );
            firstTickIntents[slot] = (heldIntents[slot] with {
                CyclePressed = cyclePressed,
                InteractPressed = simInteract,
                JumpPressed = jumpPressed,
                JumpReleased = jumpReleased,
            });
        }

        // While creator mode is up, the overlay shows the single creator bar (its physical buttons remapped to the
        // authoring verbs + the selected-primitive readout); otherwise one 12-slot bar per active player, each joined
        // against ITS page state and context.
        if (m_frameSource is { CreatorActive: true } creatorBar) {
            m_bindingBarAdapter?.PublishCreator(
                heldButtons: creatorButtons,
                shapeIndex: creatorBar.CreatorShapeIndex
            );
        } else {
            m_bindingBarAdapter?.Publish(
                activeSlots: activeSlots,
                contextIconForSlot: slot => contextIcons[slot],
                isHeldForSlot: (slot, command) => (m_pageInputs[slot]?.IsHeld(command: command) ?? false)
            );
        }
    }

    private void AdvanceBareRoom(int tickCount) {
        var world = m_world!;

        m_intentSource!.BeginFrame(firstTick: world.CurrentTick);

        // Publish this frame's binding-bar state (active page, per-slot icons, pressed highlights) for the
        // overlay to render.
        if ((m_bindingBarAdapter is not null) && (m_routerSource is not null)) {
            m_bindingBarAdapter.Publish(source: m_routerSource);
        }

        for (var index = 0; (index < tickCount); index++) {
            var tick = world.CurrentTick;

            ApplyDebugBoots(world: world);

            // Roster events FIRST, so a joiner is present (and a leaver gone) for this tick's intents + step.
            foreach (var rosterEvent in m_rosterSource!.EventsForTick(tick: tick)) {
                if (rosterEvent.Kind == RosterEventKind.Join) {
                    _ = world.AddPlayer(playerId: rosterEvent.PlayerId);
                } else {
                    // A leaver's held modifier chord must not survive into whoever binds the slot next.
                    m_pagedBindings?.Reset(slot: world.SlotOf(playerId: rosterEvent.PlayerId));
                    _ = world.RemovePlayer(playerId: rosterEvent.PlayerId);
                }
            }

            var intents = m_intentSource.CollectTick(tick: tick, players: world.RosterBySlot());

            world.Advance(intentsBySlot: intents);
        }
    }

    // The switchboard coupling for a console's world-lens — who drives whom. BrickDrives: the player is ENGAGED (owns
    // this cabinet), so their input moves the BRICK sprite and the real-world avatar FOLLOWS it (machine→world) — the
    // locked "you play the game, the game moves you" state, from the moment they seat in. WorldDrives (default, an
    // UNOWNED cabinet): input moves the avatar and the brick mirrors it (walk-to-goal) — the state a player enters when
    // they disengage after the reveal. The authority baton + the avatar-follow both read this one switch.
    private BrickCoupling CouplingFor(int consoleIndex) =>
        (((m_consoleOwner is { Length: > 0 } owners) && (consoleIndex >= 0) && (consoleIndex < owners.Length) && (owners[consoleIndex] >= 0))
            ? BrickCoupling.BrickDrives
            : BrickCoupling.WorldDrives);

    // The authority baton written into the world-lens sensor page: the GAME reads the joypad whenever it is driving
    // (BrickDrives or the both-driven Parallel); otherwise the world drives (mirror).
    private bool WorldLensGameControl(int consoleIndex) =>
        (CouplingFor(consoleIndex: consoleIndex) is BrickCoupling.BrickDrives or BrickCoupling.Parallel);

    // How CLOSE a console's pane camera sits (0 = the wide room, 1 = the screen filling the pane natively). Every
    // visible pane sits fully close so its diegetic screen reads like a real GB/GBA panel: before the reveal the
    // immersed "inside the ROM" look, and after the reveal an ENGAGED (BrickDrives) cabinet's game fills its secondary
    // slice so the standing player can actually play it. A non-driven cabinet stays on the room camera (hidden anyway).
    private float PaneClosenessFor(int consoleIndex) {
        if (!m_revealed) {
            return 1f;
        }

        return ((CouplingFor(consoleIndex: consoleIndex) == BrickCoupling.BrickDrives) ? 1f : 0f);
    }

    // The presentation-only avatar-follow (machine→world): for a player driving a cabinet (BrickDrives), their avatar
    // renders where their brick sprite is — the game tile the ROM published, mapped back to a room position. Returns
    // null for every other slot (the avatar stays at its simulation body). Never touches the hashed sim.
    private WorldCoord3? WorldLensAvatarOverride(int slot) {
        if ((m_slotConsole is not { Length: > 0 } slotConsole) || (slot < 0) || (slot >= slotConsole.Length)) {
            return null;
        }

        var console = slotConsole[slot];

        if ((console < 0) || (console >= m_bricks.Length) || (CouplingFor(consoleIndex: console) != BrickCoupling.BrickDrives) || !m_bricks[console].IsAssigned) {
            return null;
        }

        var (tileX, tileY) = m_bricks[console].WorldLensGameTile;

        return m_world!.RoomPositionForFraction(fraction: OverworldWorldLens.RoomFractionForTile(tileX: tileX, tileY: tileY));
    }

    // The player slot whose room position feeds a console's world-lens: the console's OWNER when a player has claimed it
    // (the walk-in overworld), otherwise the player at the console's own slot (the world-lens immersed default seats player
    // i at console i). Falls back to slot 0 so an unclaimed lens still tracks the primary room player.
    private int WorldLensPlayerSlot(int consoleIndex) {
        if ((m_consoleOwner is { Length: > 0 } owners) && (consoleIndex >= 0) && (consoleIndex < owners.Length) && (owners[consoleIndex] >= 0)) {
            return owners[consoleIndex];
        }

        return (((consoleIndex >= 0) && (consoleIndex < OverworldWorld.MaxPlayers)) ? consoleIndex : 0);
    }

    // The immersed seating pass: player slot i takes cabinet i (pad index = player slot = stand index — the spawn
    // already stands them there), so the pane count tracks the players. The boot rides the config-path insert/boot
    // (deterministic given the pad roster, which is host input, never hashed sim state). In --rom mode a TAKEOVER then
    // routes the slot's pad to that machine alone (the player is seated, driving the game). In the WORLD-LENS default
    // there is NO takeover: the player stays unseated and WALKS the room, their pad moving their avatar while the lens
    // mirrors them. Applied ONCE per slot activation: the ledger clears on eviction (UpdateConsoleRoster) so a rejoining
    // pad re-seats at its stand.
    private void ApplyImmersedSeating(OverworldWorld world) {
        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (m_immersedSeated[slot] || (slot >= m_consoles.Count) || (world.Slots[slot] is null)) {
                continue;
            }

            m_immersedSeated[slot] = true;

            // World-lens cabinets start empty, so insert the selected (world-lens) cart and boot; --rom cabinets are
            // already loaded, so a plain boot suffices (a no-op if pre-booted).
            if (m_worldLens) {
                _ = world.InsertSelectedAndBoot(consoleIndex: slot);
            } else {
                _ = world.Boot(consoleIndex: slot);
            }

            // The seated player is LOCKED to their machine from the start (BOTH modes now): the takeover routes their
            // pad to the brick and freezes their avatar, so they PLAY the game while the brick drives their real-world
            // avatar (machine→world). They stay locked until they disengage — which only becomes possible after the
            // reveal opens the room.
            if (world.IsBooted(consoleIndex: slot) && (m_consoleOwner[slot] < 0) && (m_slotConsole[slot] < 0)) {
                TakeOverConsole(console: slot, slot: slot);
            }
        }
    }

    // The scripted boot schedule: PUCK_OVERWORLD_DEBUG_BOOT's k-th tick boots console k when the sim reaches it — a
    // config-path Boot (deterministic given the env), so captures can walk every layout stage without a controller.
    private void ApplyDebugBoots(OverworldWorld world) {
        while (
            (m_debugBootsApplied < m_debugBootTicks.Length) &&
            (world.CurrentTick >= m_debugBootTicks[m_debugBootsApplied])
        ) {
            // Cabinets start EMPTY in the current model, so a bare Boot would be refused — insert the cabinet's selected
            // cart first (cabinet k defaults to cart type k % CartTypeCount, so cabinet 0 = the world-lens).
            _ = world.InsertSelectedAndBoot(consoleIndex: m_debugBootsApplied);

            m_debugBootsApplied++;
        }
    }

    private static JoypadButtons MirrorOf(Vector2 move, bool jumpHeld) {
        var buttons = default(JoypadButtons);

        if (move.X < -MirrorThreshold) { buttons |= JoypadButtons.Left; }
        if (move.X > MirrorThreshold) { buttons |= JoypadButtons.Right; }
        if (move.Y > MirrorThreshold) { buttons |= JoypadButtons.Up; }
        if (move.Y < -MirrorThreshold) { buttons |= JoypadButtons.Down; }
        if (jumpHeld) { buttons |= JoypadButtons.A; }

        return buttons;
    }

    // A player's full brick joypad image PROJECTED FROM THEIR COMMANDS — the command switchboard's feed to a brick.
    // The gameplay lines are command-driven (the D-pad from the overworld MOVE command, A from JUMP — the very commands
    // that drive the avatar); the non-modal system keys (B/Start/Select) pass through from the pad. This is what makes a
    // brick a command sink instead of a second raw-pad path: the same command that moves your avatar moves your brick.
    private static JoypadButtons CommandJoypad(Vector2 move, bool jumpHeld, in GamepadState raw) {
        var buttons = MirrorOf(move: move, jumpHeld: jumpHeld);

        if (0 != (raw.Buttons & GamepadButtons.ButtonEast)) { buttons |= JoypadButtons.B; }
        if (0 != (raw.Buttons & GamepadButtons.Start)) { buttons |= JoypadButtons.Start; }
        if (0 != (raw.Buttons & GamepadButtons.Back)) { buttons |= JoypadButtons.Select; }

        return buttons;
    }

    private static int DebugPlayerCount() {
        return ((int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_OVERWORLD_DEBUG_PLAYERS"), out var count))
            ? Math.Clamp(count, 0, OverworldWorld.MaxPlayers)
            : 0);
    }
    private static long DebugSpawnCell() {
        return (long.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_OVERWORLD_CELL"), out var cell) ? cell : 0L);
    }
    private static int DebugCaptureFrame() {
        return (int.TryParse(Environment.GetEnvironmentVariable(variable: "PUCK_OVERWORLD_CAPTURE_FRAME"), out var frame) ? frame : -1);
    }
    private static ulong[] DebugBootTicks() {
        var raw = Environment.GetEnvironmentVariable(variable: "PUCK_OVERWORLD_DEBUG_BOOT");

        if (string.IsNullOrWhiteSpace(value: raw)) {
            return [];
        }

        var parts = raw.Split(separator: ',', options: (StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var ticks = new List<ulong>(capacity: parts.Length);

        foreach (var part in parts) {
            if (ulong.TryParse(s: part, result: out var tick)) {
                ticks.Add(item: tick);
            }
        }

        // The schedule boots console 0, 1, 2… in tick order, so it must be sorted to apply in one forward pass.
        ticks.Sort();

        return [.. ticks];
    }

    // Each debug player walks toward its corner so the swarm spreads out (bare-room capture aid).
    private static PlayerIntent DebugScript(ulong tick, int slot) {
        return new PlayerIntent(
            Move: new Vector2(x: (((slot % 2) == 0) ? -1f : 1f), y: ((slot < 2) ? -1f : 1f)),
            JumpHeld: false,
            JumpPressed: false,
            JumpReleased: false
        );
    }
    // The console-mode room player's fixed identity: single-player by design, mirrored outward into the bricks.
    private static Guid RoomPlayerId() =>
        new(b: [0xB0, 0x00, 0x00, 0xA5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
    private static Guid DeterministicGuid(uint salt) {
        var bytes = new byte[16];

        BitConverter.TryWriteBytes(destination: bytes, value: (0xA571_0000u | salt));

        return new Guid(b: bytes);
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // Forward to the root (the overlay tears down its own GPU resources, then forwards to the SDF producer). The
        // decoupled machines are no longer under the SDF producer, so forward to them too; their CPU emulation state
        // survives, only the GPU-side resources rebuild.
        m_root?.OnDeviceLost();

        foreach (var brick in m_bricks) {
            brick.OnDeviceLost();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        // The overlay's Dispose forwards to the wrapped producer; when the overlay is absent the root IS the producer.
        m_root?.Dispose();

        // The machines are no longer compositor children, so THIS node owns their disposal (their GPU resources +
        // battery-save flush) rather than the SDF engine.
        foreach (var brick in m_bricks) {
            brick.Dispose();
        }
    }
}

/// <summary>The host seam the demo's <c>creator</c> console command drives: it flips the in-engine SDF authoring mode
/// (creator mode) on the live overworld root. Presentation only — creator shapes never touch the deterministic sim.</summary>
internal interface ICreatorModeHost {
    /// <summary>Whether creator mode is currently active.</summary>
    bool CreatorModeActive { get; }

    /// <summary>Toggles creator mode and returns the new state (false if the root is not yet ready).</summary>
    bool ToggleCreatorMode();
}

/// <summary>How a player's input is coupled to their world-lens cabinet — the switchboard's per-connection setting.</summary>
internal enum BrickCoupling {
    /// <summary>Input drives the AVATAR; the brick sprite mirrors it (world→machine). The walk-to-goal default.</summary>
    WorldDrives,
    /// <summary>Input drives the brick SPRITE; the avatar follows it (machine→world). The post-reveal takeover.</summary>
    BrickDrives,
    /// <summary>Input drives BOTH independently — "walk the room walks the games" (the classic mirror). Expressible but
    /// not auto-selected yet.</summary>
    Parallel,
}
