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
        try {
            if (!SdfMaterialLayers.IsValid(
                inset: entry.Inset?.ToInset(resolve: Resolve),
                weathering: entry.Weathering?.ToWeathering(resolve: Resolve)
            )) {
                errors.Add(item: new(
                    Message: "Invalid inset or weathering: check frames, ordered stops, surfaces, ranges, and lane [0, 3].",
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

        if (
            (entry.Bounce is { } bounce) &&
            !HexColor.TryParse(
            rgb: out _,
            value: bounce
        ) &&
            !HexColor.IsStateBinding(value: bounce)
        ) {
            errors.Add(item: new(
                Message: "bounce must be #RRGGBB or a state.<row>[.<key>] binding.",
                Path: $"palette[{index}].bounce"
            ));
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
