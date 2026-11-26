using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

/// <summary>The rotation and vector builtins of the document language, folded while lowering: a quaternion is an
/// array <c>[x, y, z, w]</c> (the spelling a shape's <c>rotation</c> holds), a point or axis an array of three numbers,
/// and an angle is read in degrees, so <c>45</c>, <c>45deg</c> and <c>0.785rad</c> are one angle.</summary>
/// <remarks>These are authoring arithmetic over the numbers a document carries, folded in double as
/// <see cref="DocumentScalars"/> folds <c>sine</c>; nothing here reaches simulation state.</remarks>
public static class DocumentVectors {
    private const double DegreesToRadians = (Math.PI / 180.0);

    /// <summary>Folds <c>rotate(axis, degrees)</c>: the unit quaternion turning <c>degrees</c> about <c>axis</c>,
    /// right-handed. The axis need not be unit length; a zero axis is refused.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The quaternion, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? Rotate(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count != 2) {
            return Refuse(call: call, message: "rotate(axis, degrees) takes two arguments", scope: scope);
        }
        if (!TryVector(call: call, index: 0, length: 3, scope: scope, vector: out var axis) || !TryDegrees(call: call, index: 1, scope: scope, radians: out var angle)) {
            return null;
        }

        var norm = Math.Sqrt(d: ((axis[0] * axis[0]) + (axis[1] * axis[1]) + (axis[2] * axis[2])));

        if (norm == 0) {
            return Refuse(call: call, message: "rotate's axis has no direction", scope: scope);
        }

        var (sine, cosine) = Math.SinCos(x: (angle / 2));
        var scale = (sine / norm);

        return Vector(span: call.Span, values: [(axis[0] * scale), (axis[1] * scale), (axis[2] * scale), cosine]);
    }
    /// <summary>Folds <c>compose(outer, inner, ...)</c>: the quaternion product, so the result turns a point by the
    /// last argument first and the first argument last, as <c>outer(inner(p))</c> reads.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The quaternion, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? Compose(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count < 2) {
            return Refuse(call: call, message: "compose(outer, inner, ...) takes two or more rotations", scope: scope);
        }
        if (!TryVector(call: call, index: 0, length: 4, scope: scope, vector: out var product)) {
            return null;
        }

        for (var index = 1; (index < call.Arguments.Count); index++) {
            if (!TryVector(call: call, index: index, length: 4, scope: scope, vector: out var next)) {
                return null;
            }

            product = Multiply(left: product, right: next);
        }

        return Vector(span: call.Span, values: product);
    }
    /// <summary>Folds <c>rotatePoint(rotation, point)</c>: the point turned by the quaternion about the origin.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The point, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? RotatePoint(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count != 2) {
            return Refuse(call: call, message: "rotatePoint(rotation, point) takes two arguments", scope: scope);
        }
        if (!TryVector(call: call, index: 0, length: 4, scope: scope, vector: out var q) || !TryVector(call: call, index: 1, length: 3, scope: scope, vector: out var p)) {
            return null;
        }

        // p' = p + 2w(u x p) + 2u x (u x p), the sandwich product q p q* for a unit q = (u, w).
        var (ux, uy, uz, w) = (q[0], q[1], q[2], q[3]);
        var tx = (2 * ((uy * p[2]) - (uz * p[1])));
        var ty = (2 * ((uz * p[0]) - (ux * p[2])));
        var tz = (2 * ((ux * p[1]) - (uy * p[0])));

        return Vector(span: call.Span, values: [
            ((p[0] + (w * tx)) + ((uy * tz) - (uz * ty))),
            ((p[1] + (w * ty)) + ((uz * tx) - (ux * tz))),
            ((p[2] + (w * tz)) + ((ux * ty) - (uy * tx))),
        ]);
    }
    /// <summary>Folds <c>polarPoint(radius, degrees, y)</c>: the point <c>radius</c> out from the vertical axis at
    /// <c>degrees</c> around it, measured from +X toward +Z, at height <c>y</c>.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The point, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? PolarPoint(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count is not (2 or 3)) {
            return Refuse(call: call, message: "polarPoint(radius, degrees, y) takes two or three arguments", scope: scope);
        }
        if (!TryScalar(call: call, index: 0, scope: scope, value: out var radius) || !TryDegrees(call: call, index: 1, scope: scope, radians: out var angle)) {
            return null;
        }

        var height = 0.0;

        if ((call.Arguments.Count == 3) && !TryScalar(call: call, index: 2, scope: scope, value: out height)) {
            return null;
        }

        var (sine, cosine) = Math.SinCos(x: angle);

        return Vector(span: call.Span, values: [(radius * cosine), height, (radius * sine)]);
    }

    /// <summary>Folds <c>overlaps(a, b)</c>: 1 when the open spans <c>[low, high]</c> share a point, so spans that only
    /// touch at an end do not overlap, else 0.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>1 or 0, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? Overlaps(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count != 2) {
            return Refuse(call: call, message: "overlaps(a, b) takes two spans", scope: scope);
        }
        if (!TryVector(call: call, index: 0, length: 2, scope: scope, vector: out var a) || !TryVector(call: call, index: 1, length: 2, scope: scope, vector: out var b)) {
            return null;
        }

        return JsonValue.Create(value: (((a[0] < b[1]) && (b[0] < a[1])) ? 1L : 0L));
    }
    /// <summary>Folds <c>within(x, span)</c>: 1 when <c>low &lt;= x &lt;= high</c> for the span <c>[low, high]</c>, else 0.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>1 or 0, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? Within(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count != 2) {
            return Refuse(call: call, message: "within(x, span) takes a number and a span", scope: scope);
        }
        if (!TryScalar(call: call, index: 0, scope: scope, value: out var x) || !TryVector(call: call, index: 1, length: 2, scope: scope, vector: out var span)) {
            return null;
        }

        return JsonValue.Create(value: (((span[0] <= x) && (x <= span[1])) ? 1L : 0L));
    }

    /// <summary>Folds <c>fraction(x)</c>: <c>x - floor(x)</c>, the part of a number above its floor.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key, which the argument is read under.</param>
    /// <returns>The fraction, or <see langword="null"/> after reporting a refusal.</returns>
    public static JsonNode? Fraction(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        if (call.Arguments.Count != 1) {
            return Refuse(call: call, message: "fraction(x) takes one argument", scope: scope);
        }

        var lowered = DocumentLowering.EvaluateValue(expr: call.Arguments[0].Value, fieldKey: fieldKey, scope: scope);

        if (DocumentNumbers.TryInteger(node: lowered, number: out _)) {
            return JsonValue.Create(value: 0L);
        }
        if (!DocumentLowering.TryReadNumber(node: lowered, number: out var value)) {
            return Refuse(call: call, message: "fraction reads a number known at compile time", scope: scope);
        }

        return DocumentLowering.NumberNode(span: call.Span, value: (value - Math.Floor(d: value)));
    }

    private static double[] Multiply(double[] left, double[] right) => [
        ((((left[3] * right[0]) + (left[0] * right[3])) + (left[1] * right[2])) - (left[2] * right[1])),
        ((((left[3] * right[1]) - (left[0] * right[2])) + (left[1] * right[3])) + (left[2] * right[0])),
        ((((left[3] * right[2]) + (left[0] * right[1])) - (left[1] * right[0])) + (left[2] * right[3])),
        ((((left[3] * right[3]) - (left[0] * right[0])) - (left[1] * right[1])) - (left[2] * right[2])),
    ];
    private static JsonNode? Refuse(DocumentScope scope, CallExpressionNode call, string message) => DocumentBuiltins.Refuse(call: call, message: message, scope: scope);
    private static bool TryDegrees(CallExpressionNode call, int index, DocumentScope scope, out double radians) {
        radians = 0;

        if (!DocumentLowering.TryReadNumber(node: DocumentLowering.EvaluateValue(expr: call.Arguments[index].Value, fieldKey: DocumentDimensions.DegreesArgument, scope: scope), number: out var degrees)) {
            Refuse(call: call, message: $"{call.Name}'s angle is a number of degrees known at compile time", scope: scope);

            return false;
        }

        radians = (degrees * DegreesToRadians);

        return true;
    }
    private static bool TryScalar(CallExpressionNode call, int index, DocumentScope scope, out double value) {
        if (DocumentLowering.TryReadNumber(node: DocumentLowering.EvaluateValue(expr: call.Arguments[index].Value, fieldKey: DocumentDimensions.ArgumentPosition, scope: scope), number: out value)) {
            return true;
        }

        Refuse(call: call, message: $"{call.Name} reads numbers known at compile time", scope: scope);

        return false;
    }
    private static bool TryVector(CallExpressionNode call, int index, int length, DocumentScope scope, out double[] vector) {
        vector = new double[length];

        if (
            (DocumentLowering.EvaluateValue(expr: call.Arguments[index].Value, fieldKey: DocumentDimensions.ArgumentPosition, scope: scope) is JsonArray array) &&
            (array.Count == length)
        ) {
            var read = true;

            for (var component = 0; (component < length); component++) {
                read &= DocumentLowering.TryReadNumber(node: array[component], number: out vector[component]);
            }

            if (read) {
                return true;
            }
        }

        var shape = length switch {
            2 => "a span [low, high]",
            4 => "a rotation [x, y, z, w]",
            _ => "a point [x, y, z]",
        };

        Refuse(call: call, message: $"{call.Name}'s argument {index + 1} is {shape} of numbers", scope: scope);

        return false;
    }
    private static JsonArray Vector(SourceSpan span, double[] values) {
        var array = new JsonArray();

        foreach (var value in values) {
            // A component within rounding of a whole number is that number, so rotate([0, 1, 0], 180) writes 0, not 6e-17.
            var rounded = Math.Round(a: value);

            array.Add(item: DocumentLowering.NumberNode(span: span, value: ((Math.Abs(value: (value - rounded)) < 1e-12) ? rounded : value)));
        }

        return array;
    }
}
