using System.Globalization;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>The most slots a history ring keeps.</summary>
    public const int MaxHistoryCapacity = WorldStateCapacity.MaxCellsPerRow;

    // A history row is a plain numeric ring: its cells are the slots 0..capacity-1 and nothing else, and every
    // other storage or time trait is refused so a push is the one way the ring changes.
    private static void ValidateHistory(WorldStateRow row, string path, List<string> errors) {
        if (row.EffectiveDomain is not WorldStateDomain.Ring history) {
            if (row.HistoryCursor != 0L) {
                errors.Add(item: $"{path} ('{row.Name}') declares historyCursor without a ring domain — historyCursor is engine bookkeeping for a history ring alone.");
            }
            return;
        }
        if (row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            errors.Add(item: $"{path} ('{row.Name}') a ring requires an integer or fixed row.");
        }
        if (history.Capacity < 1 || history.Capacity > MaxHistoryCapacity) {
            errors.Add(item: $"{path} ('{row.Name}') domain.capacity {history.Capacity} must be between 1 and {MaxHistoryCapacity}.");
        }
        if (row.Capacity is not null) {
            errors.Add(item: $"{path} ('{row.Name}') a ring's capacity lives on its domain, not the row's own 'capacity' member.");
        }
        if (row.ValuesFrom is not null || row.Phase is not null ||
            row.Field is not null || row.Draw is not null || row.Advance is not null || row.Cycle is not null || row.Dynamics is not null || row.Evicts) {
            errors.Add(item: $"{path} ('{row.Name}') a ring admits no other storage or time trait on the row.");
        }
        if (row.ClampToEnvelope(history.Empty) != history.Empty) {
            errors.Add(item: $"{path} ('{row.Name}') history.empty {history.Empty} is outside the row's envelope.");
        }
        if (row.HistoryCursor < 0L) {
            errors.Add(item: $"{path} ('{row.Name}') historyCursor {row.HistoryCursor} is negative.");
        }
        // The cells are the slots 0..n-1 in slot order, so a push and an age read index the list directly.
        var cells = row.Cells ?? [];
        if (cells.Count > history.Capacity) {
            errors.Add(item: $"{path} ('{row.Name}') history holds {cells.Count} cells past its capacity {history.Capacity}.");
        }
        if (row.HistoryCursor < history.Capacity && cells.Count > row.HistoryCursor) {
            errors.Add(item: $"{path} ('{row.Name}') history holds {cells.Count} cells but its cursor {row.HistoryCursor} says fewer were ever pushed.");
        }
        for (var slot = 0; slot < cells.Count; slot++) {
            var cell = cells[slot];
            if (cell is null) {
                continue;
            }
            if (!int.TryParse(cell.Key.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed != slot) {
                errors.Add(item: $"{path} ('{row.Name}') history cell {slot} is keyed '{cell.Key}'; ring cells are the slots 0..n-1 in order.");
            }
            if (cell.Advance is not null || cell.Cycle is not null || cell.Dynamics is not null) {
                errors.Add(item: $"{path} ('{row.Name}') history cell '{cell.Key}' carries a time trait; a ring slot is a plain value.");
            }
        }
    }
}
