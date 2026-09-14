namespace Puck.State;

/// <summary>
/// Which value-over-time trait, if any, governs one cell — resolved once here from the cell's own declaration and
/// its row's default, so every consumer (<see cref="StateReader"/>, <see cref="StateFrame"/>, the document
/// validator, rebase/settle, JSON conversion, save capture) agrees on which trait applies to a cell and reads its
/// timing state off the identical place (<see cref="StateCell.Clock"/>).
/// </summary>
public readonly struct EffectiveBehavior {
    /// <summary>Gets the resolved <see cref="StateAdvance"/>, or <see langword="null"/> when this is not the
    /// effective behavior.</summary>
    public StateAdvance? Advance { get; private init; }
    /// <summary>Gets the resolved <see cref="StateDynamics"/>, or <see langword="null"/> when this is not the
    /// effective behavior.</summary>
    public StateDynamics? Dynamics { get; private init; }
    /// <summary>Gets the resolved <see cref="StateCycle"/>, or <see langword="null"/> when this is not the
    /// effective behavior.</summary>
    public StateCycle? Cycle { get; private init; }
    /// <summary>Gets a value indicating whether no trait applies.</summary>
    public bool IsNone => ((Advance is null) && (Dynamics is null) && (Cycle is null));

    /// <summary>The resolved absence of a trait.</summary>
    public static readonly EffectiveBehavior None = default;

    /// <summary>Wraps a resolved <see cref="StateAdvance"/>.</summary>
    public static EffectiveBehavior OfAdvance(StateAdvance advance) => new() { Advance = advance };
    /// <summary>Wraps a resolved <see cref="StateDynamics"/>.</summary>
    public static EffectiveBehavior OfDynamics(StateDynamics dynamics) => new() { Dynamics = dynamics };
    /// <summary>Wraps a resolved <see cref="StateCycle"/>.</summary>
    public static EffectiveBehavior OfCycle(StateCycle cycle) => new() { Cycle = cycle };
    /// <summary>Resolves <paramref name="cell"/>'s effective behavior against its carrying <paramref name="row"/>:
    /// the cell's own trait when it declares one, none when the cell opts out with
    /// <see cref="StateCellBehavior.None"/>, else the row's own default. A cell keyed
    /// <see cref="StateRow.SlotKey"/> — a slot row's one cell — never carries an override of its own; its
    /// behavior is always the row's default.</summary>
    /// <param name="row">The carrying row.</param>
    /// <param name="cell">The cell, or <see langword="null"/> for one that does not exist yet (a bare row read, or
    /// a key a write is about to mint) — resolves to the row's own default exactly as an existing cell with no
    /// override would.</param>
    public static EffectiveBehavior Resolve(StateRow row, StateCell? cell) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (
            (cell is not null) &&
            (cell.Key != StateRow.SlotKey)
        ) {
            if (cell.Behavior == StateCellBehavior.None) {
                return None;
            }
            if (cell.Advance is { } cellAdvance) {
                return OfAdvance(advance: cellAdvance);
            }
            if (cell.Dynamics is { } cellDynamics) {
                return OfDynamics(dynamics: cellDynamics);
            }
            if (cell.Cycle is { } cellCycle) {
                return OfCycle(cycle: cellCycle);
            }
        }

        if (row.Advance is { } rowAdvance) {
            return OfAdvance(advance: rowAdvance);
        }
        if (row.Dynamics is { } rowDynamics) {
            return OfDynamics(dynamics: rowDynamics);
        }
        if (row.Cycle is { } rowCycle) {
            return OfCycle(cycle: rowCycle);
        }

        return None;
    }
}
