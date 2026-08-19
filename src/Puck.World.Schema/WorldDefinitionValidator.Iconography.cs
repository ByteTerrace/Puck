namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // One icon row: exactly one of Glyph/Label, structurally sound (a known committed font id, a glyph string that
    // parses to one Unicode scalar, a label within the character ceiling).
    private static void ValidateIconRow(WorldIconRow row, string path, List<string> errors) {
        var hasGlyph = (row.Glyph is not null);
        var hasLabel = (row.Label is not null);

        if (hasGlyph == hasLabel) {
            errors.Add(item: $"{path} must carry exactly one of glyph or label.");

            return;
        }

        if (row.Glyph is { } glyph) {
            if (!WorldIconFontCatalog.IsKnown(name: glyph.Font)) {
                errors.Add(item: $"{path}.glyph.font '{(glyph.Font ?? "(absent)")}' is not a committed fixed-UI font id.");
            }

            if (!WorldIconGlyphRef.TryResolveCodePoint(
                glyph: glyph.Glyph,
                codePoint: out _
            )) {
                errors.Add(item: $"{path}.glyph.glyph '{(glyph.Glyph ?? "(absent)")}' must spell exactly one Unicode scalar (a literal character or 'U+XXXX').");
            }
        } else if (row.Label is { } label) {
            if (
                (label.Length < 1) ||
                (label.Length > WorldIconCapacity.MaxLabelChars)
            ) {
                errors.Add(item: $"{path}.label '{label}' must be 1..{WorldIconCapacity.MaxLabelChars} characters.");
            }
        }
    }
    // The icons.icons + icons.badges rows. Returns the declared icon-name set and whether ANY document in the basis
    // chain authored the section at all — the absence gate a binding-entry icon-string check downstream applies
    // (see ValidateBindingOverlays): no authored section means no icons, drawn as blank plates, never a refusal.
    private static (HashSet<string> Names, bool Authored) ValidateIconography(WorldDefinition definition, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (definition.IconsRaw is null) {
            return (names, false);
        }

        var icons = definition.Icons;

        if (icons.Icons.Count > WorldIconCapacity.MaxIcons) {
            errors.Add(item: $"icons.icons declares {icons.Icons.Count} rows, exceeding the {WorldIconCapacity.MaxIcons}-icon ceiling.");
        }

        for (var index = 0; (index < icons.Icons.Count); index++) {
            var row = icons.Icons[index];
            var path = $"icons.icons[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: row.Name)) {
                errors.Add(item: $"{path}.name is required.");
            } else if (!names.Add(item: row.Name)) {
                errors.Add(item: $"{path}.name '{row.Name}' is duplicated.");
            }

            ValidateIconRow(
                row: row,
                path: path,
                errors: errors
            );
        }

        if (icons.Badges.Count > WorldIconCapacity.MaxBadges) {
            errors.Add(item: $"icons.badges declares {icons.Badges.Count} rows, exceeding the {WorldIconCapacity.MaxBadges}-badge ceiling.");
        }

        var seenButtons = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < icons.Badges.Count); index++) {
            var badge = icons.Badges[index];
            var path = $"icons.badges[{index}]";

            if (badge is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: badge.Button)) {
                errors.Add(item: $"{path}.button is required.");
            } else if (!seenButtons.Add(item: badge.Button)) {
                errors.Add(item: $"{path}.button '{badge.Button}' is duplicated.");
            } else if (
                (GamepadButtonVocabularyHook.IsKnownButtonName is { } isKnownButton) &&
                !isKnownButton(badge.Button) &&
                (badge.Button is not ("LeftTrigger" or "RightTrigger"))
            ) {
                errors.Add(item: $"{path}.button '{badge.Button}' is not a declared GamepadButtons name (nor the analog 'LeftTrigger'/'RightTrigger' pseudo-buttons the modifier pips also badge).");
            }

            if (string.IsNullOrWhiteSpace(value: badge.Icon)) {
                errors.Add(item: $"{path}.icon is required.");
            } else if (!names.Contains(item: badge.Icon)) {
                errors.Add(item: $"{path}.icon '{badge.Icon}' names no row in icons.icons.");
            }

            if (badge.Overrides.Count > WorldIconCapacity.MaxFamilyOverridesPerBadge) {
                errors.Add(item: $"{path}.overrides declares {badge.Overrides.Count} entries, exceeding the {WorldIconCapacity.MaxFamilyOverridesPerBadge}-family ceiling.");
            }

            var seenFamilies = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var overrideIndex = 0; (overrideIndex < badge.Overrides.Count); overrideIndex++) {
                var over = badge.Overrides[overrideIndex];
                var overridePath = $"{path}.overrides[{overrideIndex}]";

                if (over is null) {
                    errors.Add(item: $"{overridePath} is required.");

                    continue;
                }

                if (string.IsNullOrWhiteSpace(value: over.Family)) {
                    errors.Add(item: $"{overridePath}.family is required.");
                } else if (!seenFamilies.Add(item: over.Family)) {
                    errors.Add(item: $"{overridePath}.family '{over.Family}' is duplicated.");
                } else if (
                    (GamepadFamilyVocabularyHook.IsKnownFamilyName is { } isKnownFamily) &&
                    !isKnownFamily(over.Family)
                ) {
                    errors.Add(item: $"{overridePath}.family '{over.Family}' is not a declared GamepadType name.");
                }

                if (string.IsNullOrWhiteSpace(value: over.Icon)) {
                    errors.Add(item: $"{overridePath}.icon is required.");
                } else if (!names.Contains(item: over.Icon)) {
                    errors.Add(item: $"{overridePath}.icon '{over.Icon}' names no row in icons.icons.");
                }
            }
        }

        return (names, true);
    }
}
