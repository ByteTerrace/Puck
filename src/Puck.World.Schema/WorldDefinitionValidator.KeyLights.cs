using Puck.World.Protocol;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private const string KeyboardSourcePrefix = "keyboard.";

    private static void ValidateKeyLights(WorldDefinition definition, int localSeats, List<string> errors) {
        if (definition.KeyLightsRaw is not { } section) {
            return;
        }

        RequireBindableColor(color: section.Idle, definition: definition, errors: errors, path: "keyLights.idle");
        RequireBindableColor(color: section.Flash, definition: definition, errors: errors, path: "keyLights.flash");
        RequireBindableColor(color: section.Modifier, definition: definition, errors: errors, path: "keyLights.modifier");

        if (!float.IsFinite(f: section.UnavailableScale) || (section.UnavailableScale < 0f) || (section.UnavailableScale > 1f)) {
            errors.Add(item: $"keyLights.unavailableScale {section.UnavailableScale} must be within [0, 1].");
        }
        if (!float.IsFinite(f: section.FlashSeconds) || (section.FlashSeconds <= 0f) || (section.FlashSeconds > WorldKeyLightCapacity.MaxFlashSeconds)) {
            errors.Add(item: $"keyLights.flashSeconds {section.FlashSeconds} must be within (0, {WorldKeyLightCapacity.MaxFlashSeconds}].");
        }

        var rows = (section.Rows ?? []);

        if (rows.Count > WorldKeyLightCapacity.MaxRows) {
            errors.Add(item: $"keyLights declares {rows.Count} rows, exceeding the {WorldKeyLightCapacity.MaxRows}-row ceiling.");
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"keyLights.rows[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(value: row.Id, seen: ids, path: path, field: "id", errors: errors);
            RequireBindableColor(color: row.Color, definition: definition, errors: errors, path: $"{path}.color");
            if (row.FlashColor is { } flashColor) {
                RequireBindableColor(color: flashColor, definition: definition, errors: errors, path: $"{path}.flashColor");
            }
            if ((row.Keys is null) && (row.Bound is null) && (row.Character is null)) {
                errors.Add(item: $"{path} names no key: author keys, bound or character.");
            }
            if (((row.Keys?.Count ?? 0) > WorldKeyLightCapacity.MaxKeysPerRow) || ((row.Bound?.Count ?? 0) > WorldKeyLightCapacity.MaxKeysPerRow)) {
                errors.Add(item: $"{path} names more than {WorldKeyLightCapacity.MaxKeysPerRow} keys or binding entries.");
            }

            foreach (var key in (row.Keys ?? [])) {
                if (
                    string.IsNullOrWhiteSpace(value: key) ||
                    !key.StartsWith(value: KeyboardSourcePrefix, comparisonType: StringComparison.OrdinalIgnoreCase) ||
                    (InputSourceVocabularyHook.IsKnownSourceId?.Invoke(arg: key) == false)
                ) {
                    errors.Add(item: $"{path}.keys '{key}' is not a keyboard input source.");
                }
            }
            foreach (var entry in (row.Bound ?? [])) {
                if (string.IsNullOrWhiteSpace(value: entry)) {
                    errors.Add(item: $"{path}.bound holds a blank binding entry id.");
                }
            }

            JudgeOptional(scalar: row.Character, field: WorldValueFields.KeyLightCharacter, name: "character");
            JudgeOptional(scalar: row.Lit, field: WorldValueFields.KeyLightLit, name: "lit");
            JudgeOptional(scalar: row.Available, field: WorldValueFields.KeyLightAvailable, name: "available");
            JudgeOptional(scalar: row.FlashSignal, field: WorldValueFields.KeyLightFlashSignal, name: "flashSignal");

            if (
                (row.Seat is { } seatToken) &&
                (!PrincipalTokens.TryParseSeat(number: out var seat, token: seatToken) || (seat > localSeats))
            ) {
                errors.Add(item: $"{path}.seat '{seatToken}' names no seat (expected seat1..seat{localSeats}).");
            }

            void JudgeOptional(BindableScalar? scalar, WorldValueField field, string name) {
                if (scalar is { } authored) {
                    JudgeScalar(definition: definition, errors: errors, field: field, path: $"{path}.{name}", scalar: authored);
                }
            }
        }
    }
    private static void ValidateHighlights(WorldDefinition definition, List<string> errors) {
        var rows = definition.Highlights;

        if (rows.Count > WorldHighlightCapacity.MaxRows) {
            errors.Add(item: $"highlights declares {rows.Count} rows, exceeding the {WorldHighlightCapacity.MaxRows}-row ceiling.");
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];
            var path = $"highlights[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(value: row.Id, seen: ids, path: path, field: "id", errors: errors);
            // The id is the stem of the files the host writes, so it is held to what a file name carries everywhere.
            if (!string.IsNullOrEmpty(value: row.Id) && row.Id.Any(predicate: static letter => (!char.IsAsciiLetterOrDigit(c: letter) && (letter is not ('-' or '_'))))) {
                errors.Add(item: $"{path}.id '{row.Id}' may hold only ASCII letters, digits, '-' and '_'.");
            }
            JudgeScalar(definition: definition, errors: errors, field: WorldValueFields.HighlightSignal, path: $"{path}.signal", scalar: row.Signal);
            if ((row.Frames < 1) || (row.Frames > WorldHighlightCapacity.MaxFrames)) {
                errors.Add(item: $"{path}.frames {row.Frames} must be within [1, {WorldHighlightCapacity.MaxFrames}].");
            }
            if (!float.IsFinite(f: row.DelaySeconds) || (row.DelaySeconds < 0f) || (row.DelaySeconds > WorldHighlightCapacity.MaxDelaySeconds)) {
                errors.Add(item: $"{path}.delaySeconds {row.DelaySeconds} must be within [0, {WorldHighlightCapacity.MaxDelaySeconds}].");
            }
        }
    }
}
