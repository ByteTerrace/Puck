using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The authoritative, DETERMINISTIC simulation state: a fixed array of player slots (a free-list, so a player's slot —
/// its dynamic-transform slot — stays stable while active and recycles when it leaves), a seeded PRNG, and the tick
/// counter. <see cref="Advance"/> is a pure function of the previous state plus the per-slot intents for one tick, so
/// the same intent + roster-event stream always produces the same state — the basis for bit-identical replays. The
/// render side reads <see cref="DynamicTransforms"/> and <see cref="Slots"/> (presentation), never the other way around.
/// <para>
/// The slot count is FIXED at <see cref="MaxPlayers"/> from the first frame: <see cref="DynamicTransforms"/> always
/// returns that many entries (free slots ride a hidden position) and the renderer always emits that many views, so the
/// world compositor's first-frame buffer/viewport sizes never change as players join and leave.
/// </para>
/// </summary>
public sealed class MiniActionWorld {
    /// <summary>The maximum concurrent players — also the fixed dynamic-transform-slot and view-slot count. Matches the
    /// world compositor's <c>MaxViewports</c>; growing past it is a later phase (it needs more than four viewports).</summary>
    public const int MaxPlayers = 4;

    // Free slots park their box far below the floor, outside every camera frustum, so a fixed count of boxes can be
    // built into the program once and the inactive ones simply march against nothing visible.
    private static readonly FixedVector3 HiddenPosition = new(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: -1000L), Z: FixedQ4816.Zero);
    private readonly FixedRoom m_collision;
    private readonly PlatformerTuning m_tuning;
    private readonly FixedQ4816 m_dt;
    private readonly PlayerSlot?[] m_slots = new PlayerSlot?[MaxPlayers];
    private readonly Dictionary<Guid, int> m_slotByPlayer = [];
    private ulong m_tick;
    private uint m_rng;

    /// <summary>Initializes the world. <paramref name="seed"/> seeds the PRNG so generated content is reproducible;
    /// <paramref name="tickSeconds"/> is the fixed simulation step (the host's <c>StepTicks</c> as seconds).</summary>
    public MiniActionWorld(MiniActionRoom room, PlatformerTuning tuning, float tickSeconds, uint seed) {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(tuning);

        // Resolve the authored room to fixed-point collision planes and the tick period to a fixed dt ONCE, so the
        // per-tick step touches no float and is bit-identical across machines.
        m_collision = FixedRoom.From(room: room);
        m_tuning = tuning;
        m_dt = FixedQ4816.FromDouble(value: tickSeconds);
        m_rng = ((seed == 0u) ? 0x9E3779B9u : seed); // a zero seed would freeze xorshift; nudge to a fixed non-zero
    }

    /// <summary>The number of fixed ticks simulated so far.</summary>
    public ulong CurrentTick => m_tick;
    /// <summary>The number of occupied slots.</summary>
    public int ActivePlayerCount { get; private set; }
    /// <summary>The slot array (null == free), for the renderer to read positions in slot order. Presentation only.</summary>
    public IReadOnlyList<PlayerSlot?> Slots => m_slots;

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

        // The spawn is a deterministic function of the slot: a fixed grid offset on the floor.
        var spawn = new FixedVector3(
            X: FixedQ4816.FromInteger(value: (((slot % 2) * 8) - 4)),
            Y: m_collision.FloorTop,
            Z: FixedQ4816.FromInteger(value: (((slot / 2) * 8) - 4))
        );

        m_slots[slot] = new PlayerSlot(Id: playerId, Body: new PlatformerBody(position: spawn));
        m_slotByPlayer[playerId] = slot;
        ActivePlayerCount++;

        return slot;
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

            player.Body.Step(intent: intent, tuning: m_tuning, dt: m_dt, room: m_collision);
        }

        m_tick++;
    }

    /// <summary>The per-frame transforms for the renderer's dynamic-transform buffer — always exactly
    /// <see cref="MaxPlayers"/> entries. An active slot reports its body; a free slot reports a hidden transform.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms() {
        var transforms = new DynamicTransform[MaxPlayers];

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            transforms[slot] = ((player is null)
                ? new DynamicTransform(Position: HiddenPosition.ToVector3(), Orientation: Quaternion.Identity)
                : new DynamicTransform(Position: player.Body.PresentationPosition, Orientation: player.Body.Orientation));
        }

        return transforms;
    }

    /// <summary>A 64-bit FNV-1a hash over the deterministic state — the per-tick probe the determinism/replay gate
    /// compares. Each slot contributes an active-mask bit (so a join/leave changes the hash even when survivors are
    /// unchanged) and, when active, its body. The identity Guid is NOT hashed — the sim is identity-agnostic; replay
    /// validates the roster separately.</summary>
    public ulong StateHash() {
        var hash = Fnv1aHash.Create();

        hash.Add(value: m_tick);
        hash.Add(value: m_rng);

        for (var slot = 0; (slot < MaxPlayers); slot++) {
            var player = m_slots[slot];

            hash.Add(value: ((player is null) ? 0u : 1u));

            if (player is null) {
                continue;
            }

            var body = player.Body;

            // The body is fixed-point, so its raw storage is folded directly — exact integers, no float bit
            // reinterpretation, deterministic on every machine.
            hash.Add(value: body.Position.X.Value);
            hash.Add(value: body.Position.Y.Value);
            hash.Add(value: body.Position.Z.Value);
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

/// <summary>A player's stable identity paired with its movement body. Its index in <see cref="MiniActionWorld.Slots"/>
/// is its dynamic-transform slot.</summary>
public sealed record PlayerSlot(Guid Id, PlatformerBody Body);
