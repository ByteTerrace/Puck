using Puck.Maths;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    private static readonly float CurveMaxCoordinate = ((float)(double)CurvatureSpline.MaxCoordinate);
    private static readonly float CurveMaxCurvature = ((float)(double)CurvatureSpline.MaxCurvature);
    // A knot's tangentYaw must arrive already reduced to the canonical interval: FixedQ4816.FromDouble saturates
    // (rather than wraps) a finite value outside its representable range, so an authored angle outside this interval
    // would silently compile at a saturated direction unrelated to the authored periodic angle instead of refusing.
    // The slack over MathF.PI absorbs float round-trip of a double PI literal without admitting a genuinely
    // unreduced angle (e.g. one authored in turns or degrees by mistake).
    private const float TangentYawSlack = 1e-3f;
    private static readonly float CurveMaxTangentYaw = (MathF.PI + TangentYawSlack);

    private static HashSet<string> ValidateCurves(IReadOnlyList<WorldCurveRow> curves, List<string> errors) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (curves.Count > WorldCurves.MaxRows) {
            errors.Add(item: $"curves declares {curves.Count} row(s), more than {WorldCurves.MaxRows}.");
        }

        for (var index = 0; (index < curves.Count); index++) {
            var row = curves[index];
            var path = $"curves[{index}]";

            if (row is null) {
                errors.Add(item: $"{path} is required.");

                continue;
            }

            RequireUniqueName(
                value: row.Name,
                seen: names,
                path: path,
                field: "",
                errors: errors
            );

            var knots = row.Knots;
            var knotCount = (knots?.Count ?? 0);
            var minKnots = (row.Closed ? 3 : 2);
            var admitted = true;

            if (knotCount < minKnots) {
                errors.Add(item: $"{path}.knots declares {knotCount} knot(s); a {(row.Closed ? "closed" : "open")} curve needs at least {minKnots}.");

                admitted = false;
            }

            if (knotCount > WorldCurves.MaxKnots) {
                errors.Add(item: $"{path}.knots declares {knotCount} knot(s), more than {WorldCurves.MaxKnots}.");

                admitted = false;
            }

            for (var knotIndex = 0; (knotIndex < knotCount); knotIndex++) {
                var knot = knots![knotIndex];
                var knotPath = $"{path}.knots[{knotIndex}]";

                if (knot is null) {
                    errors.Add(item: $"{knotPath} is required.");

                    admitted = false;

                    continue;
                }

                var position = knot.Position.Value;

                RequireRange(value: position.X, min: -CurveMaxCoordinate, max: CurveMaxCoordinate, name: $"{knotPath}.position.x", errors: errors);
                RequireRange(value: position.Y, min: -CurveMaxCoordinate, max: CurveMaxCoordinate, name: $"{knotPath}.position.y", errors: errors);
                RequireRange(value: position.Z, min: -CurveMaxCoordinate, max: CurveMaxCoordinate, name: $"{knotPath}.position.z", errors: errors);
                RequireRange(value: knot.TangentYaw, min: -CurveMaxTangentYaw, max: CurveMaxTangentYaw, name: $"{knotPath}.tangentYaw", errors: errors);
                RequireRange(value: knot.Curvature, min: -CurveMaxCurvature, max: CurveMaxCurvature, name: $"{knotPath}.curvature", errors: errors);

                var knotAdmitted = (
                    float.IsFinite(f: position.X) && (MathF.Abs(x: position.X) <= CurveMaxCoordinate) &&
                    float.IsFinite(f: position.Y) && (MathF.Abs(x: position.Y) <= CurveMaxCoordinate) &&
                    float.IsFinite(f: position.Z) && (MathF.Abs(x: position.Z) <= CurveMaxCoordinate) &&
                    float.IsFinite(f: knot.TangentYaw) && (MathF.Abs(x: knot.TangentYaw) <= CurveMaxTangentYaw) &&
                    float.IsFinite(f: knot.Curvature) && (MathF.Abs(x: knot.Curvature) <= CurveMaxCurvature)
                );

                if (!knotAdmitted) {
                    admitted = false;
                }
            }

            // The per-field checks above can pass every ceiling and still fail to compile: chord length, tangent/
            // curvature consistency, an unreachable root, an interior cusp, and Q32 carrier overflow are all
            // geometric facts only the exact solve can decide. Running the SAME derivation the simulation compiles
            // from catches every such refusal in one place instead of duplicating CurvatureSpline's own ladder here.
            if (admitted) {
                try {
                    _ = row.Compiled;
                } catch (CurvatureSplineException exception) {
                    errors.Add(item: $"{path} does not compile — {exception.Message}");
                }
            }
        }

        return names;
    }
}
