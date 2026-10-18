using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    private static void ValidatePaletteLayers(PaletteEntryDocument entry, List<DocumentValidationError> errors, int index) {
        Vector3 Resolve(string color) {
            if (
                !HexColor.TryParse(
                rgb: out _,
                value: color
            ) &&
                !HexColor.IsStateBinding(value: color)
            ) {
                errors.Add(item: new(
                    Message: "Layer colors require #RRGGBB or a state binding.",
                    Path: $"palette[{index}]"
                ));
            }
            return Vector3.Zero;
        }
        var bands = (entry.Bands ?? []);

        if (bands.Count > CreationDocument.MaxPaletteBands) {
            errors.Add(item: new(
                Message: $"{bands.Count} bands exceed the {CreationDocument.MaxPaletteBands} a palette slot paints.",
                Path: $"palette[{index}].bands"
            ));
        }
        try {
            if (!SdfMaterialLayers.IsValid(
                inset: entry.Inset?.ToInset(resolve: Resolve),
                weathering: entry.Weathering?.ToWeathering(resolve: Resolve),
                band: ((bands.Count > 0) ? bands[0].ToBand(resolve: Resolve) : null),
                secondBand: ((bands.Count > 1) ? bands[1].ToBand(resolve: Resolve) : null)
            )) {
                errors.Add(item: new(
                    Message: "Invalid inset, weathering or band: check frames, ordered stops, surfaces, ranges, lane [0, 3], a nonzero band normal and a positive band half-width.",
                    Path: $"palette[{index}]"
                ));
            }
        } catch (Exception ex) when ((ex is NullReferenceException or ArgumentNullException)) {
            errors.Add(item: new(
                Message: "Material layers require non-null paint, stops, and surfaces.",
                Path: $"palette[{index}]"
            ));
        }
    }
    private static void ValidatePaletteShading(PaletteEntryDocument entry, List<DocumentValidationError> errors, int index) {
        ValidateUnitRange(
            value: entry.Wrap,
            name: "wrap",
            errors: errors,
            path: $"palette[{index}].wrap"
        );
        ValidateUnitRange(
            value: entry.Soften,
            name: "soften",
            errors: errors,
            path: $"palette[{index}].soften"
        );

        if ((entry.Receive is { } receive) && (!float.IsFinite(f: receive) || (receive < 0f))) {
            errors.Add(item: new(
                Message: "receive must be finite and non-negative.",
                Path: $"palette[{index}].receive"
            ));
        }
        Color(entry.Fill, "fill");
        Color(entry.Bleed, "bleed");

        void Color(string? value, string name) {
            if ((value is not null) && !HexColor.TryParse(rgb: out _, value: value) && !HexColor.IsStateBinding(value: value)) {
                errors.Add(item: new(Message: $"{name} must be #RRGGBB or a state.<row>[.<key>] binding.", Path: $"palette[{index}].{name}"));
            }
        }
    }
    // Per-shape lane-driven erosion: a defined lane, a finite from/to pair that differ (SdfProgramBuilder.LaneErode
    // refuses an equal pair one door later — t would divide by zero), and a finite noise scale.
    private static void ValidateShapeErode(ShapeDocument shape, List<DocumentValidationError> errors, int index) {
        if (
            (shape.Erode is { } erode) &&
            ((((uint)erode.Lane) > 3u) || !float.IsFinite(f: erode.From) || !float.IsFinite(f: erode.To) || (erode.From == erode.To) || !float.IsFinite(f: (erode.Noise ?? 1f)))
        ) {
            errors.Add(item: new(
                Message: "erode requires a defined lane, a finite from/to that differ, and a finite noise.",
                Path: $"shapes[{index}].erode"
            ));
        }
    }
}
