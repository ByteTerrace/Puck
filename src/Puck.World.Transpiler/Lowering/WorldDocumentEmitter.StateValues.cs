using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private static ExpressionNode ResolveStateLiteral(ExpressionNode expr, DocumentScope scope) => ResolveStateLiteral(
        expr: expr,
        refused: out _,
        scope: scope
    );
    // The literal a state value spells; refused when it reads a name whose read was refused where it stands, so the
    // literal is only that refusal's placeholder.
    private static ExpressionNode ResolveStateLiteral(ExpressionNode expr, DocumentScope scope, out bool refused) {
        refused = false;

        if (expr is LiteralExpressionNode) {
            return expr;
        }
        if (DocumentLowering.LowerValue(
            expr: expr,
            scope: scope
        ) is not JsonValue lowered) {
            return expr;
        }

        refused = DocumentLowering.IsRefusedRead(value: lowered);

        object? value = null;

        if (lowered.TryGetValue<long>(value: out var whole)) {
            value = whole;
        } else if (lowered.TryGetValue<bool>(value: out var flag)) {
            value = flag;
        } else if (lowered.TryGetValue<string>(value: out var text)) {
            value = text;
        } else if (lowered.TryGetValue<decimal>(value: out var exact)) {
            value = (((exact == decimal.Truncate(d: exact)) && (exact >= long.MinValue) && (exact <= long.MaxValue))
                ? ((object)((long)exact))
                : ((double)exact)
            );
        } else if (lowered.TryGetValue<double>(value: out var real)) {
            value = real;
        }

        return ((value is null)
            ? expr
            : new LiteralExpressionNode(
                Column: expr.Column,
                Length: expr.Length,
                Line: expr.Line,
                Offset: expr.Offset,
                Value: value
            )
        );
    }
    private static JsonNode LowerStateScalarValue(ExpressionNode expr, string kind, string context, DocumentScope scope) {
        var literal = ResolveStateLiteral(
            expr: expr,
            refused: out var refused,
            scope: scope
        );

        // A refused read was reported where it was read; its kind's zero stands in without a second report.
        if (refused) {
            return kind switch {
                "Bool" => JsonValue.Create(value: false),
                "Text" => JsonValue.Create(value: string.Empty),
                "Fixed" => JsonValue.Create(value: "0"),
                _ => JsonValue.Create(value: 0L),
            };
        }

        return kind switch {
            "Bool" => LowerStateBoolValue(context: context, expr: literal, scope: scope),
            "Text" => LowerStateTextValue(context: context, expr: literal, scope: scope),
            "Fixed" => LowerStateFixedValue(context: context, expr: literal, scope: scope),
            _ => LowerStateIntValue(context: context, expr: literal, scope: scope),
        };
    }
    private static JsonNode LowerStateIntValue(ExpressionNode expr, string context, DocumentScope scope) {
        if ((expr is LiteralExpressionNode { Unit: null, Value: decimal exact }) &&
            (exact == decimal.Truncate(d: exact)) && (exact >= long.MinValue) && (exact <= long.MaxValue)) {
            return JsonValue.Create(((long)exact))!;
        }
        if (expr is LiteralExpressionNode { Unit: null, Value: long l }) {
            return JsonValue.Create(value: l)!;
        }
        if (
            (expr is LiteralExpressionNode { Unit: null, Value: ulong ul }) &&
            (ul <= long.MaxValue)
        ) {
            return JsonValue.Create(value: ((long)ul))!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a whole number for an Int row",
            span: expr.Span
        );

        return JsonValue.Create(value: 0L)!;
    }
    private static JsonNode LowerStateBoolValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (expr is LiteralExpressionNode { Unit: null, Value: bool b }) {
            return JsonValue.Create(value: b)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be 'true' or 'false' for a Bool row",
            span: expr.Span
        );

        return JsonValue.Create(value: false)!;
    }
    private static JsonNode LowerStateTextValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (expr is LiteralExpressionNode { Unit: null, Value: string s }) {
            return JsonValue.Create(value: s)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a string for a Text row",
            span: expr.Span
        );

        return JsonValue.Create(value: "")!;
    }
    private static JsonNode LowerStateFixedValue(ExpressionNode expr, string context, DocumentScope scope) {
        if (TryFormatFixedLiteral(
            expr: expr,
            text: out var text
        )) {
            return JsonValue.Create(value: text)!;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
            message: $"{context} must be a decimal number for a Fixed row",
            span: expr.Span
        );

        return JsonValue.Create(value: "0")!;
    }
    private static bool TryFormatFixedLiteral(ExpressionNode expr, out string text) {
        text = "";

        if (expr is not LiteralExpressionNode { Unit: null, Value: var raw }) {
            return false;
        }

        var decimalText = (raw switch {
            long l => l.ToString(provider: CultureInfo.InvariantCulture),
            ulong ul => ul.ToString(provider: CultureInfo.InvariantCulture),
            decimal exact => exact.ToString(provider: CultureInfo.InvariantCulture),
            double d => d.ToString(format: "R", provider: CultureInfo.InvariantCulture),
            _ => null,
        });

        if (
            (decimalText is null) ||
            !FixedQ4816.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var parsed,
            s: decimalText
        )
        ) {
            return false;
        }

        text = parsed.ToString();

        return true;
    }
    // A decimal `perSecond` rate reduces to an exact fraction from the author's own digits, never from a double's
    // raw bits: a directly authored literal carries its exact source text on RawText (set by the lexer before it
    // ever rounds that text into a double). Only a value with none — an identifier or expression the lowering
    // pipeline already folded through `double` arithmetic before this reduction ever sees it — falls back to that
    // double's own shortest round-trip text, since no more precise source exists once the value has actually been
    // computed in `double`.
    private static bool TryReduceRate(ExpressionNode expr, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        if (expr is not LiteralExpressionNode { Unit: null, Value: var raw } literal) {
            return false;
        }

        switch (raw) {
            case long l:
                numerator = l;
                denominator = 1L;

                return true;
            case ulong ul when (ul <= long.MaxValue):
                numerator = ((long)ul);
                denominator = 1L;

                return true;
            case double d:
                return TryReduceDecimalRate(
                    denominator: out denominator,
                    numerator: out numerator,
                    text: (literal.RawText ?? d.ToString(format: "R", provider: CultureInfo.InvariantCulture))
                );
            default:
                return false;
        }
    }
    // Parses `text` (a sign, digits, an optional '.', and an optional exponent — exactly what the lexer or
    // decimal.ToString can produce) into an exact unscaled BigInteger and a base-10 scale with no intermediate
    // double or decimal, so a rate with more significant digits than either type holds still reduces from the
    // author's own digits instead of silently rounding.
    private static bool TryParseExactDecimalText(string text, out BigInteger unscaled, out int scale) {
        unscaled = BigInteger.Zero;
        scale = 0;

        var mantissa = text;
        var negative = false;

        if ((mantissa.Length > 0) && (mantissa[0] is '+' or '-')) {
            negative = (mantissa[0] == '-');
            mantissa = mantissa[1..];
        }

        var exponent = 0;
        var exponentIndex = mantissa.IndexOfAny(anyOf: ['e', 'E']);

        if (exponentIndex >= 0) {
            if (!int.TryParse(
                provider: CultureInfo.InvariantCulture,
                result: out exponent,
                s: mantissa[(exponentIndex + 1)..],
                style: NumberStyles.AllowLeadingSign
            )) {
                return false;
            }

            mantissa = mantissa[..exponentIndex];
        }

        var pointIndex = mantissa.IndexOf(value: '.');
        var digits = ((pointIndex < 0) ? mantissa : (mantissa[..pointIndex] + mantissa[(pointIndex + 1)..]));
        var fractionLength = ((pointIndex < 0) ? 0 : ((mantissa.Length - pointIndex) - 1));

        if (digits.Length == 0) {
            return false;
        }
        foreach (var c in digits) {
            if (!char.IsAsciiDigit(c: c)) {
                return false;
            }
        }

        unscaled = BigInteger.Parse(
            provider: CultureInfo.InvariantCulture,
            value: digits
        );
        scale = (fractionLength - exponent);

        if (scale < 0) {
            unscaled *= BigInteger.Pow(
                exponent: -scale,
                value: 10
            );
            scale = 0;
        }
        if (negative) {
            unscaled = -unscaled;
        }

        return true;
    }
    private static bool TryReduceDecimalRate(string text, out long numerator, out long denominator) {
        numerator = 0L;
        denominator = 1L;

        if (!TryParseExactDecimalText(
            scale: out var scale,
            text: text,
            unscaled: out var unscaled
        )) {
            return false;
        }

        var scaledDenominator = BigInteger.Pow(
            exponent: scale,
            value: 10
        );
        var gcd = BigInteger.GreatestCommonDivisor(
            left: BigInteger.Abs(value: unscaled),
            right: scaledDenominator
        );

        if (gcd > BigInteger.Zero) {
            unscaled /= gcd;
            scaledDenominator /= gcd;
        }
        if (
            (unscaled < long.MinValue) ||
            (unscaled > long.MaxValue) ||
            (scaledDenominator > long.MaxValue)
        ) {
            return false;
        }

        numerator = ((long)unscaled);
        denominator = ((long)scaledDenominator);

        return true;
    }
}
