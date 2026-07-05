using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The authoritative, DETERMINISTIC simulation state: a fixed array of player slots (a free-list, so a player's slot —
/// its dynamic-transform slot — stays stable while active and recycles when it leaves), a seeded PRNG, and the tick
/// counter. <see cref="Advance"/> is a pure function of the previous state plus the per-slot intents for one tick, so
/// the same intent + roster-event stream always produces the same state — the basis for bit-identical replays. The
/// render side reads <see cref="DynamicTransforms"/> and <see cref="Slots"/> (presentation), never the other way around.
/// <para>
/// The slot count is FIXED at <see cref="MaxPlayers"/> from the first frame: <see cref="DynamicTransforms"/> always
/// returns that many entries (free slots ride a hidden position) and the screen director always emits its fixed
/// <see cref="ScreenDirector.ViewCount"/> views, so the world compositor's first-frame buffer/viewport sizes never
/// change as players join and leave.
/// </para>
/// </summary>
public sealed class OverworldWorld {
    /// <summary>The maximum concurrent players — also the fixed dynamic-transform slot count for player boxes. Matches
    /// the room's console capacity (one pane per player under the compositor's room-plus-panes viewport budget);
    /// growing past it is a later phase.</summary>
    public const int MaxPlayers = 4;

    /// <summary>The number of cartridge TYPES a cabinet can hold (custom / camera / showcase), cycled at the cabinet.</summary>
    public const int CartTypeCount = 3;

    private readonly FixedRoom m_collision;
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

    /// <summary>Initializes the world. <paramref name="seed"/> seeds the PRNG so generated content is reproducible;
    /// <paramref name="tickSeconds"/> is the fixed simulation step (the host's <c>StepTicks</c> as seconds). The optional
    /// spawn cell (<paramref name="spawnCellX"/>/<paramref name="spawnCellY"/>/<paramref name="spawnCellZ"/>) places the
    /// whole room at an arbitrary world cell (default the origin cell); the simulation is cell-agnostic, so a far cell
    /// reproduces the SAME per-tick local motion while proving the planet-scale coordinate seam.
    /// <paramref name="preInsertedConsoles"/> marks which consoles (by index, length must equal the room's console
    /// count) start with a cartridge already seated — the unified cartridge table's first indices, assigned in console
    /// order to exactly those consoles. <paramref name="libraryCount"/> is the shelf's cartridge count (must equal the
    /// room's shelf-slot count); library cartridges occupy the remaining unified indices, one per shelf slot, and start
    /// located there. <paramref name="spawnAtConsoles"/> moves player slot i's spawn (for i below the console count) to
    /// standing in front of console stand i — the IMMERSED start's seating positions; slots past the console count keep
    /// the default grid. Pure configuration (the spawn stays a deterministic function of the slot), and the default
    /// keeps every pre-existing hash byte-identical.</summary>
    public OverworldWorld(OverworldRoom room, PlatformerTuning tuning, float tickSeconds, uint seed, long spawnCellX = 0L, long spawnCellY = 0L, long spawnCellZ = 0L, bool startLoaded = false, bool spawnAtConsoles = false, int startCartType = -1) {
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
        // player (world-lens) — so a per-player seating-boot inserts the right cart. Otherwise the last type (the
        // showcase ROM) is the loaded default, and empty cabinets stagger their selection across the cart cycle.
        var uniform = ((startCartType >= 0) && (startCartType < CartTypeCount));
        var bootType = (uniform ? startCartType : (CartTypeCount - 1));

        for (var console = 0; (console < consoleCount); console++) {
            // Empty by default (the overworld: you insert a cart to bring a cabinet alive). The selected type is uniform
            // when a startCartType is set, else staggered so the overworld's cabinets each offer a different cart by
            // default (world-lens / camera / showcase / world-lens).
            m_consoleSelectedType[console] = (uniform ? bootType : (startLoaded ? bootType : (console % CartTypeCount)));

            if (startLoaded) {
                m_consoleLoadedType[console] = bootType;
                m_bootedMask |= (1u << console);
                m_bootOrder.Add(item: console);
            } else {
                m_consoleLoadedType[console] = -1;
            }
        }
    }

    /// <summary>The number of fixed ticks simulated so far.</summary>
    public ulong CurrentTick => m_tick;
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

    /// <summary>Ejects a cabinet: removes its cart and un-boots it (the presentation eases its pane closed). Two-way —
    /// unlike the old one-way boot, a cabinet returns to empty. Idempotent on an already-empty cabinet.</summary>
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
        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            if (player is null) {
                continue;
            }

            var intent = ((slot < intentsBySlot.Count) ? intentsBySlot[slot] : PlayerIntent.None);

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

        m_tick++;
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
