using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    // A Sweep's own control-point/radius geometry, admitted only on that type and required there (its zero set IS
    // the curve — a Sweep with no curve, or a curve on any other primitive, has nowhere to read dimensions from).
    // Not a closed solid (SdfSolidPrimitive.Sweep's remarks), so it is refused alongside every other facet that
    // needs its own field scope or a collider CreationStampEmitter.EmitFixed never builds for it. The ratio checks
    // below mirror SdfProgramBuilder.Sweep's own admission exactly, so a bad curve is refused HERE, where it is
    // authored, rather than by an uncaught exception at emission.
    private static void ValidateCurve(ShapeDocument shape, List<DocumentValidationError> errors, string path) {
        if (shape.Curve is not { } curve) {
            if (shape.Type == SdfSolidPrimitive.Sweep) {
                errors.Add(item: new(Message: "a Sweep primitive requires a curve.", Path: path));
            }

            return;
        }

        if (shape.Type != SdfSolidPrimitive.Sweep) {
            errors.Add(item: new(Message: $"a curve is admitted only on a Sweep primitive; this shape is {shape.Type}.", Path: path));

            return;
        }

        if (shape.Panel is not null) {
            errors.Add(item: new(Message: "a Sweep cannot also carry a panel; it has no local face to inset.", Path: path));
        }
        if (shape.Trims is { Count: > 0 }) {
            errors.Add(item: new(Message: "a Sweep cannot also carry trims.", Path: path));
        }
        if (shape.Flare is not null) {
            errors.Add(item: new(Message: "a Sweep cannot also carry a flare; the curve already authors its own silhouette.", Path: path));
        }
        if (shape.Shear is not null) {
            errors.Add(item: new(Message: "a Sweep cannot also carry a shear; the curve already authors its own silhouette.", Path: path));
        }
        if (shape.Bumps is { Count: > 0 }) {
            errors.Add(item: new(Message: "a Sweep cannot also carry bumps.", Path: path));
        }
        if (shape.Domain is { Count: > 0 }) {
            errors.Add(item: new(Message: "a Sweep cannot also carry domain operators.", Path: path));
        }

        if (!IsFinite(vector: curve.A)) {
            errors.Add(item: new(Message: "a is non-finite.", Path: $"{path}.a"));
        }
        if (!IsFinite(vector: curve.B)) {
            errors.Add(item: new(Message: "b is non-finite.", Path: $"{path}.b"));
        }
        if (!IsFinite(vector: curve.C)) {
            errors.Add(item: new(Message: "c is non-finite.", Path: $"{path}.c"));
        }

        var radiusStart = curve.RadiusStart;
        var radiusEnd = curve.RadiusEnd;

        if (!float.IsFinite(f: radiusStart) || (radiusStart <= 0f)) {
            errors.Add(item: new(Message: "radiusStart must be finite and positive.", Path: $"{path}.radiusStart"));

            return;
        }
        if (!float.IsFinite(f: radiusEnd) || (radiusEnd <= 0f)) {
            errors.Add(item: new(Message: "radiusEnd must be finite and positive.", Path: $"{path}.radiusEnd"));

            return;
        }

        var bulge = (curve.Bulge ?? 0f);
        var twist = (curve.Twist ?? 0f);
        var strandOffset = (curve.StrandOffset ?? 0f);
        var strands = (curve.Strands ?? SdfProgramBuilder.MinSweepStrands);
        var maxRadius = MathF.Max(x: radiusStart, y: radiusEnd);
        var minRadius = MathF.Min(x: radiusStart, y: radiusEnd);

        if (!float.IsFinite(f: bulge)) {
            errors.Add(item: new(Message: "bulge is non-finite.", Path: $"{path}.bulge"));
        } else if (MathF.Abs(x: bulge) > (SdfProgramBuilder.MaxSweepBulgeRatio * maxRadius)) {
            errors.Add(item: new(Message: $"bulge magnitude {MathF.Abs(x: bulge)} exceeds {SdfProgramBuilder.MaxSweepBulgeRatio} times the largest radius {maxRadius}.", Path: $"{path}.bulge"));
        }

        if (!float.IsFinite(f: twist)) {
            errors.Add(item: new(Message: "twist is non-finite.", Path: $"{path}.twist"));
        }

        if (!float.IsFinite(f: strandOffset) || (strandOffset < 0f)) {
            errors.Add(item: new(Message: "strandOffset must be finite and non-negative.", Path: $"{path}.strandOffset"));
        } else if (strandOffset > (SdfProgramBuilder.MaxSweepStrandOffsetRatio * maxRadius)) {
            errors.Add(item: new(Message: $"strandOffset {strandOffset} exceeds {SdfProgramBuilder.MaxSweepStrandOffsetRatio} times the largest radius {maxRadius}.", Path: $"{path}.strandOffset"));
        }

        if ((strands < SdfProgramBuilder.MinSweepStrands) || (strands > SdfProgramBuilder.MaxSweepStrands)) {
            errors.Add(item: new(Message: $"strands must be {SdfProgramBuilder.MinSweepStrands}..{SdfProgramBuilder.MaxSweepStrands}; got {strands}.", Path: $"{path}.strands"));
        }

        if (MathF.Abs(x: (radiusEnd - radiusStart)) > (SdfProgramBuilder.MaxSweepTaperRatio * minRadius)) {
            errors.Add(item: new(Message: $"the radius taper |{radiusEnd} - {radiusStart}| exceeds {SdfProgramBuilder.MaxSweepTaperRatio} times the smaller radius {minRadius}.", Path: $"{path}.radiusEnd"));
        }
    }
}
