using System.Globalization;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>The most slots a history ring keeps.</summary>
    public const int MaxHistoryCapacity = WorldStateCapacity.MaxCellsPerRow;

    // A history row is a plain numeric ring: its cells are the slots 0..capacity-1 and nothing else, and every
    // other storage or time trait is refused so a push is the one way the ring changes.
    private static void ValidateHistory(WorldStateRow row, string path, List<string> errors) {
        if (row.History is not { } history) {
            if (row.HistoryCursor != 0L) {
                errors.Add(item: $"{path} ('{row.Name}') declares historyCursor without history — historyCursor is engine bookkeeping for a history ring alone.");
            }
            return;
        }
        if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            errors.Add(item: $"{path} ('{row.Name}') history requires an integer or fixed row.");
        }
        if (history.Capacity < 1 || history.Capacity > MaxHistoryCapacity) {
            errors.Add(item: $"{path} ('{row.Name}') history.capacity {history.Capacity} must be between 1 and {MaxHistoryCapacity}.");
        }
        if (row.Capacity is { } declared && declared != history.Capacity) {
            errors.Add(item: $"{path} ('{row.Name}') history.capacity {history.Capacity} disagrees with the row's capacity {declared}.");
        }
        if (row.Board is not null || row.Zone is not null || row.Tokens is not null || row.Phase is not null || row.KeysFrom is not null || row.ValuesFrom is not null ||
            row.Lattice is not null || row.Draw is not null || row.Advance is not null || row.Cycle is not null || row.Dynamics is not null || row.Evicts) {
            errors.Add(item: $"{path} ('{row.Name}') history admits no other storage or time trait on the row.");
        }
        if (row.ClampToEnvelope(history.Empty) != history.Empty) {
            errors.Add(item: $"{path} ('{row.Name}') history.empty {history.Empty} is outside the row's envelope.");
        }
        if (row.HistoryCursor < 0L) {
            errors.Add(item: $"{path} ('{row.Name}') historyCursor {row.HistoryCursor} is negative.");
        }
        foreach (var cell in row.Cells ?? []) {
            if (cell is null) {
                continue;
            }
            if (!int.TryParse(cell.Key.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot >= history.Capacity) {
                errors.Add(item: $"{path} ('{row.Name}') history cell '{cell.Key}' is not a ring slot 0..{history.Capacity - 1}.");
            }
            if (cell.Advance is not null || cell.Cycle is not null || cell.Dynamics is not null) {
                errors.Add(item: $"{path} ('{row.Name}') history cell '{cell.Key}' carries a time trait; a ring slot is a plain value.");
            }
        }
    }
}
