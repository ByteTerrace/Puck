using System.Globalization;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Lowering;

/// <summary>Lowers the parts of a parsed document that mean the same thing in every schema: values, constants,
/// units, arithmetic, template invocation, and canonical key order. What a SECTION means is a vocabulary's own
/// emitter; what <c>1.5m</c>, <c>-spread</c> or <c>2 * 3</c> mean is here.</summary>
public static partial class DocumentLowering {
    // Internal evaluation returns borrowed, read-only values. Clone when constructing an output container, never
    // merely to inspect a value or select one element of a cached array.
    internal static JsonNode? EvaluateValue(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(scope);
        using var evaluation = scope.Budget.Enter(span: expr.Span);

        if (scope.Vocabulary.TryLowerValue(
            expression: expr,
            fieldKey: fieldKey,
            scope: scope,
            value: out var specialized
        )) { return specialized; }

        switch (expr) {
            case LiteralExpressionNode lit:
                return LowerLiteral(
                    fieldKey: fieldKey,
                    lit: lit,
                    scope: scope
                );

            case InterpolatedStringNode interpolated: {
                    var built = new System.Text.StringBuilder();

                    foreach (var segment in interpolated.Segments) {
                        switch (segment) {
                            case InterpolationSegment.Literal text:
                                built.Append(value: text.Text);

                                break;

                            case InterpolationSegment.Hole hole:
                                built.Append(value: FormatHole(node: LowerValue(
                                    expr: hole.Expression,
                                    scope: scope
                                )));

                                break;
                        }
                        if (built.Length > DocumentEvaluationBudget.TextLimit) {
                            throw new DocumentEvaluationException(
                                "The interpolated string exceeds the text limit.",
                                expr.Span
                            );
                        }
                    }
                    return JsonValue.Create(value: built.ToString());
                }

            case ColorExpressionNode color:
                return JsonValue.Create(value: color.Hex);

            case IdentifierExpressionNode ident:
                if (scope.TryEvaluateBinding(
                    ident.Name,
                    fieldKey,
                    out var bound
                )) {
                    return bound;
                }
                if (string.Equals(
                    a: ident.Name,
                    b: "null",
                    comparisonType: StringComparison.Ordinal
                )) {
                    return null;
                }
                if (string.Equals(
                    a: ident.Name,
                    b: "true",
                    comparisonType: StringComparison.Ordinal
                )) {
                    return JsonValue.Create(value: true);
                }
                if (string.Equals(
                    a: ident.Name,
                    b: "false",
                    comparisonType: StringComparison.Ordinal
                )) {
                    return JsonValue.Create(value: false);
                }
                if (string.Equals(
                    a: ident.Name,
                    b: "auto",
                    comparisonType: StringComparison.Ordinal
                )) {
                    return JsonValue.Create(value: "auto");
                }

                return JsonValue.Create(value: ident.Name);

            case MemberAccessExpressionNode memberAccess when (memberAccess.Target is IdentifierExpressionNode targetId):
                var qualified = $"{targetId.Name}.{memberAccess.Member}";
                if (scope.TryEvaluateBinding(
                    fieldKey: fieldKey,
                    name: qualified,
                    value: out var qBound
                )) {
                    return qBound;
                }
                return JsonValue.Create(value: qualified);

            case ArrayExpressionNode arr: {
                    scope.Budget.Collection(
                        count: arr.Elements.Count,
                        span: arr.Span
                    );
                    var jsonArr = new JsonArray();

                    foreach (var elem in arr.Elements) {
                        jsonArr.AppendNode(item: LowerValue(
                            expr: elem,
                            fieldKey: fieldKey,
                            scope: scope
                        ));
                    }

                    return jsonArr;
                }

            case ObjectExpressionNode obj: {
                    var jsonObj = new JsonObject();

                    var holder = MemberContext(scope: scope);
                    // A discriminated object and its call spelling share one member contract. Choose the arm
                    // before lowering any value, even when the discriminator is the object's last property.
                    var discriminator = obj.Properties.FirstOrDefault(predicate: static property => (property.Name == "$type"));
                    if (discriminator is not null) {
                        var name = discriminator.Value switch {
                            IdentifierExpressionNode identifier => identifier.Name,
                            LiteralExpressionNode { Value: string text } => text,
                            _ => null,
                        };
                        if (name is not null) { holder = (scope.Vocabulary.CallContext(callName: name, context: holder) ?? holder); }
                    }

                    foreach (var prop in obj.Properties) {
                        jsonObj[prop.Name] = LowerMember(
                            fieldKey: prop.Name,
                            holder: holder,
                            holderName: null,
                            memberName: prop.Name,
                            scope: scope,
                            value: prop.Value
                        );
                    }

                    return jsonObj;
                }

            case CallExpressionNode call: {
                    var context = MemberContext(scope: scope);
                    var arm = scope.Vocabulary.CallContext(callName: call.Name, context: context);
                    // A call filling a declared document arm constructs that arm. Only calls outside such a
                    // position are compile-time builtins, even when the two vocabularies share a name.
                    if (((context is null) || (arm is null)) && DocumentBuiltins.TryEvaluate(
                        call: call,
                        fieldKey: fieldKey,
                        result: out var builtin,
                        scope: scope
                    )) {
                        return builtin;
                    }

                    var jsonObj = new JsonObject {
                        ["$type"] = call.Name,
                    };
                    var positionalIndex = 0;

                    foreach (var arg in call.Arguments) {
                        var key = (arg.Name ?? (scope.Vocabulary.NameCallArgument(
                            callName: call.Name,
                            positionalIndex: positionalIndex
                        ) ?? $"arg{positionalIndex}"));

                        // Classified by the qualified `call.argument` key, so a unit reads against the argument's own
                        // dimension rather than whatever a same-named block property elsewhere means.
                        jsonObj[key] = LowerMember(
                            fieldKey: $"{call.Name}.{key}",
                            holder: arm,
                            holderName: call.Name,
                            memberName: key,
                            scope: scope,
                            value: arg.Value
                        );
                        positionalIndex++;
                    }

                    return jsonObj;
                }

            // A vocabulary that classifies an argument as an operand lowers it; one that does not never produces the
            // node, so the text reaches the document as written.
            case OperandExpressionNode operand:
                return JsonValue.Create(value: SpliceAtoms(operand: operand, scope: scope));

            case IndexExpressionNode indexed:
                return EvaluateIndex(
                    fieldKey: fieldKey,
                    indexed: indexed,
                    scope: scope
                );

            case BinaryExpressionNode bin:
                return EvaluateBinary(
                    bin: bin,
                    fieldKey: fieldKey,
                    scope: scope
                );

            case UnaryExpressionNode un:
                return EvaluateUnary(
                    fieldKey: fieldKey,
                    scope: scope,
                    un: un
                );

            case RangeExpressionNode range: {
                    var rangeArr = new JsonArray();

                    rangeArr.AppendNode(item: ((range.Start is { } start) ? LowerValue(expr: start, fieldKey: fieldKey, scope: scope) : null));
                    rangeArr.AppendNode(item: ((range.End is { } end) ? LowerValue(expr: end, fieldKey: fieldKey, scope: scope) : null));

                    return rangeArr;
                }

            case LambdaExpressionNode lambda:
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.LambdaOutsideBuiltin,
                    message: $"a lambda is an argument to {string.Join(
                        separator: "/",
                        values: DocumentBuiltins.Names
                    )} and nothing else",
                    span: lambda.Span
                );

                return null;

            default:
                return null;
        }
    }

    private const string MemberContextKey = "MemberContext";

    /// <summary>Gets the position the value being lowered fills, in the vocabulary's own terms.</summary>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The position, or <see langword="null"/> when none is known.</returns>
    public static object? MemberContext(DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(argument: scope);

        return scope.Annotations.GetValueOrDefault(key: MemberContextKey);
    }
    /// <summary>Lowers one member of an object or a call in the member's one spelling: the vocabulary says what the
    /// member holds, a misspelling is refused, and the value is lowered at the member's own position.</summary>
    /// <param name="holder">The position of the object or call holding the member.</param>
    /// <param name="holderName">The call's name, or <see langword="null"/> for an object.</param>
    /// <param name="memberName">The member's name.</param>
    /// <param name="value">The authored value.</param>
    /// <param name="fieldKey">The key a unit on the value is classified by.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The lowered value.</returns>
    public static JsonNode? LowerMember(object? holder, string? holderName, string memberName, ExpressionNode value, string? fieldKey, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(argument: memberName);
        ArgumentNullException.ThrowIfNull(argument: scope);
        ArgumentNullException.ThrowIfNull(argument: value);

        var form = scope.Vocabulary.ClassifyMember(context: holder, memberName: memberName);

        RefuseMisspelledMember(form: form, holderName: holderName, memberName: memberName, scope: scope, value: value);
        if ((value is IdentifierExpressionNode word) && scope.Vocabulary.IsChoiceWord(context: holder, memberName: memberName, word: word.Name)) {
            return JsonValue.Create(value: word.Name);
        }

        return At(
            context: scope.Vocabulary.MemberContext(context: holder, memberName: memberName),
            lower: () => scope.Vocabulary.NormalizeMemberValue(
                form: form,
                scope: scope,
                value: LowerValue(expr: Operand(form: form, value: value), fieldKey: fieldKey, scope: scope)
            ),
            scope: scope
        );
    }
    /// <summary>Runs <paramref name="lower"/> with <paramref name="context"/> as the position its values fill.</summary>
    /// <typeparam name="T">What <paramref name="lower"/> returns.</typeparam>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="context">The position, in the vocabulary's own terms.</param>
    /// <param name="lower">The lowering to run.</param>
    /// <returns>What <paramref name="lower"/> returned.</returns>
    public static T At<T>(DocumentScope scope, object? context, Func<T> lower) {
        ArgumentNullException.ThrowIfNull(argument: lower);
        ArgumentNullException.ThrowIfNull(argument: scope);

        var outer = MemberContext(scope: scope);

        scope.Annotations[MemberContextKey] = context;

        try {
            return lower();
        } finally {
            scope.Annotations[MemberContextKey] = outer;
        }
    }

    // The vocabulary classifies a member after parsing. Convert its syntax structurally, preserving explicit
    // grouping and atom identity. An array lowers element by element at the same position.
    private static ExpressionNode Operand(ExpressionNode value, DocumentValueForm form) => (value switch {
        _ when (form is not (DocumentValueForm.Name or DocumentValueForm.Key or DocumentValueForm.Expression)) => value,
        OperandExpressionNode operand => (operand with { Form = form }),
        ArrayExpressionNode array => (array with { Elements = [.. array.Elements.Select(selector: element => Operand(form: form, value: element))] }),
        LiteralExpressionNode { Value: string } or InterpolatedStringNode or ObjectExpressionNode or IdentifierExpressionNode { Name: "null" } => value,
        _ => Parsing.PuckParser.CreateOperand(expression: value, form: form),
    });

    /// <summary>Refuses every argument of <paramref name="call"/> that is not written in its one spelling.</summary>
    /// <param name="call">The call.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <remarks>Generic call lowering asks this itself; a vocabulary that lowers a call by hand asks it for that
    /// call.</remarks>
    public static void RefuseMisspelledArguments(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(argument: call);
        ArgumentNullException.ThrowIfNull(argument: scope);

        var arm = scope.Vocabulary.CallContext(callName: call.Name, context: MemberContext(scope: scope));

        for (var index = 0; (index < call.Arguments.Count); index++) {
            var argument = call.Arguments[index];

            if ((argument.Name ?? scope.Vocabulary.NameCallArgument(callName: call.Name, positionalIndex: index)) is { } name) {
                RefuseMisspelledMember(
                    form: scope.Vocabulary.ClassifyMember(context: arm, memberName: name),
                    holderName: call.Name,
                    memberName: name,
                    scope: scope,
                    value: argument.Value
                );
            }
        }
    }

    // A member has one spelling: a name, a key, an expression and a closed word are bare, and text is a string
    // literal. An interpolated string computes one name, one key or text at compile time; an expression is never
    // built as text, so it takes an interpolated string only as an atom inside its bare spelling.
    private static void RefuseMisspelledMember(string? holderName, string memberName, DocumentValueForm form, ExpressionNode value, DocumentScope scope) {
        if (form == DocumentValueForm.Unclassified) {
            return;
        }
        if (value is ArrayExpressionNode array) {
            foreach (var element in array.Elements) {
                RefuseMisspelledMember(form: form, holderName: holderName, memberName: memberName, scope: scope, value: element);
            }

            return;
        }

        var member = ((holderName is null) ? $"'{memberName}'" : $"'{memberName}' of '{holderName}'");

        if (form == DocumentValueForm.Text) {
            if (
                (value is IdentifierExpressionNode { Name: not ("null" or "true" or "false" or "auto") } word) &&
                !scope.TryEvaluateBinding(fieldKey: null, name: word.Name, value: out _)
            ) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.ArgumentWrittenQuoted,
                    message: $"{member} is text; write '{memberName}: \"{word.Name}\"'",
                    span: word.Span
                );
            }

            return;
        }

        var holds = (form switch {
            DocumentValueForm.Name => "a name",
            DocumentValueForm.Key => "a cell key",
            DocumentValueForm.Expression => "a value expression",
            _ => "one word of a closed vocabulary",
        });

        if ((form == DocumentValueForm.Expression) && (value is InterpolatedStringNode built)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.ExpressionBuiltAsText,
                message: $"{member} holds {holds} and is written bare, with an interpolated string only where it computes a name; write '{memberName}: {BareInterpolation(interpolated: built)}'",
                span: value.Span
            );

            return;
        }

        // The empty string names nothing, as `null` does, and no bare word spells it.
        if (value is LiteralExpressionNode { Value: string { Length: > 0 } written }) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.ArgumentWrittenBare,
                message: $"{member} holds {holds} and is written bare; write '{memberName}: {BareSpelling(form: form, written: written)}'",
                span: value.Span
            );
        } else if ((form == DocumentValueForm.Name) && (value is ObjectExpressionNode)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.ArgumentWrittenBare,
                message: $"{member} holds {holds} and is written bare; a pool field is written 'binding.field'",
                span: value.Span
            );
        }
    }

    /// <summary>Returns the bare spelling of what a document holds in an argument of the given form.</summary>
    /// <param name="form">The argument's form.</param>
    /// <param name="written">The document's spelling.</param>
    /// <returns>The text an author writes.</returns>
    public static string BareSpelling(DocumentValueForm form, string written) {
        ArgumentNullException.ThrowIfNull(argument: written);

        if (form == DocumentValueForm.Name) {
            // Punctuation alone does not make a name a channel or an expression: `cards(active)` is also a
            // legal literal row name. Only a parsed channel may keep its call syntax here.
            if (written.Contains(value: '`')) {
                return ("$" + Parsing.PuckStrings.Write(value: written.Replace(comparisonType: StringComparison.Ordinal, newValue: "{{", oldValue: "{").Replace(comparisonType: StringComparison.Ordinal, newValue: "}}", oldValue: "}")));
            }
            return ((Puck.State.ExpressionSpelling.IsBareName(name: written) || (Puck.State.StateChannelRef.Parse(spelling: written).Call is not null) || long.TryParse(result: out _, s: written))
                ? Puck.State.ExpressionSpelling.ToSourceDialect(text: written)
                : $"`{written}`"
            );
        }
        if (form != DocumentValueForm.Key) {
            return Puck.State.ExpressionSpelling.ToSourceDialect(text: written);
        }

        var into = new System.Text.StringBuilder();

        Puck.State.ExpressionSpelling.AppendSourceKey(into: into, key: written);

        return into.ToString();
    }

    private static JsonNode? EvaluateBinary(BinaryExpressionNode bin, DocumentScope scope, string? fieldKey) {
        var leftNode = EvaluateValue(
            expr: bin.Left,
            scope: scope,
            fieldKey: fieldKey
        );
        var rightNode = EvaluateValue(
            expr: bin.Right,
            scope: scope,
            fieldKey: fieldKey
        );

        if ((bin.Operator is "+" or "-") && (leftNode is JsonArray leftPoint) && (rightNode is JsonArray rightPoint) &&
            (leftPoint.Count is 2 or 3) && (leftPoint.Count == rightPoint.Count)) {
            scope.Budget.Collection(count: leftPoint.Count, span: bin.Span);
            var result = new JsonArray();

            for (var index = 0; (index < leftPoint.Count); index++) {
                result.AppendNode(EvaluateNumberBinary(bin, scope, leftPoint[index], rightPoint[index]));
            }
            return result;
        }
        return EvaluateNumberBinary(bin: bin, leftNode: leftNode, rightNode: rightNode, scope: scope);
    }
    private static JsonNode? EvaluateNumberBinary(BinaryExpressionNode bin, DocumentScope scope, JsonNode? leftNode, JsonNode? rightNode) {

        if (
            !TryReadNumber(
            node: leftNode,
            number: out var lNum
        ) ||
            !TryReadNumber(
            node: rightNode,
            number: out var rNum
        )
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: $"'{bin.Operator}' needs numbers known at compile time.",
                span: bin.Span
            );
            return null;
        }

        // A comparison is worth 1 or 0, never a JSON boolean — the rule language has no boolean either, so this is
        // what keeps `cleared + (row > 0)` meaning the same thing on both sides of the compiler. `IsTruthy` reads a
        // non-zero number as true, so a comparison still reads as a condition wherever one is wanted.
        var order = DocumentNumbers.Compare(
            left: leftNode,
            right: rightNode
        );
        var comparison = bin.Operator switch {
            "==" => (order == 0),
            "!=" => (order != 0),
            "<" => (order < 0),
            "<=" => (order <= 0),
            ">" => (order > 0),
            ">=" => (order >= 0),
            _ => ((bool?)null),
        };

        if (comparison is { } verdict) {
            return JsonValue.Create(value: (verdict
                ? 1L
                : 0L));
        }

        if (
            DocumentNumbers.TryInteger(
            node: leftNode,
            number: out var left
        ) &&
            DocumentNumbers.TryInteger(
            node: rightNode,
            number: out var right
        )
        ) {
            try {
                long? exact = bin.Operator switch {
                    "+" => checked((left + right)),
                    "-" => checked((left - right)),
                    "*" => checked((left * right)),
                    "%" => ((right is 0 or -1)
                    ? 0
                    : (left % right)),
                    "/" when (right == 0) => 0,
                    "/" when (right == -1) => checked(-left),
                    "/" when ((left % right) == 0) => (left / right),
                    _ => null,
                };

                if (exact is { } result) {
                    return JsonValue.Create(result);
                }
            } catch (OverflowException) {
                throw new DocumentEvaluationException(
                    "Integer arithmetic exceeds the signed 64-bit range.",
                    bin.Span,
                    PuckDiagnosticCodes.InvalidValue
                );
            }
        }

        // A sum, a difference and a product of exact operands stay decimal when a decimal holds the result exactly.
        // One it would overflow on or round, a product too small for its scale included, is computed in double
        // below, which keeps the magnitude a rounded decimal would lose.
        if (
            (bin.Operator is "+" or "-" or "*") &&
            DocumentNumbers.TryExact(
                node: leftNode,
                number: out var exactLeft
            ) &&
            DocumentNumbers.TryExact(
                node: rightNode,
                number: out var exactRight
            ) &&
            DocumentNumbers.TryExactArithmetic(
                left: exactLeft,
                operation: bin.Operator,
                result: out var exactResult,
                right: exactRight
            )
        ) {
            return DocumentNumbers.ExactNode(number: exactResult);
        }

        return NumberNode(
            value: bin.Operator switch {
                "+" => (lNum + rNum),
                "-" => (lNum - rNum),
                "*" => (lNum * rNum),
                "/" => ((rNum != 0)
                ? (lNum / rNum)
                : 0),
                // There is deliberately no `//` for integer division: `//` opens a line comment, so `a // b` can only
                // ever read as `a` followed by a comment. `floor(a / b)` is the spelling, and it is the rule language's
                // own `floor` — the same name, the same rounding — rather than a second one invented here.
                // A zero divisor yields zero rather than failing, matching division.
                "%" => ((rNum != 0)
                ? (lNum % rNum)
                : 0),
                _ => 0,
            },
            span: bin.Span
        );
    }
    private static JsonNode? EvaluateIndex(IndexExpressionNode indexed, DocumentScope scope, string? fieldKey) {
        var target = EvaluateValue(
            expr: indexed.Target,
            scope: scope,
            fieldKey: fieldKey
        );
        var index = EvaluateValue(
            expr: indexed.Index,
            scope: scope
        );

        if (
            (target is JsonObject obj) &&
            (index is JsonValue key) &&
            key.TryGetValue<string>(value: out var name)
        ) {
            obj.TryGetPropertyValue(
                jsonNode: out var member,
                propertyName: name
            );
            return member;
        }

        if (target is not JsonArray arr) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.IndexRefused,
                message: "only an array or an object can be indexed",
                span: indexed.Span
            );

            return null;
        }

        if (!DocumentNumbers.TryInteger(
            node: index,
            number: out var ordinal
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.IndexRefused,
                message: "an array index is a whole number known at compile time",
                span: indexed.Span
            );

            return null;
        }

        if (
            (ordinal < 0) ||
            (ordinal >= arr.Count)
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.IndexRefused,
                message: $"index {ordinal} is outside the array's 0..{(arr.Count - 1)}",
                span: indexed.Span
            );

            return null;
        }

        return arr[((int)ordinal)];
    }
    // The operand is lowered under the SAME fieldKey, so a unit inside it converts against the field the sign is
    // written for; the sign is then applied to the converted number.
    private static JsonNode? EvaluateUnary(UnaryExpressionNode un, DocumentScope scope, string? fieldKey) {
        var operand = EvaluateValue(
            un.Operand,
            scope,
            fieldKey
        );

        if (DocumentNumbers.TryInteger(
            node: operand,
            number: out var integer
        )) {
            if (
                (un.Operator == "-") &&
                (integer == long.MinValue)
            ) {
                throw new DocumentEvaluationException(
                    "Integer negation exceeds the signed 64-bit range.",
                    un.Span,
                    PuckDiagnosticCodes.InvalidValue
                );
            }
            return JsonValue.Create(((un.Operator == "-")
                ? -integer
                : integer));
        }
        if (DocumentNumbers.TryExact(
            node: operand,
            number: out var exact
        )) {
            return DocumentNumbers.ExactNode(number: ((un.Operator == "-")
                ? -exact
                : exact));
        }
        if (!TryReadNumber(
            node: operand,
            number: out var number
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: "A unary numeric operator needs a number.",
                span: un.Span
            );
            return null;
        }

        return NumberNode(
            value: ((un.Operator == "-")
            ? -number
            : number),
            span: un.Span
        );
    }
    // A hole's value as it reads inside a string. A whole number prints without a decimal point, so
    // `$"row-{floor(i / 4)}"` reads "row-1" rather than "row-1.0".
    private static string FormatHole(JsonNode? node) {
        if (node is not JsonValue value) {
            return (node?.ToJsonString() ?? string.Empty);
        }

        if (value.TryGetValue<string>(value: out var text)) {
            return text;
        }

        if (value.TryGetValue<bool>(value: out var flag)) {
            return (flag
                ? "true"
                : "false"
            );
        }

        if (value.TryGetValue<long>(value: out var whole)) {
            return whole.ToString(provider: CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<double>(value: out var real)) {
            return real.ToString(
                format: "R",
                provider: CultureInfo.InvariantCulture
            );
        }

        return value.ToJsonString();
    }
    private static JsonNode? LowerLiteral(LiteralExpressionNode lit, string? fieldKey, DocumentScope scope) {
        if (lit.Value is null) {
            return null;
        }

        if (lit.Value is bool b) {
            return JsonValue.Create(value: b);
        }

        if (lit.Value is string s) {
            return JsonValue.Create(value: s);
        }

        var numVal = lit.Value switch {
            long l => l,
            double d => d,
            int i => i,
            _ => Convert.ToDouble(
            value: lit.Value,
            provider: CultureInfo.InvariantCulture
        ),
        };
        // A literal that kept its text lowers as the decimal it spells, so no digit an author wrote is lost to a
        // binary approximation on the way to a decimal field.
        decimal? authored = ((lit.Value is decimal exactValue) ? exactValue : ((lit.Value is long integer)
            ? integer
            : (DecimalValues.TryAuthored(
                authored: out var spelled,
                literal: lit
            )
                ? spelled
                : null
            )
        ));

        if (lit.Unit is not null) {
            return LowerUnitLiteral(
                authored: authored,
                numVal: numVal,
                unit: lit.Unit,
                fieldKey: fieldKey,
                span: lit.Span,
                scope: scope
            );
        }

        if (authored is { } exact) {
            return DocumentNumbers.ExactNode(number: exact);
        }

        if (!double.IsFinite(d: numVal)) {
            throw new DocumentEvaluationException(
                "The literal is outside the finite numeric range.",
                lit.Span,
                PuckDiagnosticCodes.InvalidValue
            );
        }
        return JsonValue.Create(value: numVal);
    }
    // Every unit is checked against the vocabulary's field-dimension table, `%`/`pct` included: a field the table
    // does not cover is PUCK024, a unit the field's own dimension does not accept is PUCK025. No unit converts
    // outside the table, so a suffix can never silently change a value the table says nothing about.
    private static JsonNode LowerUnitLiteral(double numVal, decimal? authored, string unit, string? fieldKey, SourceSpan span, DocumentScope scope) {
        var dimension = ((fieldKey is null)
            ? UnitDimension.None
            : scope.Vocabulary.ClassifyField(fieldKey: fieldKey)
        );

        if (
            (authored is { } exact) &&
            UnitConversion.TryConvertExact(
                converted: out var scaled,
                dimension: dimension,
                unit: unit,
                value: exact
            )
        ) {
            return DocumentNumbers.ExactNode(number: scaled);
        }
        if (UnitConversion.TryConvert(
            converted: out var converted,
            dimension: dimension,
            numericValue: numVal,
            unit: unit
        )) {
            return NumberNode(
                span: span,
                value: converted
            );
        }

        if (dimension == UnitDimension.None) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnitOnUnknownField,
                message: $"'{(fieldKey ?? "this field")}' admits no unit — remove the '{unit}' suffix",
                span: span
            );
        } else {
            var accepted = string.Join(
                separator: "/",
                values: UnitConversion.AcceptedUnits(dimension: dimension)
            );

            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnitNotAdmitted,
                message: $"'{fieldKey}' accepts {accepted}, not '{unit}'",
                span: span
            );
        }

        return JsonValue.Create(value: numVal);
    }
    // A block whose NAME is a template parameter takes the bound value as its name, so one template can declare a
    // differently named row per invocation.
    private static StatementNode Rename(StatementNode statement, DocumentScope scope) {
        var written = (statement switch {
            BlockNode block => block.Name,
            RuleBlockNode rule => rule.Name,
            _ => null,
        });

        if (
            string.IsNullOrEmpty(value: written) ||
            !scope.TryLowerBinding(
            written,
            out var nameValue
        )
        ) {
            return statement;
        }

        var bound = (nameValue?.ToString() ?? written);

        return (statement switch {
            BlockNode block => (block with { Name = bound }),
            RuleBlockNode rule => (rule with { Name = bound }),
            _ => statement,
        });
    }

    /// <summary>Writes a lowered value into a section, extending rather than replacing when both the value and
    /// what is already there are arrays.</summary>
    /// <param name="target">The object the section belongs to.</param>
    /// <param name="key">The section name.</param>
    /// <param name="value">The lowered value.</param>
    /// <remarks>A section's array is built in statement order: a sugar block appends one row and a property
    /// carrying an array appends its rows, so the two compose instead of the later one erasing the earlier.
    /// Assigning over a statement-built array is how rows went missing with no diagnostic.</remarks>
    public static void AssignOrExtend(JsonObject target, string key, JsonNode? value) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(key);

        if (
            (target[key] is JsonArray existing) &&
            (value is JsonArray addition)
        ) {
            var items = addition.ToList();

            addition.Clear();

            foreach (var item in items) {
                existing.AppendNode(item: item);
            }

            return;
        }

        target[key] = value;
    }
    /// <summary>Recursively canonicalizes a JSON node by sorting every object's properties ordinally.</summary>
    /// <param name="node">The node to canonicalize.</param>
    /// <returns>A new canonicalized node, or <see langword="null"/> when the input was null.</returns>
    public static JsonNode? Canonicalize(JsonNode? node) {
        if (node is null) {
            return null;
        }

        if (node is JsonObject obj) {
            var sorted = new JsonObject();

            foreach (var entry in obj.OrderBy(
                keySelector: static pair => pair.Key,
                comparer: StringComparer.Ordinal
            ).ToList()) {
                sorted[entry.Key] = Canonicalize(node: entry.Value);
            }

            return sorted;
        }

        if (node is JsonArray arr) {
            var canonicalArr = new JsonArray();

            foreach (var item in arr) {
                canonicalArr.AppendNode(item: Canonicalize(node: item));
            }

            return canonicalArr;
        }

        return node.DeepClone();
    }
    /// <summary>Expands a <c>for</c> block, emitting its body once per element of the sequence.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="target">The object the body's statements land in.</param>
    /// <param name="scope">The enclosing scope.</param>
    /// <param name="sink">The vocabulary's own statement processor.</param>
    /// <remarks>Compile-time: the document carries the statements produced, never the loop. The bound names are
    /// locals, so they shadow a constant of the same name for the length of one iteration and are gone after.</remarks>
    public static void ExpandFor(ForStatementNode loop, JsonObject target, DocumentScope scope, Action<StatementNode, JsonObject, DocumentScope> sink) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);

        foreach (var (statement, iteration) in ExpandForStatements(
            loop: loop,
            scope: scope
        )) {
            sink(
                statement,
                target,
                iteration
            );
        }
    }
    /// <summary>Flattens a <c>for</c> into the statements it produces, each paired with the scope its bindings are
    /// live in, for a section whose rows a dedicated dispatcher reads.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="scope">The enclosing scope.</param>
    /// <returns>Each produced statement and the scope to lower it under.</returns>
    public static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandForStatements(ForStatementNode loop, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);
        using var expansion = scope.Budget.Enter(span: loop.Span);

        if (!ValidateForBody(
            loop: loop,
            scope: scope
        )) {
            yield break;
        }

        if (EvaluateValue(
            expr: loop.Sequence,
            scope: scope
        ) is not JsonArray sequence) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.ForSequenceRefused,
                message: "a 'for' walks an array known at compile time",
                span: loop.Sequence.Span
            );

            yield break;
        }

        for (var index = 0; (index < sequence.Count); ++index) {
            var locals = new Dictionary<string, JsonNode?>(
                dictionary: scope.Locals,
                comparer: StringComparer.Ordinal
            ) {
                [loop.Item] = sequence[index],
            };

            if (loop.Index is { } ordinal) {
                locals[ordinal] = JsonValue.Create(value: ((long)index));
            }

            var iteration = scope.WithLocals(lambdaLocals: locals);

            foreach (var statement in loop.Body) {
                scope.Budget.Spend(
                    count: 1,
                    span: statement.Span
                );
                yield return (statement, iteration);
            }
        }
    }
    /// <summary>Expands a template invocation, binding its arguments and feeding each statement of its body to
    /// <paramref name="sink"/> under a scope carrying those bindings as constants.</summary>
    /// <param name="call">The invocation as written.</param>
    /// <param name="target">The object the expanded statements land in.</param>
    /// <param name="scope">The invoking scope.</param>
    /// <param name="sink">The vocabulary's own statement processor.</param>
    /// <param name="annotations">An optional isolated annotation set for this expansion.</param>
    /// <param name="bindArgument">An optional transform applied after type validation and before an argument is
    /// bound. A vocabulary can use this to preserve reference provenance through later document rewrites.</param>
    /// <param name="initializeInvocation">An optional initializer run after declarations and arguments are bound,
    /// before the body is lowered.</param>
    public static void ExpandTemplate(
        CallExpressionNode call,
        JsonObject target,
        DocumentScope scope,
        Action<StatementNode, JsonObject, DocumentScope> sink,
        Dictionary<string, object?>? annotations = null,
        Func<TemplateParameterNode, ExpressionNode, DocumentScope, ExpressionNode>? bindArgument = null,
        Action<DocumentScope, IReadOnlyList<StatementNode>>? initializeInvocation = null
    ) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(sink);
        using var expansion = scope.Budget.Enter(span: call.Span);

        var templateName = call.Name;

        if (scope.TryLowerBinding(call.Name, out var indirectName) && (indirectName is JsonValue indirectValue) && indirectValue.TryGetValue<string>(value: out var boundName)) {
            templateName = boundName;
        }

        if (!scope.Templates.TryGetValue(
            key: templateName,
            value: out var template
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: $"Unknown module or template '{call.Name}'.",
                span: call.Span
            );
            return;
        }
        using var definitionOrigin = scope.SourceMap?.PushOrigin(sourcePath: template.DefinitionPath);

        var definitionScope = scope.TemplateDefinitionScope(name: templateName);

        if (template.DefinitionPath is not null) { definitionScope = definitionScope.WithBasePath(basePath: Path.GetDirectoryName(path: template.DefinitionPath)); }
        var localConstants = new Dictionary<string, ExpressionNode>(dictionary: definitionScope.Constants);
        var invocationScope = definitionScope.WithConstants(invocationConstants: localConstants, annotations: (annotations ?? scope.Annotations));
        var consumed = new HashSet<int>();

        for (var index = 0; (index < template.Parameters.Count); ++index) {
            var param = template.Parameters[index];
            ExpressionNode? boundValue = null;

            for (var argumentIndex = 0; (argumentIndex < call.Arguments.Count); ++argumentIndex) {
                var arg = call.Arguments[argumentIndex];

                if (string.Equals(
                    a: arg.Name,
                    b: param.Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    if (boundValue is not null) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.InvalidValue,
                            message: $"Argument '{param.Name}' is supplied more than once.",
                            span: arg.Span
                        );
                        return;
                    }
                    boundValue = arg.Value;
                    consumed.Add(item: argumentIndex);
                }
            }

            if (
                (boundValue is null) &&
                (index < call.Arguments.Count) &&
                (call.Arguments[index].Name is null)
            ) {
                boundValue = call.Arguments[index].Value;
                consumed.Add(item: index);
            }

            var isDefault = (boundValue is null);

            boundValue ??= param.DefaultValue;

            if (boundValue is not null) {
                if (!ValidateModuleArgument(parameter: param, scope: (isDefault ? invocationScope : scope), value: boundValue)) {
                    return;
                }
                invocationScope.BindArgument(
                    param.Name,
                    (bindArgument?.Invoke(param, boundValue, (isDefault ? invocationScope : scope)) ?? boundValue),
                    (isDefault
                    ? invocationScope
                    : scope)
                );
            } else {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.InvalidValue,
                    message: $"{(template.IsModule ? "Module" : "Template")} '{templateName}' requires argument '{param.Name}'.",
                    span: call.Span
                );
                return;
            }
        }

        if (consumed.Count != call.Arguments.Count) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: $"{(template.IsModule ? "Module" : "Template")} '{templateName}' received an unknown or duplicate argument.",
                span: call.Span
            );
            return;
        }

        invocationScope.IndexDeclarations(statements: template.Body.Statements);
        initializeInvocation?.Invoke(invocationScope, template.Body.Statements);

        foreach (var stmt in template.Body.Statements) {
            scope.Budget.Spend(
                count: 1,
                span: stmt.Span
            );
            var diagnosticStart = scope.Diagnostics.Count;

            try {
                sink(
                    Rename(scope: invocationScope, statement: stmt),
                    target,
                    invocationScope
                );
            } catch (DocumentEvaluationException error) when ((template.DefinitionPath is not null)) {
                throw new DocumentEvaluationException($"{template.DefinitionPath}: {error.Message}", error.Span, error.Code);
            } finally {
                scope.Diagnostics.AttributeFrom(diagnosticStart, template.DefinitionPath);
            }
        }
    }

    private static bool ValidateModuleArgument(TemplateParameterNode parameter, ExpressionNode value, DocumentScope scope) {
        if (parameter.Kind is null) {
            return true;
        }
        var lowered = EvaluateValue(value, scope, parameter.Kind switch { "Point" => "position", "Angle" => "yawDegrees", _ => null });
        var valid = parameter.Kind switch {
            "Point" => ((lowered is JsonArray point) && (point.Count is 2 or 3) && point.All(predicate: IsNumber)),
            "Angle" => ((lowered is JsonValue angle) && (angle.TryGetValue<long>(value: out _) || angle.TryGetValue<double>(value: out _) || angle.TryGetValue<decimal>(value: out _))),
            "Asset" => ((lowered is JsonValue asset) && asset.TryGetValue<string>(value: out var assetPath) && (assetPath.Length > 0)),
            "Pool" => IsDeclaredReference(catalogName: "DeclaredPools", lowered: lowered, scope: scope),
            "Row" => IsDeclaredReference(catalogName: "DeclaredRows", lowered: lowered, scope: scope),
            "Gate" => (((lowered is JsonValue gate) && gate.TryGetValue<bool>(value: out _)) || IsDeclaredReference(catalogName: "DeclaredGates", lowered: lowered, scope: scope)),
            "Module" => ValidateModuleSurface(lowered: lowered, parameter: parameter, scope: scope),
            _ => false,
        };

        if (!valid) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.InvalidValue,
                message: $"Argument '{parameter.Name}' must be a {parameter.Kind}{((parameter.RequiredExports.Count == 0) ? "." : $" exporting {string.Join(separator: ", ", values: parameter.RequiredExports)}.")}",
                span: value.Span
            );
        }
        return valid;

        static bool IsNumber(JsonNode? node) => ((node is JsonValue number) &&
            (number.TryGetValue<long>(value: out _) || number.TryGetValue<double>(value: out _) || number.TryGetValue<decimal>(value: out _)));
    }
    private static bool IsDeclaredReference(JsonNode? lowered, DocumentScope scope, string catalogName) =>
        ((lowered is JsonValue value) && value.TryGetValue<string>(value: out var name) &&
        scope.Annotations.TryGetValue(key: catalogName, value: out var catalog) && ((HashSet<string>)catalog!).Contains(item: name));
    private static bool ValidateModuleSurface(TemplateParameterNode parameter, JsonNode? lowered, DocumentScope scope) {
        if ((lowered is not JsonValue value) || !value.TryGetValue<string>(value: out var name) || !scope.Templates.TryGetValue(key: name, value: out var module) || !module.IsModule) {
            return false;
        }
        var memo = new Dictionary<(string Module, string Member), bool>();
        var visiting = new HashSet<(string Module, string Member)>();

        return parameter.RequiredExports.All(predicate: required => Exports(candidate: module, moduleName: name, required: required));

        bool Exports(string moduleName, TemplateNode candidate, string required) {
            var key = (moduleName, required);

            if (memo.TryGetValue(key: key, value: out var cached)) { return cached; }
            if (!visiting.Add(item: key)) { return false; }
            using var traversal = scope.Budget.Enter(span: candidate.Span);

            scope.Budget.Spend(count: 1, span: candidate.Span);

            var declared = new HashSet<string>(comparer: StringComparer.Ordinal);

            CollectDeclared(candidate.Body.Statements, declared);
            var uses = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var use in candidate.Body.Statements.OfType<ExpressionStatementNode>()) {
                if (use is { IsUse: true, UseAlias: { } alias, Expression: CallExpressionNode call }) { uses.TryAdd(key: alias, value: call.Name); }
            }
            var result = false;

            foreach (var exportName in candidate.Body.Statements.OfType<ExportNode>().SelectMany(selector: static export => export.Names)) {
                var dot = exportName.IndexOf(value: '.');

                if (dot < 0) {
                    if ((exportName == required) && declared.Contains(item: exportName)) { result = true; break; }
                    continue;
                }
                var alias = exportName[..dot];
                var member = exportName[(dot + 1)..];

                if ((member != required) || !uses.TryGetValue(key: alias, value: out var usedName)) { continue; }

                var promised = candidate.Parameters.FirstOrDefault(predicate: param => ((param.Kind == "Module") && (param.Name == usedName)));

                if (promised?.RequiredExports.Contains(member, StringComparer.Ordinal) == true) { result = true; break; }
                if (scope.Templates.TryGetValue(key: usedName, value: out var usedModule) && usedModule.IsModule && Exports(candidate: usedModule, moduleName: usedName, required: member)) {
                    result = true;
                    break;
                }
            }
            visiting.Remove(item: key);
            memo[key] = result;
            return result;
        }

        static void CollectDeclared(IReadOnlyList<StatementNode> statements, HashSet<string> names) {
            foreach (var statement in statements) {
                switch (statement) {
                    case BlockNode { Name: { } name, Identifier: not ("state" or "world") } block: names.Add(item: name); CollectDeclared(block.Statements, names); break;
                    case BlockNode block: CollectDeclared(block.Statements, names); break;
                    case RuleBlockNode rule: names.Add(item: rule.Name); break;
                    case StateTableDeclarationNode row: names.Add(item: row.Name); break;
                    case StateSlotDeclarationNode row: names.Add(item: row.Name); break;
                    case StatePileDeclarationNode row: names.Add(item: row.Name); break;
                    case StateGridDeclarationNode row: names.Add(item: row.Name); break;
                    case StatePoolDeclarationNode pool: names.Add(item: pool.Name); break;
                    case StatePairPoolDeclarationNode pool: names.Add(item: pool.Name); break;
                }
            }
        }
    }

    /// <summary>Flattens a template invocation into the statements it produces, each paired with the scope its
    /// argument bindings are live in, for a section whose rows a dedicated dispatcher reads.</summary>
    /// <param name="call">The invocation as written.</param>
    /// <param name="scope">The invoking scope.</param>
    /// <returns>Each produced statement and the scope to lower it under; nothing when no template bears the name.</returns>
    public static IEnumerable<(StatementNode Statement, DocumentScope Scope)> ExpandTemplateStatements(CallExpressionNode call, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        var captured = new List<(StatementNode, DocumentScope)>();

        ExpandTemplate(
            call: call,
            target: new JsonObject(),
            scope: scope,
            sink: (statement, _, invocationScope) => captured.Add(item: (statement, invocationScope))
        );

        return captured;
    }
    /// <summary>Reads a node as a truth value: the condition `select` branches on and `filter` keeps by. A boolean is
    /// itself; a number is true when non-zero, which is how a comparison's 1/0 reads; a string is true when non-empty;
    /// and a present container is true.</summary>
    /// <param name="node">The lowered node.</param>
    /// <returns><see langword="true"/> when the node reads as true.</returns>
    public static bool IsTruthy(JsonNode? node) {
        if (node is not JsonValue value) {
            return (node is not null);
        }

        if (value.TryGetValue<bool>(value: out var flag)) {
            return flag;
        }

        if (TryReadNumber(
            node: node,
            number: out var number
        )) {
            return (number != 0);
        }

        return (
            value.TryGetValue<string>(value: out var text) &&
            (text.Length > 0)
        );
    }
    /// <summary>Reads a lowered value as an object key: a string as itself, a number by its shortest round-trip
    /// spelling, and anything else as nothing.</summary>
    /// <param name="node">The lowered node.</param>
    /// <returns>The key text, or <see langword="null"/> when the value cannot key an object.</returns>
    public static string? KeyText(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        if (value.TryGetValue<string>(value: out var text)) {
            return text;
        }

        if (DocumentNumbers.TryInteger(
            node: node,
            number: out var integer
        )) {
            return integer.ToString(provider: CultureInfo.InvariantCulture);
        }
        return (TryReadNumber(
            node: node,
            number: out var number
        )
            ? ((number == Math.Truncate(d: number))
                ? ((long)number).ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                : number.ToString(provider: System.Globalization.CultureInfo.InvariantCulture))
            : null
        );
    }
    /// <summary>Lowers one expression to JSON.</summary>
    /// <param name="expr">The expression to lower.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key this expression fills — a property name, or a call argument's
    /// resolved <c>call.argument</c> key — threaded down so a unit-suffixed literal reachable from it (directly, or
    /// through an array, object, range, arithmetic or <c>let</c> indirection) validates against the field it
    /// actually lands on. <see langword="null"/> when no such key applies.</param>
    /// <returns>The lowered node, or <see langword="null"/> for a null literal or an unrecognized expression.</returns>
    public static JsonNode? LowerValue(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) =>
        scope.Budget.Copy(
            EvaluateValue(
                expr: expr,
                fieldKey: fieldKey,
                scope: scope
            ),
            expr.Span
        );
    /// <summary>Narrows an integral result back to a long, so arithmetic and a written literal reach the document as
    /// the same JSON kind.</summary>
    /// <param name="value">The folded number.</param>
    /// <param name="span">The expression responsible for the result.</param>
    /// <returns>A long-valued node when the value is integral, a double-valued one otherwise.</returns>
    public static JsonNode NumberNode(double value, SourceSpan span = default) {
        if (!double.IsFinite(d: value)) {
            throw new DocumentEvaluationException(
                code: PuckDiagnosticCodes.InvalidValue,
                message: "The calculation is outside the finite numeric range.",
                span: span
            );
        }
        if (
            (value >= long.MinValue) &&
            (value < 9223372036854775808d) &&
            (Math.Truncate(d: value) == value)
        ) {
            return JsonValue.Create(value: ((long)value));
        }

        return JsonValue.Create(value: value);
    }
    /// <summary>Returns a block's name, resolving an interpolated header.</summary>
    /// <param name="block">The block.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The resolved name, or <see langword="null"/> when the block carries none.</returns>
    public static string? ResolveBlockName(BlockNode block, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(scope);

        return ResolveHeaderName(
            nameExpression: block.NameExpression,
            scope: scope,
            written: block.Name
        );
    }
    /// <summary>Returns a rule's name, resolving an interpolated header the way a block's is.</summary>
    /// <param name="rule">The rule.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <returns>The resolved name, or the written one when the header carries no interpolation.</returns>
    public static string? ResolveRuleName(RuleBlockNode rule, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(scope);

        return ResolveHeaderName(
            nameExpression: rule.NameExpression,
            scope: scope,
            written: rule.Name
        );
    }

    private static string? ResolveHeaderName(ExpressionNode? nameExpression, string? written, DocumentScope scope) {
        if (nameExpression is null) {
            return written;
        }

        return (((LowerValue(
            expr: nameExpression,
            scope: scope
        ) is JsonValue value) && value.TryGetValue<string>(value: out var text))
            ? text
            : written
        );
    }

    /// <summary>Reads a lowered number whatever JSON numeric kind it landed as.</summary>
    /// <param name="node">The node to read.</param>
    /// <param name="number">The value read, or zero.</param>
    /// <returns><see langword="true"/> when the node held a number.</returns>
    /// <remarks>A lowered number reaches a caller as a long whenever it came out integral, so a reader that asks
    /// only for a double refuses every whole number.</remarks>
    public static bool TryReadNumber(JsonNode? node, out double number) {
        number = 0;

        if (node is not JsonValue value) {
            return false;
        }

        if (value.TryGetValue<double>(value: out var asDouble)) {
            number = asDouble;

            return true;
        }

        if (value.TryGetValue<long>(value: out var asLong)) {
            number = asLong;

            return true;
        }

        if (value.TryGetValue<int>(value: out var asInt)) {
            number = asInt;
            return true;
        }
        if (value.TryGetValue<decimal>(value: out var exact)) {
            number = ((double)exact);
            return true;
        }

        return false;
    }
    /// <summary>Refuses a <c>for</c> body that assigns a field instead of emitting a row.</summary>
    /// <param name="loop">The loop as written.</param>
    /// <param name="scope">The scope refusals are reported into.</param>
    /// <returns><see langword="true"/> when every body statement is admissible.</returns>
    /// <remarks>
    /// <para>A <c>for</c> EMITS ROWS. Building a value out of a sequence is <c>map</c>'s job, in value position, and
    /// the two are not interchangeable: a row goes through its section's own sugar, defaults and refusals, while a
    /// value is the JSON it lowers to and nothing else.</para>
    /// <para>Assigning a field from a loop body had no honest reading. A scalar (<c>scalars: p</c>) kept the last
    /// iteration and silently dropped every earlier one. An array (<c>curve [p, 0]</c>) went through
    /// <see cref="AssignOrExtend"/>, whose extend exists so <c>shapes:</c> can merge with the array the row sugar
    /// already built — so each iteration's elements were spliced into the field rather than appended to it,
    /// flattening <c>[[1,0],[2,0]]</c> to <c>[1,0,2,0]</c> and corrupting any array the document had authored above
    /// it. Both were silent. Refusing is what keeps one syntax from meaning append-element here and merge-lists
    /// there, which is a distinction no reader could recover from the source.</para>
    /// </remarks>
    public static bool ValidateForBody(ForStatementNode loop, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scope);

        var admissible = true;

        foreach (var statement in loop.Body) {
            if (statement is not PropertyNode property) {
                continue;
            }

            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.ForAssignsAField,
                message: ((string)$"a 'for' emits rows, and '{property.Name}' is a field assignment — every iteration would write the same field. Build the value with map(...) in value position instead."),
                span: property.Span
            );

            admissible = false;
        }

        return admissible;
    }
}
