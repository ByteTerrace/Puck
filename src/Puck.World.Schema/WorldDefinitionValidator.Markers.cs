namespace Puck.World;

/// <summary>The <c>markers</c> section's validation: unique ids, a resolvable icon (gated the same way
/// <c>bindingOverlays</c>' icon references are — refused only once some document in the basis chain authors an
/// <c>icons</c> section at all), a closed <see cref="WorldMarkerRing.Field"/> vocabulary, and the ring/style
/// co-presence rule (a row authors both <see cref="WorldMarkerRow.Ring"/> and its style's ring color/alpha, or
/// neither).</summary>
public static partial class WorldDefinitionValidator {
    private static void ValidateMarkers(WorldDefinition definition, IReadOnlySet<string> iconNames, bool iconsAuthored, List<string> errors) {
        var markers = definition.Markers;

        if (markers.Count > WorldMarkerCapacity.MaxRows) {
            errors.Add(item: $"markers declares {markers.Count} rows, exceeding the {WorldMarkerCapacity.MaxRows}-row ceiling.");
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < markers.Count); index++) {
            var marker = markers[index];
            var path = $"markers[{index}]";

            if (marker is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            if (string.IsNullOrWhiteSpace(value: marker.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: marker.Id)) {
                errors.Add(item: $"{path}.id '{marker.Id}' is duplicated.");
            }

            if (
                (marker.Source is WorldMarkerSource.Point point) &&
                !IsFinite(value: point.Position)
            ) {
                errors.Add(item: $"{path}.source.position must contain finite coordinates.");
            }

            if (
                iconsAuthored &&
                !string.IsNullOrEmpty(value: marker.Icon) &&
                !iconNames.Contains(item: marker.Icon)
            ) {
                errors.Add(item: $"{path}.icon '{marker.Icon}' names no row in icons.icons.");
            } else if (string.IsNullOrEmpty(value: marker.Icon)) {
                errors.Add(item: $"{path}.icon is required.");
            }

            var ringPresent = (marker.Ring is not null);
            var styleRingPresent = ((marker.Style.RingColor is not null) || (marker.Style.RingAlpha is not null));

            if (ringPresent != styleRingPresent) {
                errors.Add(item: $"{path} authors {(ringPresent ? "a ring policy without a style.ringColor/ringAlpha pair" : "a style.ringColor/ringAlpha without a ring policy")} — a ring and its style ride together, or neither.");
            }

            if (marker.Ring is { } ring) {
                if (!string.Equals(a: ring.Field, b: WorldMarkerRing.SpeakerRadiusField, comparisonType: StringComparison.Ordinal)) {
                    errors.Add(item: $"{path}.ring.field '{ring.Field}' names no recognized field — the only one is '{WorldMarkerRing.SpeakerRadiusField}'.");
                } else if (marker.Source is not WorldMarkerSource.Speakers) {
                    errors.Add(item: $"{path}.ring.field '{ring.Field}' names a speakers-only field, but {path}.source is not speakers.");
                }
            }

            RequireBindableUnitScalar(definition: definition, errors: errors, path: $"{path}.style.chipAlpha", scalar: marker.Style.ChipAlpha);
            RequirePositive(errors: errors, name: $"{path}.style.size", value: marker.Style.Size);

            if (marker.Style.RingColor is { } ringColor) {
                RequireBindableColor(color: ringColor, definition: definition, errors: errors, path: $"{path}.style.ringColor");
            }

            if (marker.Style.RingAlpha is { } ringAlpha) {
                RequireBindableUnitScalar(definition: definition, errors: errors, path: $"{path}.style.ringAlpha", scalar: ringAlpha);
            }
        }
    }
}
