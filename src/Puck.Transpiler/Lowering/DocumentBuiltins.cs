using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

/// <summary>The builtins, evaluated while lowering rather than at run time: a document carries the array they
/// produced, not the call that produced it.</summary>
/// <remarks><para>Evaluated in compile-time value positions. A call constructing a declared document arm belongs
/// to its vocabulary even when a builtin shares the name (such as a world's <c>sort</c> transform).
/// Statement-position calls likewise belong to their vocabulary (a cartridge's <c>map</c> step is untouched).</para>
/// <para>Two families meet here. The COLLECTION builtins below are the document language's own — a rule has no
/// arrays to fold, so they exist in one language only. The SCALAR ones are not restated here at all: they come from
/// <see cref="ExpressionVocabulary"/>, the table the rule language is built from, so the two cannot drift over a
/// spelling. <see cref="DocumentScalars"/> folds them.</para></remarks>
public static partial class DocumentBuiltins {
    /// <summary>The collection builtins — the document language's own, with no rule-language counterpart — by name:
    /// each one's spelling and meaning, as the language server shows them, beside the evaluation a call runs.</summary>
    public static IReadOnlyDictionary<string, DocumentCollectionBuiltin> Collections { get; } = new Dictionary<string, DocumentCollectionBuiltin>(comparer: StringComparer.Ordinal) {
        ["range"] = new(
            description: "Produces the integers from start up to, but not including, end: range(1, 4) is [1, 2, 3]. End is never below start.",
            evaluate: static (call, scope, _) => Range(args: call.Arguments, call: call, scope: scope),
            signature: "range(start, end)"
        ),
        ["length"] = new(
            description: "Returns the number of array elements, object properties, or UTF-16 string code units.",
            evaluate: static (call, scope, fieldKey) => Length(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "length(value)"
        ),
        ["scalars"] = new(
            description: "Returns a string's Unicode scalar values in order, one whole number per character.",
            evaluate: static (call, scope, fieldKey) => Scalars(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "scalars(text)"
        ),
        ["concat"] = new(
            description: "Concatenates arrays and scalar values into one array.",
            evaluate: static (call, scope, fieldKey) => Concat(args: call.Arguments, fieldKey: fieldKey, scope: scope),
            signature: "concat(values...)"
        ),
        ["map"] = new(
            description: "Transforms each element. The index parameter is optional.",
            evaluate: static (call, scope, fieldKey) => Map(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "map(array, (item, index) => value)"
        ),
        ["flatMap"] = new(
            description: "Transforms each element and splices an array result into the output, so a flatten is one call. The index parameter is optional.",
            evaluate: static (call, scope, fieldKey) => FlatMap(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "flatMap(array, (item, index) => value)"
        ),
        ["filter"] = new(
            description: "Keeps elements whose condition is truthy. The index parameter is optional.",
            evaluate: static (call, scope, fieldKey) => Filter(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "filter(array, (item, index) => condition)"
        ),
        ["reduce"] = new(
            description: "Folds elements into an accumulated value starting with initial.",
            borrows: true,
            evaluate: static (call, scope, fieldKey) => Reduce(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "reduce(array, initial, (accumulator, item) => value)"
        ),
        ["distinct"] = new(
            description: "Removes duplicate values, preserving the first occurrence.",
            evaluate: static (call, scope, fieldKey) => Distinct(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "distinct(array)"
        ),
        ["sort"] = new(
            description: "Stably sorts an array. The key lambda is optional.",
            evaluate: static (call, scope, fieldKey) => Sort(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "sort(array, item => key)"
        ),
        ["groupBy"] = new(
            description: "Groups elements into an object of arrays by key. The index parameter is optional.",
            evaluate: static (call, scope, fieldKey) => GroupBy(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            signature: "groupBy(array, (item, index) => key)"
        ),
        ["any"] = new(
            description: "Returns 1 when some element's condition is truthy, else 0; with no lambda, when some element is truthy, stopping at the first. A lambda makes it a list fold (ListFolds).",
            evaluate: static (call, scope, fieldKey) => Any(call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "any(array, (item, index) => condition)"
        ),
        ["all"] = new(
            description: "Returns 1 when every element's condition is truthy, else 0 (1 for an empty array); with no lambda, when every element is truthy, stopping at the first miss. A lambda makes it a list fold (ListFolds).",
            evaluate: static (call, scope, fieldKey) => Any(call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "all(array, (item, index) => condition)"
        ),
        ["count"] = new(
            description: "Returns how many elements' conditions are truthy; with no lambda, how many elements are. A lambda makes it a list fold (ListFolds).",
            evaluate: static (call, scope, fieldKey) => Any(call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "count(array, (item, index) => condition)"
        ),
        ["sum"] = new(
            description: "Adds the elements, or the lambda's value for each element, as + does: numbers add exactly and numeric arrays add element by element. An empty array sums to 0. A lambda makes it a list fold (ListFolds).",
            evaluate: static (call, scope, fieldKey) => Sum(call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "sum(array, (item, index) => value)"
        ),
        ["scan"] = new(
            description: "Returns the running totals in one pass: one more value than the array, starting at 0 and ending at the total, so scan([3, 4, 5]) is [0, 3, 7, 12] and element i is the sum of the first i elements. The lambda is optional, as sum's.",
            evaluate: static (call, scope, fieldKey) => Scan(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "scan(array, (item, index) => value)"
        ),
        ["dot"] = new(
            description: "Returns the dot product of two arrays of numbers of one length.",
            evaluate: static (call, scope, fieldKey) => Dot(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "dot(a, b)"
        ),
        ["contains"] = new(
            description: "Returns 1 when an element equals the value, as == compares, else 0.",
            evaluate: static (call, scope, fieldKey) => Contains(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "contains(array, value)"
        ),
        ["indexOf"] = new(
            description: "Returns the position of the first element equal to the value, or of the first whose lambda condition is truthy; -1 when none is.",
            evaluate: static (call, scope, fieldKey) => Find(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "indexOf(array, value) or indexOf(array, (item, index) => condition)"
        ),
        ["find"] = new(
            description: "Returns the first element whose condition is truthy, or null when none is.",
            evaluate: static (call, scope, fieldKey) => Find(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "find(array, (item, index) => condition)"
        ),
        ["zip"] = new(
            description: "Pairs the arrays' elements by position: zip([1, 2], [3, 4]) is [[1, 3], [2, 4]]. The arrays share one length.",
            evaluate: static (call, scope, fieldKey) => Zip(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "zip(a, b, ...)"
        ),
        ["reverse"] = new(
            description: "Returns the array's elements in the opposite order.",
            evaluate: static (call, scope, fieldKey) => Reverse(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "reverse(array)"
        ),
        ["slice"] = new(
            description: "Returns the elements from start up to, but not including, end (the array's end when omitted). A negative bound counts from the end, and a bound beyond either end stops there.",
            evaluate: static (call, scope, fieldKey) => Slice(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "slice(array, start, end)"
        ),
        ["join"] = new(
            description: "Joins the strings and numbers of an array into one string, the separator (empty when omitted) between them.",
            evaluate: static (call, scope, fieldKey) => Join(args: call.Arguments, call: call, fieldKey: fieldKey, scope: scope),
            positionalOnly: true,
            signature: "join(array, separator)"
        ),
        ["fraction"] = new(
            description: "Returns the part of a number above its floor, x - floor(x), in [0, 1).",
            evaluate: static (call, scope, fieldKey) => DocumentVectors.Fraction(call: call, fieldKey: fieldKey, scope: scope),
            signature: "fraction(x)"
        ),
        ["overlaps"] = new(
            description: "Returns 1 when two spans [low, high] share a point, so spans that only touch at an end do not overlap, else 0.",
            evaluate: static (call, scope, _) => DocumentVectors.Overlaps(call: call, scope: scope),
            signature: "overlaps(a, b)"
        ),
        ["within"] = new(
            description: "Returns 1 when x lies in the span [low, high], ends included, else 0.",
            evaluate: static (call, scope, _) => DocumentVectors.Within(call: call, scope: scope),
            signature: "within(x, span)"
        ),
        ["rotate"] = new(
            description: "Returns the quaternion [x, y, z, w] turning degrees about axis, right-handed.",
            evaluate: static (call, scope, _) => DocumentVectors.Rotate(call: call, scope: scope),
            signature: "rotate(axis, degrees)"
        ),
        ["compose"] = new(
            description: "Multiplies rotations: the result turns a point by the last rotation first and the first rotation last.",
            evaluate: static (call, scope, _) => DocumentVectors.Compose(call: call, scope: scope),
            signature: "compose(outer, inner, ...)"
        ),
        ["rotatePoint"] = new(
            description: "Turns a point [x, y, z] about the origin by a rotation [x, y, z, w].",
            evaluate: static (call, scope, _) => DocumentVectors.RotatePoint(call: call, scope: scope),
            signature: "rotatePoint(rotation, point)"
        ),
        ["polarPoint"] = new(
            description: "Returns the point radius out from the vertical axis at degrees around it, from +X toward +Z, at height y (0 when omitted).",
            evaluate: static (call, scope, _) => DocumentVectors.PolarPoint(call: call, scope: scope),
            signature: "polarPoint(radius, degrees, y)"
        ),
    };
    /// <summary>The names of the builtins that take a lambda argument, in declaration order: the collection builtins
    /// whose signature spells one.</summary>
    public static IReadOnlyList<string> LambdaTakers { get; } = [.. Collections.Where(predicate: static entry => entry.Value.Signature.Contains(value: "=>", comparisonType: StringComparison.Ordinal)).Select(selector: static entry => entry.Key)];
    /// <summary>The named numeric constants every value position reads at compile time, by name: <c>pi</c> is the
    /// ratio of a circle's circumference to its diameter, in double precision. A <c>let</c>, a parameter or a loop
    /// variable of the same name hides one.</summary>
    public static IReadOnlyDictionary<string, double> NamedConstants { get; } = new Dictionary<string, double>(comparer: StringComparer.Ordinal) {
        ["pi"] = Math.PI,
    };
    /// <summary>The abbreviations of the document language's own builtins, beside the shared ones of
    /// <see cref="ExpressionVocabulary.Aliases"/>: each is read as its canonical name before the call is evaluated.</summary>
    private static readonly Dictionary<string, string> OwnAliases = new(comparer: StringComparer.Ordinal) {
        ["frac"] = "fraction",
        ["fract"] = "fraction",
    };
    /// <summary>Every name reserved in value position: the document language's own builtins, every named function of
    /// the shared expression vocabulary, and the abbreviations either admits.</summary>
    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>(
        collection: Collections.Keys.Concat(second: ExpressionVocabulary.Functions.Keys).Concat(second: ExpressionVocabulary.Aliases.Keys).Concat(second: OwnAliases.Keys),
        comparer: StringComparer.Ordinal
    );

    /// <summary>Returns the canonical spelling of a value-position function name: the builtin an abbreviation stands
    /// for (<c>sqrt</c> is <c>squareRoot</c>, <c>fract</c> is <c>fraction</c>), or the name itself.</summary>
    /// <param name="name">The name as written.</param>
    /// <returns>The canonical spelling.</returns>
    public static string Canonical(string name) => (OwnAliases.TryGetValue(key: name, value: out var own) ? own : ExpressionVocabulary.Canonical(name: name));

    /// <summary>Returns the one spelling a refusal may name for a call written as an abbreviation of it: the first
    /// value-position name, in ordinal order, that begins with <paramref name="written"/>. It is a hint in a refusal's
    /// message, never an alias the lowering accepts.</summary>
    /// <param name="written">The call's name as written.</param>
    /// <returns>The name, or <see langword="null"/> when none begins with <paramref name="written"/>.</returns>
    public static string? SpellingFor(string written) {
        ArgumentException.ThrowIfNullOrEmpty(argument: written);

        return Names
            .Where(predicate: name => (
                (name.Length > written.Length) &&
                name.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: written
                )
            ))
            .Order(comparer: StringComparer.Ordinal)
            .FirstOrDefault();
    }

    // The lambda's parameters are bound as locals rather than as constants: a bound value is an already-lowered
    // JSON node, not an expression that could be lowered again. The result is borrowed: read it, or take it into a
    // container through ApplyOwned's ownership.
    private static JsonNode? Apply(LambdaExpressionNode lambda, DocumentScope scope, string? fieldKey, JsonNode? first, JsonNode? second) => ApplyOwned(
        fieldKey: fieldKey,
        first: first,
        lambda: lambda,
        owned: out _,
        scope: scope,
        second: second
    );
    private static JsonNode? ApplyOwned(LambdaExpressionNode lambda, DocumentScope scope, string? fieldKey, JsonNode? first, JsonNode? second, out bool owned) {
        if (
            (lambda.Parameters.Count > 1) &&
            (lambda.Parameters[0] == lambda.Parameters[1])
        ) {
            throw new DocumentEvaluationException(
                "A lambda cannot repeat a parameter name.",
                lambda.Span,
                PuckDiagnosticCodes.InvalidValue
            );
        }
        return DocumentLowering.Evaluate(
            expr: lambda.Body,
            fieldKey: fieldKey,
            owned: out owned,
            scope: ((lambda.Parameters.Count > 1)
                ? scope.WithLocals(first: lambda.Parameters[0], firstValue: first, second: lambda.Parameters[1], secondValue: second)
                : scope.WithLocal(name: lambda.Parameters[0], value: first))
        );
    }
    // Appends `lowered` to `arr`: an array splices its items in, anything else is one item. The one rule concat and
    // flatMap share, so the two spellings of a flatten cannot drift.
    private static void AppendSpliced(JsonArray arr, JsonNode? lowered, bool owned, DocumentScope scope, SourceSpan span) {
        scope.Budget.Collection(
            count: (arr.Count + ((lowered is JsonArray list)
            ? list.Count
            : 1)),
            span: span
        );

        if (lowered is JsonArray nested) {
            var items = new Lent(list: nested, owned: owned);

            for (var index = 0; (index < items.Count); index++) {
                arr.AppendNode(item: items.Take(budget: scope.Budget, index: index, span: span));
            }

            return;
        }

        arr.AppendNode(item: scope.Budget.Take(
            owned: owned,
            span: span,
            value: lowered
        ));
    }
    private static JsonNode Concat(IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        var arr = new JsonArray();

        foreach (var arg in args) {
            var lowered = DocumentLowering.Evaluate(
                expr: arg.Value,
                fieldKey: fieldKey,
                owned: out var owned,
                scope: scope
            );

            AppendSpliced(
                arr: arr,
                lowered: lowered,
                owned: owned,
                scope: scope,
                span: arg.Span
            );
        }

        return arr;
    }
    /// <summary>The elements of a list a builtin reads, and how the builtin's result takes them: from a list the
    /// evaluation alone holds, an element moves, the list letting go of every element first; from a borrowed list,
    /// the result takes a copy. Either way the budget charges the element as it enters the result.</summary>
    /// <remarks>An owned list owns every node beneath it (<see cref="DocumentLowering.Evaluate"/>), so an element
    /// moved out of one is held by nothing else. Each element is taken at most once.</remarks>
    private readonly struct Lent {
        private readonly JsonArray? m_borrowed;
        private readonly JsonNode?[]? m_released;

        /// <summary>Initializes a new instance of the <see cref="Lent"/> struct over a list.</summary>
        /// <param name="list">The list, which an owned list leaves empty.</param>
        /// <param name="owned">Whether the evaluation alone holds <paramref name="list"/>.</param>
        public Lent(JsonArray list, bool owned) {
            if (owned) {
                m_released = new JsonNode?[list.Count];
                ((ICollection<JsonNode?>)list).CopyTo(array: m_released, arrayIndex: 0);
                // Clearing detaches every element from the list, so each can take a new parent.
                list.Clear();
            } else {
                m_borrowed = list;
            }
        }

        /// <summary>Gets the number of elements.</summary>
        public int Count => (m_released?.Length ?? m_borrowed!.Count);

        /// <summary>Gets an element to read.</summary>
        /// <param name="index">The element's position.</param>
        public JsonNode? this[int index] => ((m_released is { } released) ? released[index] : m_borrowed![index]);

        /// <summary>Takes an element into a container: the element itself from an owned list, else a copy.</summary>
        /// <param name="budget">The compilation's budget, which charges the element.</param>
        /// <param name="index">The element's position.</param>
        /// <param name="span">The call taking it.</param>
        /// <returns>The node to place.</returns>
        public JsonNode? Take(DocumentEvaluationBudget budget, int index, SourceSpan span) => budget.Take(
            owned: (m_released is not null),
            span: span,
            value: this[index]
        );
    }
    // The list a builtin reads as its first argument, lent: its elements move from a list the evaluation alone holds.
    private static bool TryLend(ExpressionNode expr, DocumentScope scope, string? fieldKey, out Lent list) {
        if (DocumentLowering.Evaluate(expr: expr, fieldKey: fieldKey, owned: out var owned, scope: scope) is JsonArray read) {
            list = new Lent(list: read, owned: owned);
            return true;
        }
        list = default;
        return false;
    }
    // `flatMap(array, lambda)` is `reduce(array, [], (all, item) => concat(all, lambda(item)))` without recopying the
    // running array each step: an array the lambda yields is spliced in, anything else is one item.
    private static JsonNode? FlatMap(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadOperand(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source,
            owned: out var sourceOwned
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "flatMap's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            var lowered = ApplyOwned(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: source[index],
                second: JsonValue.Create(value: ((long)index)),
                owned: out var owned
            );

            AppendSpliced(
                arr: arr,
                lowered: lowered,
                owned: owned,
                scope: scope,
                span: call.Span
            );
        }

        return arr;
    }
    // `distinct(array)` keeps the first of each equal run by VALUE, not by reference: two separately-written
    // `[0, 1]` literals are one element, which is what an author generating coordinates means by it.
    private static JsonNode? Distinct(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(
                call: call,
                message: "distinct(array) takes one argument",
                scope: scope
            );
        }

        if (!TryLend(expr: args[0].Value, fieldKey: fieldKey, list: out var source, scope: scope)) {
            return Refuse(
                call: call,
                message: "distinct(array) reads an array",
                scope: scope
            );
        }

        var arr = new JsonArray();

        scope.Budget.Collection(
            count: source.Count,
            span: call.Span
        );
        var seen = new HashSet<JsonNode?>(comparer: DocumentValueEqualityComparer.Instance);

        for (var index = 0; (index < source.Count); ++index) {
            if (!seen.Add(item: source[index])) {
                continue;
            }

            arr.AppendNode(item: source.Take(budget: scope.Budget, index: index, span: call.Span));
        }

        return arr;
    }
    private static JsonNode? Filter(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadOperand(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source,
            owned: out var sourceOwned
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "filter's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var arr = new JsonArray();
        var items = new Lent(list: source!, owned: sourceOwned);

        for (var index = 0; (index < items.Count); ++index) {
            var kept = Apply(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: items[index],
                second: JsonValue.Create(value: ((long)index))
            );

            if (DocumentLowering.IsTruthy(node: kept)) {
                arr.AppendNode(item: items.Take(budget: scope.Budget, index: index, span: call.Span));
            }
        }

        return arr;
    }
    // `groupBy(array, lambda)` yields an object keyed by the lambda's result, each value the array of items that
    // produced it. Keys keep first-seen order, so the object reads the way the source does.
    private static JsonNode? GroupBy(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadOperand(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source,
            owned: out var sourceOwned
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "groupBy's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var grouped = new JsonObject();
        var items = new Lent(list: source!, owned: sourceOwned);

        for (var index = 0; (index < items.Count); ++index) {
            var key = Apply(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: items[index],
                second: JsonValue.Create(value: ((long)index))
            );
            var name = DocumentLowering.KeyText(node: key);

            if (name is null) {
                return Refuse(
                    call: call,
                    message: "groupBy's lambda yields a string or a number to key by",
                    scope: scope
                );
            }

            if (grouped[propertyName: name] is not JsonArray bucket) {
                bucket = [];
                grouped[propertyName: name] = bucket;
            }

            bucket.AppendNode(item: items.Take(budget: scope.Budget, index: index, span: call.Span));
        }

        return grouped;
    }
    private static JsonNode? Length(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(
                call: call,
                message: "length(value) takes one argument",
                scope: scope
            );
        }

        return DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) switch {
            JsonArray arr => JsonValue.Create(value: ((long)arr.Count)),
            JsonObject obj => JsonValue.Create(value: ((long)obj.Count)),
            JsonValue value when value.TryGetValue<string>(value: out var text) => JsonValue.Create(value: ((long)text.Length)),
            _ => Refuse(
            call: call,
            message: "length(value) reads an array, an object or a string",
            scope: scope
        ),
        };
    }
    // Text judged a character at a time (a typed word, a spelled answer) is authored as the string it is: this hands
    // the rule language the numbers a Text input event carries, so no author writes code points by hand.
    private static JsonNode? Scalars(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 1) {
            return Refuse(
                call: call,
                message: "scalars(text) takes one argument",
                scope: scope
            );
        }

        if (
            (DocumentLowering.EvaluateValue(
                expr: args[0].Value,
                scope: scope,
                fieldKey: fieldKey
            ) is not JsonValue value) ||
            !value.TryGetValue<string>(value: out var text)
        ) {
            return Refuse(
                call: call,
                message: "scalars(text) reads a string",
                scope: scope
            );
        }

        var scalars = new JsonArray();

        foreach (var rune in text.EnumerateRunes()) {
            scalars.Add(item: JsonValue.Create(value: ((long)rune.Value)));
        }

        return scalars;
    }
    private static JsonNode? Map(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadOperand(
            args: args,
            call: call,
            expected: 2,
            fieldKey: fieldKey,
            lambda: out var lambda,
            scope: scope,
            source: out var source,
            owned: out var sourceOwned
        )) {
            return null;
        }

        if (lambda!.Parameters.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "map's lambda takes the item, or the item and its index",
                scope: scope
            );
        }

        var arr = new JsonArray();

        for (var index = 0; (index < source!.Count); ++index) {
            var mapped = ApplyOwned(
                lambda: lambda,
                scope: scope,
                fieldKey: fieldKey,
                first: source[index],
                second: JsonValue.Create(value: ((long)index)),
                owned: out var owned
            );

            arr.AppendNode(item: scope.Budget.Take(owned: owned, span: call.Span, value: mapped));
        }

        return arr;
    }
    private static JsonNode? Range(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope) {
        if (args.Count != 2) {
            return Refuse(
                call: call,
                message: "range(start, end) takes two arguments",
                scope: scope
            );
        }

        if (
            !TryWholeNumber(
            node: DocumentLowering.EvaluateValue(
                expr: args[0].Value,
                scope: scope
            ),
            value: out var start
        ) ||
            !TryWholeNumber(
            node: DocumentLowering.EvaluateValue(
                expr: args[1].Value,
                scope: scope
            ),
            value: out var end
        )
        ) {
            return Refuse(
                call: call,
                message: "range(start, end) takes two whole numbers known at compile time",
                scope: scope
            );
        }

        if (end < start) {
            return Refuse(
                call: call,
                message: $"range(start, end) never runs backwards: its end {end} is below its start {start}",
                scope: scope
            );
        }

        // With end >= start the difference is at most 2^64 - 1, so it is exact as an unsigned number.
        var span = unchecked((ulong)end - (ulong)start);

        if (span > long.MaxValue) {
            return Refuse(
                call: call,
                message: "range exceeds the signed 64-bit integer range",
                scope: scope
            );
        }

        var count = (long)span;

        scope.Budget.Collection(
            count: count,
            span: call.Span
        );

        var arr = new JsonArray();

        for (var index = 0L; (index < count); ++index) {
            arr.AppendNode(item: JsonValue.Create(value: (start + index)));
        }

        return arr;
    }
    private static JsonNode? Reduce(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 3) {
            return Refuse(
                call: call,
                message: "reduce(array, seed, lambda) takes three arguments",
                scope: scope
            );
        }

        if (DocumentLowering.EvaluateValue(
            expr: args[0].Value,
            scope: scope,
            fieldKey: fieldKey
        ) is not JsonArray source) {
            return Refuse(
                call: call,
                message: "reduce's first argument is an array",
                scope: scope
            );
        }

        if (args[2].Value is not LambdaExpressionNode lambda) {
            return Refuse(
                call: call,
                message: "reduce's third argument is a lambda",
                scope: scope
            );
        }

        if (lambda.Parameters.Count != 2) {
            return Refuse(
                call: call,
                message: "reduce's lambda takes the running value and the item",
                scope: scope
            );
        }

        scope.Budget.Collection(
            count: source.Count,
            span: call.Span
        );
        var accumulated = DocumentLowering.EvaluateValue(
            expr: args[1].Value,
            scope: scope,
            fieldKey: fieldKey
        );

        foreach (var item in source.ToList()) {
            accumulated = Apply(
                fieldKey: fieldKey,
                first: accumulated,
                lambda: lambda,
                scope: scope,
                second: item
            );
        }

        return accumulated;
    }
    internal static JsonNode? Refuse(DocumentScope scope, CallExpressionNode call, string message) {
        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.BuiltinRefused,
            message: DiagnosticText.Compose(
                fix: "Correct the call so each argument matches the form above",
                problem: $"{message}, and this call to '{call.Name}' passes {call.Arguments.Count.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)} {((call.Arguments.Count == 1) ? "argument" : "arguments")}"
            ),
            span: call.Span
        );

        return null;
    }
    // `sort(array)` and `sort(array, lambda)` — the lambda projects each item to the key it sorts by. The order is
    // STABLE and total: numbers before strings before everything else, so a mixed array has one answer rather than
    // depending on which comparison happened to run.
    private static JsonNode? Sort(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count is < 1 or > 2) {
            return Refuse(
                call: call,
                message: "sort(array) or sort(array, lambda) takes one or two arguments",
                scope: scope
            );
        }

        if (!TryLend(expr: args[0].Value, fieldKey: fieldKey, list: out var items, scope: scope)) {
            return Refuse(
                call: call,
                message: "sort's first argument is an array",
                scope: scope
            );
        }

        LambdaExpressionNode? lambda = null;

        if (args.Count == 2) {
            if (args[1].Value is not LambdaExpressionNode written) {
                return Refuse(
                    call: call,
                    message: "sort's second argument is a lambda",
                    scope: scope
                );
            }

            if (written.Parameters.Count != 1) {
                return Refuse(
                    call: call,
                    message: "sort's lambda takes the item",
                    scope: scope
                );
            }

            lambda = written;
        }

        scope.Budget.Collection(
            count: items.Count,
            span: call.Span
        );
        var keyed = new List<(JsonNode? Key, int Index)>(capacity: items.Count);

        for (var index = 0; (index < items.Count); ++index) {
            keyed.Add(item: (
                Key: ((lambda is null)
                ? items[index]
                : Apply(
                    lambda: lambda,
                    scope: scope,
                    fieldKey: fieldKey,
                    first: items[index],
                    second: JsonValue.Create(value: ((long)index))
                )),
                Index: index));
        }

        var arr = new JsonArray();

        foreach (var entry in keyed.OrderBy(
            keySelector: static entry => entry.Key,
            comparer: DocumentValueComparer.Instance
        )) {
            arr.AppendNode(item: items.Take(budget: scope.Budget, index: entry.Index, span: call.Span));
        }

        return arr;
    }
    // A fold over a list evaluates each element's term once and folds the values through the chain ListFolds
    // describes, one step at a time (DocumentFoldSteps), so the fold's arithmetic is the document's own `+`, `or`,
    // `and`, `minimum`, `maximum` and choice, and no evaluation nests deeper as the list grows. A fold over truth values
    // folds each term's truth, as the rule language reads it.
    private static JsonNode? ListFold(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        var args = call.Arguments;

        if (DocumentLowering.EvaluateValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey) is not JsonArray source) {
            return Refuse(call: call, message: $"{call.Name}'s first argument is a list", scope: scope);
        }
        var first = (call.Name == "first");
        var term = (LambdaExpressionNode)args[1].Value;
        var value = ((first && (args.Count == 4)) ? (args[2].Value as LambdaExpressionNode) : null);

        if ((term.Parameters.Count > 2) || (value?.Parameters.Count is > 2) || (first && (args.Count == 4) && (value is null))) {
            return Refuse(call: call, message: $"{call.Name}'s lambdas take the item, or the item and its index", scope: scope);
        }
        scope.Budget.Collection(count: source.Count, span: call.Span);
        var terms = new List<FoldValue>(capacity: source.Count);
        var values = (first ? new List<FoldValue>(capacity: source.Count) : null);
        var truth = (first || ListFolds.ReadsTruth(name: call.Name));

        for (var index = 0; (index < source.Count); index++) {
            var item = source[index];
            var position = JsonValue.Create(value: ((long)index));
            var read = Apply(lambda: term, scope: scope, fieldKey: fieldKey, first: item, second: position);

            terms.Add(item: new(Node: (truth ? JsonValue.Create(value: (DocumentLowering.IsTruthy(node: read) ? 1L : 0L)) : read)));
            values?.Add(item: new(Node: ((value is null) ? item : Apply(lambda: value, scope: scope, fieldKey: fieldKey, first: item, second: position))));
        }
        var otherwise = (first ? new FoldValue(Node: DocumentLowering.EvaluateValue(expr: args[^1].Value, scope: scope, fieldKey: fieldKey)) : null);

        if (ListFolds.Fold(name: call.Name, otherwise: otherwise, refusal: out var refusal, spelling: new DocumentFoldSteps(call: call, fieldKey: fieldKey, scope: scope), terms: terms, values: values) is not { } folded) {
            return Refuse(call: call, message: refusal!, scope: scope);
        }
        return folded.Node;
    }
    // One value of a fold: a term's or a lambda's value as it was produced (borrowed), or a step's result. A null node
    // is a value, the null literal's.
    private sealed record FoldValue(JsonNode? Node);
    // A fold's steps evaluated as ListFolds makes them, each over values already evaluated: what evaluating the chain
    // written out gives, without nesting. Each step applies the document's own operator (DocumentLowering.ApplyBinary
    // and ShortCircuits, DocumentScalars.Apply, IsTruthy), and a refusal is placed at the fold's call.
    private sealed class DocumentFoldSteps(CallExpressionNode call, DocumentScope scope, string? fieldKey) : ListFoldSpelling<FoldValue> {
        private CallExpressionNode? m_pair;

        public override FoldValue Number(decimal value) => new(Node: DocumentLowering.EvaluateValue(
            expr: new LiteralExpressionNode(Value: (decimal.IsInteger(value: value) ? ((object)(long)value) : (double)value)),
            fieldKey: fieldKey,
            scope: scope
        ));
        public override FoldValue Binary(FoldValue left, string symbol, FoldValue right) {
            scope.Budget.Spend(count: 1, span: call.Span);
            if (symbol is "and" or "or") {
                var truth = DocumentLowering.IsTruthy(node: left.Node);

                return new(Node: JsonValue.Create(value: ((DocumentLowering.ShortCircuits(left: truth, symbol: symbol) ? truth : DocumentLowering.IsTruthy(node: right.Node)) ? 1L : 0L)));
            }
            return new(Node: DocumentLowering.ApplyBinary(
                bin: new BinaryExpressionNode(Column: call.Column, Left: call, Length: call.Length, Line: call.Line, Offset: call.Offset, Operator: symbol, Right: call),
                leftNode: left.Node,
                rightNode: right.Node,
                scope: scope
            ));
        }
        // The nested call written out passes two arguments, which a refusal counts.
        public override FoldValue Call(string name, FoldValue left, FoldValue right) {
            scope.Budget.Spend(count: 1, span: call.Span);
            m_pair ??= new(Arguments: [call.Arguments[0], call.Arguments[1]], Column: call.Column, Length: call.Length, Line: call.Line, Name: name, Offset: call.Offset);
            return new(Node: DocumentScalars.Apply(arguments: [left.Node, right.Node], call: m_pair, scope: scope));
        }
        public override FoldValue Choice(FoldValue condition, FoldValue whenTrue, FoldValue whenFalse) {
            scope.Budget.Spend(count: 1, span: call.Span);
            return (DocumentLowering.IsTruthy(node: condition.Node) ? whenTrue : whenFalse);
        }
        // The terms of a fold over truth values are already reduced to 1 or 0.
        public override bool IsTruth(FoldValue term) => true;
    }
    private static bool TryReadOperand(
        CallExpressionNode call,
        IReadOnlyList<ArgumentNode> args,
        int expected,
        DocumentScope scope,
        string? fieldKey,
        out JsonArray? source,
        out bool owned,
        out LambdaExpressionNode? lambda
    ) {
        source = null;
        owned = false;
        lambda = null;

        if (args.Count != expected) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}(array, lambda) takes two arguments"
            );

            return false;
        }

        if (DocumentLowering.Evaluate(
            expr: args[0].Value,
            fieldKey: fieldKey,
            owned: out var read,
            scope: scope
        ) is not JsonArray arr) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}'s first argument is an array"
            );

            return false;
        }

        if (args[1].Value is not LambdaExpressionNode written) {
            Refuse(
                scope: scope,
                call: call,
                message: $"{call.Name}'s second argument is a lambda"
            );

            return false;
        }

        scope.Budget.Collection(
            count: arr.Count,
            span: call.Span
        );
        source = arr;
        owned = read;
        lambda = written;

        return true;
    }
    private static bool TryWholeNumber(JsonNode? node, out long value) {
        return DocumentNumbers.TryInteger(
            node: node,
            number: out value
        );
    }

    /// <summary>Evaluates a builtin call.</summary>
    /// <param name="call">The call as written.</param>
    /// <param name="scope">The lowering scope.</param>
    /// <param name="fieldKey">The enclosing JSON key, threaded into each element's own lowering.</param>
    /// <param name="result">The evaluated node.</param>
    /// <param name="owned">Whether the caller alone holds <paramref name="result"/> (<see cref="DocumentLowering.Evaluate"/>):
    /// every builtin builds its result afresh except <c>reduce</c> and a list fold, which may answer a value a binding
    /// holds.</param>
    /// <returns><see langword="true"/> when <paramref name="call"/> names a builtin; refusals are reported into the
    /// scope's diagnostics and yield a null <paramref name="result"/>.</returns>
    public static bool TryFold(CallExpressionNode call, DocumentScope scope, string? fieldKey, out JsonNode? result, out bool owned) {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(scope);

        result = null;
        owned = false;

        // A fold is told by its lambda, so a construct or a scalar of the same name (a gate's `all`, `minimum(a, b)`)
        // keeps its own meaning.
        if (ListFolds.Shaped(arguments: call.Arguments.Count, name: call.Name, secondIsLambda: ((call.Arguments.Count > 1) && (call.Arguments[1].Value is LambdaExpressionNode)))) {
            result = ListFold(call: call, fieldKey: fieldKey, scope: scope);
            return true;
        }
        if (!Names.Contains(item: call.Name)) {
            return false;
        }
        if (Canonical(name: call.Name) is var canonical && !string.Equals(a: canonical, b: call.Name, comparisonType: StringComparison.Ordinal)) {
            call = (call with { Name = canonical });
        }

        if (
            Collections.TryGetValue(key: call.Name, value: out var named) &&
            named.PositionalOnly &&
            call.Arguments.Any(predicate: static argument => (argument.Name is not null))
        ) {
            return false;
        }

        var isCollection = Collections.TryGetValue(key: call.Name, value: out var collection);

        result = (isCollection
            ? collection!.Evaluate(arg1: call, arg2: scope, arg3: fieldKey)
            : DocumentScalars.Evaluate(
                call: call,
                fieldKey: fieldKey,
                scope: scope
            ));
        owned = !(isCollection && collection!.Borrows);

        return true;
    }
}
/// <summary>One collection builtin of <see cref="DocumentBuiltins.Collections"/>: how a call to it is written, what
/// it produces, and the evaluation the lowering runs for that call.</summary>
/// <param name="signature">The call as an author writes it, its lambda's parameters named.</param>
/// <param name="description">What the call produces, in one or two sentences.</param>
/// <param name="evaluate">The evaluation of a call: the call as written, the lowering scope and the enclosing JSON
/// key, returning the produced node or <see langword="null"/> after reporting a refusal into the scope.</param>
/// <param name="positionalOnly">Whether a call that names any argument is not this builtin: a vocabulary arm of the same
/// name (<c>all(predicates: [...])</c>) reads its named arguments, so a call written that way is the arm's.</param>
/// <param name="borrows">Whether a call may answer a value something else holds (<c>reduce</c> answers its seed or
/// its lambda's value as they stand); every other builtin builds its result afresh.</param>
public sealed class DocumentCollectionBuiltin(string signature, string description, Func<CallExpressionNode, DocumentScope, string?, JsonNode?> evaluate, bool positionalOnly = false, bool borrows = false) {
    /// <summary>Gets the call as an author writes it.</summary>
    public string Signature { get; } = signature;
    /// <summary>Gets what the call produces.</summary>
    public string Description { get; } = description;
    /// <summary>Gets whether a call naming any argument belongs to a vocabulary arm instead of this builtin.</summary>
    public bool PositionalOnly { get; } = positionalOnly;
    /// <summary>Gets whether a call may answer a value something else holds, so its result is borrowed.</summary>
    public bool Borrows { get; } = borrows;

    internal Func<CallExpressionNode, DocumentScope, string?, JsonNode?> Evaluate { get; } = evaluate;
}
