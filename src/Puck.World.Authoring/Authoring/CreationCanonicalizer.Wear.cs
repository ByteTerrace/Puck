using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    private static void ValidatePaletteLayers(PaletteEntryDocument entry, ShapeLookup lookup, IReadOnlyList<ShapeDocument> shapes, List<DocumentValidationError> errors, int index) {
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
        // A paint's primitive takes a shape's own admission (SdfSolidGeometry.TryValidateScaledPrimitive), less a path
        // profile, and its parent names a shape whose slot carries the shape's own frame: never a domain-bearing one,
        // whose slot carries its parent's delta frame instead.
        SdfPaint? ResolvePaint(PalettePaintDocument? paint, string path) {
            if (paint is null) {
                errors.Add(item: new(Message: "paint entry is null.", Path: path));

                return null;
            }

            var refused = false;

            void Refuse(string message, string field) {
                errors.Add(item: new(Message: message, Path: $"{path}.{field}"));
                refused = true;
            }
            if (paint.Parent is { } parent) {
                var shapeIndex = lookup.Find(name: parent);

                if (shapeIndex < 0) {
                    Refuse(message: $"parent '{parent}' names no shape in this creation.", field: "parent");
                } else if (shapes[shapeIndex].Domain is { Count: > 0 }) {
                    Refuse(message: $"parent '{parent}' carries a domain fold, whose slot does not carry its own frame.", field: "parent");
                }
            }
            if (!Enum.IsDefined(value: paint.Type)) {
                Refuse(message: $"type '{paint.Type}' is not a primitive.", field: "type");

                return null;
            }
            if (paint.Profile is { Kind: SdfPrismProfileKind.Path }) {
                Refuse(message: "a paint cannot cover a path profile.", field: "profile");
            }
            if ((paint.Stroke is { } stroke) && (!float.IsFinite(f: stroke) || (stroke < 0f))) {
                Refuse(message: "stroke must be finite and non-negative.", field: "stroke");
            }
            if ((paint.Type == SdfSolidPrimitive.Sweep) != (paint.Curve is not null)) {
                Refuse(message: "a Sweep paint requires a curve, and only a Sweep paint carries one.", field: "curve");
            }
            if (
                (paint.Type != SdfSolidPrimitive.Sweep) &&
                !SdfSolidGeometry.TryValidateScaledPrimitive(
                    chamfer: (paint.Chamfer ?? 0f),
                    exponent: (paint.Exponent ?? SdfProgramBuilder.MinSuperellipsoidExponent),
                    lift: (paint.Lift ?? SdfLift.Extrude),
                    profile: paint.Profile,
                    refusal: out var refusal,
                    rounding: (paint.Rounding ?? 0f),
                    scale: (paint.Scale?.Value ?? Vector3.One),
                    taper: (paint.Taper ?? 0.5f),
                    type: paint.Type
                )
            ) {
                Refuse(message: $"the primitive cannot be emitted: {refusal}.", field: "scale");
            }
            if (refused) {
                return null;
            }
            try {
                return paint.ToPaint(resolve: Resolve);
            } catch (ArgumentException exception) {
                errors.Add(item: new(Message: exception.Message, Path: path));

                return null;
            }
        }
        var paints = (entry.Paints ?? []);

        if (paints.Count > CreationDocument.MaxPalettePaints) {
            errors.Add(item: new(
                Message: $"{paints.Count} paints exceed the {CreationDocument.MaxPalettePaints} a palette slot draws.",
                Path: $"palette[{index}].paints"
            ));
        }
        var resolved = new List<SdfPaint>(capacity: paints.Count);

        for (var paintIndex = 0; (paintIndex < paints.Count); paintIndex++) {
            if (ResolvePaint(paint: paints[paintIndex], path: $"palette[{index}].paints[{paintIndex}]") is { } paint) {
                resolved.Add(item: paint);
            }
        }
        try {
            if (!SdfMaterialLayers.IsValid(
                inset: entry.Inset?.ToInset(resolve: Resolve),
                weathering: entry.Weathering?.ToWeathering(resolve: Resolve),
                paints: resolved
            )) {
                errors.Add(item: new(
                    Message: "Invalid inset, weathering or paint: check frames, ordered stops, surfaces, ranges, lane [0, 3], a nonzero paint rotation, a positive paint scale and a non-negative stroke.",
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
        if (!SdfMaterial.IsEmissiveLane(lane: entry.EmissiveLane)) {
            errors.Add(item: new(
                Message: $"emissiveLane must be a render lane in [0, 3]; got {entry.EmissiveLane}.",
                Path: $"palette[{index}].emissiveLane"
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
