using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>The closed set of cell value kinds a <see cref="T:Puck.World.WorldStateRow"/> declares, shared by every cell the row
/// carries. Carries no float kind: simulation state is float-free by the determinism contract (see
/// <see cref="Fixed"/> for how a fractional value still rides here). A counter is represented as
/// <see cref="Fixed"/>; a timer is <see cref="Int"/> with <see cref="P:Puck.World.WorldStateRow.NonNegative"/> set.</summary>
[JsonConverter(typeof(StrictEnumConverter<CellKind>))]
public enum CellKind : byte {
    /// <summary>A whole 64-bit signed integer cell (a score, a round counter, an inventory count, or — with
    /// <see cref="P:Puck.World.WorldStateRow.NonNegative"/> set — a tick-count timer).</summary>
    Int,

    /// <summary>A fixed-point cell holding raw <c>FixedQ4816</c> bits — the deterministic replacement for a float in
    /// simulation state. Human-authored surfaces (document JSON, console verb arguments, validator refusal text,
    /// read-back echoes) use the decimal representation via <c>FixedQ4816.TryParse</c>/<c>ToString</c>; only the
    /// addon ABI channel wire and the per-cell mutation payload carry the raw bit pattern.</summary>
    Fixed,

    /// <summary>A boolean cell (a win flag, a toggle). Carries no range — a gauge cannot bind to it.</summary>
    Bool,

    /// <summary>A short-text cell (a status label, a player name slot), bounded to
    /// <see cref="F:Puck.World.WorldStateCapacity.MaxTextValueLength"/> UTF-16 code units. Carries no range — a gauge cannot bind
    /// to it. The only kind whose value is carried in <see cref="P:Puck.World.WorldStateCell.Text"/> rather than
    /// <see cref="P:Puck.World.WorldStateCell.Value"/>.</summary>
    Text,
}
