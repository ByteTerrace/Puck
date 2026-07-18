using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SdfVm.Queries;

namespace Puck.Demo.Overworld;

/// <summary>
/// The movement direction lock a loaded world can impose — sim CONFIG (like the collision surfaces), applied on a
/// tick boundary and NEVER folded into <see cref="OverworldWorld.StateHash"/>. When locked, each occupied slot's
/// intent move vector is quantized onto the mode's direction set before the body steps: the direction with the
/// maximum raw dot product wins (ties break to the FIRST direction in the mode's documented enumeration order), and
/// the quantized move is that unit direction scaled by the projection of the intent onto it — analog magnitude is
/// preserved along the locked axis, no square roots anywhere. <see cref="Free"/> is today's untouched analog path.
/// </summary>
public enum MovementLock {
    /// <summary>No lock — the intent's analog move passes through untouched (the wire default).</summary>
    Free,
    /// <summary>Four cardinal directions. Enumeration (tie-break) order: +X, −X, +Y, −Y.</summary>
    Four,
    /// <summary>Four cardinals plus four diagonals. Enumeration (tie-break) order: +X, −X, +Y, −Y, then
    /// (+,+), (+,−), (−,+), (−,−) — a cardinal beats a diagonal on an exact tie.</summary>
    Eight,
    /// <summary>Six pointy-top hex directions (matching the hex walk grid): pure E/W plus four 60° diagonals, no
    /// vertical neighbor. Enumeration (tie-break) order: +X, −X, then (+½,+v), (+½,−v), (−½,+v), (−½,−v)
    /// where v = √3⁄2 — pure E/W beats a diagonal on an exact tie.</summary>
    Hex,
}

/// <summary>
/// The authoritative, DETERMINISTIC simulation state: a fixed array of player slots (a free-list, so a player's slot —
/// its dynamic-transform slot — stays stable while active and recycles when it leaves), a seeded PRNG, and the tick
/// counter. <see cref="Advance"/> is a pure function of the previous state plus the per-slot intents for one tick, so
/// the same intent + roster-event stream always produces the same state — the basis for bit-identical replays. The
/// render side reads <see cref="DynamicTransforms"/> and <see cref="Slots"/> (presentation), never the other way around.
/// <para>
/// The slot count is FIXED at <see cref="MaxPlayers"/> from the first frame: <see cref="DynamicTransforms"/> always
/// returns that many entries (free slots ride a hidden position) and the screen director always emits its fixed
/// <see cref="ScreenLayoutDirector.ViewCount"/> views, so the world compositor's first-frame buffer/viewport sizes never
/// change as players join and leave.
/// </para>
/// </summary>
public sealed class OverworldWorld {
    /// <summary>One planted garden: a seed and the tick it was planted, plus the room-local X/Z it was planted at —
    /// the WHOLE of a garden's sim state (see <see cref="PlantGarden"/>). Deliberately NOT the tree geometry itself:
    /// growth is a pure function of <c>(Seed, CurrentTick − PlantedTick)</c>, recomputed fresh every frame by
    /// <c>Puck.Demo.Garden.GardenTreeGenerator</c>/<c>GardenRenderer</c> — never sim state, so replaying the SAME
    /// plant/tick stream always regrows the SAME tree.</summary>
    public readonly record struct GardenPlant(uint Seed, ulong PlantedTick, FixedQ4816 LocalX, FixedQ4816 LocalZ);

    /// <summary>The maximum concurrent players — also the fixed dynamic-transform slot count for player boxes. Matches
    /// the room's console capacity (one pane per player under the compositor's room-plus-panes viewport budget);
    /// growing past it is a later phase.</summary>
    public const int MaxPlayers = 4;

    /// <summary>The number of cartridge TYPES a cabinet can hold (world-lens / camera / showcase / AVATAR / VOLLEY / BRICKFALL
    /// / CHROMA / SOLITAIRE / POKER / JUKEBOX / SCENE / ORACLE / CRITTER-SWAP), cycled at the cabinet. Types 4–8 are the
    /// five five-star framework games (genuine SM83 ROMs); type 11 (ORACLE) is a sixth, spare framework game (a text-only
    /// fortune cart, eagerly sourced like the other games); type 12 (CRITTER-SWAP) is a link-trading toy (a genuine SM83
    /// cart with battery-backed SRAM whose whole point is the link cable — two cabinets Cycled to it and linked swap their
    /// held critters). Types 3 (AVATAR walker), 9 (JUKEBOX tune) and 10 (SDF-ART SCENE creature) are the three in-session
    /// FORGED subjects (the create/author→forge→hot-swap loop — see <see cref="Forge.ForgeSubject"/>); each is baked LAZILY
    /// the first time a cabinet wants it, so a forged type is Cycle-reachable but never a boot default. Purely a count; the
    /// sim never learns what bytes a type maps to (that is host-side, in the render node's ROM table).</summary>
    public const int CartTypeCount = 13;

    /// <summary>The SHOWCASE cart type — the pre-inserted <c>--rom</c> cartridge's slot in the host ROM table. It is the
    /// LOADED default for an immersed non-world-lens (<c>--rom</c>) boot: a plain, always-sourced ROM (never a forged
    /// type that would need a bake before one exists), so bumping <see cref="CartTypeCount"/> can never make a lazily-
    /// forged type a cabinet's boot default. Host-side mapping only; the sim just carries the index.</summary>
    public const int ShowcaseCartType = 2;

    /// <summary>The maximum concurrently planted gardens (the deterministic-garden feature) — a fixed capacity pool,
    /// exactly like <see cref="MaxPlayers"/>/the console arrays, so the presentation side's capacity probe (see
    /// <c>Puck.Demo.Garden.GardenRenderer</c>) can freeze a worst-case envelope from it.</summary>
    public const int MaxGardens = 6;

    /// <summary>One RTS unit's fully fixed-point simulation state. Like every other body
    /// here. Unlike <see cref="GardenPlant"/> (write-once, never mutated after planting) a unit's fields change
    /// every tick it has a live order, so occupancy is an explicit <see cref="Active"/> flag on a plain struct array
    /// rather than a nullable-entry pool.</summary>
    public readonly record struct RtsUnit(bool Active, bool Selected, bool HasTarget, FixedQ4816 X, FixedQ4816 Y, FixedQ4816 Z, FixedQ4816 TargetX, FixedQ4816 TargetZ);

    /// <summary>A read-only snapshot of the gravity scenario's <see cref="FieldWalkerBody"/> — the
    /// presentation/console-facing echo of its state, mirroring <see cref="RtsUnit"/>'s role for the RTS pool but for
    /// a SINGLE body rather than a pool (the proof is one avatar circumnavigating one planetoid, not many). Default
    /// (<c>Active</c> false) when no walker has been spawned yet.</summary>
    public readonly record struct FieldWalkerSnapshot(bool Active, WorldCoord3 Position, FixedVector3 Velocity, FixedVector3 Up, FixedQ4816 FacingAngle, bool Grounded);

    /// <summary>The outcome of <see cref="MovePlayer"/> — distinguishes "no such slot" from "refused, blocked" from
    /// "moved", so the console verb's echo is exact.</summary>
    public enum PlayerMoveResult {
        /// <summary>The slot index is out of range.</summary>
        SlotOutOfRange,
        /// <summary>The slot is empty — no player occupies it.</summary>
        SlotEmpty,
        /// <summary>The destination is blocked (a console/shelf keep-out or a baked walk-grid cell); the slot's body
        /// is left untouched.</summary>
        Blocked,
        /// <summary>The slot's body now sits at the requested destination.</summary>
        Moved,
    }

    /// <summary>The maximum concurrent RTS units — a fixed capacity pool, exactly like <see cref="MaxGardens"/>, so
    /// the presentation side's capacity probe (<c>Puck.Demo.Rts.RtsUnitInstanceEmitter</c>) can freeze a worst-case
    /// envelope from it.</summary>
    public const int MaxRtsUnits = 12;

    // Straight-line move speed (world units/sec) and the ground-height probe half-range (world units) either side of
    // a unit's current Y — generous enough for a flat arena with a couple of raised terrain patches. A spawn is
    // rejected only when it overlaps BLOCKED geometry within this radius (never the ground itself).
    private static readonly FixedQ4816 RtsUnitSpeedRaw = FixedQ4816.FromDouble(value: 2.5);
    private static readonly FixedQ4816 RtsGroundProbeRaw = FixedQ4816.FromDouble(value: 4.0);
    private static readonly FixedQ4816 RtsUnitSpawnRadiusRaw = FixedQ4816.FromDouble(value: 0.35);

    // The movement direction sets, as compile-time raw Q48.16 unit components — each irrational rounded ONCE, here,
    // and documented as a settled contract fact: One = 65536 (1.0, exact), Half = 32768 (0.5, exact),
    // InvSqrt2 = 46341 (1/√2 = 0.7071067811… × 2^16 = 46340.95… → 46341), HexVertical = 56756
    // (√3/2 = 0.8660254038… × 2^16 = 56755.84… → 56756).
    private const long DirectionOneRaw = 65536L;
    private const long DirectionHalfRaw = 32768L;
    private const long DirectionHexVerticalRaw = 56756L;
    private const long DirectionInvSqrt2Raw = 46341L;

    // The per-mode direction sets, in their DOCUMENTED enumeration order (see MovementLock) — the order IS the
    // deterministic tie-break: the selection loop keeps the first-seen maximum.
    private static readonly (long X, long Y)[] FourDirections = [
        (DirectionOneRaw, 0L), (-DirectionOneRaw, 0L), (0L, DirectionOneRaw), (0L, -DirectionOneRaw),
    ];
    private static readonly (long X, long Y)[] EightDirections = [
        (DirectionOneRaw, 0L), (-DirectionOneRaw, 0L), (0L, DirectionOneRaw), (0L, -DirectionOneRaw),
        (DirectionInvSqrt2Raw, DirectionInvSqrt2Raw), (DirectionInvSqrt2Raw, -DirectionInvSqrt2Raw),
        (-DirectionInvSqrt2Raw, DirectionInvSqrt2Raw), (-DirectionInvSqrt2Raw, -DirectionInvSqrt2Raw),
    ];
    private static readonly (long X, long Y)[] HexDirections = [
        (DirectionOneRaw, 0L), (-DirectionOneRaw, 0L),
        (DirectionHalfRaw, DirectionHexVerticalRaw), (DirectionHalfRaw, -DirectionHexVerticalRaw),
        (-DirectionHalfRaw, DirectionHexVerticalRaw), (-DirectionHalfRaw, -DirectionHexVerticalRaw),
    ];

    // Mutable (not readonly): LoadWorld replaces the whole collision surface wholesale, on a tick boundary, when a
    // world document loads — the ONE legal authoring→sim seam (see LoadWorld's remarks).
    private FixedRoom m_collision;
    // The movement direction lock — sim CONFIG like m_collision (tick-boundary application, never hashed). Free by
    // default, so every pre-existing run takes today's exact analog path.
    private MovementLock m_movementLock;
    private readonly PlatformerTuning m_tuning;
    private readonly FixedQ4816 m_dt;
    private readonly PlayerSlot?[] m_slots = new PlayerSlot?[MaxPlayers];
    // Reused per-frame dynamic-transform buffer — refilled (never reallocated) each DynamicTransforms() call so a
    // high-rate VRR present loop allocates nothing here. PRESENTATION ONLY.
    private readonly DynamicTransform[] m_dynamicTransforms = new DynamicTransform[MaxPlayers];
    private readonly Dictionary<Guid, int> m_slotByPlayer = [];
    // Whether the per-slot spawn seats slot i in front of console stand i (the immersed start) instead of the default
    // grid — pure construction-time configuration, folded into positions the hash already covers.
    private readonly bool m_spawnAtConsoles;
    // The cell every body and the room live in — the origin cell by default; a far cell places the whole room
    // astronomically far from the world origin to exercise the planet-scale coordinate path.
    private readonly long m_spawnCellX;
    private readonly long m_spawnCellY;
    private readonly long m_spawnCellZ;
    // Free slots park their box far below the floor (in the spawn cell), outside every camera frustum, so a fixed count
    // of boxes can be built into the program once and the inactive ones simply march against nothing visible.
    private readonly WorldCoord3 m_hiddenPosition;
    private ulong m_tick;
    private uint m_rng;
    // The console boot state: a bit per console index, plus the order the consoles booted in (the pane-assignment
    // order the presentation reads). Both are DETERMINISTIC state — folded into the hash.
    private uint m_bootedMask;
    private readonly List<int> m_bootOrder = [];

    // The cabinet cartridge state. Each cabinet holds one cart TYPE at a time — no shelf, no carrying: the choice lives
    // at the cabinet. m_consoleLoadedType is the type currently RUNNING (0..CartTypeCount-1) or -1 (empty); a loaded
    // cabinet is a booted one (insert boots, eject un-boots), so m_bootedMask/m_bootOrder ARE the running-cabinet state.
    // m_consoleSelectedType is what North will insert next, advanced by the cycle button at the cabinet.
    private readonly int[] m_consoleLoadedType;
    private readonly int[] m_consoleSelectedType;

    // The planted-garden pool (null == empty slot) — a fixed capacity, like the console arrays above. Deterministic
    // state (see PlantGarden/HashState): a slot's Seed/PlantedTick/position never change once planted, only the
    // GROWN GEOMETRY the presentation side derives from them (never stored, never hashed).
    private readonly GardenPlant?[] m_gardens = new GardenPlant?[MaxGardens];

    // The RTS scenario's fixed-capacity unit pool, like the garden pool above.
    // m_rtsQuery is sim CONFIG (like m_collision — see ConfigureRtsQuery), never folded into the hash.
    private readonly RtsUnit[] m_rtsUnits = new RtsUnit[MaxRtsUnits];
    private IWorldQuery? m_rtsQuery;

    // The gravity scenario's field walker — null until planet.spawn seats it (mirrors the garden
    // pool's null-slot convention, just for a singular body rather than a fixed array). m_fieldEvaluator is sim
    // CONFIG (like m_rtsQuery — see ConfigureFieldEvaluator), never folded into the hash. The queued-walk fields
    // (m_fieldWalkerWalkTicks/m_fieldWalkerWalkMove) ARE real sim state (like an RTS unit's HasTarget/TargetX/Z move
    // order) — see AdvanceFieldWalker/HashState.
    private FieldWalkerBody? m_fieldWalker;
    private IFieldEvaluator? m_fieldEvaluator;
    private readonly FieldWalkerTuning m_fieldWalkerTuning = FieldWalkerTuning.Default;
    private int m_fieldWalkerWalkTicks;
    private FixedVector2 m_fieldWalkerWalkMove = new(X: FixedQ4816.Zero, Y: FixedQ4816.One); // forward (X=strafe, Y=forward — see PlayerIntent.Move)

    /// <summary>Initializes the world. <paramref name="seed"/> seeds the PRNG so generated content is reproducible;
    /// <paramref name="tickSeconds"/> is the fixed simulation step (the host's <c>StepTicks</c> as seconds). The optional
    /// spawn cell (<paramref name="spawnCellX"/>/<paramref name="spawnCellY"/>/<paramref name="spawnCellZ"/>) places the
    /// whole room at an arbitrary world cell (default the origin cell); the simulation is cell-agnostic, so a far cell
    /// reproduces the SAME per-tick local motion while proving the planet-scale coordinate seam.
    /// <paramref name="spawnAtConsoles"/> moves player slot i's spawn (for i below the console count) to
    /// standing in front of console stand i — the IMMERSED start's seating positions; slots past the console count keep
    /// the default grid. Pure configuration (the spawn stays a deterministic function of the slot), and the default
    /// preserves the default configuration's state hash. <paramref name="perConsoleStartCartType"/> overrides a SPECIFIC
    /// console's boot cart type (index i overrides console i; a value outside 0..<see cref="CartTypeCount"/>-1,
    /// including the default -1, means "no override for this console" — it falls through to the SAME policy
    /// <paramref name="startCartType"/> already drives). Null (the default) applies no override.</summary>
    public OverworldWorld(OverworldRoom room, PlatformerTuning tuning, float tickSeconds, uint seed, long spawnCellX = 0L, long spawnCellY = 0L, long spawnCellZ = 0L, bool startLoaded = false, bool spawnAtConsoles = false, int startCartType = -1, IReadOnlyList<int>? perConsoleStartCartType = null) {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(tuning);

        // Resolve the authored room to fixed-point collision planes and the tick period to a fixed dt ONCE, so the
        // per-tick step touches no float and is bit-identical across machines.
        m_collision = FixedRoom.From(room: room);
        m_tuning = tuning;
        m_dt = FixedQ4816.FromDouble(value: tickSeconds);
        m_rng = ((seed == 0u) ? 0x9E3779B9u : seed); // a zero seed would freeze xorshift; nudge to a fixed non-zero
        m_spawnAtConsoles = spawnAtConsoles;
        m_spawnCellX = spawnCellX;
        m_spawnCellY = spawnCellY;
        m_spawnCellZ = spawnCellZ;
        m_hiddenPosition = new WorldCoord3(
            CellX: spawnCellX,
            CellY: spawnCellY,
            CellZ: spawnCellZ,
            Local: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: -1000L), Z: FixedQ4816.Zero)
        ).Normalize();

        var consoleCount = m_collision.Consoles.Length;

        m_consoleLoadedType = new int[consoleCount];
        m_consoleSelectedType = new int[consoleCount];

        // The boot cart every cabinet defaults to. A caller-specified startCartType (the world-lens default passes 0)
        // makes it UNIFORM — every cabinet selects that cart, whether it starts loaded (--rom) or empty and boots per
        // player (world-lens) — so a per-player seating-boot inserts the right cart. Otherwise the SHOWCASE type (the
        // pre-inserted --rom cartridge) is the loaded default, and empty cabinets stagger their selection across the
        // cart cycle. The showcase (a plain, always-sourced ROM) is used rather than the LAST type on purpose: the last
        // slots are now the lazily-FORGED subjects (jukebox/scene), which must never be a boot default (they need a
        // bake before one exists) — anchoring the default to the showcase keeps that true no matter how CartTypeCount grows.
        var uniform = ((startCartType >= 0) && (startCartType < CartTypeCount));
        var bootType = (uniform ? startCartType : ShowcaseCartType);

        for (var console = 0; (console < consoleCount); console++) {
            // A per-console override (already range-clamped by the caller) wins over the global policy outright —
            // it is UNIFORM for this one console regardless of whether the global startCartType is set.
            var overrideType = (((perConsoleStartCartType is { } overrides) && (console < overrides.Count)) ? overrides[console] : -1);
            var consoleUniform = (uniform || ((overrideType >= 0) && (overrideType < CartTypeCount)));
            var consoleBootType = (((overrideType >= 0) && (overrideType < CartTypeCount)) ? overrideType : bootType);

            // Empty by default (the overworld: you insert a cart to bring a cabinet alive). The selected type is uniform
            // when a startCartType (global or per-console) is set, else staggered so the overworld's cabinets each offer
            // a different cart by default (world-lens / camera / showcase / world-lens).
            m_consoleSelectedType[console] = (consoleUniform ? consoleBootType : (startLoaded ? bootType : (console % CartTypeCount)));

            if (startLoaded) {
                m_consoleLoadedType[console] = consoleBootType;
                m_bootedMask |= (1u << console);
                m_bootOrder.Add(item: console);
            } else {
                m_consoleLoadedType[console] = -1;
            }
        }
    }

    /// <summary>The number of fixed ticks simulated so far.</summary>
    public ulong CurrentTick => m_tick;
    /// <summary>Observation-only hook fired once per <see cref="Advance"/> call, AFTER the tick completes:
    /// <c>(tick, hashBefore, hashAfter)</c>, where the hashes bracket the step with <see cref="StateHash"/> samples
    /// taken immediately before intents apply and immediately after. Exists so a console-facing tick transcript
    /// (<see cref="Puck.Commands.TickTranscript"/>) can narrate "what did this tick do to the hash" without the sim
    /// exposing anything new to READ: both samples are computed ONLY when a subscriber is attached, and the hook
    /// itself never mutates simulation state.</summary>
    public Action<ulong, ulong, ulong>? OnTickAdvanced { get; set; }
    /// <summary>The number of occupied slots.</summary>
    public int ActivePlayerCount { get; private set; }
    /// <summary>The slot array (null == free), for the renderer to read positions in slot order. Presentation only.</summary>
    public IReadOnlyList<PlayerSlot?> Slots => m_slots;
    /// <summary>The origin (zero local offset) of the cell the room and every body live in — the frame the static room
    /// geometry is authored in, so the renderer can express the render anchor in it. Coordinate plumbing only.</summary>
    public WorldCoord3 SpawnAnchor => new(CellX: m_spawnCellX, CellY: m_spawnCellY, CellZ: m_spawnCellZ, Local: FixedVector3.Zero);
    /// <summary>The number of console stands in the room.</summary>
    public int ConsoleCount => m_collision.Consoles.Length;
    /// <summary>The booted consoles as a bit per console index. Deterministic state.</summary>
    public uint BootedMask => m_bootedMask;
    /// <summary>The number of booted consoles.</summary>
    public int BootedCount => m_bootOrder.Count;
    /// <summary>The console indices in the order they booted — the presentation's pane-assignment order (the first
    /// booted console owns the first pane, and so on). Deterministic state.</summary>
    public IReadOnlyList<int> BootOrder => m_bootOrder;

    /// <summary>The active room player at <paramref name="slot"/> projected to NORMALIZED room coordinates — X and Z
    /// each in [0,1] across the walkable floor — or <see langword="null"/> when that slot is empty. A presentation-only
    /// read: the world-lens peripheral maps it into a machine's sensor page (the world→machine membrane). It never
    /// feeds back into the simulation, so the determinism hash is untouched.</summary>
    /// <param name="slot">The player slot to project.</param>
    public Vector2? PlayerRoomFraction(int slot) {
        if ((slot < 0) || (slot >= m_slots.Length) || (m_slots[slot] is not { } player)) {
            return null;
        }

        var local = player.Body.Position.Local;
        var minX = (double)m_collision.MinX;
        var maxX = (double)m_collision.MaxX;
        var minZ = (double)m_collision.MinZ;
        var maxZ = (double)m_collision.MaxZ;
        var fractionX = ((maxX > minX) ? (((double)local.X - minX) / (maxX - minX)) : 0.5);
        var fractionZ = ((maxZ > minZ) ? (((double)local.Z - minZ) / (maxZ - minZ)) : 0.5);

        return new Vector2(x: (float)Math.Clamp(value: fractionX, min: 0d, max: 1d), y: (float)Math.Clamp(value: fractionZ, min: 0d, max: 1d));
    }

    /// <summary>The local-space position of the body at <paramref name="slot"/>, or the origin when the slot is free —
    /// the narrow, least-privilege read the addon host feeds a scripted ghost each tick (never the whole body). A
    /// presentation-side read only; it never feeds back into the simulation, so the determinism hash is untouched.</summary>
    /// <param name="slot">The roster slot to read.</param>
    /// <returns>The slot body's local position, or <see cref="FixedVector3.Zero"/> when the slot is empty.</returns>
    public FixedVector3 LocalPositionForSlot(int slot) {
        if ((slot < 0) || (slot >= MaxPlayers) || (m_slots[slot] is not { } player)) {
            return FixedVector3.Zero;
        }

        return player.Body.Position.Local;
    }

    /// <summary>The inverse of <see cref="PlayerRoomFraction"/>: a normalized room fraction (X,Z each in [0,1]) back to a
    /// world position ON THE FLOOR, in the room's cell. PRESENTATION-only — the host uses it to place a driving player's
    /// avatar where their brick sprite is (the machine→world half of the membrane); it never touches the simulation,
    /// so determinism is untouched.</summary>
    /// <param name="fraction">The normalized room position (X,Z each in [0,1]).</param>
    /// <returns>A floor-height world position at that fraction.</returns>
    public WorldCoord3 RoomPositionForFraction(Vector2 fraction) {
        var minX = (double)m_collision.MinX;
        var maxX = (double)m_collision.MaxX;
        var minZ = (double)m_collision.MinZ;
        var maxZ = (double)m_collision.MaxZ;
        var x = (minX + (Math.Clamp(value: fraction.X, min: 0f, max: 1f) * (maxX - minX)));
        var z = (minZ + (Math.Clamp(value: fraction.Y, min: 0f, max: 1f) * (maxZ - minZ)));

        return new WorldCoord3(
            CellX: m_spawnCellX,
            CellY: m_spawnCellY,
            CellZ: m_spawnCellZ,
            Local: new FixedVector3(X: FixedQ4816.FromDouble(value: x), Y: m_collision.FloorTop, Z: FixedQ4816.FromDouble(value: z))
        );
    }

    /// <summary>Whether the console at <paramref name="consoleIndex"/> has booted.</summary>
    public bool IsBooted(int consoleIndex) =>
        (0u != (m_bootedMask & (1u << consoleIndex)));

    /// <summary>The cart TYPE (0..<see cref="CartTypeCount"/>-1) currently loaded/running in a cabinet, or -1 when it is
    /// empty. The host maps this to the cart's ROM bytes.</summary>
    public int InsertedCartridge(int consoleIndex) =>
        (((consoleIndex >= 0) && (consoleIndex < m_consoleLoadedType.Length)) ? m_consoleLoadedType[consoleIndex] : -1);

    /// <summary>The cart TYPE a cabinet will insert next (advanced by the cycle button) — the obvious selection the
    /// presentation surfaces.</summary>
    public int SelectedCartridge(int consoleIndex) =>
        (((consoleIndex >= 0) && (consoleIndex < m_consoleSelectedType.Length)) ? m_consoleSelectedType[consoleIndex] : 0);

    /// <summary>Sets a cabinet's SELECTED cart type directly (0..<see cref="CartTypeCount"/>-1) — the scripted equivalent
    /// of walking up and pressing Cycle until the wanted cart is selected (the <c>cart</c> console verb). Wraps the same
    /// selection machinery as <see cref="ResolveCycle"/>: when the cabinet is already booted, the loaded cart live-swaps
    /// to the new selection so the pane changes game without a restart. Out-of-range indices/types are refused.</summary>
    /// <param name="consoleIndex">The cabinet to set.</param>
    /// <param name="cartType">The cart type to select (0..<see cref="CartTypeCount"/>-1).</param>
    /// <returns><see langword="true"/> when the selection was applied; <see langword="false"/> when either argument was
    /// out of range.</returns>
    public bool SetSelectedCartType(int consoleIndex, int cartType) {
        if ((consoleIndex < 0) || (consoleIndex >= m_consoleSelectedType.Length) || (cartType < 0) || (cartType >= CartTypeCount)) {
            return false;
        }

        m_consoleSelectedType[consoleIndex] = cartType;

        // Live-swap a running cabinet to the new selection, exactly as ResolveCycle does when Cycle lands on a booted
        // cabinet (host reconcile assembles the new cart bytes into the pane next frame).
        if (IsBooted(consoleIndex: consoleIndex)) {
            m_consoleLoadedType[consoleIndex] = cartType;
        }

        return true;
    }

    /// <summary>Boots a console directly — the scripted/config path (debug capture harnesses and tests); the live path
    /// boots through a player's interact intent in <see cref="Advance"/>. Idempotent per console; out-of-range indices
    /// AND consoles with no inserted cartridge (<see cref="InsertedCartridge"/> &lt; 0) are refused. Booting is
    /// one-way: a console stays on for the rest of the session.</summary>
    /// <param name="consoleIndex">The console index to boot.</param>
    /// <returns><see langword="true"/> when the console transitioned to booted on this call.</returns>
    public bool Boot(int consoleIndex) {
        if (
            (consoleIndex < 0) ||
            (consoleIndex >= m_collision.Consoles.Length) ||
            IsBooted(consoleIndex: consoleIndex) ||
            (InsertedCartridge(consoleIndex: consoleIndex) < 0)
        ) {
            return false;
        }

        m_bootedMask |= (1u << consoleIndex);
        m_bootOrder.Add(item: consoleIndex);

        return true;
    }

    /// <summary>Scripted/config path: inserts a cabinet's currently-SELECTED cart and boots it — the empty→running half
    /// of the contextual interact, exposed for debug capture harnesses (<c>PUCK_OVERWORLD_DEBUG_BOOT</c>). Live play reaches
    /// the same state through a player's interact intent (<see cref="Advance"/>). No-op when out of range or already
    /// booted.</summary>
    /// <param name="consoleIndex">The cabinet to load its selected cart into and boot.</param>
    /// <returns><see langword="true"/> when the cabinet transitioned to booted on this call.</returns>
    public bool InsertSelectedAndBoot(int consoleIndex) {
        if ((consoleIndex < 0) || (consoleIndex >= m_collision.Consoles.Length) || IsBooted(consoleIndex: consoleIndex)) {
            return false;
        }

        m_consoleLoadedType[consoleIndex] = m_consoleSelectedType[consoleIndex];

        return Boot(consoleIndex: consoleIndex);
    }

    /// <summary>Ejects a cabinet: removes its cart and un-boots it (the presentation eases its pane closed). Two-way:
    /// a cabinet returns to empty. Idempotent on an already-empty cabinet.</summary>
    /// <param name="consoleIndex">The cabinet to eject.</param>
    /// <returns><see langword="true"/> when the cabinet transitioned to empty on this call.</returns>
    public bool Eject(int consoleIndex) {
        if (
            (consoleIndex < 0) ||
            (consoleIndex >= m_collision.Consoles.Length) ||
            !IsBooted(consoleIndex: consoleIndex)
        ) {
            return false;
        }

        m_bootedMask &= ~(1u << consoleIndex);
        _ = m_bootOrder.Remove(item: consoleIndex);
        m_consoleLoadedType[consoleIndex] = -1;

        return true;
    }

    /// <summary>Adds a player with the given external identity into the lowest free slot, returning that slot (or -1 when
    /// full). Idempotent: an already-present id returns its existing slot. The spawn position is a deterministic function
    /// of the slot, so a recycled slot respawns identically.</summary>
    public int AddPlayer(Guid playerId) {
        if (m_slotByPlayer.TryGetValue(key: playerId, value: out var existing)) {
            return existing;
        }

        var slot = LowestFreeSlot();

        if (slot < 0) {
            return -1;
        }

        // The spawn is a deterministic function of the slot (a fixed grid, or the slot's console-front seat), in the
        // world's spawn cell.
        var spawn = new WorldCoord3(
            CellX: m_spawnCellX,
            CellY: m_spawnCellY,
            CellZ: m_spawnCellZ,
            Local: SpawnLocalFor(slot: slot)
        ).Normalize();

        m_slots[slot] = new PlayerSlot(Id: playerId, Body: new PlatformerBody(position: spawn));
        m_slotByPlayer[playerId] = slot;
        ActivePlayerCount++;

        return slot;
    }

    // The per-slot spawn point in the room's local frame, FIXED-POINT end to end (no float enters the sim). Default:
    // the 2×2 grid on the floor. spawnAtConsoles: slot i (below the console count) stands in FRONT of stand i — the
    // stand's own XZ center pushed toward the room interior by a fixed 1.25 world units (past the stand's expanded
    // collision half-depth, inside the 1.8 per-axis InteractRange, so the takeover verb resolves at that stand
    // immediately; 1.25 is dyadic, so the Q48.16 conversion is exact). Stands sit against a wall, so "toward the
    // interior" is toward the room's Z center line (the sign opposite the stand's own).
    private FixedVector3 SpawnLocalFor(int slot) {
        if (m_spawnAtConsoles && (slot < m_collision.Consoles.Length)) {
            var stand = m_collision.Consoles[slot];
            var offset = FixedQ4816.FromDouble(value: 1.25);

            return new FixedVector3(
                X: stand.CenterX,
                Y: m_collision.FloorTop,
                Z: ((stand.CenterZ < FixedQ4816.Zero) ? (stand.CenterZ + offset) : (stand.CenterZ - offset))
            );
        }

        return new FixedVector3(
            X: FixedQ4816.FromInteger(value: (((slot % 2) * 8) - 4)),
            Y: m_collision.FloorTop,
            Z: FixedQ4816.FromInteger(value: (((slot / 2) * 8) - 4))
        );
    }

    /// <summary>Seats a padless occupant at a SPECIFIC slot — an addon-driven ghost's exclusive slot, distinct from
    /// <see cref="AddPlayer"/>'s lowest-free seating. Idempotent for the same identity. The spawn is the slot's
    /// deterministic spawn point, so a re-seated ghost respawns identically.</summary>
    /// <param name="playerId">The occupant's external identity.</param>
    /// <param name="slot">The exact slot to seat at.</param>
    /// <returns><see langword="true"/> when the slot holds <paramref name="playerId"/> after the call; <see langword="false"/>
    /// when the slot is out of range, already holds a different identity, or the identity is seated elsewhere.</returns>
    public bool AddPlayerAtSlot(Guid playerId, int slot) {
        if ((slot < 0) || (slot >= MaxPlayers)) {
            return false;
        }

        if (m_slots[slot] is { } occupant) {
            return (occupant.Id == playerId);
        }

        if (m_slotByPlayer.ContainsKey(key: playerId)) {
            return false;
        }

        var spawn = new WorldCoord3(
            CellX: m_spawnCellX,
            CellY: m_spawnCellY,
            CellZ: m_spawnCellZ,
            Local: SpawnLocalFor(slot: slot)
        ).Normalize();

        m_slots[slot] = new PlayerSlot(Id: playerId, Body: new PlatformerBody(position: spawn));
        m_slotByPlayer[playerId] = slot;
        ActivePlayerCount++;

        return true;
    }

    /// <summary>Removes a player, freeing its slot for recycling. Idempotent: an absent id returns -1. Returns the freed slot.</summary>
    public int RemovePlayer(Guid playerId) {
        if (!m_slotByPlayer.TryGetValue(key: playerId, value: out var slot)) {
            return -1;
        }

        m_slots[slot] = null;
        _ = m_slotByPlayer.Remove(key: playerId);
        ActivePlayerCount--;

        return slot;
    }

    /// <summary>Resolves a player's slot, or -1 when absent.</summary>
    public int SlotOf(Guid playerId) {
        return (m_slotByPlayer.TryGetValue(key: playerId, value: out var slot) ? slot : -1);
    }

    /// <summary>Teleports player <paramref name="slot"/>'s body to room-local XZ (<paramref name="x"/>,
    /// <paramref name="z"/>), holding Y at the room's floor height — a TICK-BOUNDARY authoring op, like
    /// <see cref="PlantGarden(uint, FixedQ4816, FixedQ4816)"/> / <see cref="LoadWorld"/>'s own direct
    /// <c>player.Body.Position</c> write. The destination is first clamped into the room's wall bounds (never
    /// teleports outside the walls — the same authority <see cref="PlatformerBody.Step"/> defers to), then checked
    /// against the console/shelf keep-outs and the baked walk grid (when one is loaded): a blocked destination
    /// REFUSES the move outright (the body is left exactly where it was) rather than creeping to the nearest free
    /// cell — a teleport has no "known-safe previous tick" to creep from the way <see cref="PlatformerBody.Step"/>'s
    /// per-axis clamp does, so a silent partial move would be a worse surprise than a clean refusal. Velocity resets
    /// to zero and the body lands Grounded (a teleport is not a shove — it sets the player down standing).</summary>
    /// <param name="slot">The player slot to move.</param>
    /// <param name="x">The room-local destination X.</param>
    /// <param name="z">The room-local destination Z.</param>
    /// <returns>The outcome — <see cref="PlayerMoveResult.Moved"/> only when the body actually relocated.</returns>
    public PlayerMoveResult MovePlayer(int slot, FixedQ4816 x, FixedQ4816 z) {
        if ((slot < 0) || (slot >= MaxPlayers)) {
            return PlayerMoveResult.SlotOutOfRange;
        }

        if (m_slots[slot] is not { } player) {
            return PlayerMoveResult.SlotEmpty;
        }

        var clampedX = FixedQ4816.Clamp(value: x, minimum: m_collision.MinX, maximum: m_collision.MaxX);
        var clampedZ = FixedQ4816.Clamp(value: z, minimum: m_collision.MinZ, maximum: m_collision.MaxZ);

        foreach (var obstacle in m_collision.Consoles) {
            if (IsInsideObstacle(obstacle: obstacle, x: clampedX, z: clampedZ)) {
                return PlayerMoveResult.Blocked;
            }
        }

        foreach (var obstacle in m_collision.Shelf) {
            if (IsInsideObstacle(obstacle: obstacle, x: clampedX, z: clampedZ)) {
                return PlayerMoveResult.Blocked;
            }
        }

        if ((m_collision.WalkGrid is { } grid) && grid.IsBlocked(x: clampedX, z: clampedZ)) {
            return PlayerMoveResult.Blocked;
        }

        var body = player.Body;
        var local = new FixedVector3(X: clampedX, Y: m_collision.FloorTop, Z: clampedZ);

        body.Position = body.Position.WithLocal(local: local);
        body.Velocity = FixedVector3.Zero;
        body.Grounded = true;

        return PlayerMoveResult.Moved;
    }

    // Mirrors PlatformerBody.ResolveAgainstObstacle's containment test (a STRICT "inside" check — a destination
    // resting exactly on an obstacle's face is not itself considered blocked, matching the resolver's own
    // early-return on the boundary).
    private static bool IsInsideObstacle(FixedConsole obstacle, FixedQ4816 x, FixedQ4816 z) =>
        ((x > obstacle.MinX) && (x < obstacle.MaxX) && (z > obstacle.MinZ) && (z < obstacle.MaxZ));

    /// <summary>The slot→identity map, fixed width <see cref="MaxPlayers"/> (free slots are <see cref="Guid.Empty"/>).</summary>
    public Guid[] RosterBySlot() {
        var roster = new Guid[MaxPlayers];

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            roster[slot] = (m_slots[slot]?.Id ?? Guid.Empty);
        }

        return roster;
    }

    /// <summary>Advances the simulation one fixed tick. <paramref name="intentsBySlot"/> is a fixed-width
    /// <see cref="MaxPlayers"/> row; free slots' entries are ignored.</summary>
    public void Advance(IReadOnlyList<PlayerIntent> intentsBySlot) {
        var tickObserver = OnTickAdvanced;
        var hashBefore = ((tickObserver is null) ? 0UL : StateHash());

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            if (player is null) {
                continue;
            }

            var intent = ((slot < intentsBySlot.Count) ? intentsBySlot[slot] : PlayerIntent.None);

            // The movement lock quantizes the intent's move vector BEFORE the body steps — Free skips the branch
            // entirely, so an unlocked world is today's exact code path (and today's exact hashes).
            if (m_movementLock != MovementLock.Free) {
                intent = (intent with { Move = QuantizeMove(mode: m_movementLock, move: intent.Move) });
            }

            // Interact + cycle fire BEFORE the step (against the pre-move position — the position the player saw when
            // they pressed), in slot order, so simultaneous presses resolve deterministically.
            if (intent.InteractPressed) {
                ResolveInteract(body: player.Body);
            }

            if (intent.CyclePressed) {
                ResolveCycle(body: player.Body);
            }

            player.Body.Step(intent: intent, tuning: m_tuning, dt: m_dt, room: m_collision);
        }

        AdvanceRtsUnits();
        AdvanceFieldWalker();

        m_tick++;

        tickObserver?.Invoke(m_tick, hashBefore, ((tickObserver is null) ? 0UL : StateHash()));
    }

    /// <summary>Loads a sculpted world's collision surfaces into the running simulation — the ONE legal
    /// authoring→sim seam. Applied on a TICK BOUNDARY (the caller invokes this between calls to <see cref="Advance"/>,
    /// never mid-step): replaces the whole collision surface wholesale (a fresh <see cref="FixedRoom"/>, built from
    /// <paramref name="room"/> plus the optional baked <paramref name="walkGrid"/>) and then deterministically clamps
    /// every OCCUPIED player's position into the new bounds, per axis, in ascending SLOT order (so two players
    /// clamping simultaneously resolve identically regardless of call order elsewhere). Everything else — the tick
    /// counter, the PRNG, the boot/cart state, free slots — is left untouched: a world load changes where the walls
    /// are, never what's running on the cabinets or how many ticks have elapsed. <see cref="StateHash"/> is therefore
    /// unaffected except through the legitimately-changed body positions it already folds in.</summary>
    /// <param name="room">The new authored room (bounds, floor, consoles/shelf) to resolve collision from.</param>
    /// <param name="walkGrid">The optional baked walk grid to attach (<see langword="null"/> = walls-only, exactly
    /// like a room with no world loaded).</param>
    /// <param name="movementLock">The world's movement direction lock (default <see cref="MovementLock.Free"/>,
    /// matching the wire's null default — a world that declares no lock RESETS any previous world's lock rather than
    /// inheriting it). The host parses the document string via <see cref="ParseMovementLock"/> and passes the enum;
    /// <see cref="SetMovementLock"/> toggles it standalone on the same tick-boundary terms.</param>
    public void LoadWorld(OverworldRoom room, FixedWalkGrid? walkGrid, MovementLock movementLock = MovementLock.Free) {
        ArgumentNullException.ThrowIfNull(argument: room);

        m_collision = FixedRoom.From(room: room, walkGrid: walkGrid);
        m_movementLock = movementLock;

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            if (m_slots[slot] is not { } player) {
                continue;
            }

            var local = player.Body.Position.Local;
            var clampedLocal = new FixedVector3(
                X: FixedQ4816.Clamp(value: local.X, minimum: m_collision.MinX, maximum: m_collision.MaxX),
                Y: m_collision.FloorTop,
                Z: FixedQ4816.Clamp(value: local.Z, minimum: m_collision.MinZ, maximum: m_collision.MaxZ)
            );

            player.Body.Position = player.Body.Position.WithLocal(local: clampedLocal);
        }
    }

    /// <summary>Sets the movement direction lock — sim CONFIG applied on a TICK BOUNDARY (between calls to
    /// <see cref="Advance"/>, never mid-step), exactly like <see cref="LoadWorld"/>'s collision swap. Like the
    /// collision surfaces, the lock is NEVER folded into <see cref="StateHash"/>: two runs whose intents happen to
    /// already lie on the locked directions hash identically whether or not the lock is set.</summary>
    /// <param name="mode">The lock to apply from the next tick onward.</param>
    public void SetMovementLock(MovementLock mode) {
        m_movementLock = mode;
    }

    /// <summary>Parses a world document's <c>movementLock</c> wire string (<c>free</c>/<c>four</c>/<c>eight</c>/
    /// <c>hex</c>, case-insensitive) into the sim's enum — the wire→typed seam, mirroring
    /// <see cref="World.WalkOverrideInput.FromDocument"/>'s kind-string resolution. Null or any unrecognized value is
    /// <see cref="MovementLock.Free"/> (the document's own null default).</summary>
    /// <param name="value">The wire string, or <see langword="null"/>.</param>
    /// <returns>The parsed lock.</returns>
    public static MovementLock ParseMovementLock(string? value) {
        if (string.Equals(a: value, b: "four", comparisonType: StringComparison.OrdinalIgnoreCase)) { return MovementLock.Four; }
        if (string.Equals(a: value, b: "eight", comparisonType: StringComparison.OrdinalIgnoreCase)) { return MovementLock.Eight; }
        if (string.Equals(a: value, b: "hex", comparisonType: StringComparison.OrdinalIgnoreCase)) { return MovementLock.Hex; }

        return MovementLock.Free;
    }

    // ---- The deterministic garden (a seed unfolds into a tree over sim ticks) --------------------------------------
    // Sim state is deliberately THIN: a seed + the tick it was planted + where. The GROWN TREE is never stored — it is
    // a pure function of (Seed, CurrentTick − PlantedTick), recomputed every frame by the presentation side
    // (Puck.Demo.Garden.GardenTreeGenerator/GardenRenderer). That keeps this class's determinism contract exactly as
    // narrow as every other authoring op below (LoadWorld/SetMovementLock): a tick-boundary write of a few integers.

    /// <summary>Plants a new garden — a TICK-BOUNDARY authoring op (like <see cref="LoadWorld"/>), never called
    /// mid-<see cref="Advance"/>. Recycles the lowest free slot (mirrors <see cref="LowestFreeSlot"/>'s player-slot
    /// policy).</summary>
    /// <param name="seed">The PRNG seed the presentation side expands into a tree.</param>
    /// <param name="localX">The room-local X to plant at.</param>
    /// <param name="localZ">The room-local Z to plant at.</param>
    /// <returns>The slot index the garden was planted into, or -1 when every <see cref="MaxGardens"/> slot is
    /// occupied (the caller/console verb reports "the garden is full").</returns>
    public int PlantGarden(uint seed, FixedQ4816 localX, FixedQ4816 localZ) {
        for (var slot = 0; (slot < MaxGardens); slot++) {
            if (m_gardens[slot] is not null) {
                continue;
            }

            m_gardens[slot] = new GardenPlant(Seed: seed, PlantedTick: m_tick, LocalX: localX, LocalZ: localZ);

            return slot;
        }

        return -1;
    }

    /// <summary>Plants a new garden near player slot 0 (offset so it doesn't overlap their body), or at a fixed spot
    /// near the workbench when that slot is empty — fully fixed-point (the stored position never passes through a
    /// presentation float), so a script's replay never depends on interpolation timing. Wraps
    /// <see cref="PlantGarden(uint, FixedQ4816, FixedQ4816)"/>.</summary>
    /// <param name="seed">The seed to plant, or <see langword="null"/> to derive one deterministically from the
    /// current tick (never wall-clock, never <see cref="System.Random"/> — see <see cref="DeriveGardenSeed"/>).</param>
    /// <returns>The slot index, or -1 when the garden is full.</returns>
    public int PlantGardenNearPlayer(uint? seed = null) {
        var offset = FixedQ4816.FromDouble(value: 0.9);
        var margin = FixedQ4816.FromDouble(value: 0.6);

        var (rawX, rawZ) = ((m_slots[0] is { } player)
            ? ((player.Body.Position.Local.X + offset), (player.Body.Position.Local.Z + offset))
            : ((WorkbenchCenterX - FixedQ4816.FromDouble(value: 1.8)), FixedQ4816.FromDouble(value: -2.4)));
        var clampedX = FixedQ4816.Clamp(value: rawX, minimum: (m_collision.MinX + margin), maximum: (m_collision.MaxX - margin));
        var clampedZ = FixedQ4816.Clamp(value: rawZ, minimum: (m_collision.MinZ + margin), maximum: (m_collision.MaxZ - margin));

        return PlantGarden(seed: (seed ?? DeriveGardenSeed()), localX: clampedX, localZ: clampedZ);
    }

    /// <summary>Uproots every planted garden — a tick-boundary op, like <see cref="PlantGarden(uint, FixedQ4816, FixedQ4816)"/>.</summary>
    public void ClearGardens() {
        Array.Clear(array: m_gardens);
    }

    /// <summary>The planted-garden slots (null == empty), for the presentation side to read seeds/plant ticks/positions.
    /// Read-only — growth itself is never sim state (see the section remarks).</summary>
    public IReadOnlyList<GardenPlant?> Gardens => m_gardens;

    // A deterministic default seed for garden.plant with no argument: folds the current tick and how many gardens are
    // already planted through the SAME FNV-1a fold StateHash uses — never wall-clock, never System.Random, so a
    // scripted replay that never passes an explicit seed still reproduces bit-for-bit.
    private uint DeriveGardenSeed() {
        var occupied = 0u;

        for (var slot = 0; (slot < MaxGardens); slot++) {
            occupied += ((m_gardens[slot] is null) ? 0u : 1u);
        }

        var hash = Fnv1aHash.Create();

        hash.Add(value: m_tick);
        hash.Add(value: occupied);

        return (uint)hash.Value;
    }

    // ---- The RTS scenario's fixed-point unit simulation --------------------------------------------------------------
    // Sim state per unit: position, an optional live move-order target, selection. AdvanceRtsUnits (called from
    // Advance, once per tick) is a pure straight-line move-to-target integrator, ground-snapped through the
    // configured IWorldQuery — the SAME "sim binds only the query interface" contract Puck.Demo.Rts.RtsScenario's
    // remarks describe. The query binding is sim CONFIG (see ConfigureRtsQuery), like LoadWorld's collision swap:
    // never folded into StateHash.

    /// <summary>Binds (or clears) the deterministic world-query provider the RTS scenario's ground-snap and spawn
    /// checks consult — a tick-boundary CONFIG op, like <see cref="LoadWorld"/>'s collision swap. Never folded into
    /// <see cref="StateHash"/>: two runs binding equivalent providers hash identically.</summary>
    /// <param name="query">The provider to bind, or <see langword="null"/> to fall back to the room's floor height.</param>
    public void ConfigureRtsQuery(IWorldQuery? query) {
        m_rtsQuery = query;
    }

    /// <summary>Spawns a new RTS unit at the given room-local XZ — a TICK-BOUNDARY authoring op, like
    /// <see cref="PlantGarden(uint, FixedQ4816, FixedQ4816)"/>. Recycles the lowest free slot. Rejected (returns -1)
    /// when the spawn point overlaps blocked geometry per the bound query (never rejected on the ground height
    /// itself — a query with no blocked layer never rejects a spawn).</summary>
    /// <param name="x">The room-local X to spawn at.</param>
    /// <param name="z">The room-local Z to spawn at.</param>
    /// <returns>The slot index the unit was spawned into, or -1 when every <see cref="MaxRtsUnits"/> slot is
    /// occupied, or the spawn point is blocked.</returns>
    public int SpawnRtsUnit(FixedQ4816 x, FixedQ4816 z) {
        var groundY = m_collision.FloorTop;

        if ((m_rtsQuery is { } query)) {
            if (query.Overlap(center: WorldCoord3.FromLocal(local: new FixedVector3(X: x, Y: groundY, Z: z)), radius: RtsUnitSpawnRadiusRaw)) {
                return -1;
            }

            if (query.TryGroundHeight(position: WorldCoord3.FromLocal(local: new FixedVector3(X: x, Y: groundY, Z: z)), probeUp: RtsGroundProbeRaw, probeDown: RtsGroundProbeRaw, groundY: out var sampled)) {
                groundY = sampled;
            }
        }

        for (var slot = 0; (slot < MaxRtsUnits); slot++) {
            if (m_rtsUnits[slot].Active) {
                continue;
            }

            m_rtsUnits[slot] = new RtsUnit(Active: true, HasTarget: false, Selected: false, TargetX: x, TargetZ: z, X: x, Y: groundY, Z: z);

            return slot;
        }

        return -1;
    }

    /// <summary>Selects every active unit whose current XZ lies inside the given box (and deselects every active
    /// unit outside it) — a TICK-BOUNDARY op, like <see cref="SpawnRtsUnit"/>. A fresh box selection always replaces
    /// the previous one (there is no additive/shift-select in this proof scenario).</summary>
    /// <param name="minX">The box's minimum X.</param>
    /// <param name="minZ">The box's minimum Z.</param>
    /// <param name="maxX">The box's maximum X.</param>
    /// <param name="maxZ">The box's maximum Z.</param>
    /// <returns>How many units are now selected.</returns>
    public int SelectRtsUnitsInBox(FixedQ4816 minX, FixedQ4816 minZ, FixedQ4816 maxX, FixedQ4816 maxZ) {
        var count = 0;

        for (var slot = 0; (slot < MaxRtsUnits); slot++) {
            var unit = m_rtsUnits[slot];

            if (!unit.Active) {
                continue;
            }

            var inside = ((unit.X >= minX) && (unit.X <= maxX) && (unit.Z >= minZ) && (unit.Z <= maxZ));

            m_rtsUnits[slot] = (unit with { Selected = inside });

            if (inside) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Orders every currently-selected active unit to move to the given room-local XZ (straight-line —
    /// see <see cref="AdvanceRtsUnits"/>) — a TICK-BOUNDARY op.</summary>
    /// <param name="targetX">The order's target X.</param>
    /// <param name="targetZ">The order's target Z.</param>
    /// <returns>How many units received the order.</returns>
    public int MoveSelectedRtsUnits(FixedQ4816 targetX, FixedQ4816 targetZ) {
        var count = 0;

        for (var slot = 0; (slot < MaxRtsUnits); slot++) {
            var unit = m_rtsUnits[slot];

            if (!unit.Active || !unit.Selected) {
                continue;
            }

            m_rtsUnits[slot] = (unit with { HasTarget = true, TargetX = targetX, TargetZ = targetZ });
            count++;
        }

        return count;
    }

    /// <summary>Despawns every RTS unit — a tick-boundary op, like <see cref="ClearGardens"/>.</summary>
    public void ClearRtsUnits() {
        Array.Clear(array: m_rtsUnits);
    }

    /// <summary>The RTS unit pool, for the presentation side (and console verbs) to read.</summary>
    public IReadOnlyList<RtsUnit> RtsUnits => m_rtsUnits;

    // The per-tick unit integrator: a pure straight-line move-to-target (no pathfinding, no separation — the
    // deliberately minimal proof-wave sim), ground-snapped through the bound query every tick (falls back to the
    // room's floor when no query is bound, or the query has no heightfield layer for this cell). Distance/step are
    // both FixedQ4816 — one Sqrt per moving unit per tick, no trig.
    private void AdvanceRtsUnits() {
        var step = (RtsUnitSpeedRaw * m_dt);

        for (var slot = 0; (slot < MaxRtsUnits); slot++) {
            var unit = m_rtsUnits[slot];

            if (!unit.Active) {
                continue;
            }

            var x = unit.X;
            var z = unit.Z;

            if (unit.HasTarget) {
                var dx = (unit.TargetX - x);
                var dz = (unit.TargetZ - z);
                var distance = new FixedVector3(X: dx, Y: FixedQ4816.Zero, Z: dz).Length;

                if (distance <= step) {
                    x = unit.TargetX;
                    z = unit.TargetZ;
                    unit = (unit with { HasTarget = false });
                } else {
                    var inverse = (FixedQ4816.One / distance);

                    x += ((dx * inverse) * step);
                    z += ((dz * inverse) * step);
                }
            }

            var y = m_collision.FloorTop;

            if ((m_rtsQuery is { } query) && query.TryGroundHeight(position: WorldCoord3.FromLocal(local: new FixedVector3(X: x, Y: unit.Y, Z: z)), probeUp: RtsGroundProbeRaw, probeDown: RtsGroundProbeRaw, groundY: out var sampled)) {
                y = sampled;
            }

            m_rtsUnits[slot] = (unit with { X = x, Y = y, Z = z });
        }
    }

    // ---- The gravity scenario's FieldWalkerBody on a live SDF field ---------------------------------------------------
    // The evaluator binding is sim CONFIG (see ConfigureFieldEvaluator), exactly like ConfigureRtsQuery's m_rtsQuery:
    // never folded into StateHash. The walker BODY itself, and its queued scripted-walk order, ARE real sim state
    // (folded into HashState below), mirroring the RTS pool's Active/HasTarget/Target fields.

    /// <summary>Binds (or clears) the deterministic field evaluator the walker's <see cref="FieldWalkerBody.Step"/>
    /// consults every tick — a tick-boundary CONFIG op, like <see cref="ConfigureRtsQuery"/>. Never folded into
    /// <see cref="StateHash"/>: two runs binding equivalent programs hash identically.</summary>
    /// <param name="evaluator">The field to walk, or <see langword="null"/> to freeze the walker (a bound-less
    /// walker takes no gravity/ground step — see <see cref="AdvanceFieldWalker"/>).</param>
    public void ConfigureFieldEvaluator(IFieldEvaluator? evaluator) {
        m_fieldEvaluator = evaluator;
    }

    /// <summary>Spawns (or respawns) the one field walker at <paramref name="position"/> with an initial "up" guess —
    /// a TICK-BOUNDARY authoring op, like <see cref="PlantGarden(uint, FixedQ4816, FixedQ4816)"/>. Re-issuing this
    /// while a walker already exists RESETS it (position/velocity/queued walk order), the same "spawn always seats
    /// fresh" contract <see cref="SpawnRtsUnit"/> gives a recycled slot.</summary>
    /// <param name="position">The spawn position.</param>
    /// <param name="up">The initial up guess (see <see cref="FieldWalkerBody"/>'s ctor remarks).</param>
    public void SpawnFieldWalker(WorldCoord3 position, FixedVector3 up) {
        m_fieldWalker = new FieldWalkerBody(position: position, up: up);
        m_fieldWalkerWalkTicks = 0;
    }

    /// <summary>Despawns the field walker — a tick-boundary op, like <see cref="ClearRtsUnits"/>.</summary>
    public void ClearFieldWalker() {
        m_fieldWalker = null;
        m_fieldWalkerWalkTicks = 0;
    }

    /// <summary>Whether a walker is currently spawned.</summary>
    public bool FieldWalkerActive => (m_fieldWalker is not null);

    /// <summary>Queues <paramref name="ticks"/> ticks of constant tangent-plane move intent — the scripted-driving
    /// seam a console verb (<c>planet.walk</c>) uses to circumnavigate the field without a pad: one tick of
    /// <paramref name="move"/> is consumed per <see cref="Advance"/> call (regardless of real produced-frame pacing —
    /// see <see cref="AdvanceFieldWalker"/>), so the resulting arc length is an EXACT function of sim ticks, not
    /// wall-clock. A fresh call REPLACES any still-queued order (mirrors <see cref="MoveSelectedRtsUnits"/>'s
    /// replace-not-append order semantics). No-op when no walker is spawned.</summary>
    /// <param name="ticks">How many ticks to apply <paramref name="move"/> for (clamped to at least 0).</param>
    /// <param name="move">The tangent-plane move vector (X = strafe, Y = forward — <see cref="PlayerIntent.Move"/>'s
    /// own convention), each component conventionally in [-1, 1]. Fixed-point, like <see cref="MoveSelectedRtsUnits"/>'s
    /// <c>targetX</c>/<c>targetZ</c> — the float-to-fixed conversion happens at the console-verb boundary
    /// (<see cref="OverworldFrameSource.WalkGravityWalker"/>), never inside sim state.</param>
    public void WalkFieldWalker(int ticks, FixedVector2 move) {
        if (m_fieldWalker is null) {
            return;
        }

        m_fieldWalkerWalkTicks = Math.Max(val1: 0, val2: ticks);
        m_fieldWalkerWalkMove = move;
    }

    /// <summary>A read-only snapshot of the walker's current state, for the presentation side (and console verbs) to
    /// read — <see langword="default"/> (<c>Active</c> false) when no walker is spawned.</summary>
    public FieldWalkerSnapshot FieldWalkerState() {
        if (m_fieldWalker is not { } body) {
            return default;
        }

        return new FieldWalkerSnapshot(Active: true, Position: body.Position, Velocity: body.Velocity, Up: body.Up, FacingAngle: body.FacingAngle, Grounded: body.Grounded);
    }

    /// <summary>The walker's render transform, REBASED by <paramref name="renderOrigin"/> and INTERPOLATED by
    /// <paramref name="alpha"/> — the gravity presentation's own <see cref="DynamicTransforms"/> sibling, for exactly
    /// one body instead of <see cref="MaxPlayers"/>. Returns a transform parked at <paramref name="renderOrigin"/>'s
    /// own cell, far below, when no walker is spawned (mirrors <see cref="m_hiddenPosition"/>'s convention) — the
    /// caller (<c>Gravity.WalkerInstanceEmitter</c>) also gates the instance's <c>active</c> flag on
    /// <see cref="FieldWalkerActive"/>, so this fallback value is never actually visible.</summary>
    /// <param name="renderOrigin">The per-frame world anchor every position is expressed relative to.</param>
    /// <param name="alpha">The interpolation fraction in <c>[0, 1)</c> between the previous and current fixed tick.</param>
    public DynamicTransform FieldWalkerTransform(WorldCoord3 renderOrigin, float alpha) {
        if (m_fieldWalker is not { } body) {
            return new DynamicTransform(Position: new Vector3(x: 0f, y: -1000f, z: 0f), Orientation: Quaternion.Identity);
        }

        return new DynamicTransform(Position: body.RenderRelativePositionAt(renderOrigin: renderOrigin, alpha: alpha), Orientation: body.OrientationAt(alpha: alpha));
    }

    // The per-tick walker step: consumes one tick of the queued walk order (see WalkFieldWalker), then steps the
    // body against the bound field. A walker with no bound evaluator or no queued order simply idles in place (its
    // OWN gravity/ground-snap still applies via a zero-move Step — Grounded settles exactly like a player standing
    // still). No-op when nothing is spawned.
    private void AdvanceFieldWalker() {
        if ((m_fieldWalker is not { } body) || (m_fieldEvaluator is not { } field)) {
            return;
        }

        var move = ((m_fieldWalkerWalkTicks > 0) ? m_fieldWalkerWalkMove : FixedVector2.Zero);

        if (m_fieldWalkerWalkTicks > 0) {
            m_fieldWalkerWalkTicks--;
        }

        // The one float boundary: PlayerIntent.Move is float BY CONVENTION (the source-agnostic per-tick input type
        // every intent source — pad/network/AI/recording — produces), never itself sim state; the fixed-point queued
        // order above IS sim state and stays fixed-point right up to this conversion.
        var intentMove = new Vector2(x: (float)(double)move.X, y: (float)(double)move.Y);
        var intent = new PlayerIntent(Move: intentMove, JumpHeld: false, JumpPressed: false, JumpReleased: false);

        body.Step(intent: intent, tuning: m_fieldWalkerTuning, dt: m_dt, field: field);
    }

    // Quantizes an intent's move vector onto a lock mode's direction set: the direction with the MAXIMUM dot product
    // wins — computed on raw longs (raws are ≤ 2^17, so a product is ≤ 2^34 and the two-term sum fits a long with
    // room to spare; no FixedQ4816 multiply, whose rounding would blur an exact compare) — with ties breaking to the
    // FIRST direction in the mode's documented enumeration order (the loops ascend and the compare is strict). The
    // result is unit × dot(move, unit) in rounded fixed-point: the projection onto the chosen axis, so analog
    // magnitude is preserved along it. Zero intent stays zero (no quantization of nothing). The float round-trip out
    // is EXACT: every component raw is far below 2^24, so float→fixed→float→fixed recovers identical bits in Step.
    private static Vector2 QuantizeMove(MovementLock mode, Vector2 move) {
        // The same float→fixed conversion Step itself applies — quantize in the sim's own number space.
        var moveX = FixedQ4816.FromDouble(value: move.X);
        var moveY = FixedQ4816.FromDouble(value: move.Y);

        if ((moveX.Value == 0L) && (moveY.Value == 0L)) {
            return move;
        }

        var directions = ((mode == MovementLock.Four) ? FourDirections : ((mode == MovementLock.Eight) ? EightDirections : HexDirections));
        var bestDot = long.MinValue;
        var bestIndex = 0;

        for (var index = 0; (index < directions.Length); index++) {
            var dot = ((moveX.Value * directions[index].X) + (moveY.Value * directions[index].Y));

            if (dot > bestDot) {
                bestDot = dot;
                bestIndex = index;
            }
        }

        var unitX = FixedQ4816.FromRawBits(value: directions[bestIndex].X);
        var unitY = FixedQ4816.FromRawBits(value: directions[bestIndex].Y);
        var projection = ((moveX * unitX) + (moveY * unitY));
        var quantizedX = (unitX * projection);
        var quantizedY = (unitY * projection);

        return new Vector2(x: ((float)(double)quantizedX), y: ((float)(double)quantizedY));
    }

    // The contextual cabinet interact (the press edge): the nearest cabinet in range TOGGLES — a running cabinet
    // EJECTS, an empty cabinet INSERTS its selected cart and boots it. No shelf, no carrying: the cart choice lives at
    // the cabinet (see ResolveCycle).
    private void ResolveInteract(PlatformerBody body) {
        var console = FindNearestObstacle(obstacles: m_collision.Consoles, range: m_collision.InteractRange, local: body.Position.Local, predicate: static _ => true);

        if (console < 0) {
            return;
        }

        if (IsBooted(consoleIndex: console)) {
            _ = Eject(consoleIndex: console);
        } else {
            m_consoleLoadedType[console] = m_consoleSelectedType[console];
            _ = Boot(consoleIndex: console);
        }
    }

    // The cycle press: advance the nearest cabinet's selected cart type (custom -> camera -> showcase -> ...); if the
    // cabinet is already running, live-swap the loaded cart to the new selection.
    private void ResolveCycle(PlatformerBody body) {
        var console = FindNearestObstacle(obstacles: m_collision.Consoles, range: m_collision.InteractRange, local: body.Position.Local, predicate: static _ => true);

        if (console < 0) {
            return;
        }

        m_consoleSelectedType[console] = ((m_consoleSelectedType[console] + 1) % CartTypeCount);

        if (IsBooted(consoleIndex: console)) {
            m_consoleLoadedType[console] = m_consoleSelectedType[console];
        }
    }

    /// <summary>Finds the console within interact range of a slot's player, filtered by booted state — the READ-ONLY
    /// proximity query behind the contextual run/activate button (unbooted) and the brick debug verbs (booted).
    /// Same math as the boot path: nearest by squared XZ distance, ties to the lower index. Fixed-point only.</summary>
    /// <param name="slot">The player slot to measure from.</param>
    /// <param name="booted">Whether to consider booted (<see langword="true"/>) or unbooted (with a cartridge already
    /// inserted) consoles.</param>
    /// <returns>The console index, or -1 when none is in range (or the slot is empty).</returns>
    public int NearestConsoleForSlot(int slot, bool booted) {
        var player = (((slot >= 0) && (slot < MaxPlayers)) ? m_slots[slot] : null);

        if (player is null) {
            return -1;
        }

        return (booted
            ? FindNearestObstacle(obstacles: m_collision.Consoles, range: m_collision.InteractRange, local: player.Body.Position.Local, predicate: index => IsBooted(consoleIndex: index))
            : FindNearestObstacle(obstacles: m_collision.Consoles, range: m_collision.InteractRange, local: player.Body.Position.Local, predicate: index => (!IsBooted(consoleIndex: index) && (InsertedCartridge(consoleIndex: index) >= 0))));
    }

    /// <summary>The nearest cabinet within interact range of a slot's player, regardless of its state (empty, loaded, or
    /// booted) — the contextual insert/eject/cycle target the host stages ownership and icons against.</summary>
    /// <param name="slot">The player slot to measure from.</param>
    /// <returns>The console index, or -1 when none is in range (or the slot is empty).</returns>
    public int NearestCabinetForSlot(int slot) {
        var player = (((slot >= 0) && (slot < MaxPlayers)) ? m_slots[slot] : null);

        return ((player is null)
            ? -1
            : FindNearestObstacle(obstacles: m_collision.Consoles, range: m_collision.InteractRange, local: player.Body.Position.Local, predicate: static _ => true));
    }

    // The diegetic WORKBENCH's fixed room-local XZ center (Stage 3 of the self-editing arc). It sits against the EAST
    // (+X) inner wall at mid-depth — clear of the console stands (far −Z wall) and the shelf brackets (west −X wall) —
    // so a player walks up to it from the room center. Read-only host presentation: the workbench is NOT a collision
    // obstacle and NEVER enters the state hash; the frame source renders its prop at exactly this point, and the
    // proximity read below drives the gated authoring entry. Derived from the collision bounds so it tracks a loaded
    // world's grown lot (like the reveal framing) rather than a magic literal.
    private FixedQ4816 WorkbenchCenterX => (m_collision.MaxX - FixedQ4816.FromDouble(value: 1.6));
    private FixedQ4816 WorkbenchCenterZ => FixedQ4816.Zero;

    /// <summary>The workbench's room-local XZ center (the frame source renders the prop here and the reveal glow reads
    /// it) — presentation plumbing only, never part of the state hash.</summary>
    public (float X, float Z) WorkbenchCenterLocal => ((float)WorkbenchCenterX, (float)WorkbenchCenterZ);

    /// <summary>Whether a slot's player stands within interact range of the diegetic workbench — the read-only
    /// proximity behind the GATED editor entry (mirrors <see cref="NearestCabinetForSlot"/>'s per-axis range test, same
    /// <c>InteractRange</c>, fixed-point only). Presentation/host routing only: the workbench is not a sim obstacle, so
    /// this touches no hashed state. The CALLER gates on the editor unlock — a dark workbench admits no entry — so the
    /// proximity itself is unconditional here.</summary>
    /// <param name="slot">The player slot to measure from.</param>
    /// <returns><see langword="true"/> when the slot is occupied and its player is within interact range of the
    /// workbench.</returns>
    public bool IsPlayerNearWorkbench(int slot) {
        var player = (((slot >= 0) && (slot < MaxPlayers)) ? m_slots[slot] : null);

        if (player is null) {
            return false;
        }

        var range = m_collision.InteractRange;
        var local = player.Body.Position.Local;
        var dx = (local.X - WorkbenchCenterX);
        var dz = (local.Z - WorkbenchCenterZ);

        return ((dx <= range) && (dx >= -range) && (dz <= range) && (dz >= -range));
    }

    private static int FindNearestObstacle(FixedConsole[] obstacles, FixedQ4816 range, FixedVector3 local, Func<int, bool> predicate) {
        var best = -1;
        var bestDistanceSq = FixedQ4816.Zero;

        for (var index = 0; (index < obstacles.Length); index++) {
            if (!predicate(index)) {
                continue;
            }

            var dx = (local.X - obstacles[index].CenterX);
            var dz = (local.Z - obstacles[index].CenterZ);

            if ((dx > range) || (dx < -range) || (dz > range) || (dz < -range)) {
                continue;
            }

            var distanceSq = ((dx * dx) + (dz * dz));

            if ((best < 0) || (distanceSq < bestDistanceSq)) {
                best = index;
                bestDistanceSq = distanceSq;
            }
        }

        return best;
    }

    /// <summary>The per-frame transforms for the renderer's dynamic-transform buffer — always exactly
    /// <see cref="MaxPlayers"/> entries, REBASED by <paramref name="renderOrigin"/> and INTERPOLATED by
    /// <paramref name="alpha"/>. An active slot reports its body (lerped between the previous and current fixed tick); a
    /// free slot reports a hidden transform. The returned list is a REUSED buffer valid only until the next call — the
    /// renderer's packers consume it synchronously within the same frame. PRESENTATION ONLY.</summary>
    /// <param name="renderOrigin">The per-frame world anchor every position is expressed relative to (subtracted in fixed point before the float cast).</param>
    /// <param name="alpha">The interpolation fraction in <c>[0, 1)</c> between the previous and current fixed tick (the frame's <c>InterpolationAlpha</c>).</param>
    /// <returns>The reused, per-slot transform buffer.</returns>
    public IReadOnlyList<DynamicTransform> DynamicTransforms(WorldCoord3 renderOrigin, float alpha) {
        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            m_dynamicTransforms[slot] = ((player is null)
                ? new DynamicTransform(Position: m_hiddenPosition.ToRenderRelative(origin: renderOrigin), Orientation: Quaternion.Identity)
                : new DynamicTransform(Position: player.Body.RenderRelativePositionAt(renderOrigin: renderOrigin, alpha: alpha), Orientation: player.Body.OrientationAt(alpha: alpha)));
        }

        return m_dynamicTransforms;
    }


    /// <summary>A 64-bit FNV-1a hash over the deterministic state — the per-tick probe the determinism/replay gate
    /// compares. Each slot contributes an active-mask bit (so a join/leave changes the hash even when survivors are
    /// unchanged) and, when active, its body (cell index AND local offset). The identity Guid is NOT hashed — the sim is
    /// identity-agnostic; replay validates the roster separately.</summary>
    public ulong StateHash() =>
        HashState(includeCell: true);
    /// <summary>A 64-bit FNV-1a hash over the deterministic state EXCLUDING the absolute cell index — the cell-INVARIANT
    /// signature. Two worlds whose rooms sit in different cells but whose bodies move identically in their local frames
    /// produce the same sequence, proving the simulation is translation-invariant across the planet-scale cell grid.</summary>
    public ulong LocalStateHash() =>
        HashState(includeCell: false);

    private ulong HashState(bool includeCell) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: m_tick);
        hash.Add(value: m_rng);
        // The console boot state is deterministic state: the mask AND the boot order both contribute (two consoles
        // booted in either order differ), in both hash variants (booting is cell-agnostic).
        hash.Add(value: m_bootedMask);
        hash.Add(value: (uint)m_bootOrder.Count);

        foreach (var consoleIndex in m_bootOrder) {
            hash.Add(value: (uint)consoleIndex);
        }

        // The cabinet cartridge state folds in too: each cabinet's loaded + selected cart type, in console order, so an
        // insert / eject / cycle changes the hash even though no player body moved.
        for (var console = 0; (console < m_consoleLoadedType.Length); console++) {
            hash.Add(value: (uint)m_consoleLoadedType[console]);
            hash.Add(value: (uint)m_consoleSelectedType[console]);
        }

        // The planted-garden pool folds in too: an occupied bit per slot, plus its seed/plant-tick/position when
        // occupied — a plant/clear changes the hash even though no player body moved, and the GROWN tree (a pure
        // function of Seed and the tick already hashed above) never needs its own contribution.
        for (var slot = 0; (slot < m_gardens.Length); slot++) {
            var garden = m_gardens[slot];

            hash.Add(value: ((garden is null) ? 0u : 1u));

            if (garden is { } planted) {
                hash.Add(value: planted.Seed);
                hash.Add(value: planted.PlantedTick);
                hash.Add(value: planted.LocalX.Value);
                hash.Add(value: planted.LocalZ.Value);
            }
        }

        // The RTS unit pool folds in too: an active bit per slot, plus its selection/order/
        // position when active — spawn/select/move/clear and every tick's ground-snapped integration all change the
        // hash exactly like a player body moving.
        for (var slot = 0; (slot < m_rtsUnits.Length); slot++) {
            var unit = m_rtsUnits[slot];

            hash.Add(value: (unit.Active ? 1u : 0u));

            if (!unit.Active) {
                continue;
            }

            hash.Add(value: (unit.Selected ? 1u : 0u));
            hash.Add(value: (unit.HasTarget ? 1u : 0u));
            hash.Add(value: unit.X.Value);
            hash.Add(value: unit.Y.Value);
            hash.Add(value: unit.Z.Value);
            hash.Add(value: unit.TargetX.Value);
            hash.Add(value: unit.TargetZ.Value);
        }

        // The gravity scenario's field walker folds in too: an active bit, plus its queued scripted-walk
        // order (mirrors the RTS unit's HasTarget/Target fields) and its full body state when spawned — the same
        // cell-index/local-offset split every other body's fold uses (see includeCell's remarks above).
        hash.Add(value: ((m_fieldWalker is null) ? 0u : 1u));

        if (m_fieldWalker is { } walker) {
            hash.Add(value: (uint)m_fieldWalkerWalkTicks);
            hash.Add(value: m_fieldWalkerWalkMove.X.Value);
            hash.Add(value: m_fieldWalkerWalkMove.Y.Value);

            if (includeCell) {
                hash.Add(value: walker.Position.CellX);
                hash.Add(value: walker.Position.CellY);
                hash.Add(value: walker.Position.CellZ);
            }

            hash.Add(value: walker.Position.Local.X.Value);
            hash.Add(value: walker.Position.Local.Y.Value);
            hash.Add(value: walker.Position.Local.Z.Value);
            hash.Add(value: walker.Velocity.X.Value);
            hash.Add(value: walker.Velocity.Y.Value);
            hash.Add(value: walker.Velocity.Z.Value);
            hash.Add(value: walker.Up.X.Value);
            hash.Add(value: walker.Up.Y.Value);
            hash.Add(value: walker.Up.Z.Value);
            hash.Add(value: walker.FacingAngle.Value);
            hash.Add(value: (walker.Grounded ? 1u : 0u));
        }

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            hash.Add(value: ((player is null) ? 0u : 1u));

            if (player is null) {
                continue;
            }

            var body = player.Body;

            // The body is fixed-point, so its raw storage is folded directly — exact integers, no float bit
            // reinterpretation, deterministic on every machine. The position is hierarchical: the local offset always
            // contributes; the absolute cell index contributes only to the full StateHash (the cell-invariant hash omits
            // it, so a body in a far cell hashes identically to the same local offset at the origin cell).
            if (includeCell) {
                hash.Add(value: body.Position.CellX);
                hash.Add(value: body.Position.CellY);
                hash.Add(value: body.Position.CellZ);
            }

            hash.Add(value: body.Position.Local.X.Value);
            hash.Add(value: body.Position.Local.Y.Value);
            hash.Add(value: body.Position.Local.Z.Value);
            hash.Add(value: body.Velocity.X.Value);
            hash.Add(value: body.Velocity.Y.Value);
            hash.Add(value: body.Velocity.Z.Value);
            hash.Add(value: body.FacingYaw.Value);
            hash.Add(value: (body.Grounded ? 1u : 0u));
        }

        return hash.Value;
    }
    private int LowestFreeSlot() {
        for (var slot = 0; (slot < MaxPlayers); slot++) {
            if (m_slots[slot] is null) {
                return slot;
            }
        }

        return -1;
    }
}

/// <summary>A player's stable identity paired with its movement body. Its index in <see cref="OverworldWorld.Slots"/>
/// is its dynamic-transform slot.</summary>
public sealed record PlayerSlot(Guid Id, PlatformerBody Body);
