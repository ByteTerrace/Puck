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
            // The icon bake draws every glyph cell from the ONE mono atlas (WorldIconTable discards the font id and
            // OverlayGlyphSdfPack bakes from OverlayGlyphAtlasSet.MonoFont), so a non-mono face would validate yet
            // bake a blank cell — refuse it by name rather than admit a field the table ignores.
            if (!string.Equals(
                a: glyph.Font,
                b: WorldIconFontCatalog.JetBrainsMonoRegular,
                comparisonType: StringComparison.Ordinal
            )) {
                errors.Add(item: $"{path}.glyph.font '{(glyph.Font ?? "(absent)")}' must be '{WorldIconFontCatalog.JetBrainsMonoRegular}' — the only face the icon bake draws from; the other committed faces bake blank cells.");
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
            } else {
                // The label resolves through OverlayGlyphSdfPack's ASCII-95 block (printable ASCII U+0020..U+007E);
                // a code unit outside it (or a surrogate half of an astral scalar) resolves to a blank cell, so a
                // validated label always renders only when every unit is renderable.
                foreach (var character in label) {
                    if (
                        (character < ' ') ||
                        (character > '~')
                    ) {
                        errors.Add(item: $"{path}.label '{label}' carries a character 'U+{((int)character):X4}' outside the renderable printable-ASCII range (U+0020..U+007E).");

                        break;
                    }
                }
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

            RequireUniqueName(
                value: row.Name,
                seen: names,
                path: path,
                field: "name",
                errors: errors
            );

            ValidateIconRow(
                row: row,
                path: path,
                errors: errors
            );
        }

        if (icons.Badges.Count > WorldIconCapacity.MaxBadges) {
            errors.Add(item: $"icons.badges declares {icons.Badges.Count} rows, exceeding the {WorldIconCapacity.MaxBadges}-badge ceiling.");
        }

        var seenSources = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < icons.Badges.Count); index++) {
            var badge = icons.Badges[index];
            var path = $"icons.badges[{index}]";

            if (badge is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (
                RequireUniqueName(
                value: badge.Source,
                seen: seenSources,
                path: path,
                field: "source",
                errors: errors
            ) &&
                (InputSourceVocabularyHook.IsKnownSourceId is { } isKnownSource) &&
                !isKnownSource(badge.Source)
            ) {
                errors.Add(item: $"{path}.source '{badge.Source}' is not a declared input source id.");
            }

            if (string.IsNullOrWhiteSpace(value: badge.Icon)) {
                errors.Add(item: $"{path}.icon is required.");
            } else if (!names.Contains(item: badge.Icon)) {
                errors.Add(item: $"{path}.icon '{badge.Icon}' names no row in icons.icons.");
            }

            var seenFamilies = new HashSet<string>(comparer: StringComparer.Ordinal);

            for (var overrideIndex = 0; (overrideIndex < badge.Overrides.Count); overrideIndex++) {
                var over = badge.Overrides[overrideIndex];
                var overridePath = $"{path}.overrides[{overrideIndex}]";

                if (over is null) {
                    errors.Add(item: $"{overridePath} is required.");

                    continue;
                }

                if (
                    RequireUniqueName(
                    value: over.Family,
                    seen: seenFamilies,
                    path: overridePath,
                    field: "family",
                    errors: errors
                ) &&
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
