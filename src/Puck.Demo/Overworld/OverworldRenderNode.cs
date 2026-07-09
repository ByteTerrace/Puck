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
/// <see cref="ScreenLayoutDirectorMode.Immersed"/> tiling — only the game panes, no room. The first machine whose
/// fourth-wall exit condition fires breaks the wall: the director eases to <see cref="ScreenLayoutDirectorMode.Revealed"/>
/// (the room fullscreen, the games playing on diegetically on the stands' screens) and the run continues — the
/// players keep their machines until they release with interact, exactly like any takeover.
/// Every capture/driving hook is now an in-session console verb (the scripted-console control plane —
/// <c>boot</c>/<c>cart</c>/<c>reveal</c>/<c>link</c>/<c>player.add</c>/<c>capture</c>/<c>step</c>/<c>state</c>), not an
/// env var; a piped stdin script drives the whole run and reads the echoed results (the demo's <c>PUCK_*</c> surface
/// was removed in the unification arc).
/// </summary>
internal sealed class OverworldRenderNode : IRenderNode, IDebugViewTarget, ICreatorModeHost {
    private const float MirrorThreshold = 0.5f;

    private readonly IServiceProvider m_serviceProvider;
    // The console sources — a MUTABLE copy of the document's list (the document itself stays immutable). Read as an
    // IReadOnlyList everywhere (.Count / .RomPath / .IsPreInserted / MachineKeyEquals / .Exit / .Victory); the single
    // WRITER is a live condition.* edit, applied through MetaVictoryWatch.ApplyPendingEdit (static, so the
    // array-mutation lives THERE, keeping this node under its analyzer coupling ceiling) — it replaces an entry with
    // `record with { Exit/Victory = ... }` so the room-level MetaVictoryWatch rebuild (which reads the console records,
    // not the bricks' fields) sees the new group/target/share — the console-source sync "the recursion" needs.
    private readonly IReadOnlyList<GamingBrickSource> m_consoles;
    private readonly IReadOnlyList<CartridgeSource> m_library;
    private readonly uint m_width;
    private readonly uint m_height;
    private readonly string? m_capturePath;
    // The scenario harness is settle-then-capture + completion-driven (see CaptureSequencer): this node no longer holds
    // any shot schedule — it just drives the frame source's state machine each frame (ScenarioTick), arms the producer's
    // one-shot capture on the path it returns, and requests a graceful shutdown once every shot is written
    // (ScenarioComplete). m_scenarioActive is latched at resource build so the per-frame path stays cheap for the demo.
    private bool m_scenarioActive;
    private bool m_scenarioExitRequested;
    private int m_producedFrames;
    private readonly bool m_hostsOnDirectX;
    private readonly bool m_immersed;
    // The document's world handle (OverworldNode.World, formerly the PUCK_OVERWORLD_WORLD env var): the saved world the
    // overworld resolves + commits at boot so the room — and the immersed reveal's target — is that sculpted world, not
    // the bare default room. Null = the plain room (the default demo, byte-unchanged). Threaded to InstallScenario →
    // LoadBootWorld on the frame source. The live mid-session path is the world.load console verb.
    private readonly string? m_bootWorld;
    // The FAR spawn cell (OverworldNode.Cell, formerly the PUCK_OVERWORLD_CELL env var): the whole room is placed at
    // this cell on BOTH the X and Z axes to demonstrate the planet-scale coordinate path — the sim is cell-agnostic
    // (identical local motion) and the floating-origin render seam keeps it crisp. Null (the default) is the origin
    // cell (0), so the default demo is byte-unchanged.
    private readonly long m_spawnCell;
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
    private ScreenLayoutDirector? m_director;
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
    // condition.show's per-cabinet snapshot only needs a fresh publish when a condition actually CHANGES — the boot
    // values on the first control drain, and a victory edit's rebuilt watch. condition.set/clear already mirror their own
    // cell SYNCHRONOUSLY on the frame source (UpdateConditionSnapshot, the SAME format DescribeExit/DescribeVictory
    // produce), so an unedited snapshot is stable; the old per-frame Describe* rebuild allocated two string[] plus a
    // per-cabinet interpolated string every produced frame for a value read only on demand. Latched true so the first
    // drain publishes the boot values.
    private bool m_conditionSnapshotDirty = true;
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
    // The fourth-wall reveal handshake, IMMERSED mode only — host-side presentation state, never sim state: a trigger
    // fires DURING m_root.ProduceFrame (the request), and the NEXT frame's ProduceFrame applies it exactly once. The
    // ladder has TWO independent rungs, each its own one-shot latch, and the two may COEXIST in one session: WORLD (rung
    // 2 — the room/town becomes visible; the director eases to the revealed layout) and EDITOR (rung 3 — the in-session
    // authoring unlock). A pending-request pair mirrors that split, so a World and an Editor reveal can both be queued.
    private bool m_worldRevealRequested;
    private bool m_editorRevealRequested;
    private bool m_worldRevealed;
    // Which console's fourth-wall condition fired (the WORLD reveal zooms OUT of that machine's screen); first to fire wins.
    private int m_revealTriggerConsole = -1;
    private int m_debugMode;
    // The immersed seating ledger, per player slot: seated exactly once per slot ACTIVATION (cleared on eviction so a
    // rejoining pad re-seats at its stand).
    private readonly bool[] m_immersedSeated = new bool[OverworldWorld.MaxPlayers];
    private GamepadManager? m_gamepadManager;
    private int m_debugCaptureCounter;
    // The link verb's half-open cable: the console marked by the first Link press, awaiting a partner (-1 = none).
    private int m_pendingLinkConsole = -1;
    private bool[] m_jumpHeldLastBySlot = new bool[OverworldWorld.MaxPlayers];
    private bool[] m_cycleHeldLastBySlot = new bool[OverworldWorld.MaxPlayers];
    private bool m_interactHeldLast;
    // Reused per-frame scratch for the hottest input path: AdvanceConsoleMode's two fixed-width intent rows and
    // AdvancePagedSlots' active-slot list + context-icon row — allocated ONCE and refilled in place (cleared, then the
    // active slots overwritten) each frame instead of newing them per frame. The two binding-bar callbacks capture only
    // instance state (m_contextIcons / m_pageInputs), so they are cached delegates too (assigned in the constructor)
    // rather than a fresh closure+delegate per Publish.
    private readonly PlayerIntent[] m_firstTickIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
    private readonly PlayerIntent[] m_heldIntents = new PlayerIntent[OverworldWorld.MaxPlayers];
    private readonly List<int> m_activeSlots = new(capacity: OverworldWorld.MaxPlayers);
    private readonly string?[] m_contextIcons = new string?[OverworldWorld.MaxPlayers];
    private readonly Func<int, string?> m_contextIconForSlot;
    private readonly Func<int, string, bool> m_isHeldForSlot;
    // Creator-mode input/model live in Puck.Demo.Creator, composed INSIDE the frame source (this node drives them
    // through thin frame-source forwarders so its own type coupling stays flat while the editor grows).
    // A queued in-engine avatar FORGE (the console 'forge' verb): the bake needs the live GPU device, so — like the
    // reveal handshake — the command sets this and the next ProduceFrame performs it once and clears it.
    private bool m_forgeAvatarRequested;
    // The in-game create/author→commit→play LOOP state (all handled on the next ProduceFrame, where the GPU device is
    // live): Start (slot 0) toggles creator; Start IN creator COMMITS the AVATAR (m_commitAvatarRequested). The SUBJECT-
    // NEUTRAL commit generalizes that: `forge scene` (the creator's scene cart) and `tracker.forge` (the tune cart) queue
    // a commit of ANY forged subject type into m_commitSubjectMask — the node forges it and, avatar-style, installs it
    // into the authoring player's nearest cabinet. m_forgeCartsNeedMask holds the forged types a cabinet has WANTED but
    // whose default has not been baked yet (the lazy first-forge, generalizing the old avatar-only flag). Both are
    // BITMASKS (bit c set == cart type c pending) so the node names no collection type at its analyzer coupling ceiling.
    // Start edge tracked for slot 0.
    private bool m_commitAvatarRequested;
    private int m_commitSubjectMask;
    private int m_forgeCartsNeedMask;
    // The forged types the LAZY default bake has already ATTEMPTED this run (a bit per cart type). A failed lazy forge (a
    // deterministic subject throw, or IGpuComputeServices unregistered) leaves its ROM slot null; without this,
    // AssignPendingCartridges would re-arm the need mask and re-run an SDF render+readback + a stderr line EVERY frame
    // until the player cart-cycles off. The bit makes a failed default retry at most once; a working GPU forges on the
    // first try and a later successful commit fills the ROM slot, so the shipped default is byte-unchanged.
    private int m_forgeCartsAttemptedMask;
    private bool m_startHeldLast;

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
    /// <param name="bootWorld">The document's world handle (<see cref="OverworldNode.World"/>): a saved world resolved
    /// + committed at boot so the room — and the immersed reveal's target — is that sculpted world, not the bare room.
    /// Null (the default) leaves the plain room.</param>
    /// <param name="spawnCell">The document's far spawn cell (<see cref="OverworldNode.Cell"/>): the whole room is
    /// placed at this cell on both the X and Z axes (the planet-scale coordinate-stability demo). Null (the default) is
    /// the origin cell.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public OverworldRenderNode(IServiceProvider serviceProvider, IReadOnlyList<GamingBrickSource> consoles, IReadOnlyList<CartridgeSource> library, uint width, uint height, string? capturePath, bool hostsOnDirectX = false, bool immersed = false, string? bootWorld = null, long? spawnCell = null) {
        ArgumentNullException.ThrowIfNull(consoles);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // A NATIVE AGB stand (GamingBrickSource.Native) hosts the real ARM7TDMI core, not the SM83 machinery: capture
        // its ROM to auto-boot the fullscreen AGB scene on the first frame, and hand the SM83 cabinet side a RomPath-less
        // copy so the stand stays a dark empty SM83 cabinet behind the takeover (never loaded/booted as SM83). The
        // fullscreen AGB scene renders over it, so a native run reads as one native machine.
        m_agbBootRomPath = consoles.FirstOrDefault(predicate: static c => (c.Native == true))?.RomPath;

        var sm83Consoles = ((m_agbBootRomPath is null)
            ? consoles
            : consoles.Select(selector: static c => ((c.Native == true) ? (c with { RomPath = null }) : c)).ToArray());

        // A mutable copy (an array behind the IReadOnlyList) — the live condition editor replaces entries in place. Built
        // by MetaVictoryWatch (static) so the array type stays out of THIS node's coupling set.
        m_consoles = MetaVictoryWatch.CopyConsoles(consoles: sm83Consoles);
        m_library = library;
        m_serviceProvider = serviceProvider;
        m_width = width;
        m_height = height;
        m_capturePath = capturePath;
        m_hostsOnDirectX = hostsOnDirectX;
        m_immersed = immersed;
        m_bootWorld = (string.IsNullOrWhiteSpace(value: bootWorld) ? null : bootWorld);
        // Normalize the nullable-optional cell at consumption (the add-a-field ritual): an omitted field is the origin.
        m_spawnCell = (spawnCell ?? 0L);
        m_worldLens = (immersed && (consoles.Count > 0) && consoles.All(predicate: static c => string.Equals(a: c.Peripheral, b: "world", comparisonType: StringComparison.OrdinalIgnoreCase)));
        // The two console-mode binding-bar callbacks capture only instance state (m_contextIcons / m_pageInputs), so
        // cache them ONCE here instead of allocating a fresh closure+delegate each frame in AdvancePagedSlots' Publish.
        m_contextIconForSlot = (slot => m_contextIcons[slot]);
        m_isHeldForSlot = ((slot, command) => (m_pageInputs[slot]?.IsHeld(command: command) ?? false));
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
    public OverworldFrameSource? CreatorFrameSource => m_frameSource;

    /// <inheritdoc/>
    public bool ToggleCreatorMode() {
        if (m_frameSource is null) {
            return false;
        }

        SetCreatorMode(active: !m_frameSource.CreatorActive);

        return m_frameSource.CreatorActive;
    }

    /// <inheritdoc/>
    public string RequestCreatorForge(string subject) {
        if (m_frameSource is not { } frameSource) {
            return "[forge: unavailable — the overworld is not ready]";
        }

        if (!frameSource.CreatorActive) {
            return "[forge: enter creator mode first (console: creator), build a creation, then forge [avatar|scene]]";
        }

        // The subject word chooses which cart the SAME creator creation forges into: `avatar` (default, back-compat) —
        // the walker overworld written to disk under ./forged-avatars, exactly as before; `scene` — the SDF-art creature
        // cart forged + hot-swapped into the nearest cabinet in-session. Both bake the full puck.creation.v1 document.
        if (string.IsNullOrWhiteSpace(value: subject) || string.Equals(a: subject, b: "avatar", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_forgeAvatarRequested = true;

            return $"[forge: queued avatar — baking {frameSource.CreatorPlacedCount} placed shape(s) into an overworld ROM on the next frame]";
        }

        if (string.Equals(a: subject, b: "scene", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_commitSubjectMask |= (1 << Puck.Demo.Forge.ForgeCommands.SceneCartType);

            return $"[forge: queued scene — baking {frameSource.CreatorPlacedCount} placed shape(s) into an SDF-art scene cart and installing it into the nearest cabinet on the next frame]";
        }

        return $"[forge: unknown subject '{subject}' — usage: forge [avatar|scene]]";
    }

    /// <inheritdoc/>
    public string RequestTuneForge() {
        if (m_frameSource is null) {
            return "[tracker.forge: unavailable — the overworld is not ready]";
        }

        if (!Puck.Demo.Forge.ForgeCommands.TrackerIsActive(services: m_serviceProvider)) {
            return "[tracker.forge: enter tracker mode first (console: tracker), author a tune, then tracker.forge]";
        }

        m_commitSubjectMask |= (1 << Puck.Demo.Forge.ForgeCommands.JukeboxCartType);

        return "[tracker.forge: queued — compiling the working tune into a jukebox cart and installing it into the nearest cabinet on the next frame]";
    }

    /// <inheritdoc/>
    public bool TrackerModeActive => Puck.Demo.Forge.ForgeCommands.TrackerIsActive(services: m_serviceProvider);

    /// <inheritdoc/>
    public bool ToggleTrackerMode() {
        // Mutual exclusion with the other authoring surfaces (the room has one at a time): entering tracker force-
        // exits creator AND world-sculpt first, exactly like the reverse guards in SetCreatorMode/ToggleWorldSculptMode.
        if (!Puck.Demo.Forge.ForgeCommands.TrackerIsActive(services: m_serviceProvider)) {
            if (m_frameSource is { CreatorActive: true }) {
                SetCreatorMode(active: false);
            }
            if (m_frameSource is { WorldSculptActive: true }) {
                SetWorldSculptMode(active: false);
            }
            if (m_frameSource is { SdfDebugActive: true }) {
                SetSdfDebugMode(active: false);
            }
            ExitAgbDebugIfActive();
        }

        return Puck.Demo.Forge.ForgeCommands.TrackerToggle(services: m_serviceProvider);
    }

    /// <inheritdoc/>
    public string ToggleWorldSculptMode() {
        if (m_frameSource is not { } frameSource) {
            return "[world: unavailable — the overworld is not ready]";
        }

        SetWorldSculptMode(active: !frameSource.WorldSculptActive);

        return (frameSource.WorldSculptActive
            ? "[world] ENTER — chord pages: bare=STAMP, hold LT=SELECT, RT=TERRAIN/LIGHTS, LT+RT=WALK OVERRIDES, RT+LT=BOUNDS/SAVE (the bar shows the pips). You are the cursor: walk to aim the ghost, South places. world.place <creation> arms the ghost; world.save makes it real."
            : "[world] EXIT — the sculpted scene stays visible; world.save writes it, world (the verb) re-enters.");
    }

    // The creating slot's authoring takeovers (creator / tracker / world-sculpt), extracted from the paged-slot loop
    // for the CA1502 ceiling. Returns true when a takeover CONSUMED the slot's input this frame (the caller skips
    // normal walking); world-sculpt returns false — the player keeps walking (they ARE the placement cursor) while
    // the sculptor consumes only the buttons/axes its chord pages bind.
    private bool AdvanceCreatingSlotTakeovers(in FrameContext context, ref GamepadButtons creatorButtons, PlayerIntent[] firstTickIntents, PlayerIntent[] heldIntents, GamingBrickPadService pads, in GamepadState raw, int slot) {
        AdvanceCreatorHotkey(raw: in raw);

        // Creator mode: its controller edits SDF shapes instead of walking the room, its avatar freezes, and its
        // brick feed goes silent. Other players keep playing; the overlay shows the single creator bar.
        if (m_frameSource is { CreatorActive: true } creator) {
            creator.AdvanceCreatorInput(raw: in raw, deltaSeconds: (float)EngineTicks.ToSeconds(ticks: context.DeltaTicks));

            if (creator.ConsumeCreatorExitRequest()) {
                SetCreatorMode(active: false);
            }
            pads.PublishPlayerJoypad(playerIndex: slot, joypad: default);
            creatorButtons = raw.Buttons;
            heldIntents[slot] = PlayerIntent.None;
            firstTickIntents[slot] = PlayerIntent.None;
            m_jumpHeldLastBySlot[slot] = false;

            return true;
        }

        // Tracker mode: exactly like creator above (mutually exclusive — the toggles force-exit one another); the
        // whole surface lives behind ForgeCommands' primitive-typed forwarders (never a Tracker.* type here). The
        // console IS tracker's display, so there is no binding-bar publish branch.
        if (Puck.Demo.Forge.ForgeCommands.TrackerAdvanceInput(services: m_serviceProvider, raw: in raw)) {
            pads.PublishPlayerJoypad(playerIndex: slot, joypad: default);
            heldIntents[slot] = PlayerIntent.None;
            firstTickIntents[slot] = PlayerIntent.None;
            m_jumpHeldLastBySlot[slot] = false;

            return true;
        }

        // SDF-debug mode: exactly like creator/tracker above (mutually exclusive — the toggles force-exit one another).
        // The orbit controller consumes the creating slot's pad, the player's joypad is zeroed, and normal walking is
        // skipped; North exits (consumed here, like creator's exit request).
        if (m_frameSource is { SdfDebugActive: true } sdfDebug) {
            sdfDebug.AdvanceSdfDebugInput(raw: in raw, deltaSeconds: (float)EngineTicks.ToSeconds(ticks: context.DeltaTicks));

            if (sdfDebug.ConsumeSdfDebugExitRequest()) {
                SetSdfDebugMode(active: false);
            }
            pads.PublishPlayerJoypad(playerIndex: slot, joypad: default);
            creatorButtons = raw.Buttons;
            heldIntents[slot] = PlayerIntent.None;
            firstTickIntents[slot] = PlayerIntent.None;
            m_jumpHeldLastBySlot[slot] = false;

            return true;
        }

        // World-sculpt mode: the THIRD takeover — but unlike the two above, the player KEEPS WALKING.
        if (m_frameSource is { WorldSculptActive: true } sculptSource) {
            sculptSource.AdvanceWorldSculptInput(deltaSeconds: (float)EngineTicks.ToSeconds(ticks: context.DeltaTicks), raw: in raw);
        }

        return false;
    }

    // Enters/leaves world-sculpt mode on the frame source — the creating slot's THIRD takeover, mutually exclusive
    // with creator and tracker (each force-exits the others; the room has one authoring surface at a time).
    private void SetWorldSculptMode(bool active) {
        if ((m_frameSource is not { } frameSource) || (frameSource.WorldSculptActive == active)) {
            return;
        }

        if (active) {
            if (frameSource.CreatorActive) {
                SetCreatorMode(active: false);
            }
            Puck.Demo.Forge.ForgeCommands.TrackerSetActive(services: m_serviceProvider, active: false);
            SetSdfDebugMode(active: false);
            ExitAgbDebugIfActive();
        }

        frameSource.SetWorldSculptActive(active: active);
        // The sculptor borrows the creator's lifted room framing (the town must read while stamping); a dedicated
        // WorldSculptView ease is deferred until the content pass demands its own framing.
        if (m_director is not null) {
            m_director.CreatorView = active;
        }
    }

    /// <inheritdoc/>
    public bool SdfDebugModeActive => (m_frameSource?.SdfDebugActive ?? false);

    /// <inheritdoc/>
    public string ToggleSdfDebugMode() {
        if (m_frameSource is not { } frameSource) {
            return "[sdf: unavailable — the overworld is not the active root]";
        }

        SetSdfDebugMode(active: !frameSource.SdfDebugActive);

        return (frameSource.SdfDebugActive
            ? "[sdf-debug] ENTER — one shape, fullscreen. Left stick orbits, triggers zoom, right stick pans, North exits. Console: sdf.shape / sdf.op / sdf.floor / sdf.info; debug.view.* (termination, slice) shade it."
            : "[sdf-debug] EXIT — back to the room.");
    }

    /// <inheritdoc/>
    public bool TryReadSdfPassTimings(Span<double> passMilliseconds, out int passCount, out double frame) {
        passCount = 0;
        frame = 0.0;

        return (m_producer?.TryReadPassTimings(passMilliseconds: passMilliseconds, passCount: out passCount, frame: out frame) ?? false);
    }

    // Enters/leaves SDF-DEBUG mode on the frame source — the fullscreen single-shape debug tool, the FOURTH creating-
    // slot takeover, mutually exclusive with creator/world-sculpt/tracker (each force-exits the others; the room has one
    // authoring surface at a time). Rides the SAME CreatorView flag world-sculpt/creator use for the fullscreen room
    // takeover. Thin by construction: it names only the frame source + director (already coupled) and primitives, so
    // the node stays under its analyzer coupling ceiling — all the debug logic lives in Puck.Demo.SdfDebug.
    private void SetSdfDebugMode(bool active) {
        if ((m_frameSource is not { } frameSource) || (frameSource.SdfDebugActive == active)) {
            return;
        }

        if (active) {
            if (frameSource.CreatorActive) {
                SetCreatorMode(active: false);
            }
            SetWorldSculptMode(active: false);
            Puck.Demo.Forge.ForgeCommands.TrackerSetActive(services: m_serviceProvider, active: false);
            ExitAgbDebugIfActive();
        }

        frameSource.SetSdfDebugActive(active: active);
        // Fullscreen takeover: the room eases to fullscreen and every game pane hides (the CreatorView flag), so the
        // debug subject fills the screen even from an immersed game.
        if (m_director is not null) {
            m_director.CreatorView = active;
        }
        Console.Error.WriteLine(value: (active
            ? "[sdf-debug] the room is replaced by one shape at the origin — orbit it, stack modifiers, and shade it with the debug views."
            : "[sdf-debug] the room returns."));
    }

    // The native AGB (ARM7TDMI) debug scene: a DI singleton owns the machine + its execution-control state (the node and
    // the frame source are both at their CA1506 ceilings, so the AGB state cannot be composed onto either — the sanctioned
    // IServiceProvider escape). Resolved lazily, exactly like the pad-routing service. This adds ONE type to the node.
    private Puck.Demo.AgbDebug.AgbDebugService? m_agbService;
    private bool m_agbServiceResolved;
    // A native AGB stand's ROM (captured from the document in the ctor), auto-booted into the fullscreen AGB scene on
    // the first produced frame; null for a run with no native stand. The one-shot latch guards the boot.
    private readonly string? m_agbBootRomPath;
    private bool m_agbAutoBooted;

    private Puck.Demo.AgbDebug.AgbDebugService? AgbService() {
        if (!m_agbServiceResolved) {
            m_agbServiceResolved = true;
            m_agbService = (m_serviceProvider.GetService(serviceType: typeof(Puck.Demo.AgbDebug.AgbDebugService)) as Puck.Demo.AgbDebug.AgbDebugService);
        }

        return m_agbService;
    }

    /// <inheritdoc/>
    public bool AgbDebugModeActive => (AgbService()?.Active ?? false);

    /// <inheritdoc/>
    public string ToggleAgbDebugMode(string? romPath) {
        if (m_frameSource is null) {
            return "[agb.debug: unavailable — the overworld is not the active root]";
        }

        if (AgbService() is not { } service) {
            return "[agb.debug: unavailable — the AGB debug service is not registered]";
        }

        if (!service.Active) {
            // The FIFTH creating-slot takeover — force-exit the other authoring modes (the room has one takeover at a time).
            if (m_frameSource.CreatorActive) {
                SetCreatorMode(active: false);
            }
            SetWorldSculptMode(active: false);
            SetSdfDebugMode(active: false);
            Puck.Demo.Forge.ForgeCommands.TrackerSetActive(services: m_serviceProvider, active: false);

            var narration = service.Enter(romPathArg: romPath);

            // Fullscreen takeover: the room eases to fullscreen and every game pane hides (the same CreatorView flag the
            // other modes ride); the frame source picks up the active state next produce (SetAgbDebugScreen) and rebuilds.
            if (service.Active && (m_director is not null)) {
                m_director.CreatorView = true;
            }

            return narration;
        }

        var exitNarration = service.Exit();

        if (m_director is not null) {
            m_director.CreatorView = false;
        }

        return exitNarration;
    }

    // Force-exits the AGB debug scene when another creating-slot takeover is entered (the mutual-exclusion counterpart of
    // the calls the other SetXMode helpers make). Pure service teardown — the entering mode owns the CreatorView flag.
    private void ExitAgbDebugIfActive() {
        if ((m_agbService ?? AgbService()) is { Active: true } service) {
            _ = service.Exit();
        }
    }

    // The native AGB scene's per-frame render-thread work, mirroring ProduceMachines' shape for the SM83 cabinets: while
    // the scene is active, feed the machine the same per-tick sampled joypad the SM83 bricks consume, step + upload it
    // (through the DI service — the node's only AGB coupling), and PUSH the resulting framebuffer handle + glow into the
    // frame source (primitives only) so its fullscreen slab samples them and BuildScreenSources returns them for the AGB
    // slot. When the scene has just closed, one last push clears the frame source's flag so the room rebuilds.
    private void ProduceAgbDebug(in FrameContext context) {
        if (m_frameSource is not { } frameSource) {
            return;
        }

        if (AgbService() is { Active: true } service) {
            service.SetJoypad(buttons: (m_padService?.SampleSharedStream(frameKey: context.RenderTicks) ?? default));
            service.ProduceFrame(context: in context);
            frameSource.SetAgbDebugScreen(active: true, handle: service.ViewHandle, glow: service.Glow);
        }
        else if (frameSource.AgbDebugActive) {
            frameSource.SetAgbDebugScreen(active: false, handle: 0, glow: Vector3.Zero);
        }
    }

    // THE GATED WORKBENCH ENTRY (Stage 3 of the self-editing arc): the DIEGETIC door into world-sculpt. Once the editor
    // reveal has LIT the workbench (OverworldFrameSource.EditorRevealed), the creating slot's player walking up to it and
    // pressing North (the same interact edge cabinets boot on) ENTERS world-sculpt — matching the narration "you can
    // shape this world." While the workbench is DARK (locked), interact prints a one-line hint and does nothing. This is
    // the ONLY entry gated on the reveal; the dev/agent path (the `world` / `creator` / `tracker` console verbs, the
    // Start-button creator entry) stays always-on and ungated — none of them reads EditorRevealed. Never fires while a
    // cabinet is also in range (the cabinet interact wins); the workbench sits far from the stands, so both-in-range
    // never happens in practice. Extracted from AdvancePagedSlots (and self-guarding to the creating slot 0, which
    // world-sculpt takes over) to keep that method under its CA1502 ceiling — the call site is unconditional.
    private void TryEnterWorkbench(OverworldWorld world, int slot, bool interactPressed, bool nearCabinet) {
        if ((slot != 0) || !interactPressed || nearCabinet || !world.IsPlayerNearWorkbench(slot: slot)) {
            return;
        }

        if (m_frameSource is { EditorRevealed: true, WorldSculptActive: false }) {
            SetWorldSculptMode(active: true);
            Console.Error.WriteLine(value: "[workbench] the terminal is live — world-sculpt is open. Walk to aim the ghost; South places. (Left bumper / the `world` verb exits.)");
        } else if (m_frameSource is { EditorRevealed: false }) {
            Console.Error.WriteLine(value: "[workbench] the terminal is dark — complete the arcade (or `reveal editor`) to open the workshop.");
        }
    }

    /// <inheritdoc/>
    public string RequestTrackerPreview(bool play) {
        if (!Puck.Demo.Forge.ForgeCommands.TrackerIsActive(services: m_serviceProvider)) {
            return "[tracker: enter tracker mode first (console: tracker)]";
        }

        return Puck.Demo.Forge.ForgeCommands.TrackerRequestPreview(services: m_serviceProvider, play: play);
    }

    // The in-session forge work that needs the LIVE GPU device (the same reuse BuildWorldLensCartRom relies on), run
    // once per ProduceFrame after the device resolves. SUBJECT-NEUTRAL (contract rule 6): the avatar, the tune, and the
    // SDF-art scene all forge + hot-swap through the ONE registry in Forge.ForgeCommands (ForgeSubjectRom). Four jobs,
    // each gated by its own flag so a brief bake hitch happens only when something actually changed: the console 'forge'
    // verb's DISK bake (avatar), the AVATAR COMMIT (Start in creator: re-forge + swap cabinets already running the avatar
    // + exit creator), the SUBJECT-INSTALL commits (`forge scene` / `tracker.forge`: forge ANY subject + install it into
    // the authoring player's nearest cabinet), and the LAZY first-forge of any forged type's default when a cabinet first
    // wants it. The GPU-vs-no-GPU dispatch is the registry's (the tune never waits on the device). The bakes live in
    // Forge.ForgeCommands so their couplings stay off this node.
    private void TryHandleForgeWork(in FrameContext context) {
        if (m_frameSource is not { } frameSource) {
            m_forgeAvatarRequested = false;
            m_commitAvatarRequested = false;
            m_commitSubjectMask = 0;

            return;
        }

        if (m_forgeAvatarRequested) {
            m_forgeAvatarRequested = false;
            Puck.Demo.Forge.ForgeCommands.Bake(context: in context, services: m_serviceProvider, frameSource: frameSource);
        }

        // AVATAR COMMIT: re-forge the avatar cart from the creator scene, swap it into any cabinet ALREADY running the
        // avatar, and drop back out of creator — the "edit → commit → back in the ROM" loop, never leaving the game.
        if (m_commitAvatarRequested) {
            m_commitAvatarRequested = false;

            if (Puck.Demo.Forge.ForgeCommands.ForgeSubjectRom(context: in context, services: m_serviceProvider, frameSource: frameSource, cartType: Puck.Demo.Forge.ForgeCommands.AvatarCartType) is { } rom) {
                m_cartTypeRoms[Puck.Demo.Forge.ForgeCommands.AvatarCartType] = rom;
                MarkForgedCabinetsForReload(cartType: Puck.Demo.Forge.ForgeCommands.AvatarCartType);
                Console.Error.WriteLine(value: $"[avatar] committed {frameSource.CreatorPlacedCount} shape(s) → the avatar cart is live; any cabinet running it reboots with your update.");
            }

            SetCreatorMode(active: false);
        }

        // SUBJECT-INSTALL commits (`forge scene`, `tracker.forge`): forge the subject, then install it into the authoring
        // player's nearest cabinet (avatar-style hot-swap) so the author immediately sees/hears it. Also re-swaps any
        // cabinet already running that type. A batch may carry more than one type; drain every pending bit this frame.
        if (m_commitSubjectMask != 0) {
            var pending = m_commitSubjectMask;

            m_commitSubjectMask = 0;

            for (var cartType = 0; (cartType < OverworldWorld.CartTypeCount); cartType++) {
                if ((pending & (1 << cartType)) != 0) {
                    CommitAndInstallForgedSubject(context: in context, frameSource: frameSource, cartType: cartType);
                }
            }
        }

        // LAZY DEFAULT: first time a cabinet wants a forged type, forge its default so it has something to run.
        if (m_forgeCartsNeedMask != 0) {
            var pending = m_forgeCartsNeedMask;

            m_forgeCartsNeedMask = 0;

            for (var cartType = 0; (cartType < OverworldWorld.CartTypeCount); cartType++) {
                if (((pending & (1 << cartType)) != 0) && (m_cartTypeRoms[cartType] is null)) {
                    // Mark the type ATTEMPTED so a null (failed) forge is not re-armed and retried every frame; on
                    // success the ROM slot fills and AssignPendingCartridges takes the load path, so the bit is a no-op there.
                    m_cartTypeRoms[cartType] = Puck.Demo.Forge.ForgeCommands.ForgeSubjectRom(context: in context, services: m_serviceProvider, frameSource: frameSource, cartType: cartType);
                    m_forgeCartsAttemptedMask |= (1 << cartType);
                }
            }
        }
    }

    // Forges a subject (tune/scene) and installs it into the authoring player's nearest cabinet — the avatar-style
    // in-session hot-swap for the `forge scene` / `tracker.forge` verbs. Swaps in the fresh bytes, then points that
    // cabinet's SELECTED cart at the type (a booted cabinet live-swaps; an empty one will insert it on boot), and
    // re-swaps any cabinet already running the type. Narrates the outcome; never throws (the forge is null-on-failure).
    private void CommitAndInstallForgedSubject(in FrameContext context, OverworldFrameSource frameSource, int cartType) {
        if (Puck.Demo.Forge.ForgeCommands.ForgeSubjectRom(context: in context, services: m_serviceProvider, frameSource: frameSource, cartType: cartType) is not { } rom) {
            return;
        }

        m_cartTypeRoms[cartType] = rom;
        MarkForgedCabinetsForReload(cartType: cartType);

        // Install into the authoring player's (slot 0) nearest cabinet so the author immediately plays it — the same
        // world seam the `cart` verb / Cycle drive (a booted cabinet live-swaps; an empty one inserts on boot). When no
        // cabinet is in range the fresh bytes still live in the type table — Cycle/boot reaches them like any cart.
        var cabinet = (m_world?.NearestCabinetForSlot(slot: 0) ?? -1);

        if ((cabinet >= 0) && (m_world is { } world)) {
            _ = world.SetSelectedCartType(consoleIndex: cabinet, cartType: cartType);
            Console.Error.WriteLine(value: $"[forge] forged the {cartType} cart and installed it into cabinet {cabinet}{(world.IsBooted(consoleIndex: cabinet) ? " (live-swapped)" : " (inserts on boot)")}.");
        } else {
            Console.Error.WriteLine(value: $"[forge] forged the {cartType} cart — Cycle or boot a cabinet to it (no cabinet in range to install into).");
        }
    }

    // START is the creator loop's one dedicated controller bind (slot 0 only; free in both gameplay and creator): press
    // it in the room to ENTER creator, press it while editing to COMMIT — re-forge the avatar cart from the scene,
    // hot-swap any cabinet running it, and drop back into the game to watch the new animation. The commit runs on the
    // next ProduceFrame (the live GPU device is there — see TryHandleForgeWork); here it only latches the intent.
    private void AdvanceCreatorHotkey(in GamepadState raw) {
        var startHeld = (0 != (raw.Buttons & GamepadButtons.Start));

        if (startHeld && !m_startHeldLast) {
            if (m_frameSource is { CreatorActive: true }) {
                m_commitAvatarRequested = true;
            } else {
                SetCreatorMode(active: true);
            }
        }

        m_startHeldLast = startHeld;
    }

    // Enters/leaves creator mode on the frame source and narrates it (the console command and the in-mode EXIT button
    // both route through here).
    private void SetCreatorMode(bool active) {
        if ((m_frameSource is not { } frameSource) || (frameSource.CreatorActive == active)) {
            return;
        }

        // Mutual exclusion with the tracker and world-sculpt modes (see the symmetric guards in ToggleTrackerMode/
        // SetWorldSculptMode): the room has one authoring surface at a time.
        if (active) {
            Puck.Demo.Forge.ForgeCommands.TrackerSetActive(services: m_serviceProvider, active: false);
            SetWorldSculptMode(active: false);
            SetSdfDebugMode(active: false);
            ExitAgbDebugIfActive();
        }

        frameSource.SetCreatorActive(active: active);
        // Ease the room to fullscreen while editing (and restore the game view on exit) so the creator ghost is visible
        // even when a game was filling the screen — creator is the "step out to the workbench" view.
        if (m_director is not null) {
            m_director.CreatorView = active;
        }
        Console.Error.WriteLine(value: (active
            ? $"[creator] ENTER — chord pages: bare=SCULPT, hold LT=SELECT, RT=STYLE, LT+RT=ANIMATE, RT+LT=RIG (the bar shows the pips; BACK still cycles). Left stick moves, RIGHT stick rotates, d-pad raises/lowers + rolls, left-stick click toggles shape↔group scope. SCULPT: bumpers cycle the primitive ({frameSource.CreatorShapeName}), South places, East undoes, West redoes, START commits (forge + swap the avatar cart), North exits without committing."
            : $"[creator] EXIT — {frameSource.CreatorPlacedCount} shape(s) placed."));
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out _)) {
            return default;
        }

        EnsureResources(context: in context);

        // In-session forge work that needs the live GPU device (disk forge, subject commits, lazy default carts) — brief
        // one-shot hitches. Subject-neutral: avatar, tune, and scene all forge + hot-swap through the one registry.
        TryHandleForgeWork(context: in context);

        // The live bake preview (the workbench easel): one rasterized view per frame at most, quantized on a worker
        // — composed inside the frame source so this node's coupling stays flat.
        m_frameSource?.TickBakePreview(context: in context);

        // The diegetic camera feeds + the procedural face feed (a live GPU device here) — composed inside the frame
        // source too, so this node names no feed type. Renders the previous frame's planned feeds (the diegetic lag).
        m_frameSource?.TickFeeds(context: in context);

        // The tracker's headless "hear it" preview: one machine frame per rendered frame while playing, exactly like
        // a booted cabinet's own PumpAudio cadence — behind ForgeCommands so this node stays coupling-flat.
        Puck.Demo.Forge.ForgeCommands.TrackerStepPreview(services: m_serviceProvider);

        // One produced frame elapsed — advance the counter both the delayed capture and the scenario shot schedule
        // gate on (the frame source's scenario driver advances its own matching per-frame counter, having already set
        // this frame's verbatim pose before the render).
        ++m_producedFrames;

        // The scripted-console control plane (IOverworldControlHost, implemented on the frame source — the render node
        // is at its analyzer coupling ceiling): push this frame's produced-frame count / immersed / revealed scalars
        // to the host, then drain the node-only requests the verbs queued (reveal, link, capture) — each routes through
        // the SAME machinery its former PUCK_* hook / debug bind used.
        DrainControlRequests();

        // The scenario harness (settle-then-capture, completion-driven): advance the frame source's per-angle state
        // machine one produced frame (settle timing only — it never touches a rendered value; it also holds THIS frame's
        // verbatim shot pose). When it returns a path, the active shot has settled — arm the producer's one-shot readback
        // (written this same produced frame, since the frame source's CaptureFrame runs inside m_root.ProduceFrame
        // below). Once every shot is written, request a GRACEFUL shutdown exactly once — the completion-driven exit; the
        // scenario's ExitAfterSeconds is only a safety net that reports a loud short-count if it ever races ahead.
        if (m_scenarioActive && (m_frameSource is { } scenarioSource)) {
            if (scenarioSource.ScenarioTick(deltaSeconds: (float)context.DeltaSeconds) is { } armPath) {
                m_producer?.RequestCapture(path: armPath);
            }

            if (scenarioSource.ScenarioComplete && !m_scenarioExitRequested) {
                m_scenarioExitRequested = true;
                Console.Error.WriteLine(value: $"[scenario] complete — {scenarioSource.ScenarioCapturedCount}/{scenarioSource.ScenarioShotCount} shots written; requesting graceful shutdown.");

                if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
                    terminal.RequestExit();
                }
            }
        }

        // THE fourth-wall moment (immersed only): a trigger fired during a previous frame's produce. The ladder's two
        // rungs are INDEPENDENT apply-once gates that may both fire in one session — WORLD (rung 2, the room becomes
        // visible) and EDITOR (rung 3, the authoring unlock). Each applies exactly once; neither disturbs the other.
        ApplyWorldRevealIfRequested();
        ApplyEditorRevealIfRequested();

        AdvanceSimulation(context: in context);

        // The machines are decoupled from the compositor slots, so THIS node steps + uploads them each frame (before
        // the SDF render reads their framebuffers for the diegetic screens). One serial pass — four machines is well
        // inside the frame; the choir park/mirror still amortizes identical ones. Their resampled output is unused now
        // (the diegetic screens sample the raw framebuffer via NativeImageViewHandle), so they produce at native size.
        // A native AGB stand auto-boots into the fullscreen AGB scene on the first frame (the --rom <path.gba> path and
        // the run-doc GamingBrickSource.Native field). One-shot; the player can leave it with `agb.debug` afterward.
        if (!m_agbAutoBooted && (m_agbBootRomPath is { } agbBootRom)) {
            m_agbAutoBooted = true;
            Console.Error.WriteLine(value: ToggleAgbDebugMode(romPath: agbBootRom));
        }

        ProduceMachines(context: in context);
        ProduceAgbDebug(context: in context);

        // The room-level meta (cooperative XOR) win, polled after the machines stepped this frame. Immersed only — the
        // reveal is the fourth-wall break, exactly like the exit/solo triggers that also only fire in immersed mode.
        if (m_immersed) {
            EvaluateMetaVictory();
        }

        // The SDF perf-bench (async, per-frame): while a run is in flight, feed the runner the PREVIOUS frame's per-pass
        // GPU ms and the render info its report names, BEFORE this frame's CaptureFrame (so a config change this step
        // rebuilds the program to the new workload this same frame). The node owns the producer's timings; the frame
        // source stays coupling-flat behind AdvanceSdfBench. No new type is named here.
        if (m_frameSource is { } benchSource && benchSource.SdfBenchRunning) {
            Span<double> passMilliseconds = stackalloc double[SdfEngineNode.PassTimingCount];
            var hasTimings = TryReadSdfPassTimings(passMilliseconds: passMilliseconds, passCount: out var passCount, frame: out var frame);
            // The bench keeps its FIXED four columns (frame + beam/views/composite) so the ladder tables stay comparable
            // run to run; a pass added to PassTimingLabels surfaces in sdf.info / [world-timing] but not as a new column.
            // The "beam" column FOLDS IN the instance-mask pass (split out of the beam kernel for occupancy), so the
            // column keeps meaning what every historical ladder measured: everything between frame start and views.
            // Resolved through SdfEngineNode (already coupled) so the node stays under its CA1506 class-coupling ceiling.
            var beam = (SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "beam")
                + SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "mask"));
            var views = SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "views");
            var composite = SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "composite");

            benchSource.AdvanceSdfBench(hasTimings: hasTimings, beam: beam, views: views, composite: composite, frame: frame, width: m_width, height: m_height, backendIsDirectX: m_hostsOnDirectX);
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
            TargetHeight = GamingBrickChildNode.NativeScreenHeight,
            TargetWidth = GamingBrickChildNode.NativeScreenWidth,
        });

        // Linked pairs first: both cabinets stage this frame's inputs, then the pair advances TOGETHER through the
        // shared link session (one budget, deterministic instruction interleave) — a linked pair is ONE step unit,
        // exactly the shape the parallel fleet split needs. Their ProduceFrame below then sees the prepared/executed
        // flags and performs only the GPU work; unlinked bricks keep the classic inline path.
        foreach (var brick in m_bricks) {
            if (brick.IsLinkPrimary && (brick.LinkPartner is { } partner)) {
                var primaryPending = brick.PrepareStep(context: in machineContext);
                var partnerPending = partner.PrepareStep(context: in machineContext);

                if (primaryPending && partnerPending) {
                    brick.ExecuteLinkedStep(partner: partner);
                }
            }
        }

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
        // The far spawn cell (OverworldNode.Cell) places the whole room at a far world cell (applied to X and Z) to
        // demonstrate the planet-scale coordinate path: the sim is cell-agnostic (identical local motion) and the
        // floating-origin render seam keeps it crisp. Default 0 = the origin cell. Threaded in as a document field.
        var farCell = m_spawnCell;

        // The cartridge TYPES a cabinet can hold, in cycle order: 0 = WORLD-LENS (a real ROM that reads a work-RAM sensor
        // page and mirrors the room it sits in — the world→machine membrane, fed the room player each frame; see
        // OverworldWorldLens), 1 = CAMERA (the Pocket Camera viewfinder, driven by the PC webcam), 2 = the SHOWCASE ROM
        // (the pre-inserted cartridge; a missing showcase leaves that slot null and the cabinet refuses to load it),
        // 4/5/6/7/8 = the five framework games (VOLLEY / BRICKFALL / CHROMA / SOLITAIRE / POKER). Types 3 (AVATAR walker),
        // 9 (JUKEBOX tune) and 10 (SDF-ART SCENE) are the three in-session FORGED subjects — baked LAZILY the first time a
        // cabinet wants one (see AssignPendingCartridges + TryHandleForgeWork), so those three slots stay NULL here. All
        // the rest are sourced pure-CPU, eagerly.
        var showcaseRom = m_consoles.Select(selector: static c => c.RomPath).FirstOrDefault(predicate: static p => p is not null);

        m_cartTypeRoms = new byte[]?[OverworldWorld.CartTypeCount];
        m_cartTypeSaves = new string?[OverworldWorld.CartTypeCount];
        m_cartTypeRoms[0] = BuildWorldLensCartRom(context: in context);
        m_cartTypeRoms[1] = Puck.Demo.Forge.CameraRom.Build(title: "PUCKCAM");

        if (showcaseRom is not null) {
            m_cartTypeRoms[2] = File.ReadAllBytes(path: showcaseRom);
            m_cartTypeSaves[2] = $"{showcaseRom}.sav";
        }

        // Types 4–8: the five five-star framework games — genuine SM83 ROMs assembled eagerly beside the camera cart.
        // Self-contained games with no host feed; cycle to them at any cabinet. Each SDF-bakes its title screen on the
        // live GPU (falling back to the hand-authored banner without one — the world-lens posture). (Type 3, the
        // player's forged avatar, is forged lazily the first time a cabinet selects it — see AssignPendingCartridges.)
        m_cartTypeRoms[4] = BuildVolleyCartRom(context: in context);
        m_cartTypeRoms[5] = BuildBrickfallCartRom(context: in context);
        m_cartTypeRoms[6] = BuildChromaCartRom(context: in context);
        m_cartTypeRoms[7] = BuildSolitaireCartRom(context: in context);
        m_cartTypeRoms[8] = BuildPokerCartRom(context: in context);

        // All five are battery-backed (their high-score tables live in cartridge SRAM); persist them beside the
        // demo's other local state (the bindings profile store's %LOCALAPPDATA%\Puck\Demo convention).
        m_cartTypeSaves[4] = Puck.Demo.Forge.VolleyRom.PrepareDefaultSavePath();
        m_cartTypeSaves[5] = Puck.Demo.Forge.BrickfallRom.PrepareDefaultSavePath();
        m_cartTypeSaves[6] = Puck.Demo.Forge.ChromaRom.PrepareDefaultSavePath();
        m_cartTypeSaves[7] = PrepareSolitaireSavePath();
        m_cartTypeSaves[8] = PreparePokerSavePath();

        m_consoleAssembledType = new int[m_consoles.Count];
        Array.Fill(array: m_consoleAssembledType, value: -1);

        // The overworld opens with EMPTY cabinets (insert a cart to bring one alive); an immersed boot starts every cabinet
        // loaded — the WORLD-LENS default with cart type 0 (each player opens inside their own lens on the room), the
        // --rom boot with the showcase cart (each player opens inside the running game). spawnAtConsoles seats immersed
        // players at their own stand.
        // The --rom immersed boot starts every cabinet LOADED (players open inside the running game); the world-lens
        // default starts EMPTY and boots cabinet i when player i joins (seating-boot), so the number of lens panes
        // tracks the number of players — each player boots their OWN instance. startCartType 0 makes the seating-boot
        // insert the world-lens cart (the built-in world-lens membrane).
        m_world = new OverworldWorld(room: room, tuning: PlatformerTuning.Default, tickSeconds: tickSeconds, seed: 1u, spawnAtConsoles: m_immersed, spawnCellX: farCell, spawnCellZ: farCell, startLoaded: (m_immersed && !m_worldLens), startCartType: (m_worldLens ? 0 : -1));

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
        // discarded — the machines are not compositor children (see the Children:null note below); this node
        // produces them itself in ProduceMachines.
        _ = BuildConsoleChildren();
        // The control-cluster animation reads the SAME per-frame joypad state each console's machine consumed
        // (GamingBrickChildNode.CurrentButtons) — never re-derived from raw input. Null when the room has no
        // consoles (the bare-room path never builds children, so m_bricks stays empty).
        var controlsSource = ((m_consoles.Count > 0) ? (Func<int, JoypadButtons>)ConsoleButtons : null);
        // An immersed run OPENS in the game-pane tiling (never animating there from a misleading room frame); a
        // standard run keeps the staged-boot layouts.
        var director = new ScreenLayoutDirector(initialMode: (m_immersed ? ScreenLayoutDirectorMode.Immersed : ScreenLayoutDirectorMode.Standard));

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

        // The headless capture aids + the --scenario driver compose on the frame source (this node is at its coupling
        // ceiling, so the scenario types live there). Resolve the delayed one-shot capture frame and read the scenario
        // shot schedule — both are needed BEFORE the render builds (the spec's CapturePath suppresses the frame-0
        // grab when either is armed). Kept in a helper so this method never names the config-settings type.
        InstallHeadlessCapture(frameSource: frameSource);

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
                // The plain --capture writes frame 0. The scenario harness OWNS the capture slot instead (its
                // settle-then-capture driver arms per-shot captures below), so the frame-0 capture stands down when a
                // scenario is active. The live in-session capture path is the `capture <png>` console verb (after
                // `step`/`settle`), which grabs the settled frame it asks for rather than frame 0.
                CapturePath = (m_scenarioActive ? null : m_capturePath),
                // NO compositor children: the machines are DECOUPLED from the viewport slots (this node produces them
                // itself in ProduceMachines, feeding their framebuffers to the DIEGETIC screens), so all five slots are
                // SDF-backed — slot 0 the room, slots 1..4 per-cabinet cameras. That is what lets a pane be a 3D shot of
                // a cabinet (immersed close-up / break-out) instead of being locked to a brick child surface.
                Children = null,
                // The console overlay wraps the binding-bar overlay so the developer console draws OVER everything
                // (including the bar) when it is open; both degrade to the bare producer if their resources are absent.
                Decorate = producer => BuildConsoleOverlay(inner: BuildBindingBarOverlay(inner: producer, frameSource: frameSource)),
                HostsOnDirectX = m_hostsOnDirectX,
                // The worst-case capacity envelope: the largest program the frame source can ever build (every screen
                // lit, the creator pool in its biggest emission form) — the engine reserves these floors up front so
                // an authoring rebuild never has to grow a GPU buffer, and UploadProgram throws loudly if an emission
                // change forgets to grow the probe with it.
                InstanceCapacity = frameSource.WorstCaseInstanceCapacity,
                ProgramWordCapacity = frameSource.WorstCaseProgramWordCapacity,
                // The dynamic-transform slot floor: the room's own movers, raised to the SDF-debug storm bench's motion
                // ceiling so a `sdf.bench storm` motion rung (up to 4096 per-frame-moving instances) fits the engine's
                // once-sized dynamic-transform buffer.
                DynamicTransformCapacity = frameSource.WorstCaseDynamicTransformCapacity,
                ScreenSources = BuildScreenSources(),
                ScreenLights = BuildScreenLights(),
                // Screen-surface transforms: the frame source implements ISdfFrameSource.ScreenSurfaceTransforms
                // directly (SdfWorldRenderBuilder reads it straight off FrameSource) — this node never spells out
                // that provider dictionary's type itself, keeping its own coupling flat.
            }
        );

        m_producer = render.Producer;
        m_producer.DebugMode = m_debugMode;
        m_root = render.Root;

        // The live bake preview plugs into the frame source's easel seam (the source owns the service; see
        // InstallBakePreview for why the composition lives there).
        frameSource.InstallBakePreview(services: m_serviceProvider);
        // The diegetic-feed director (the camera-feed pool + procedural face feed + named-feed registry) composes on
        // the frame source too — the render node stays coupling-flat; it only drives the render-thread tick below.
        frameSource.InstallFeeds(hostsOnDirectX: m_hostsOnDirectX, services: m_serviceProvider);
        ConnectLinkCable(frameSource: frameSource);
        ApplyCreatorStartupHooks(frameSource: frameSource);
    }

    // Composes the --scenario capture driver on the frame source and latches whether one is active — kept in one helper
    // so EnsureResources, at its class-coupling ceiling, never names the scenario type. The frame source owns the
    // scenario objects; the per-frame ProduceFrame path drives it through ScenarioTick / ScenarioComplete.
    private void InstallHeadlessCapture(OverworldFrameSource frameSource) {
        frameSource.InstallScenario(bootWorld: m_bootWorld, services: m_serviceProvider);
        m_scenarioActive = frameSource.ScenarioActive;
    }

    // The --scenario review harness's creator hook: an active scenario (Scenario:Creation) loads its creation into the
    // scene and opens straight into creator mode so the turntable shots frame the loaded workpiece. The live in-session
    // path to the same state is the `creator` / `creator.load` console verbs (the former PUCK_OVERWORLD_CREATOR /
    // PUCK_CREATOR_LOAD env aids were removed — this scenario branch is all that remains).
    private void ApplyCreatorStartupHooks(OverworldFrameSource frameSource) {
        var scenarioCreation = Puck.Demo.Configuration.ScenarioAccessor.ScenarioCreation(services: m_serviceProvider);

        if (scenarioCreation is { Length: > 0 }) {
            Console.Error.WriteLine(value: frameSource.LoadCreationFile(nameOrPath: scenarioCreation));
            SetCreatorMode(active: true);
        } else if (frameSource.ScenarioActive) {
            SetCreatorMode(active: true);
        }
    }

    // Wires the diegetic link cable's primitive-typed pair reader (two int-returning delegates, never a tuple —
    // keeps this node's own compiled coupling from spelling ValueTuple<int,int>). A lambda over this node's OWN
    // already-coupled brick bookkeeping (GamingBrickChildNode is already named here) adds no new type coupling.
    // A pair goes live when the `link <i> <j>` console verb connects two cabinets (or an in-game Bricks-page press).
    private void ConnectLinkCable(OverworldFrameSource frameSource) {
        frameSource.LinkedConsoleASource = LinkedCableConsoleA;
        frameSource.LinkedConsoleBSource = LinkedCableConsoleB;
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
                brick.ExitConditionMet = () => RequestWorldReveal(consoleIndex: consoleIndex);
                brick.VictoryConditionMet = () => RequestWorldReveal(consoleIndex: consoleIndex);
            }

            children[1 + index] = brick;
            m_bricks[index] = brick;
            // Carts are inserted live, so a machine's identity is only known at insert time — no build-time choir grouping.
            m_choirLeaders[index] = -1;
        }

        m_metaVictoryWatch = MetaVictoryWatch.Build(consoles: m_consoles);

        return children;
    }

    // The scripted-console control plane's per-frame drain: publishes the node scalars the control host (the frame
    // source) can't see on its own, then applies the node-only requests the verbs queued. boot / player.add / state /
    // step-settle are owned by the frame source directly (it holds m_world + m_director); only reveal, link, and
    // capture need node internals, so only those are drained here — each through the SAME path its debug hook used.
    private void DrainControlRequests() {
        if (m_frameSource is not { } source) {
            return;
        }

        // Publish BOTH ladder rungs so `state` and the reveal guards see live values (the editor latch lives on the
        // frame source itself, so it needs no republish). worldRevealed drives everything that used to mean "is the room
        // visible / can I disengage".
        source.PublishControlSnapshot(immersed: m_immersed, producedFrames: m_producedFrames, worldRevealed: m_worldRevealed);

        // reveal world: the frame source can't reach the reveal handshake, so it flags a request the node routes through
        // the SAME world-reveal path a machine's win (and the debug reveal verb) fired.
        if (source.ConsumePendingReveal() && m_immersed && !m_worldRevealed) {
            RequestWorldReveal(consoleIndex: 0);
        }

        // reveal editor: the dev/agent `reveal editor` verb forces rung 3 (a later arc drives it from meta-victory). Not
        // immersion-gated — the editor unlock is a session state, not a fourth-wall camera moment; the apply is idempotent.
        if (source.ConsumePendingEditorReveal()) {
            RequestEditorReveal();
        }

        // link: validate + connect exactly as HandleLinkVerb does (both consoles booted and at the timeline head, choir
        // dissolved first), logging the outcome to stderr like every other debug link — the machines stay pure
        // functions of (config, consumed stream + cable bits) and the sim's state hash never sees the cable.
        if ((source.ConsumePendingLink() is { } link) && (m_world is { } linkWorld)) {
            ApplyControlLink(world: linkWorld, first: link.First, second: link.Second);
        }

        // capture: arm the producer's one-shot readback on the path the verb queued — the SAME in-flight readback the
        // Right-Shoulder debug capture uses (written THIS produced frame). The frame source created the directory.
        if (source.ConsumePendingCapture() is { } capturePath) {
            m_producer?.RequestCapture(path: capturePath);
        }

        // win: force each queued cabinet's game to its win — the frame source can't reach the bricks/cartridges, so it
        // masks the cabinets and the node writes each one's authored victory bytes into the SRAM region the meta gate
        // reads. The room's REAL XOR poll (MetaVictoryWatch) then counts it and fires the editor reveal when a whole
        // group completes — so a script can drive "complete X games → the workshop opens" without real gameplay input.
        ApplyControlWins(mask: source.ConsumePendingWins());

        // condition.set / condition.clear: a live re-forge of a cabinet's win/reveal gate ("the recursion"). The WHOLE
        // apply — draining the frame source's parsed edit, mutating the brick (its setters re-parse, clear the fired
        // one-shot so a re-edited cabinet may win again, and re-seed a meta share into the running machine), syncing the
        // console-source records (`record with { … }`), REBUILDING the room-level meta watch on a victory edit, and the
        // transient-invalid WARN — lives on MetaVictoryWatch (static). Keeping the whole flow there means this node never
        // names the Scene victory-condition type, staying under its CA1506 coupling ceiling; it just stores the (possibly
        // rebuilt) watch. RE-VALIDATION POLICY: a live edit can leave a group non-XOR-consistent (that check runs only at
        // document LOAD) — ACCEPTED (it just won't fire) and WARNED, never refused; the dev authoring path is never gated.
        StoreMetaWatch(updated: MetaVictoryWatch.ApplyPendingEdit(source: source, bricks: m_bricks, consoles: m_consoles, current: m_metaVictoryWatch));

        // Publish the live per-cabinet condition descriptions so `condition.show` reads them — but ONLY when a condition
        // may have changed (the first drain's boot values, or a victory rebuild), not every produced frame: the per-frame
        // Describe* rebuild was pure Gen0 churn for an on-demand value (see m_conditionSnapshotDirty). Built on
        // MetaVictoryWatch (static) so this node names no extra type.
        PublishConditionSnapshotIfDirty(source: source);
    }

    // Stores the (possibly rebuilt) meta watch a live condition edit produced, re-arming the condition.show snapshot when
    // the watch REBUILT — the signal of an applied VICTORY edit (whose nested description is the costliest to rebuild). An
    // EXIT edit returns the same watch and keeps its cell exact through the frame source's synchronous override, so it
    // needs no republish.
    private void StoreMetaWatch(MetaVictoryWatch? updated) {
        if (!ReferenceEquals(objA: updated, objB: m_metaVictoryWatch)) {
            m_conditionSnapshotDirty = true;
        }

        m_metaVictoryWatch = updated;
    }

    // Publishes the per-cabinet condition snapshot from the live brick truth only when the dirty latch is set: the boot
    // values on the first drain, and a victory-condition rebuild. condition.set/clear already mirror their own cell
    // synchronously on the frame source, so an unedited snapshot needs no per-frame rebuild.
    private void PublishConditionSnapshotIfDirty(OverworldFrameSource source) {
        if (!m_conditionSnapshotDirty) {
            return;
        }

        m_conditionSnapshotDirty = false;
        source.PublishConditionSnapshot(exit: MetaVictoryWatch.DescribeExits(bricks: m_bricks), victory: MetaVictoryWatch.DescribeVictories(bricks: m_bricks));
    }

    // Applies a scripted-console `link i j`: the node half of the control host's queued link (the frame source lacks
    // the bricks + choir bookkeeping). Mirrors HandleLinkVerb's ready-check + dissolve + TryLink, narrating to stderr.
    private void ApplyControlLink(OverworldWorld world, int first, int second) {
        if ((first < 0) || (first >= m_bricks.Length) || (second < 0) || (second >= m_bricks.Length) || (first == second)) {
            Console.Error.WriteLine(value: $"[control] link: bad indices {first},{second}.");

            return;
        }

        if (!LinkReady(world: world, console: first) || !LinkReady(world: world, console: second)) {
            Console.Error.WriteLine(value: $"[control] link: consoles {first}+{second} not linkable — both must be booted and at the shared timeline's head (or player-owned).");

            return;
        }

        DissolveChoirFor(console: first);
        DissolveChoirFor(console: second);

        Console.Error.WriteLine(value: (GamingBrickChildNode.TryLink(first: m_bricks[first], second: m_bricks[second])
            ? $"[control] link: consoles {first}↔{second} connected by the serial cable (console {first} is the primary)."
            : $"[control] link: consoles {first}+{second} refused the cable (already linked, parked, or empty)."));
    }

    // Applies the queued `win` verbs (the node half of the control host's win mask — the frame source can't reach the
    // bricks): for each set cabinet, force its brick to write its authored victory bytes into the SRAM win region
    // (GamingBrickChildNode.ForceVictoryWin) so the room's REAL meta XOR counts it. Extracted so DrainControlRequests
    // stays flat under the node's coupling ceiling; narrates each outcome to stderr like the other control verbs.
    private void ApplyControlWins(int mask) {
        if (mask == 0) {
            return;
        }

        for (var index = 0; (index < m_bricks.Length); index++) {
            if ((mask & (1 << index)) == 0) {
                continue;
            }

            Console.Error.WriteLine(value: (m_bricks[index].ForceVictoryWin() is { } reason
                ? $"[control] win: console {index} could not be won — {reason}."
                : $"[control] win: console {index} — authored victory bytes written to the win region; the room's meta XOR now counts it."));
        }
    }

    // Requests the WORLD reveal (rung 2 — a machine's exit condition or a solo 128-bit win; applied exactly once by the
    // next ProduceFrame). The trigger-console note is a WORLD concern only — the reveal zooms OUT of that machine's
    // screen; first to fire wins. Independent of the editor latch, so both rungs may be requested in one session. (The
    // public control host names the RevealKind enum; the node keeps two rung-specific request paths to stay under its
    // class-coupling ceiling — the enum type never enters the node's type set.)
    private void RequestWorldReveal(int consoleIndex) {
        if (m_revealTriggerConsole < 0) {
            m_revealTriggerConsole = consoleIndex;
        }

        m_worldRevealRequested = true;
    }

    // Requests the EDITOR reveal (rung 3 — the authoring unlock; applied exactly once by the next ProduceFrame). No
    // trigger console: the editor reveal has no camera. Independent of the world latch.
    private void RequestEditorReveal() =>
        m_editorRevealRequested = true;

    // Applies a pending WORLD reveal (rung 2) exactly once: the director eases to the revealed layout (the panes
    // collapse in place while the room grows in; the games play on diegetically on the stands' screens) and the run
    // CONTINUES — every player stays the owner of their machine until they release it with interact, like any takeover.
    private void ApplyWorldRevealIfRequested() {
        if (!m_worldRevealRequested || m_worldRevealed) {
            return;
        }

        m_worldRevealed = true;
        m_director!.SetMode(mode: ScreenLayoutDirectorMode.Revealed);
        // Zoom OUT of the triggering machine's screen (the game the player was inside) toward the iso overview.
        m_director.BeginReveal(triggerPaneIndex: Math.Max(val1: 0, val2: m_revealTriggerConsole));
        // The reveal does NOT free anyone: players stay LOCKED to their machines (engaged since they seated in) — now
        // the room is visible and they can CHOOSE to disengage (Left bumper) into free roam.
        Console.Error.WriteLine(value: "[fourth-wall] the wall breaks — welcome to the overworld. Players stay at their machines until they disengage.");
    }

    // Applies a pending EDITOR reveal (rung 3) exactly once. This stage's outcome is MINIMAL — the state-machine latch
    // and the seam only: it sets the frame source's in-session EditorRevealed unlock and narrates a line DISTINCT from
    // the world reveal's. NO camera choreography, and it gates NO authoring path (creator / Start / console verbs stay
    // always-on regardless). The diegetic workbench that lights up on this unlock is a later stage.
    private void ApplyEditorRevealIfRequested() {
        if (!m_editorRevealRequested || (m_frameSource is not { } source) || source.EditorRevealed) {
            return;
        }

        source.EditorRevealed = true;
        Console.Error.WriteLine(value: "[editor] the workshop is open — you can shape this world.");
    }

    // The room-level META win, polled each frame: the watch XORs each group's cabinet regions and returns the console to
    // reveal once a whole group reaches its shared target (all cabinets present — the "no cabinet wins alone" guarantee).
    private void EvaluateMetaVictory() {
        if (m_metaVictoryWatch is not { } watch) {
            return;
        }

        var trigger = watch.Poll(bricks: m_bricks);

        if (trigger >= 0) {
            // The meta XOR completion is the RUNG-3 hook: a later arc gates the editor reveal on completing X games, and
            // the room-level meta victory is exactly that "complete X games" signal — so the meta win drives the EDITOR
            // reveal. The intro's per-brick exit/solo win keeps driving the WORLD reveal (rung 2) independently.
            RequestEditorReveal();
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

    // The screen mux's dedicated headroom: slots 4..7 (0..3 are cabinets, 3 is also the easel's settled borrow) are
    // GENERIC — reserved for whatever a future caller registers through OverworldFrameSource.RegisterScreenClaimant
    // (no concrete claimant of this node's own exists today). A floating claim's granted slot can vary frame to
    // frame (registrant count changes, ledger degrade), so every headroom index registers a provider that asks the
    // frame source's ResolveDynamicSource/Light/Transform seam what (if anything) currently holds THIS slot — the
    // alternative (keying only the currently-granted index) cannot work since SdfEngineNode's provider dictionary is
    // built once, at construction, with fixed keys.
    private const int ScreenMuxHeadroomStart = 4;

    // One screen-source provider per console (the diegetic-screen seam, prototype-arc item 1): screenIndex ==
    // console index, matching OverworldFrameSource.BuildProgram's declared SdfScreenSurface. A provider returns the
    // brick's native (unresampled) framebuffer view only while the stand is BOTH booted and assigned — an
    // unbooted/unassigned stand's screen slab falls back to the flat/procedural material exactly as before
    // (m_world is captured once; the booted mask is read fresh every poll, which SdfEngineNode does once per frame
    // AFTER children have produced this frame's fresh framebuffer). The ledger arbitrates ownership of slots 0-3
    // (cabinets always win their own index unless the creator easel borrows slot 3 — the SAME settled contract, now
    // expressed as ScreenSlotLedger priorities inside OverworldFrameSource.ResolveScreenMux); slots 4-7 are GENERIC
    // headroom — this node never knows or cares who (if anyone) claims them, it only asks the frame source's
    // ResolveDynamicSource seam what a claimed slot's current source is (see RegisterScreenClaimant's remarks: a
    // future caller wires a new claimant there without ever touching this method).
    //
    // W-SEAM decision: cabinets 0-3 deliberately DON'T route their source/light through RegisterScreenClaimant — the
    // claimant seam is the right shape for a claimant OUTSIDE the frame source, but a cabinet's framebuffer view lives
    // on THIS node (GamingBrickChildNode.NativeImageViewHandle), so moving it onto the claimant seam would only shove
    // the very same Func<nint> closure through a different door (this node still owns the brick, still closes over it,
    // and now also has to register/withdraw a claimant as cabinets boot/eject) — pure relocation, not decoupling. The
    // cabinet already has a clean Anchored ledger CLAIM for slot arbitration; its SOURCE stays a direct provider. Only
    // the headroom slots ride the full claimant seam, because their content genuinely comes from elsewhere.
    private IReadOnlyDictionary<int, Func<nint>>? BuildScreenSources() {
        var world = m_world!;
        var sources = new Dictionary<int, Func<nint>>(capacity: (m_bricks.Length + 5));

        for (var index = 0; (index < m_bricks.Length); index++) {
            var brick = m_bricks[index];
            var consoleIndex = index;

            // A world.wire that routed a camera/named feed onto THIS cabinet's screen wins over the booted brick — the
            // frame source's per-slot override returns the wired feed handle (0 = nothing wired, keep the brick). This
            // is how a creation camera's feed (the fish's lure) shows on a diegetic cabinet slab. Primitive-typed, so
            // this node names no feed type.
            sources[consoleIndex] = () => ((m_frameSource?.ResolveWiredFeedOverride(screenIndex: consoleIndex) is { } wired and not 0)
                ? wired
                : ((world.IsBooted(consoleIndex: consoleIndex) && brick.IsAssigned)
                    ? brick.NativeImageViewHandle
                    : 0));
        }

        // The preview easel's slot: while creator mode is up its slab samples the bake preview INSTEAD of the
        // borrowed cabinet's framebuffer (the frame source suppresses that cabinet's screen slab in the same
        // rebuild, so the surface table and this provider always agree). Present even in the bare room — the easel
        // needs a source with no cabinets at all.
        if (m_frameSource is { } frameSource) {
            var previewIndex = frameSource.CreatorPreviewScreenIndex;
            var cabinetSource = sources.GetValueOrDefault(key: previewIndex);

            sources[previewIndex] = () => (frameSource.CreatorActive
                ? frameSource.CreatorPreviewHandle
                : (cabinetSource?.Invoke() ?? 0));

            // The headroom slots (4..7): generically delegated — 0 (the flat/procedural fallback) when nothing
            // registered a claim on the slot this frame. The AGB debug slot additionally returns the native AGB
            // framebuffer while its fullscreen scene is up (the frame source's pushed primitive state — no AGB type here).
            for (var index = ScreenMuxHeadroomStart; (index < OverworldFrameSource.MaxScreenSurfaceCount); index++) {
                var slot = index;

                sources[slot] = () => ((slot == OverworldFrameSource.AgbDebugScreenSlot) && frameSource.AgbDebugActive)
                    ? frameSource.AgbDebugScreenHandle
                    : frameSource.ResolveDynamicSource(slot: slot);
            }
        }

        return ((sources.Count > 0) ? sources : null);
    }

    // The colored light each booted stand's diegetic screen casts into the room (the CRT glow), PARALLEL to
    // BuildScreenSources: screenIndex == console index, the color is the brick's per-frame framebuffer average. Zero
    // (no light) while the stand is unbooted/unassigned — matching the source provider, so a dark stand neither samples
    // nor lights. The engine gates on the same booted screen mask, so this only ever adds light for a lit screen.
    private IReadOnlyDictionary<int, Func<Vector3>>? BuildScreenLights() {
        var world = m_world!;
        var lights = new Dictionary<int, Func<Vector3>>(capacity: (m_bricks.Length + 1));

        for (var index = 0; (index < m_bricks.Length); index++) {
            var brick = m_bricks[index];
            var consoleIndex = index;

            // Scaled by the director's room-light factor (0 while immersed) so a lit screen casts NO glow into the
            // black immersed letterbox — the bars stay pure black; the glow eases in with the room on the reveal.
            lights[consoleIndex] = () => ((world.IsBooted(consoleIndex: consoleIndex) && brick.IsAssigned)
                ? (brick.AverageColor * (m_director?.RoomLightFactor ?? 1f))
                : Vector3.Zero);
        }

        // The preview easel's glow, parallel to BuildScreenSources' mux: while editing, the workbench glows with
        // the creation's baked average instead of the borrowed cabinet's framebuffer average.
        if (m_frameSource is { } frameSource) {
            var previewIndex = frameSource.CreatorPreviewScreenIndex;
            var cabinetLight = lights.GetValueOrDefault(key: previewIndex);

            lights[previewIndex] = () => (frameSource.CreatorActive
                ? (frameSource.CreatorPreviewLight * (m_director?.RoomLightFactor ?? 1f))
                : (cabinetLight?.Invoke() ?? Vector3.Zero));

            // The headroom slots' glow: generically delegated, scaled by the director's room-light factor exactly
            // like every other light provider. The AGB debug slot returns the native screen's glow while its scene is up.
            for (var index = ScreenMuxHeadroomStart; (index < OverworldFrameSource.MaxScreenSurfaceCount); index++) {
                var slot = index;

                lights[slot] = () => (((slot == OverworldFrameSource.AgbDebugScreenSlot) && frameSource.AgbDebugActive)
                    ? frameSource.AgbDebugScreenGlow
                    : frameSource.ResolveDynamicLight(slot: slot)) * (m_director?.RoomLightFactor ?? 1f);
            }
        }

        return ((lights.Count > 0) ? lights : null);
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
            } else if (Puck.Demo.Forge.ForgeCommands.IsForgedCartType(cartType: wantType) && ((m_forgeCartsAttemptedMask & (1 << wantType)) == 0)) {
                // A FORGED type (avatar/tune/scene) is baked LAZILY the first time a cabinet wants it — flag the next
                // ProduceFrame (which has the live GPU device) to bake its default; the cabinet stays dark for that
                // frame. Guarded by the attempted mask so a FAILED default is not re-forged every frame (see the field).
                m_forgeCartsNeedMask |= (1 << wantType);
            }
            // else: this cart type could not be sourced on this machine (e.g. no showcase ROM) — leave the cabinet dark.
        }
    }

    // Forces every cabinet currently running the given FORGED cart type to reassemble on the next AssignPendingCartridges
    // pass (a sentinel that never equals a real want type), so a freshly re-forged ROM swaps in live after a commit —
    // the subject-neutral generalization of the old avatar-only reload (any subject: avatar/tune/scene).
    private void MarkForgedCabinetsForReload(int cartType) {
        for (var index = 0; (index < m_consoleAssembledType.Length); index++) {
            if (m_consoleAssembledType[index] == cartType) {
                m_consoleAssembledType[index] = -2;
            }
        }
    }

    // The peripheral feed a cart TYPE binds: type 0 = the world-lens sensor page (world→machine membrane), type 1 = the
    // Pocket Camera webcam. Other types are self-contained ROMs with no host feed.
    private static string? CartPeripheral(int cartType) =>
        cartType switch {
            0 => "world",
            1 => "camera",
            _ => null, // showcase (2), avatar (3), the framework games (4-8), the tune (9) + scene (10) are self-contained ROMs
        };

    // Builds the world-lens cart (type 0). The two-worlds romance: SDF-FORGE the room background on the overworld's own GPU
    // so the very room the player walks becomes the tiles their brick renders. Falls back to the CPU-authored grid
    // room if the GPU/compute services are unavailable or the forge throws (e.g. too many unique tiles), so the cabinet
    // always has a cart. The forge is a one-shot render + readback, run once at startup.
    private byte[] BuildWorldLensCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildWorldLensCart(context: in context, services: m_serviceProvider);

    // Builds the three framework-game carts (types 4/5/6): each title screen SDF-bakes on the live GPU when
    // available, and the hand-authored banner ships otherwise (ForgeCommands narrates which) — kept off
    // EnsureResources for the same coupling-budget reason as the world-lens cart above.
    private byte[] BuildVolleyCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildVolleyCart(context: in context, services: m_serviceProvider);

    private byte[] BuildBrickfallCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildBrickfallCart(context: in context, services: m_serviceProvider);

    private byte[] BuildChromaCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildChromaCart(context: in context, services: m_serviceProvider);

    private byte[] BuildSolitaireCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildSolitaireCart(context: in context, services: m_serviceProvider);

    private static string PrepareSolitaireSavePath() =>
        Puck.Demo.Forge.ForgeCommands.PrepareSolitaireSavePath();

    private byte[] BuildPokerCartRom(in FrameContext context) =>
        Puck.Demo.Forge.ForgeCommands.BuildPokerCart(context: in context, services: m_serviceProvider);

    private static string PreparePokerSavePath() =>
        Puck.Demo.Forge.ForgeCommands.PreparePokerSavePath();

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

        if (pages.WasPressed(command: DemoActionCommandModule.BrickLinkToggleCommand)) {
            HandleLinkVerb(world: world, slot: slot);
        }

        if (pages.WasPressed(command: DemoActionCommandModule.BrickStateHashCommand)) {
            Console.Error.WriteLine(value: $"[debug] tick {world.CurrentTick}: state hash 0x{world.StateHash():X16}");
        }

        if (pages.WasPressed(command: DemoActionCommandModule.BrickFleetStatusCommand)) {
            for (var index = 0; (index < m_bricks.Length); index++) {
                var brick = m_bricks[index];
                var leader = ((m_choirLeaders[index] >= 0) ? $" (leader {m_choirLeaders[index]})" : "");

                var presenting = ((brick.PresentationModel == brick.BootModel) ? "" : $", presenting {brick.PresentationModel}");
                var linked = (brick.IsLinked ? $", linked to {brick.LinkPartnerOrdinal}{(brick.IsLinkPrimary ? " (primary)" : "")}" : "");

                Console.Error.WriteLine(value: $"[debug] console {index}: {(world.IsBooted(consoleIndex: index) ? "booted" : "dark")}, boots as {brick.BootModel}{presenting}, speed {(brick.IsSpeedPinned ? "pinned" : "hardware")}, choir {m_choirStates[index]}{leader}{linked}, cursor {(m_timeline!.IsAtHead(consoleIndex: index) ? "at head" : "behind")}");
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

    // The serial link cable verb (Bricks page): press near a booted console to mark it as the pending link end; a
    // second press near a DIFFERENT booted console connects the two as a linked pair; near a LINKED console it
    // unlinks; near the pending console again it cancels. Host-side wiring exactly like the other debug verbs — the
    // machines stay pure functions of (configuration, consumed stream + cable bits) and the sim's state hash never
    // sees any of it; the link/unlink lands between frames (render-thread verb dispatch, never mid-step), so every
    // exchanged bit is part of the deterministic step that follows.
    private void HandleLinkVerb(OverworldWorld world, int slot) {
        var target = world.NearestConsoleForSlot(slot: slot, booted: true);

        if (target < 0) {
            Console.Error.WriteLine(value: "[debug] link: no booted console in range.");

            return;
        }

        if (m_bricks[target].IsLinked) {
            var partner = m_bricks[target].LinkPartnerOrdinal;

            GamingBrickChildNode.Unlink(node: m_bricks[target]);
            Console.Error.WriteLine(value: $"[debug] link: console {target} UNLINKED from console {partner} — both step independently again.");

            return;
        }

        if (m_pendingLinkConsole == target) {
            m_pendingLinkConsole = -1;
            Console.Error.WriteLine(value: $"[debug] link: console {target} pending cable canceled.");

            return;
        }

        if (m_pendingLinkConsole < 0) {
            m_pendingLinkConsole = target;
            Console.Error.WriteLine(value: $"[debug] link: console {target} holds one end of the cable — press Link near another booted console to connect.");

            return;
        }

        var pending = m_pendingLinkConsole;

        m_pendingLinkConsole = -1;

        if (!LinkReady(world: world, console: pending) || !LinkReady(world: world, console: target)) {
            Console.Error.WriteLine(value: $"[debug] link: consoles {pending}+{target} not linkable — both must be booted and at the shared timeline's head (or player-owned).");

            return;
        }

        // A linked machine's serial state genuinely diverges from any choir twin — dissolve first, exactly like the
        // speed pin.
        DissolveChoirFor(console: pending);
        DissolveChoirFor(console: target);

        if (GamingBrickChildNode.TryLink(first: m_bricks[pending], second: m_bricks[target])) {
            Console.Error.WriteLine(value: $"[debug] link: consoles {pending}↔{target} connected by the serial cable — the pair now steps in lockstep (console {pending} is the primary).");
        } else {
            Console.Error.WriteLine(value: $"[debug] link: consoles {pending}+{target} refused the cable (already linked, parked, or empty).");
        }
    }

    // A console can take one end of the cable when it is booted with an assembled machine and consumes exactly one
    // input segment per frame: at the shared timeline's head, or pad-sampled (a takeover console whose SegmentSource
    // cleared). A late-booted, still fast-forwarding console must converge first — a linked pair steps ONE shared
    // budget per frame, so neither end may be draining history.
    private bool LinkReady(OverworldWorld world, int console) {
        var brick = m_bricks[console];

        return (world.IsBooted(consoleIndex: console)
            && brick.IsAssigned
            && ((brick.SegmentSource is null) || m_timeline!.IsAtHead(consoleIndex: console)));
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
        // The active roster is the greater of the connected-pad count (0 with no gamepad manager — a headless scripted
        // run) and the permanent room player plus any SCRIPTED players the `player.add` control verb requested (tracked
        // on the frame source, the control host). A scripted player joins even with no pad AND no manager, and is not
        // evicted below — so a piped script can add players on a CI machine with no controller.
        var scriptedPlayers = (m_frameSource?.ScriptedPlayerCount ?? 0);
        var connectedPads = (m_gamepadManager?.ConnectedDevices().Count ?? 0);
        var padCount = Math.Clamp(value: Math.Max(val1: connectedPads, val2: (1 + scriptedPlayers)), min: 1, max: OverworldWorld.MaxPlayers);

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
                "dmg" => ConsoleAccentPalette.Dmg,
                "agb" => ConsoleAccentPalette.Agb,
                _ => ConsoleAccentPalette.Cgb,
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
        // held state carries across the rest — the same frame-sampled discipline IPlayerIntentSource documents. Reused
        // across frames and cleared to default(PlayerIntent) here (exactly what a fresh new[] gave), so an inactive or
        // unfilled slot reads back None before the samplers below overwrite the active ones.
        var firstTickIntents = m_firstTickIntents;
        var heldIntents = m_heldIntents;

        Array.Clear(array: firstTickIntents);
        Array.Clear(array: heldIntents);
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
        if (m_immersed && !m_worldRevealed) {
            ApplyImmersedSeating(world: world);
        }

        // Mirror the room player's movement into the brick joypad image FIRST (directions + A on jump), so the
        // segment recorded below — and any other consumer this frame — already carries it; non-directional buttons
        // still pass through from the first pad, so game menus stay navigable.
        m_padService?.PublishMirror(mirror: MirrorOf(move: roomMove, jumpHeld: roomJumpHeld));

        // A committed world (deliberate world.save / world.load) hot-applies HERE, on the tick boundary before the
        // first Advance of the frame — the frame source performs the whole application (sim collision + walk grid +
        // movement lock + its own rendered walls, together) and hands back only the narration, keeping this node's
        // coupling flat. Save = make-it-real: the world you just saved is the world you are standing in.
        if ((m_frameSource?.ConsumePendingWorldLoad()) is { } worldNarration) {
            Console.Error.WriteLine(value: worldNarration);
        }

        for (var index = 0; (index < tickCount); index++) {
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
            } else if (m_consoleOwner[console] >= 0) {
                // The press ejected an OWNED cabinet — and the presser need NOT be the owner: a non-owner's interact still
                // reaches the sim and un-boots it. Release the ACTUAL owner (not just the pressing slot) so they rejoin
                // the room instead of being wedged — frozen (owned => PlayerIntent.None), pad routed to a now-dark brick.
                ReleaseConsole(slot: m_consoleOwner[console]);
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

            // A parked follower's machine does not consume (its leader steps for it), and an OWNED machine
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

        // Reused across frames (the cached bar callbacks read m_contextIcons / m_activeSlots): clear before this frame
        // refills them — an inactive or creator-takeover slot never writes its cell, so it must read back empty exactly
        // as a fresh new List / new[] did.
        var activeSlots = m_activeSlots;
        var contextIcons = m_contextIcons;
        // The creating slot's live button mask (creator mode only) — feeds the creator bar's pressed highlights.
        var creatorButtons = default(GamepadButtons);

        activeSlots.Clear();
        Array.Clear(array: contextIcons);

        for (var slot = 0; (slot < OverworldWorld.MaxPlayers); slot++) {
            if (world.Slots[slot] is null) {
                continue;
            }

            var pages = (m_pageInputs[slot] ??= new OverworldPageInput(bindings: bindings, slot: slot));
            var raw = pads.SamplePlayerRaw(playerIndex: slot, frameKey: context.RenderTicks);

            pages.BeginFrame(state: in raw);
            pads.SetPlayerBrickInputEnabled(playerIndex: slot, enabled: pages.AllowsBrickInput);

            // START (slot 0) drives the create→commit→play loop and hosts the three authoring takeovers — extracted
            // into AdvanceCreatingSlotTakeovers so this loop's cyclomatic weight stays under the analyzer ceiling.
            if ((slot == 0) && AdvanceCreatingSlotTakeovers(context: in context, creatorButtons: ref creatorButtons, firstTickIntents: firstTickIntents, heldIntents: heldIntents, pads: pads, raw: in raw, slot: slot)) {
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
            // sim; the West context button also inserts when standing at a cabinet, else it is the hold-to-run.
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

            if (interactPressed && nearCabinet && (!m_worldLens || m_worldRevealed)) {
                pendingTakeover[slot] = nearestCabinet;
            }

            // THE GATED WORKBENCH ENTRY (Stage 3): the DIEGETIC door into world-sculpt. Extracted into a helper (which
            // self-guards to the creating slot) so this loop's cyclomatic weight stays under the CA1502 ceiling
            // (AdvancePagedSlots sits AT it) — the call site adds no branch of its own.
            TryEnterWorkbench(world: world, slot: slot, interactPressed: interactPressed, nearCabinet: nearCabinet);

            // The dedicated disengage (Left bumper): a seated player leaves their console and rejoins free room
            // movement. Held off while immersed and not yet revealed (that tiling has no room to walk into yet).
            if (pages.WasPressed(command: OverworldInput.LeaveCommand) && (m_slotConsole[slot] >= 0) && (m_worldRevealed || !m_immersed)) {
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
        // active verb PAGE's authoring verbs); while WORLD-SCULPT is up, the world bar (same chord model, the
        // sculptor's own pages); otherwise one 12-slot bar per active player, each joined against ITS page state
        // and context.
        if (m_frameSource is { CreatorActive: true } creatorSource) {
            m_bindingBarAdapter?.PublishCreator(heldButtons: creatorButtons, page: creatorSource.CreatorBarPage);
        } else if (m_frameSource is { WorldSculptActive: true } worldSource) {
            m_bindingBarAdapter?.PublishWorld(heldButtons: worldSource.WorldSculptHeldButtons, page: worldSource.WorldSculptBarPage);
        } else {
            m_bindingBarAdapter?.Publish(
                activeSlots: activeSlots,
                contextIconForSlot: m_contextIconForSlot,
                isHeldForSlot: m_isHeldForSlot
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
        if (!m_worldRevealed) {
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

    // The diegetic link cable's live pair reader (OverworldFrameSource.LinkedPairSource): the FIRST currently-linked
    // pair among this node's own bricks (host-side ownership bookkeeping — the sim's state hash never sees it), found
    // once and cached for both A/B readers this frame — a linked pair is reported ordinal-ascending (A < B) so the two
    // int readers agree on which end is which. -1/-1 (no cached pair) means none is linked. A pair goes live when the
    // `link <i> <j>` console verb connects two cabinets (or an in-game Bricks-page press).
    private int m_linkedCableA = -1;
    private int m_linkedCableB = -1;
    private int m_linkedCableCacheFrame = -1;

    private void RefreshLinkedCablePair() {
        if (m_linkedCableCacheFrame == m_producedFrames) {
            return; // already resolved this produced frame.
        }

        m_linkedCableCacheFrame = m_producedFrames;
        m_linkedCableA = -1;
        m_linkedCableB = -1;

        for (var index = 0; (index < m_bricks.Length); index++) {
            if (m_bricks[index].IsLinked && (m_bricks[index].LinkPartnerOrdinal > index)) {
                m_linkedCableA = index;
                m_linkedCableB = m_bricks[index].LinkPartnerOrdinal;

                return;
            }
        }
    }
    private int LinkedCableConsoleA() {
        RefreshLinkedCablePair();

        return m_linkedCableA;
    }
    private int LinkedCableConsoleB() {
        RefreshLinkedCablePair();

        return m_linkedCableB;
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
        // decoupled machines are not under the SDF producer, so forward to them too; their CPU emulation state
        // survives, only the GPU-side resources rebuild.
        m_root?.OnDeviceLost();

        foreach (var brick in m_bricks) {
            brick.OnDeviceLost();
        }

        (m_agbService ?? AgbService())?.OnDeviceLost();
    }

    /// <inheritdoc/>
    public void Dispose() {
        // The bake preview's persistent rasterizer/upload live on the frame source; tear them down with the node.
        m_frameSource?.DisposeBakePreview();
        // The diegetic-feed director's GPU resources (the face-feed upload + the camera-feed pool's engines) live on
        // the frame source too — tear them down with the node so no feed engine leaks past the device.
        m_frameSource?.DisposeFeeds();
        // The tracker preview's headless machine + host audio stream (a leak of either is a silent, unkillable
        // background process) — behind ForgeCommands so this node stays coupling-flat.
        Puck.Demo.Forge.ForgeCommands.TrackerDispose();
        // The overlay's Dispose forwards to the wrapped producer; when the overlay is absent the root IS the producer.
        m_root?.Dispose();

        // The machines are not compositor children, so THIS node owns their disposal (their GPU resources +
        // battery-save flush) rather than the SDF engine.
        foreach (var brick in m_bricks) {
            brick.Dispose();
        }

        // The native AGB scene's machine + framebuffer upload (a DI singleton, but its GPU upload must fall before the
        // device is destroyed, exactly like the bricks above).
        (m_agbService ?? AgbService())?.Shutdown();
    }
}

/// <summary>The host seam the demo's <c>creator</c>/<c>tracker</c> console commands drive: it flips the in-engine SDF
/// authoring mode and the in-engine music tracker on the live overworld root. Presentation/authoring only — neither
/// mode's state ever touches the deterministic sim. Every tracker member below is PRIMITIVE-typed (bool/string) on
/// purpose: <see cref="Overworld.OverworldRenderNode"/> sits AT its analyzer coupling ceiling, so the tracker's own
/// types (<c>Tracker.TrackerModeState</c> and friends) must never appear in its signature — they live behind
/// <see cref="Forge.ForgeCommands"/>'s static forwarders instead (the same facade pattern the Solitaire/Poker cart
/// wiring already uses to stay under the same budget).</summary>
internal interface ICreatorModeHost {
    /// <summary>Whether creator mode is currently active.</summary>
    bool CreatorModeActive { get; }

    /// <summary>The live overworld frame source (the creator objects' composition point), when the root is ready —
    /// the <c>creator.*</c> console verbs reach the authored scene through it.</summary>
    OverworldFrameSource? CreatorFrameSource { get; }

    /// <summary>Toggles creator mode and returns the new state (false if the root is not yet ready).</summary>
    bool ToggleCreatorMode();

    /// <summary>Queues a FORGE of the current creator creation into a cart on the next frame (the live GPU device is
    /// there). The <paramref name="subject"/> word chooses which cart the SAME creation forges: <c>avatar</c> (default,
    /// back-compat) — the walker overworld <c>.gbc</c> written to disk under <c>./forged-avatars</c>; <c>scene</c> — the
    /// SDF-art creature cart forged + hot-swapped into the nearest cabinet in-session. Returns a status line.</summary>
    /// <param name="subject">The forge subject: <c>avatar</c> (default) or <c>scene</c>.</param>
    string RequestCreatorForge(string subject);

    /// <summary>Queues a FORGE of the tracker's working tune into a JUKEBOX cart on the next frame, then installs it into
    /// the nearest cabinet (the tune subject's in-session hot-swap — GPU-free). Returns a status line.</summary>
    string RequestTuneForge();

    /// <summary>Whether tracker mode is currently active.</summary>
    bool TrackerModeActive { get; }

    /// <summary>Toggles world-sculpt mode (the creating slot's third takeover — mutually exclusive with the creator
    /// and tracker modes) and returns the narration line. The <c>world.*</c> verbs reach the sculpted scene through
    /// <see cref="CreatorFrameSource"/>, every authoring surface's composition point.</summary>
    string ToggleWorldSculptMode();

    /// <summary>Toggles tracker mode and returns the new state.</summary>
    bool ToggleTrackerMode();

    /// <summary>Whether the fullscreen SDF-debug mode is currently active.</summary>
    bool SdfDebugModeActive { get; }

    /// <summary>Toggles the fullscreen SDF-debug mode (the single-shape debug tool; mutually exclusive with
    /// creator/world-sculpt/tracker) and returns the narration line. The <c>sdf.*</c> verbs reach the debug scene
    /// through <see cref="CreatorFrameSource"/>, every authoring surface's composition point.</summary>
    string ToggleSdfDebugMode();

    /// <summary>Reads the previous frame's per-pass GPU times for <c>sdf.info</c> — a passthrough of the producer's
    /// <c>SdfEngineNode.TryReadPassTimings</c> that fills <paramref name="passMilliseconds"/> (one entry per
    /// <c>SdfWorldEngine.PassTimingLabels</c>, in order) and the whole-frame span, so the node names no engine type it
    /// does not already. False when the producer is absent or timing is off (<c>PUCK_TIMING=1</c> / the spec Timing flag).</summary>
    /// <param name="passMilliseconds">Receives each render pass's milliseconds; size it to <c>SdfWorldEngine.PassTimingCount</c>.</param>
    /// <param name="passCount">The number of pass entries written (0 when unavailable).</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    bool TryReadSdfPassTimings(Span<double> passMilliseconds, out int passCount, out double frame);

    /// <summary>Starts or stops the headless preview of the working tune. Returns a status line for the console.</summary>
    /// <param name="play"><see langword="true"/> to (re)start the preview, <see langword="false"/> to stop it.</param>
    string RequestTrackerPreview(bool play);

    /// <summary>Whether the fullscreen native AGB (ARM7TDMI) debug scene is currently active.</summary>
    bool AgbDebugModeActive { get; }

    /// <summary>Toggles the fullscreen native AGB debug scene (the FIFTH creating-slot takeover, mutually exclusive with
    /// creator/world-sculpt/tracker/sdf-debug) and returns the narration. On enter it boots <paramref name="romPath"/>
    /// (or the pending native/<c>--rom</c> cartridge, else a built-in micro-ROM) through the DI-held
    /// <c>AgbDebugService</c>; the <c>agb.*</c> execution-control verbs drive that service directly.</summary>
    /// <param name="romPath">An optional explicit cartridge ROM path.</param>
    string ToggleAgbDebugMode(string? romPath);
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
