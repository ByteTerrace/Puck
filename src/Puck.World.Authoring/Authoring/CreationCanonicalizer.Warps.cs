using System.Numerics;

using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

// Normalize/Validate for ShapeDocument.Bumps/Shear — split out of CreationCanonicalizer.cs to keep it under the
// file-length ceiling (LEN001); a partial-class split, not a separate concern.
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
            var radii = (IsFinite(vector: bump.Radii) ? bump.Radii.Value : Vector3.One);
            var clampedRadii = new Vector3(
                x: MathF.Max(x: MathF.Abs(radii.X), y: SdfProgramBuilder.GaussianPushMinRadius),
                y: MathF.Max(x: MathF.Abs(radii.Y), y: SdfProgramBuilder.GaussianPushMinRadius),
                z: MathF.Max(x: MathF.Abs(radii.Z), y: SdfProgramBuilder.GaussianPushMinRadius)
            );
            var push = (IsFinite(vector: bump.Push) ? bump.Push.Value : Vector3.Zero);
            var pushLength = push.Length();
            var clampedPush = (((pushLength > ShapeBumpDocument.MaxPushMagnitude) && (pushLength > 0f))
                ? (push * (ShapeBumpDocument.MaxPushMagnitude / pushLength))
                : push
            );

            normalized[i] = bump with {
                Center = (IsFinite(vector: bump.Center) ? bump.Center.Value : Vector3.Zero),
                Push = clampedPush,
                Radii = clampedRadii,
            };
        }

        return normalized;
    }
    private static ShapeShearDocument? NormalizeShear(ShapeShearDocument? shear) {
        if (shear is null) { return null; }
        static float Coefficient(float value) => Math.Clamp(float.IsFinite(value) ? value : 0f, -ShapeDocument.MaxShear, ShapeDocument.MaxShear);
        return shear with { Linear = Coefficient(shear.Linear), Quadratic = Coefficient(shear.Quadratic), Cubic = Coefficient(shear.Cubic) };
    }
    // No field-scope reasoning is needed here (unlike Panel/Trims): a bump is a point-warp instruction on the
    // shape's own chain, never a second composed shape.
    private static void ValidateBumps(ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Bumps is not { Count: > 0 } bumps) {
            return;
        }

        if (bumps.Count > ShapeBumpDocument.MaxBumps) {
            errors.Add(item: new(Message: $"{bumps.Count} entries exceeds the {ShapeBumpDocument.MaxBumps}-bump list.", Path: path));
        }

        for (var i = 0; (i < bumps.Count); i++) {
            var bump = bumps[i];
            var bumpPath = $"{path}[{i}]";

            if (!IsFinite(vector: bump.Center)) {
                errors.Add(item: new(Message: "center is non-finite.", Path: $"{bumpPath}.center"));
            }
            if (!IsFinite(vector: bump.Radii) || (bump.Radii.X < 0f) || (bump.Radii.Y < 0f) || (bump.Radii.Z < 0f)) {
                errors.Add(item: new(Message: "radii must be finite and non-negative.", Path: $"{bumpPath}.radii"));
            }
            if (!IsFinite(vector: bump.Push)) {
                errors.Add(item: new(Message: "push is non-finite.", Path: $"{bumpPath}.push"));
            }
        }
    }
    private static void ValidateShear(ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Shear is not { } shear) {
            return;
        }

        if (!float.IsFinite(shear.Linear) || !float.IsFinite(shear.Quadratic) || !float.IsFinite(shear.Cubic) || (uint)shear.Target > 2u || (uint)shear.Driver > 2u || shear.Target == shear.Driver) {
            errors.Add(item: new(Message: "shear requires finite coefficients and distinct target/driver axes in [0, 2].", Path: path));
        }
    }
}
