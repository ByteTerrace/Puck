using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>A live identity backed by one owned <see cref="WorldDefinition"/>.</summary>
public sealed class WorldIdentity {
    private readonly float m_noseFactor;
    private readonly string m_neutralColor;
    private FixedQ4816 m_moveSpeed;
    private FixedQ4816 m_turnSpeed;

    /// <summary>Builds an identity from an owned world.</summary>
    /// <param name="document">The owned world.</param>
    /// <param name="defaults">The player defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="defaults"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="document"/>'s <see cref="WorldDefinition.Identity"/> is <see langword="null"/>.</exception>
    public WorldIdentity(WorldDefinition document, WorldPlayerDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: document);
        ArgumentNullException.ThrowIfNull(argument: defaults);
        var identity = document.Identity ?? throw new InvalidOperationException(message: "an owned world requires identity");
        Document = document;
        Id = identity.Id;
        Name = identity.Name;
        ColorHex = identity.Color;
        Color = ParseColor(hex: identity.Color, fallbackHex: defaults.NeutralColor);
        m_moveSpeed = ReadFixed(document.State, identity.MoveSpeedState, document.Motion.MoveSpeed);
        m_turnSpeed = ReadFixed(document.State, identity.TurnSpeedState, document.Motion.TurnSpeed);
        Bindings = document.BindingOverlays.FirstOrDefault()?.Document;
        Hud = document.Hud.Panels.FirstOrDefault();
        // Control feel travels with the profile exactly as the two layers above do: read off this identity's OWN
        // document, delivered on the same selection that delivers its bindings and HUD.
        SeatLook = document.PlayerDefaults.SeatLook;
        m_noseFactor = defaults.NoseFactor;
        m_neutralColor = defaults.NeutralColor;
    }

    private WorldIdentity(string name, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, WorldPlayerDefaults defaults) {
        Id = name;
        Name = name;
        ColorHex = defaults.NeutralColor;
        Color = ParseColor(hex: ColorHex, fallbackHex: ColorHex);
        m_moveSpeed = moveSpeed;
        m_turnSpeed = turnSpeed;
        m_noseFactor = defaults.NoseFactor;
        m_neutralColor = defaults.NeutralColor;
    }

    /// <summary>Gets the owned world, or <see langword="null"/> for a replay-pinned identity.</summary>
    public WorldDefinition? Document { get; private set; }
    /// <summary>Gets the stable identity/world id.</summary>
    public string Id { get; }
    /// <summary>Gets the display name.</summary>
    public string Name { get; private set; }
    /// <summary>Gets the authored color.</summary>
    public string ColorHex { get; private set; }
    /// <summary>Gets the parsed body color.</summary>
    public Vector3 Color { get; private set; }
    /// <summary>Gets the accent color.</summary>
    public Vector3 NoseColor => (Color * m_noseFactor);
    /// <summary>Gets the locomotion speed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not finite and positive.</exception>
    public float MoveSpeed { get => (float)(double)m_moveSpeed; set { m_moveSpeed = RequirePositiveRate(value: value, name: nameof(MoveSpeed)); WriteFixed(slot: Document?.Identity?.MoveSpeedState, value: m_moveSpeed); } }
    /// <summary>Gets the deterministic locomotion speed.</summary>
    public FixedQ4816 FixedMoveSpeed => m_moveSpeed;
    /// <summary>Gets the turn speed.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The assigned value is not finite and positive.</exception>
    public float TurnSpeed { get => (float)(double)m_turnSpeed; set { m_turnSpeed = RequirePositiveRate(value: value, name: nameof(TurnSpeed)); WriteFixed(slot: Document?.Identity?.TurnSpeedState, value: m_turnSpeed); } }
    /// <summary>Gets the deterministic turn speed.</summary>
    public FixedQ4816 FixedTurnSpeed => m_turnSpeed;
    /// <summary>Gets the identity-owned binding layer.</summary>
    public BindingProfileDocument? Bindings { get; set; }
    /// <summary>Gets the identity-owned private HUD panel.</summary>
    public WorldHudPanel? Hud { get; set; }
    /// <summary>Gets this identity's control feel — the orbit response its seat wakes with, carried on the identity so
    /// it follows the player rather than the world.</summary>
    /// <remarks>Null for exactly one case: a replay-pinned identity (<see cref="Pinned"/>), which has no
    /// <see cref="Document"/> to carry one. A pinned identity exists to re-drive a recorded tape offline, where there
    /// is no camera and nothing reads a feel, so the absence is a statement that this identity HAS no feel rather than
    /// a value left unset. Null resolves to the world document's own
    /// <see cref="WorldPlayerDefaults.SeatLook"/> — the portable input preference selected by the occupied seat.
    /// That is also the answer BEFORE a profile has been delivered for a seat, which is the same
    /// answer whether the profile is about to arrive in-process or across a link: nothing here assumes an identity
    /// document can only be built locally.</remarks>
    public WorldSeatLook? SeatLook { get; set; }

    /// <summary>Changes display identity in the owned world.</summary>
    /// <param name="name">The new display name.</param>
    /// <param name="colorHex">The new authored color, as <c>#RRGGBB</c>.</param>
    public void SetIdentity(string name, string colorHex) {
        Name = name;
        ColorHex = colorHex;
        Color = ParseColor(hex: colorHex, fallbackHex: m_neutralColor);
        if (Document?.Identity is { } identity) {
            Document = Document with { Identity = identity with { Name = name, Color = colorHex } };
        }
    }

    /// <summary>Mints a detached replay identity with bit-exact rates.</summary>
    /// <param name="name">The display name.</param>
    /// <param name="moveSpeed">The deterministic locomotion speed.</param>
    /// <param name="turnSpeed">The deterministic turn speed.</param>
    /// <param name="defaults">The player defaults.</param>
    /// <returns>The detached identity.</returns>
    public static WorldIdentity Pinned(string name, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, WorldPlayerDefaults defaults) =>
        new(name: name, moveSpeed: moveSpeed, turnSpeed: turnSpeed, defaults: defaults);

    /// <summary>Reads a durable value from the owned world's state section.</summary>
    /// <remarks>The row FIND is the shared <see cref="WorldDefinitionRows.FindStateRow"/>. The VALUE read stays here
    /// rather than routing through <see cref="WorldStateReader"/>: an identity document is a durable store with no
    /// server and no tick, so it has nothing honest to pass as the tick the shared reader carries. Folding this side
    /// onto that seam is a design decision about what an identity's state means over time, not a consolidation.</remarks>
    /// <param name="name">The state row name.</param>
    /// <param name="row">The matching row; meaningful only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the owned world has a state row named <paramref name="name"/>.</returns>
    public bool TryReadState(string name, out WorldStateRow row) {
        row = ((Document is { } document) ? WorldDefinitionRows.FindStateRow(rows: document.State, name: name)! : null!);
        return row is not null;
    }

    /// <summary>Replaces or adds one durable state row.</summary>
    /// <param name="row">The state row to write.</param>
    public void WriteState(WorldStateRow row) {
        if (Document is null) {
            return;
        }
        var state = Document.State.Where(candidate => !string.Equals(a: candidate.Name, b: row.Name, comparisonType: StringComparison.Ordinal)).Append(row).ToArray();
        Document = Document with { State = state };
    }

    /// <summary>Replaces the backing owned world after a composed edit.</summary>
    /// <param name="document">The replacement owned world.</param>
    public void ReplaceDocument(WorldDefinition document) => Document = document;

    /// <summary>Appends one text cell to a bounded, evicting keyed row already declared on this identity's document
    /// — the ONE append primitive a self-authored chat log and a cross-document delivery into a bounded inbox both
    /// use (see <c>Server.WorldOwnedWorlds.Decide</c>'s text arm), so the two can never disagree about eviction
    /// order, key uniqueness, or determinism. The cell's key comes from <paramref name="rowName"/>'s own derived
    /// <c>&lt;row&gt;-seq</c> monotonic counter (see the private sequence minter below), never a tick or wall-clock
    /// value — two fresh runs of the identical command sequence always mint identical keys.</summary>
    /// <param name="rowName">The already-declared bounded, evicting text row to append to.</param>
    /// <param name="text">The text to append.</param>
    /// <param name="evictedKey">The evicted key, or <see langword="null"/> when nothing was evicted.</param>
    /// <param name="reason">Why the append was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the append applied.</returns>
    public bool TryAppendEvictingText(WorldCellName rowName, string text, out WorldCellName? evictedKey, out string reason) {
        evictedKey = null;

        if (!TryReadState(name: rowName, row: out var row)) {
            reason = $"no state row named '{rowName}'";

            return false;
        }

        if (row is not { Kind: CellKind.Text, Evicts: true, Capacity: not null }) {
            reason = $"state row '{rowName}' is not a bounded, evicting text row";

            return false;
        }

        if (!TryNextSequenceKey(rowName: rowName, key: out var key, reason: out reason)) {
            return false;
        }

        if (!WorldStateCellWriter.TryComposeTextCell(row: row, key: key, text: text, cells: out var cells, evictedKey: out evictedKey, reason: out reason)) {
            return false;
        }

        WriteState(row: row with { Cells = cells });

        return true;
    }

    // Mints the next monotonic sequence key for rowName's derived "<row>-seq" Int slot counter — reads its current
    // value (0 if the counter row does not exist yet), increments it, persists the increment, and returns the
    // incremented value's decimal string as a WorldCellName. Deterministic: no wall clock, no RNG — the identical
    // command sequence always mints the identical keys, on every run and every replay.
    private bool TryNextSequenceKey(WorldCellName rowName, out WorldCellName key, out string reason) {
        var seqRowName = WorldCellName.Parse(candidate: $"{rowName}-seq");
        var next = 1L;

        if (TryReadState(name: seqRowName, row: out var seqRow)) {
            if (seqRow is not { Kind: CellKind.Int, IsSlot: true }) {
                key = default;
                reason = $"state row '{seqRowName}' is not an int slot";

                return false;
            }

            next = checked(seqRow.Cells![0].Value + 1);
        }

        WriteState(row: new WorldStateRow(Name: seqRowName, Kind: CellKind.Int, NonNegative: true, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: next)]));

        return WorldCellName.TryParse(candidate: next.ToString(provider: CultureInfo.InvariantCulture), name: out key, reason: out reason);
    }

    // The type-level wall for a live locomotion rate: the verb door (identity.motion) refuses this range with a
    // named console error before any assignment, so reaching this throw means a NEW caller wrote the property
    // without walking a door — the invariant lives here so no door can be forgotten.
    private static FixedQ4816 RequirePositiveRate(float value, string name) {
        if (!float.IsFinite(f: value) || (value <= 0f)) {
            throw new ArgumentOutOfRangeException(paramName: name, actualValue: value, message: "a locomotion rate must be finite and positive");
        }

        return FixedQ4816.FromDouble(value: value);
    }

    private void WriteFixed(WorldCellName? slot, FixedQ4816 value) {
        if (slot is { } name) {
            WriteState(row: new WorldStateRow(Name: name, Kind: CellKind.Fixed, Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: value.Value)]));
        }
    }

    private static FixedQ4816 ReadFixed(IReadOnlyList<WorldStateRow> rows, string name, float fallback) =>
        (WorldDefinitionRows.FindStateRow(rows: rows, name: name) is { Kind: CellKind.Fixed, IsSlot: true } row)
            ? FixedQ4816.FromRawBits(value: row.Cells![0].Value)
            : FixedQ4816.FromDouble(value: fallback);

    /// <summary>Parses a hex color with a fallback.</summary>
    /// <param name="hex">The <c>#RRGGBB</c> hex color to parse.</param>
    /// <param name="fallbackHex">The <c>#RRGGBB</c> hex color used when <paramref name="hex"/> does not parse.</param>
    /// <returns>The parsed RGB color, or a neutral gray when neither <paramref name="hex"/> nor <paramref name="fallbackHex"/> parses.</returns>
    public static Vector3 ParseColor(string hex, string fallbackHex) {
        var value = (TryParseHex(hex: hex, color: out var parsed) ? parsed : (TryParseHex(hex: fallbackHex, color: out var fallback) ? fallback : 0x808080));
        return new Vector3(x: ((value >> 16) & 0xff) / 255f, y: ((value >> 8) & 0xff) / 255f, z: (value & 0xff) / 255f);
    }

    private static bool TryParseHex(string hex, out int color) {
        color = 0;
        return (hex is { Length: 7 }) && (hex[0] == '#')
            && int.TryParse(s: hex.AsSpan(start: 1), style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out color);
    }
}
