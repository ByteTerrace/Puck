using System.Numerics;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

// Normalize/Validate for ShapeDocument.Bumps/Shear — split out of CreationCanonicalizer.cs for length; a
// partial-class split, not a separate concern.
public static partial class CreationCanonicalizer {
    // Radii clamp away from zero (SdfProgramBuilder.GaussianPushMinRadius) exactly like SdfProgramBuilder.GaussianPush
    // itself, so a direct Normalize() call on already-invalid data cannot hand the builder a zero divisor; Push clamps
    // to MaxPushMagnitude by direction-preserving scale, mirroring a domain-op normal's own normalize-on-clamp.
    private static IReadOnlyList<ShapeBumpDocument>? NormalizeBumps(IReadOnlyList<ShapeBumpDocument>? bumps) {
        if (bumps is not { Count: > 0 }) {
            return null;
        }

        var normalized = new ShapeBumpDocument[bumps.Count];

        for (var i = 0; (i < bumps.Count); i++) {
            var bump = bumps[i];
            var radii = (VectorFunctions.IsFinite(vector: bump.Radii)
                ? bump.Radii.Value
                : Vector3.One
            );
            var clampedRadii = new Vector3(
                x: MathF.Max(
                    x: MathF.Abs(x: radii.X),
                    y: SdfProgramBuilder.GaussianPushMinRadius
                ),
                y: MathF.Max(
                    x: MathF.Abs(x: radii.Y),
                    y: SdfProgramBuilder.GaussianPushMinRadius
                ),
                z: MathF.Max(
                    x: MathF.Abs(x: radii.Z),
                    y: SdfProgramBuilder.GaussianPushMinRadius
                )
            );
            var push = (VectorFunctions.IsFinite(vector: bump.Push)
                ? bump.Push.Value
                : Vector3.Zero
            );
            var pushLength = push.Length();
            var clampedPush = (((pushLength > ShapeBumpDocument.MaxPushMagnitude) && (pushLength > 0f))
                ? (push * (ShapeBumpDocument.MaxPushMagnitude / pushLength))
                : push
            );

            normalized[i] = bump with {
                Center = (VectorFunctions.IsFinite(vector: bump.Center)
                ? bump.Center.Value
                : Vector3.Zero),
                Push = clampedPush,
                Radii = clampedRadii,
            };
        }

        return normalized;
    }
    private static ShapeWarpDocument? NormalizeWarp(ShapeWarpDocument? warp) =>
        ((warp is null)
            ? null
            : warp with {
                Amplitude = Magnitude(value: warp.Amplitude, max: ShapeWarpDocument.MaxAmplitude),
                Frequency = Frequencies(value: warp.Frequency, max: ShapeWarpDocument.MaxFrequency),
            });
    private static ShapeDisplaceDocument? NormalizeDisplace(ShapeDisplaceDocument? displace) =>
        ((displace is null)
            ? null
            : displace with {
                Amplitude = Magnitude(value: displace.Amplitude, max: ShapeDisplaceDocument.MaxAmplitude),
                Frequency = Frequencies(value: displace.Frequency, max: ShapeDisplaceDocument.MaxFrequency),
            });
    private static DocumentVector3? NormalizeElongate(DocumentVector3? elongate) {
        if (elongate is not { } extents) { return null; }

        var clamped = new Vector3(
            x: Math.Clamp(value: (float.IsFinite(f: extents.X) ? MathF.Abs(x: extents.X) : 0f), min: 0f, max: ShapeDocument.MaxElongate),
            y: Math.Clamp(value: (float.IsFinite(f: extents.Y) ? MathF.Abs(x: extents.Y) : 0f), min: 0f, max: ShapeDocument.MaxElongate),
            z: Math.Clamp(value: (float.IsFinite(f: extents.Z) ? MathF.Abs(x: extents.Z) : 0f), min: 0f, max: ShapeDocument.MaxElongate)
        );

        return ((clamped == Vector3.Zero) ? null : clamped);
    }
    private static float Magnitude(float value, float max) => Math.Clamp(value: (float.IsFinite(f: value) ? value : 0f), min: -max, max: max);
    private static Vector3 Frequencies(Vector3 value, float max) => new(
        x: Magnitude(value: value.X, max: max),
        y: Magnitude(value: value.Y, max: max),
        z: Magnitude(value: value.Z, max: max)
    );
    private static void ValidateWarpFacets(ShapeDocument shape, List<DocumentValidationError> errors, int index) {
        if ((shape.Warp is { } warp) && (!VectorFunctions.IsFinite(vector: warp.Frequency) || !float.IsFinite(f: warp.Amplitude))) {
            errors.Add(item: new(Message: "warp requires a finite frequency and amplitude.", Path: $"shapes[{index}].warp"));
        }
        if (shape.Displace is { } displace) {
            if (!VectorFunctions.IsFinite(vector: displace.Frequency) || !float.IsFinite(f: displace.Amplitude)) {
                errors.Add(item: new(Message: "displace requires a finite frequency and amplitude.", Path: $"shapes[{index}].displace"));
            }
            if (shape.Type == SdfSolidPrimitive.Sweep) {
                errors.Add(item: new(Message: "displace requires a closed primitive; Sweep has no bounded solid offset contract.", Path: $"shapes[{index}].displace"));
            }
        }
        if ((shape.Elongate is { } elongate) && !VectorFunctions.IsFinite(vector: elongate)) {
            errors.Add(item: new(Message: "elongate is non-finite.", Path: $"shapes[{index}].elongate"));
        }
    }
    private static ShapeShearDocument? NormalizeShear(ShapeShearDocument? shear) {
        if (shear is null) { return null; }
        static float Coefficient(float value) => Math.Clamp(
            (float.IsFinite(f: value)
            ? value
            : 0f),
            -ShapeDocument.MaxShear,
            ShapeDocument.MaxShear
        );
        return shear with { Linear = Coefficient(value: shear.Linear), Quadratic = Coefficient(value: shear.Quadratic), Cubic = Coefficient(value: shear.Cubic) };
    }
    // No field-scope reasoning is needed here (unlike Panel/Trims): a bump is a point-warp instruction on the
    // shape's own chain, never a second composed shape.
    private static void ValidateBumps(ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Bumps is not { Count: > 0 } bumps) {
            return;
        }

        if (bumps.Count > ShapeBumpDocument.MaxBumps) {
            errors.Add(item: new(
                Message: $"{bumps.Count} entries exceeds the {ShapeBumpDocument.MaxBumps}-bump list.",
                Path: path
            ));
        }

        for (var i = 0; (i < bumps.Count); i++) {
            var bump = bumps[i];
            var bumpPath = $"{path}[{i}]";

            if (!VectorFunctions.IsFinite(vector: bump.Center)) {
                errors.Add(item: new(
                    Message: "center is non-finite.",
                    Path: $"{bumpPath}.center"
                ));
            }
            if (
                !VectorFunctions.IsFinite(vector: bump.Radii) ||
                (bump.Radii.X < 0f) ||
                (bump.Radii.Y < 0f) ||
                (bump.Radii.Z < 0f)
            ) {
                errors.Add(item: new(
                    Message: "radii must be finite and non-negative.",
                    Path: $"{bumpPath}.radii"
                ));
            }
            if (!VectorFunctions.IsFinite(vector: bump.Push)) {
                errors.Add(item: new(
                    Message: "push is non-finite.",
                    Path: $"{bumpPath}.push"
                ));
            }
        }
    }
    private static void ValidateShear(ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Shear is not { } shear) {
            return;
        }

        if (
            !float.IsFinite(f: shear.Linear) ||
            !float.IsFinite(f: shear.Quadratic) ||
            !float.IsFinite(f: shear.Cubic) ||
            (((uint)shear.Target) > 2u) ||
            (((uint)shear.Driver) > 2u) ||
            (shear.Target == shear.Driver)
        ) {
            errors.Add(item: new(
                Message: "shear requires finite coefficients and distinct target/driver axes in [0, 2].",
                Path: path
            ));
        }
    }
}
