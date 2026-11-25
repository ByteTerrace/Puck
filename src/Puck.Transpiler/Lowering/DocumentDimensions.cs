using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Lowering;

/// <summary>What a value's physical dimension is known to be at compile time.</summary>
public enum DimensionKnowledge {
    /// <summary>Nothing is known: a record, a text, a member read, a call no rule covers. Never refused.</summary>
    Unknown,
    /// <summary>A bare number, which takes the dimension of what it meets in a sum and of the field it fills.</summary>
    Free,
    /// <summary>A definite dimension, from a unit an author wrote.</summary>
    Known,
}
/// <summary>A value's inferred dimension: how much is known, and the dimension when it is known.</summary>
/// <param name="Knowledge">How much is known.</param>
/// <param name="Dimension">The dimension, when <paramref name="Knowledge"/> is <see cref="DimensionKnowledge.Known"/>.</param>
public readonly record struct InferredDimension(DimensionKnowledge Knowledge, PhysicalDimension Dimension) {
    /// <summary>Gets the inference of a value nothing is known about.</summary>
    public static InferredDimension Unknown => default;
    /// <summary>Gets the inference of a bare number.</summary>
    public static InferredDimension Free => new(Knowledge: DimensionKnowledge.Free, Dimension: PhysicalDimension.None);
    /// <summary>Returns the inference of a value of a definite dimension.</summary>
    /// <param name="dimension">The dimension.</param>
    /// <returns>The inference.</returns>
    public static InferredDimension Of(PhysicalDimension dimension) => new(Knowledge: DimensionKnowledge.Known, Dimension: dimension);
}
/// <summary>
/// The compile-time dimension check of a <c>.puck</c> document. A unit an author writes (<c>3m</c>, <c>8m/s</c>, a
/// <c>let reach: m = 3</c> annotation) gives a value a physical dimension; arithmetic carries it (a product multiplies
/// dimensions, a sum needs two of one dimension), and a value that fills a field the vocabulary classifies must carry
/// that field's dimension. A bare number is free: it takes the dimension of what it is added to and of the field it
/// fills, read in that field's own unit. The check reads only syntax and reports into the scope's diagnostics; it
/// changes no value, so a document's numbers, and its runtime cost, are what they would be without it.
/// </summary>
/// <remarks>The one place a dimension is judged: <see cref="DocumentLowering"/> calls <see cref="CheckField"/> once
/// for each expression a field receives, where the field changes, and a <c>let</c>'s annotation lowers through
/// <see cref="ReadsInUnit"/>. Anything the rules here do not cover infers as <see cref="DimensionKnowledge.Unknown"/>
/// and is never refused, so the check can only refuse a value whose dimension it has proven wrong.</remarks>
public static class DocumentDimensions {
    /// <summary>The field key a trigonometric function's argument is lowered under: an angle, written in
    /// radians.</summary>
    public const string AngleArgument = "$angle";
    private const int MaximumDepth = 64;
    private static readonly PhysicalDimension Angle = new(Angle: 1);

    [ThreadStatic]
    private static HashSet<ExpressionNode>? DimensionsInProgress;

    /// <summary>Returns the class of the field a key names: the vocabulary's classification, or radians for
    /// <see cref="AngleArgument"/>.</summary>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The field key, or <see langword="null"/> for a value that fills no field.</param>
    /// <returns>The field's class.</returns>
    public static UnitDimension Classify(DocumentScope scope, string? fieldKey) {
        ArgumentNullException.ThrowIfNull(scope);

        return fieldKey switch {
            null => UnitDimension.None,
            AngleArgument => UnitDimension.Radians,
            _ => scope.Vocabulary.ClassifyField(fieldKey: fieldKey),
        };
    }
    /// <summary>Checks the dimension of a value a field receives, reporting a value whose dimension is proven to differ
    /// from the field's (<see cref="PuckDiagnosticCodes.UnitNotAdmitted"/>), one that carries a dimension into a field
    /// that admits none (<see cref="PuckDiagnosticCodes.UnitOnUnknownField"/>), and any mismatch inside it
    /// (<see cref="PuckDiagnosticCodes.DimensionMismatch"/>). An array's elements are each judged against the field,
    /// as a vector's components are.</summary>
    /// <param name="expression">The value as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The field key, or <see langword="null"/> for a value that fills no field.</param>
    public static void CheckField(ExpressionNode expression, DocumentScope scope, string? fieldKey) {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);

        if (expression is ArrayExpressionNode array) {
            foreach (var element in array.Elements) {
                CheckField(expression: element, fieldKey: fieldKey, scope: scope);
            }

            return;
        }

        var inferred = Infer(expression: expression, scope: scope);

        if (inferred.Knowledge != DimensionKnowledge.Known) {
            return;
        }

        var dimension = Classify(fieldKey: fieldKey, scope: scope);

        if (fieldKey == AngleArgument) {
            if (!inferred.Dimension.IsNone && (inferred.Dimension != Angle)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.DimensionMismatch,
                    message: $"a trigonometric function takes an angle or a pure number, but this value is {Spelling(dimension: inferred.Dimension)}",
                    span: expression.Span
                );
            }
        } else if (UnitConversion.DimensionOf(dimension: dimension) is { } expected) {
            if (inferred.Dimension != expected) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.UnitNotAdmitted,
                    message: $"{FieldName(fieldKey: fieldKey)} takes {ValueIn(dimension: expected)}, but this value is {Spelling(dimension: inferred.Dimension)}",
                    span: expression.Span
                );
            }
        } else if (!inferred.Dimension.IsNone) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnitOnUnknownField,
                message: $"{FieldName(fieldKey: fieldKey)} admits no unit, but this value is {Spelling(dimension: inferred.Dimension)}; remove the unit",
                span: expression.Span
            );
        }
    }
    /// <summary>Returns whether a <c>let</c>'s annotated value is read in its unit: a free value is (<c>let reach: cm
    /// = 50</c> is half a metre), and a value of the unit's dimension stands as it is. A value of another dimension, or
    /// a unit no suffix spells, is reported.</summary>
    /// <param name="annotation">The annotated value.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns><see langword="true"/> when the value lowers as a number in the annotation's unit.</returns>
    public static bool ReadsInUnit(UnitAnnotationNode annotation, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(scope);

        if (!UnitConversion.TryRead(degrees: false, unit: annotation.Unit, value: out var unit)) {
            ReportUnknownUnit(scope: scope, span: annotation.Span, unit: annotation.Unit);

            return false;
        }

        var inferred = Infer(expression: annotation.Value, scope: scope);

        if ((inferred.Knowledge == DimensionKnowledge.Known) && (inferred.Dimension != unit.Dimension)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.DimensionMismatch,
                message: $"this value is {Spelling(dimension: inferred.Dimension)}, but its annotation says {annotation.Unit}, {ValueIn(dimension: unit.Dimension)}",
                span: annotation.Value.Span
            );
        }

        return (inferred.Knowledge == DimensionKnowledge.Free);
    }
    /// <summary>Reports a unit no suffix spells.</summary>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="span">Where the unit is written.</param>
    /// <param name="unit">The unit as written.</param>
    public static void ReportUnknownUnit(DocumentScope scope, Diagnostics.SourceSpan span, string unit) {
        ArgumentNullException.ThrowIfNull(scope);

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.UnitNotAdmitted,
            message: $"'{unit}' is not a unit; a unit is one of m, cm, mm, s, ms, min, h, hz, deg, rad, %, pct, lx, lm, cd, nit and K, or several joined by '/' and raised by '^' (m/s, m/s^2)",
            span: span
        );
    }
    /// <summary>Infers a value's dimension from its syntax, reporting each place two dimensions meet that must agree
    /// (<see cref="PuckDiagnosticCodes.DimensionMismatch"/>).</summary>
    /// <param name="expression">The value as written.</param>
    /// <param name="scope">The scope its names resolve in.</param>
    /// <returns>What is known of its dimension.</returns>
    public static InferredDimension Infer(ExpressionNode expression, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(scope);

        var inProgress = (DimensionsInProgress ??= new HashSet<ExpressionNode>(comparer: ReferenceEqualityComparer.Instance));

        if ((inProgress.Count >= MaximumDepth) || !inProgress.Add(item: expression)) {
            return InferredDimension.Unknown;
        }

        try {
            return InferCore(expression: expression, scope: scope);
        } finally {
            _ = inProgress.Remove(item: expression);
        }
    }
    private static InferredDimension InferCore(ExpressionNode expression, DocumentScope scope) {
        switch (expression) {
            case LiteralExpressionNode { Value: long or ulong or int or double or decimal, Unit: null }:
                return InferredDimension.Free;
            case LiteralExpressionNode { Value: long or ulong or int or double or decimal, Unit: { } unit }:
                return (UnitConversion.TryRead(degrees: false, unit: unit, value: out var read)
                    ? InferredDimension.Of(dimension: read.Dimension)
                    : InferredDimension.Unknown);
            case UnitAnnotationNode annotated:
                return (UnitConversion.TryRead(degrees: false, unit: annotated.Unit, value: out var annotation)
                    ? InferredDimension.Of(dimension: annotation.Dimension)
                    : InferredDimension.Unknown);
            case IdentifierExpressionNode identifier:
                return Identifier(name: identifier.Name, scope: scope);
            case UnaryExpressionNode { Operator: "-" } negated:
                return Infer(expression: negated.Operand, scope: scope);
            case UnaryExpressionNode { Operator: "not" }:
                return InferredDimension.Free;
            case BinaryExpressionNode binary:
                return Binary(binary: binary, scope: scope);
            case ConditionalExpressionNode conditional:
                return Agree(
                    left: Infer(expression: conditional.WhenTrue, scope: scope),
                    right: Infer(expression: conditional.WhenFalse, scope: scope),
                    scope: scope,
                    span: conditional.Span,
                    what: "a choice's two arms"
                );
            case CallExpressionNode call:
                return Call(call: call, scope: scope);
            default:
                return InferredDimension.Unknown;
        }
    }
    // A name reads the dimension of what it is bound to: a loop variable or lambda parameter holding a number is free,
    // and a let or a template's argument is inferred from its expression where it was written.
    private static InferredDimension Identifier(string name, DocumentScope scope) {
        if (scope.Locals.TryGetValue(key: name, value: out var local)) {
            return ((local is JsonValue value) && DocumentLowering.TryReadNumber(node: value, number: out _))
                ? InferredDimension.Free
                : InferredDimension.Unknown;
        }

        return ((scope.TryGetExpression(expression: out var bound, lexical: out var lexical, name: name) && (bound is not null) && (bound is not FunctionExpressionNode))
            ? Infer(expression: bound, scope: lexical)
            : InferredDimension.Unknown);
    }
    private static InferredDimension Binary(BinaryExpressionNode binary, DocumentScope scope) {
        switch (binary.Operator) {
            case "+" or "-" or "%":
                return Agree(
                    left: Infer(expression: binary.Left, scope: scope),
                    right: Infer(expression: binary.Right, scope: scope),
                    scope: scope,
                    span: binary.Span,
                    what: $"'{binary.Operator}'"
                );
            case "==" or "!=" or "<" or "<=" or ">" or ">=": {
                    var compared = Agree(
                        left: Infer(expression: binary.Left, scope: scope),
                        right: Infer(expression: binary.Right, scope: scope),
                        scope: scope,
                        span: binary.Span,
                        what: $"'{binary.Operator}'"
                    );

                    return ((compared.Knowledge == DimensionKnowledge.Unknown) ? InferredDimension.Unknown : InferredDimension.Free);
                }
            case "*" or "/": {
                    var left = Infer(expression: binary.Left, scope: scope);
                    var right = Infer(expression: binary.Right, scope: scope);

                    if ((left.Knowledge == DimensionKnowledge.Unknown) || (right.Knowledge == DimensionKnowledge.Unknown)) {
                        return InferredDimension.Unknown;
                    }

                    if ((left.Knowledge == DimensionKnowledge.Free) && (right.Knowledge == DimensionKnowledge.Free)) {
                        return InferredDimension.Free;
                    }

                    return InferredDimension.Of(dimension: ((binary.Operator == "*")
                        ? (left.Dimension * right.Dimension)
                        : (left.Dimension / right.Dimension)));
                }
            default:
                return InferredDimension.Unknown;
        }
    }
    // The compile-time scalar functions: a trigonometric function takes an angle or a pure number and gives a pure
    // number; a rounding keeps its argument's dimension; minimum, maximum and clamp need their arguments to agree; a
    // square root halves the powers. Every other call is unknown.
    private static InferredDimension Call(CallExpressionNode call, DocumentScope scope) {
        var arguments = call.Arguments;

        switch (call.Name) {
            // Its argument is judged where it is lowered, under AngleArgument (CheckField).
            case "sine" or "cosine" when (arguments.Count == 1):
                return InferredDimension.Free;
            case "absolute" or "ceiling" or "floor" or "round" when (arguments.Count == 1):
                return Infer(expression: arguments[0].Value, scope: scope);
            case "sign" when (arguments.Count == 1):
                return InferredDimension.Free;
            case "squareRoot" when (arguments.Count == 1): {
                    var squared = Infer(expression: arguments[0].Value, scope: scope);

                    return squared.Knowledge switch {
                        DimensionKnowledge.Known when squared.Dimension.TrySquareRoot(root: out var root) => InferredDimension.Of(dimension: root),
                        DimensionKnowledge.Free => InferredDimension.Free,
                        _ => InferredDimension.Unknown,
                    };
                }
            case "minimum" or "maximum" or "clamp" when (arguments.Count > 0): {
                    var agreed = Infer(expression: arguments[0].Value, scope: scope);

                    for (var index = 1; (index < arguments.Count); index++) {
                        agreed = Agree(
                            left: agreed,
                            right: Infer(expression: arguments[index].Value, scope: scope),
                            scope: scope,
                            span: call.Span,
                            what: $"'{call.Name}''s arguments"
                        );
                    }

                    return agreed;
                }
            case "length" when (arguments.Count == 1):
                return InferredDimension.Free;
            default:
                return InferredDimension.Unknown;
        }
    }
    // Two values that must share a dimension: a free one takes the other's; two known ones of different dimensions are
    // a mismatch, reported once where they meet.
    private static InferredDimension Agree(InferredDimension left, InferredDimension right, DocumentScope scope, Diagnostics.SourceSpan span, string what) {
        if ((left.Knowledge == DimensionKnowledge.Unknown) || (right.Knowledge == DimensionKnowledge.Unknown)) {
            return InferredDimension.Unknown;
        }

        if (left.Knowledge == DimensionKnowledge.Free) {
            return right;
        }

        if (right.Knowledge == DimensionKnowledge.Free) {
            return left;
        }

        if (left.Dimension != right.Dimension) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.DimensionMismatch,
                message: $"{what} joins {Name(dimension: left.Dimension)} with {Name(dimension: right.Dimension)}",
                span: span
            );

            return InferredDimension.Unknown;
        }

        return left;
    }
    private static string FieldName(string? fieldKey) => (fieldKey switch {
        null => "this value's position",
        AngleArgument => "a trigonometric function",
        _ => $"'{fieldKey}'",
    });
    private static string Spelling(PhysicalDimension dimension) => (dimension.IsNone ? "a pure number" : $"in {dimension}");
    private static string Name(PhysicalDimension dimension) => (dimension.IsNone ? "a pure number" : dimension.ToString());
    private static string ValueIn(PhysicalDimension dimension) => (dimension.IsNone ? "a pure number" : $"a value in {dimension}");
}
