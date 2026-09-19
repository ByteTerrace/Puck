using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World.Server;

/// <summary>The arena slot lanes every body's named action state is stored in: a slot declared in <c>state.body</c>
/// rides the participant lane, one declared in <c>state.identity</c> rides the identity lane, and both are addressed
/// by the body's own entity index.</summary>
/// <remarks>The register file is a world-wide product of the two declaration lists, so the slot-to-row map is
/// resolved once per installed catalog rather than per body or per kit. Both lanes are sized to the population's
/// capacity, so every entity index addresses both.</remarks>
public sealed class WorldActionStateLane {
    private StateArena? m_arena;
    private CompiledActionStateSlot[] m_definitions = [];
    private int[] m_rows = [];

    /// <summary>Gets the compiled register file the lanes answer, in slot order.</summary>
    public IReadOnlyList<CompiledActionStateSlot> Definitions => m_definitions;
    /// <summary>Gets a value indicating whether an arena has been bound.</summary>
    public bool IsBound => (m_arena is not null);

    /// <summary>Binds the arena the lanes are stored in and resolves every register slot's row.</summary>
    /// <param name="arena">The server's columnar store.</param>
    /// <param name="definitions">The world's compiled register file.</param>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> or <paramref name="definitions"/> is
    /// <see langword="null"/>.</exception>
    public void Bind(StateArena arena, CompiledActionStateSlot[] definitions) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: definitions);

        var catalog = arena.Catalog;
        var rows = new int[definitions.Length];

        for (var slot = 0; (slot < definitions.Length); slot++) {
            rows[slot] = (catalog.TryResolve(
                handle: out var handle,
                lane: LaneOf(lifetime: definitions[slot].Lifetime),
                name: definitions[slot].Name
            )
                ? handle.Ordinal
                : -1
            );
        }

        m_arena = arena;
        m_definitions = definitions;
        m_rows = rows;
    }
    /// <summary>Returns whether one entity index has joined both lanes.</summary>
    /// <param name="ordinal">The entity index.</param>
    /// <returns><see langword="true"/> when the index is joined.</returns>
    public bool IsJoined(int ordinal) => ((m_arena is { } arena) && arena.IsJoined(
        lane: StateLane.Participant,
        ordinal: ordinal
    ));
    /// <summary>Admits one entity index to both lanes and births every register slot at its authored initial value.
    /// A already-joined index keeps what it holds.</summary>
    /// <param name="ordinal">The entity index.</param>
    public void Join(int ordinal) {
        if (
            (m_arena is not { } arena) ||
            IsJoined(ordinal: ordinal)
        ) {
            return;
        }

        _ = arena.TryJoin(
            lane: StateLane.Participant,
            ordinal: ordinal,
            reason: out _
        );
        _ = arena.TryJoin(
            lane: StateLane.Identity,
            ordinal: ordinal,
            reason: out _
        );
        Birth(ordinal: ordinal);
    }
    /// <summary>Releases one entity index from both lanes, clearing every slot it answered.</summary>
    /// <param name="ordinal">The entity index.</param>
    public void Leave(int ordinal) {
        if (m_arena is not { } arena) {
            return;
        }

        _ = arena.TryLeave(
            lane: StateLane.Participant,
            ordinal: ordinal,
            reason: out _
        );
        _ = arena.TryLeave(
            lane: StateLane.Identity,
            ordinal: ordinal,
            reason: out _
        );
    }
    /// <summary>Writes every register slot's authored initial value for one entity index.</summary>
    /// <param name="ordinal">The entity index.</param>
    public void Birth(int ordinal) {
        for (var slot = 0; (slot < m_definitions.Length); slot++) {
            Write(
                ordinal: ordinal,
                raw: InitialRaw(definition: in m_definitions[slot]),
                slot: slot
            );
        }
    }
    /// <summary>Reads one register slot's stored raw value.</summary>
    /// <param name="slot">The register-file slot.</param>
    /// <param name="ordinal">The entity index.</param>
    /// <returns>The raw counter bits or timer ticks, zero when the slot or the index answers nothing.</returns>
    public long Read(int slot, int ordinal) {
        if (
            (m_arena is not { } arena) ||
            (((uint)slot) >= ((uint)m_rows.Length)) ||
            (m_rows[slot] < 0) ||
            !arena.TryReadSlot(
            ordinal: ordinal,
            rowOrdinal: m_rows[slot],
            value: out var value
        )
        ) {
            return 0L;
        }

        return ((value.Kind == CellKind.Fixed)
            ? value.AsFixed
            : value.AsInt
        );
    }
    /// <summary>Reads one counter slot.</summary>
    /// <param name="slot">The register-file slot.</param>
    /// <param name="ordinal">The entity index.</param>
    /// <returns>The counter value.</returns>
    public FixedQ4816 Counter(int slot, int ordinal) => FixedQ4816.FromRawBits(value: Read(
        ordinal: ordinal,
        slot: slot
    ));
    /// <summary>Reads one timer slot.</summary>
    /// <param name="slot">The register-file slot.</param>
    /// <param name="ordinal">The entity index.</param>
    /// <returns>The remaining engine ticks.</returns>
    public ulong Timer(int slot, int ordinal) => unchecked((ulong)Math.Max(
        val1: 0L,
        val2: Read(
            ordinal: ordinal,
            slot: slot
        )
    ));
    /// <summary>Stores one register slot's raw value.</summary>
    /// <param name="slot">The register-file slot.</param>
    /// <param name="ordinal">The entity index.</param>
    /// <param name="raw">The raw counter bits or timer ticks.</param>
    public void Write(int slot, int ordinal, long raw) {
        if (
            (m_arena is not { } arena) ||
            (((uint)slot) >= ((uint)m_rows.Length)) ||
            (m_rows[slot] < 0)
        ) {
            return;
        }

        _ = arena.TryWriteSlot(
            operand: raw,
            ordinal: ordinal,
            reason: out _,
            rowOrdinal: m_rows[slot],
            write: StateWriteKind.Set
        );
    }
    /// <summary>Returns the raw value a register slot is born at.</summary>
    /// <param name="definition">The compiled slot.</param>
    /// <returns>The raw counter bits or timer ticks.</returns>
    public static long InitialRaw(in CompiledActionStateSlot definition) => ((definition.Kind == ActionStateKind.Counter)
        ? definition.InitialValue.Value
        : checked((long)definition.InitialTicks)
    );
    /// <summary>Returns the lane a declared lifetime is stored in.</summary>
    /// <param name="lifetime">The slot's declared lifetime.</param>
    /// <returns>The lane.</returns>
    public static StateLane LaneOf(ActionStateLifetime lifetime) => ((lifetime == ActionStateLifetime.Durable)
        ? StateLane.Identity
        : StateLane.Participant
    );
}
