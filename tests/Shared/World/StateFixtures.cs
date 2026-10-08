
namespace Puck.World.Testing;

/// <summary>The suite's one construction of scalar state rows and its one read of a live slot: a law states the
/// row's name and value, never the cell layout a slot row carries.</summary>
internal static partial class StateFixtures {
    /// <summary>Builds a state comparison gate: <paramref name="state"/> compared against a literal
    /// <paramref name="value"/> or another state channel.</summary>
    /// <param name="state">The compared state channel.</param>
    /// <param name="comparison">The comparison.</param>
    /// <param name="value">The literal comparand, or <see langword="null"/> when a state comparand is named.</param>
    /// <param name="key">The compared channel's cell key, or <see langword="null"/> for its slot.</param>
    /// <param name="comparandState">The comparand state channel, or <see langword="null"/> for a literal.</param>
    /// <param name="comparandKey">The comparand channel's cell key, or <see langword="null"/> for its slot.</param>
    /// <returns>The predicate.</returns>
    public static ActionPredicate.CompareState Compare(string state, ExpressionOp comparison, decimal? value = null, string? key = null, string? comparandState = null, string? comparandKey = null) => new(
        ComparandKey: StateChannelRef.OfNullable(spelling: comparandKey),
        ComparandState: StateChannelRef.OfNullable(spelling: comparandState),
        Comparison: comparison,
        Key: StateChannelRef.OfNullable(spelling: key),
        State: state,
        Value: value
    );
    /// <summary>Builds an Int or Bool cell under <paramref name="key"/>.</summary>
    /// <param name="key">The cell key.</param>
    /// <param name="value">The value; for a Bool cell, nonzero is true.</param>
    /// <param name="kind">The cell kind, Int or Bool.</param>
    /// <returns>The cell.</returns>
    public static StateCell Cell(string key, long value = 1, CellKind kind = CellKind.Int) => new(
        Key: CellName.Parse(candidate: key),
        Value: ((kind == CellKind.Bool)
            ? CellValue.Bool(value: (value != 0L))
            : CellValue.Int(value: value))
    );
    /// <summary>Builds a Fixed slot row: one cell under <c>SlotKey</c>.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="rawBits">The Q48.16 raw bits of the initial value.</param>
    /// <returns>The row.</returns>
    public static WorldStateRow FixedSlot(string name, long rawBits) => Slot(
        kind: CellKind.Fixed,
        name: name,
        value: CellValue.Fixed(rawBits: rawBits)
    );
    /// <summary>Builds an Int slot row: one cell under <c>SlotKey</c>.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="value">The initial value.</param>
    /// <param name="min">The row's floor, or <see langword="null"/> for none.</param>
    /// <returns>The row.</returns>
    public static WorldStateRow IntSlot(string name, long value = 0, long? min = null) => Slot(
        kind: CellKind.Int,
        min: min,
        name: name,
        value: CellValue.Int(value: value)
    );
    /// <summary>Builds a slot row of any kind: one cell under <c>SlotKey</c>.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="kind">The row's cell kind.</param>
    /// <param name="value">The initial value, of <paramref name="kind"/>.</param>
    /// <param name="min">The row's floor, or <see langword="null"/> for none.</param>
    /// <param name="advance">The row's per-second advance, or <see langword="null"/> for a row that holds its value.</param>
    /// <returns>The row.</returns>
    public static WorldStateRow Slot(string name, CellKind kind, CellValue value, long? min = null, StateAdvance? advance = null) => new(
        Name: CellName.Parse(candidate: name),
        Kind: kind,
        Min: min,
        Advance: advance,
        Cells: [new StateCell(
            Key: WorldStateRow.SlotKey,
            Value: value
        )]
    );
    /// <summary>Builds a Text slot row: one cell under <c>SlotKey</c>.</summary>
    /// <param name="name">The row name.</param>
    /// <param name="text">The initial text.</param>
    /// <returns>The row.</returns>
    public static WorldStateRow TextSlot(string name, string text) => Slot(
        kind: CellKind.Text,
        name: name,
        value: CellValue.Text(value: text)
    );
}
