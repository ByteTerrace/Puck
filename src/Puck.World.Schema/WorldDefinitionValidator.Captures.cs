using Puck.World.Authoring;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // Structural only: station uniqueness, tick ordering/uniqueness within a station, and a parseable palette. Which
    // camera a station's ticks actually show is a document DECISION (state + rules + a camera program's select op),
    // never checked here — a station name is a label, not a reference.
    private static void ValidateCaptures(WorldDefinition definition, List<string> errors) {
        if (definition.Captures is not { } captures) {
            return;
        }

        if (string.IsNullOrWhiteSpace(value: captures.Directory)) {
            errors.Add(item: "captures.directory is required.");
        }

        var rows = (captures.Rows ?? []);

        if (rows.Count > WorldCapturesCapacity.MaxRows) {
            errors.Add(item: $"captures.rows count {rows.Count} exceeds {WorldCapturesCapacity.MaxRows}.");
        }

        var stations = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"captures.rows[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (!stations.Add(item: row.Station.Value)) {
                errors.Add(item: $"{path}.station '{row.Station}' is declared more than once.");
            }

            ValidateCaptureTicks(
                errors: errors,
                path: path,
                ticks: row.Ticks
            );
            ValidateCapturePalette(
                errors: errors,
                palette: row.Palette,
                path: path
            );
        }
    }
    private static void ValidateCaptureTicks(IReadOnlyList<ulong>? ticks, string path, List<string> errors) {
        var list = (ticks ?? []);

        if (list.Count == 0) {
            errors.Add(item: $"{path}.ticks must declare at least one tick.");

            return;
        }

        if (list.Count > WorldCapturesCapacity.MaxTicksPerRow) {
            errors.Add(item: $"{path}.ticks count {list.Count} exceeds {WorldCapturesCapacity.MaxTicksPerRow}.");
        }

        var seen = new HashSet<ulong>();

        for (var index = 0; (index < list.Count); index++) {
            var tick = list[index];

            // Tick 0 is the boot coordinate itself — before the fixed-step pump has completed a step, so no composed
            // frame yet reflects any simulation state; scheduling there is already unreachable, the same way a
            // console world.wait refuses a zero count. This is the whole "captures scheduled in the past" check this
            // section can make statically — a document carries no notion of "now" beyond its own boot.
            if (tick == 0UL) {
                errors.Add(item: $"{path}.ticks[{index}] is 0 — already in the past; the world has not produced a frame yet.");
            }

            if (!seen.Add(item: tick)) {
                errors.Add(item: $"{path}.ticks[{index}] repeats tick {tick} — a station may capture a given tick once.");
            }
        }
    }
    private static void ValidateCapturePalette(IReadOnlyList<WorldCapturePaletteEntry>? palette, string path, List<string> errors) {
        var list = (palette ?? []);

        if (list.Count == 0) {
            errors.Add(item: $"{path}.palette must declare at least one material — the census has nothing to sort pixels against otherwise.");

            return;
        }

        if (list.Count > WorldCapturesCapacity.MaxPaletteEntriesPerRow) {
            errors.Add(item: $"{path}.palette count {list.Count} exceeds {WorldCapturesCapacity.MaxPaletteEntriesPerRow}.");
        }

        var materials = new HashSet<int>();

        for (var index = 0; (index < list.Count); index++) {
            var entry = list[index];
            var entryPath = $"{path}.palette[{index}]";

            if (entry is null) {
                errors.Add(item: $"{entryPath} is required.");

                continue;
            }

            if (entry.Material < 0) {
                errors.Add(item: $"{entryPath}.material {entry.Material} must be non-negative.");
            } else if (!materials.Add(item: entry.Material)) {
                errors.Add(item: $"{entryPath}.material {entry.Material} is declared more than once.");
            }

            if (!HexColor.TryParseRgba(
                rgba: out _,
                value: entry.Color
            )) {
                errors.Add(item: $"{entryPath}.color '{entry.Color}' must be #RRGGBB or #RRGGBBAA.");
            }
        }
    }
}
