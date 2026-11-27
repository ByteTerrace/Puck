using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Transpiler.Ast;

namespace Puck.Transpiler.Lowering;

// The list library: folds, searches and reshapes over compile-time arrays, beside the transforms (map, filter, reduce)
// in DocumentBuiltins.cs. Every arithmetic fold goes through DocumentLowering.ApplyBinary, the fold the operators use, so
// `sum`, `scan` and `dot` add and multiply numbers, exact decimals and numeric arrays exactly as `+` and `*` do.
public static partial class DocumentBuiltins {
    // `any(array)`, `all(array)` and `count(array)` over the elements' own truth. The lambda forms of this and the other
    // folds (`sum`, `minimum`, `maximum`, `first`) are ListFolds', which TryFold hands a call before it reaches here.
    private static JsonNode? Any(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        if (!TryReadPlainList(call: call, fieldKey: fieldKey, list: out var list, scope: scope)) {
            return null;
        }

        var matched = 0L;

        for (var index = 0; (index < list.Count); ++index) {
            if (DocumentLowering.IsTruthy(node: list[index])) {
                ++matched;

                // A yes needs one match and a no needs one miss, so these two stop at the answer; a count reads every item.
                if (call.Name == "any") {
                    return JsonValue.Create(value: 1L);
                }
            } else if (call.Name == "all") {
                return JsonValue.Create(value: 0L);
            }
        }

        return call.Name switch {
            "any" => JsonValue.Create(value: 0L),
            "all" => JsonValue.Create(value: 1L),
            _ => JsonValue.Create(value: matched),
        };
    }
    // The fold's operator written between two values already evaluated, placed at the call.
    private static JsonNode? Combine(CallExpressionNode call, DocumentScope scope, string op, JsonNode? left, JsonNode? right) =>
        DocumentLowering.ApplyBinary(
            bin: new BinaryExpressionNode(
                Left: call,
                Operator: op,
                Right: call,
                Offset: call.Offset,
                Length: call.Length,
                Line: call.Line,
                Column: call.Column
            ),
            leftNode: left,
            rightNode: right,
            scope: scope
        );
    private static JsonNode? Contains(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 2) {
            return Refuse(call: call, message: "contains(array, value) takes two arguments", scope: scope);
        }
        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 2, list: out var list, scope: scope, usage: "contains(array, value)")) {
            return null;
        }

        var wanted = DocumentLowering.EvaluateValue(expr: args[1].Value, scope: scope, fieldKey: fieldKey);

        return JsonValue.Create(value: ((IndexOfValue(list: list, value: wanted) >= 0) ? 1L : 0L));
    }
    private static JsonNode? Dot(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count != 2) {
            return Refuse(call: call, message: "dot(a, b) takes two arguments", scope: scope);
        }

        var left = DocumentLowering.EvaluateValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey);
        var right = DocumentLowering.EvaluateValue(expr: args[1].Value, scope: scope, fieldKey: fieldKey);

        if ((left is not JsonArray a) || (right is not JsonArray b) || a.Any(predicate: static item => (item is JsonArray)) || b.Any(predicate: static item => (item is JsonArray))) {
            return Refuse(call: call, message: "dot(a, b) reads two arrays of numbers", scope: scope);
        }
        if (a.Count != b.Count) {
            return Refuse(call: call, message: $"dot(a, b) reads two arrays of one length, and these have {a.Count.ToString(provider: CultureInfo.InvariantCulture)} and {b.Count.ToString(provider: CultureInfo.InvariantCulture)} elements", scope: scope);
        }

        scope.Budget.Collection(count: a.Count, span: call.Span);

        var total = ((JsonNode?)JsonValue.Create(value: 0L));

        for (var index = 0; (index < a.Count); ++index) {
            total = Combine(
                call: call,
                left: total,
                op: "+",
                right: Combine(call: call, left: a[index], op: "*", right: b[index], scope: scope),
                scope: scope
            );

            if (total is null) {
                return null;
            }
        }

        return total;
    }
    // `find` answers the item, `indexOf` its place; either takes the value sought or an `item => condition`.
    private static JsonNode? Find(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        var usage = ((call.Name == "find") ? "find(array, item => condition)" : "indexOf(array, value) or indexOf(array, item => condition)");

        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 2, list: out var list, scope: scope, usage: usage)) {
            return null;
        }
        if (args.Count != 2) {
            return Refuse(call: call, message: $"{usage} takes two arguments", scope: scope);
        }

        var position = -1;

        if (args[1].Value is LambdaExpressionNode) {
            if (ReadItemLambda(arg: args[1], call: call, scope: scope) is not { } lambda) {
                return null;
            }

            for (var index = 0; ((position < 0) && (index < list.Count)); ++index) {
                if (DocumentLowering.IsTruthy(node: Apply(first: list[index], fieldKey: fieldKey, lambda: lambda, scope: scope, second: JsonValue.Create(value: ((long)index))))) {
                    position = index;
                }
            }
        } else if (call.Name == "find") {
            return Refuse(call: call, message: "find's second argument is a lambda", scope: scope);
        } else {
            position = IndexOfValue(
                list: list,
                value: DocumentLowering.EvaluateValue(expr: args[1].Value, scope: scope, fieldKey: fieldKey)
            );
        }

        return ((call.Name == "find")
            ? ((position < 0) ? null : scope.Budget.Copy(value: list[position], span: call.Span))
            : JsonValue.Create(value: ((long)position))
        );
    }
    private static int IndexOfValue(JsonArray list, JsonNode? value) {
        for (var index = 0; (index < list.Count); ++index) {
            if (DocumentValueEqualityComparer.Instance.Equals(x: list[index], y: value)) {
                return index;
            }
        }

        return -1;
    }
    private static JsonNode? Join(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 2, list: out var list, scope: scope, usage: "join(array, separator)")) {
            return null;
        }

        var separator = string.Empty;

        if (args.Count == 2) {
            if (
                (DocumentLowering.EvaluateValue(expr: args[1].Value, scope: scope) is not JsonValue sepValue) ||
                !sepValue.TryGetValue<string>(value: out var written)
            ) {
                return Refuse(call: call, message: "join's separator is a string", scope: scope);
            }

            separator = written;
        }

        scope.Budget.Collection(count: list.Count, span: call.Span);

        var text = new StringBuilder();

        for (var index = 0; (index < list.Count); ++index) {
            if (list[index] is JsonArray or JsonObject) {
                return Refuse(call: call, message: "join reads an array of strings and numbers", scope: scope);
            }
            if (index > 0) {
                text.Append(value: separator);
            }

            text.Append(value: DocumentLowering.FormatHole(node: list[index]));

            if (text.Length > DocumentEvaluationBudget.TextLimit) {
                throw new DocumentEvaluationException("The joined string exceeds the text limit.", call.Span);
            }
        }

        var joined = JsonValue.Create(value: text.ToString());

        scope.Vocabulary.NoteWrittenString(scope: scope, value: joined);

        return joined;
    }
    // The smallest or the largest number of a list: `minimum(array)` and `maximum(array)`, called by the scalar
    // folding when the name has one argument. The item kept is the one written, so an exact decimal stays exact.
    internal static JsonNode? Extreme(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        if (!TryReadList(args: call.Arguments, call: call, fieldKey: fieldKey, maximum: 1, list: out var list, scope: scope, usage: $"{call.Name}(array)")) {
            return null;
        }

        JsonNode? best = null;

        foreach (var item in list) {
            if (!DocumentLowering.TryReadNumber(node: item, number: out _)) {
                return Refuse(call: call, message: $"{call.Name} reads an array of numbers known at compile time", scope: scope);
            }

            var order = ((best is null) ? 0 : DocumentNumbers.Compare(left: item, right: best));

            if ((best is null) || ((call.Name == "minimum") ? (order < 0) : (order > 0))) {
                best = item;
            }
        }

        return ((best is null)
            ? Refuse(call: call, message: $"{call.Name} of an empty array has no value", scope: scope)
            : scope.Budget.Copy(value: best, span: call.Span)
        );
    }
    private static JsonNode? Reverse(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 1, list: out var list, scope: scope, usage: "reverse(array)")) {
            return null;
        }

        var reversed = new JsonArray();

        for (var index = (list.Count - 1); (index >= 0); --index) {
            reversed.AppendNode(item: scope.Budget.Copy(value: list[index], span: call.Span));
        }

        return reversed;
    }
    // Prefix sums in one pass: element i is the sum of the first i projected items, so the array holds one more value
    // than the list, starts at zero and ends at the total (`scan([3, 4, 5])` is [0, 3, 7, 12]). A numeric array item
    // sums element-wise, and its zero is the zero array of its length.
    private static JsonNode? Scan(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (!TryReadFold(args: args, call: call, fieldKey: fieldKey, lambda: out var lambda, list: out var list, scope: scope, usage: "scan(array) or scan(array, item => value)")) {
            return null;
        }

        scope.Budget.Collection(count: (list.Count + 1), span: call.Span);

        var prefixes = new JsonArray();
        JsonNode? running = null;

        for (var index = 0; (index < list.Count); ++index) {
            var item = ProjectItem(index: index, lambda: lambda, list: list, scope: scope, fieldKey: fieldKey);

            if (running is null) {
                running = ZeroLike(node: item);
                prefixes.AppendNode(item: scope.Budget.Copy(value: running, span: call.Span));
            }

            running = Combine(call: call, left: running, op: "+", right: item, scope: scope);

            if (running is null) {
                return null;
            }

            prefixes.AppendNode(item: scope.Budget.Copy(value: running, span: call.Span));
        }

        if (list.Count == 0) {
            prefixes.Add(item: 0L);
        }

        return prefixes;
    }
    private static JsonNode? ProjectItem(int index, LambdaExpressionNode? lambda, JsonArray list, DocumentScope scope, string? fieldKey) =>
        ((lambda is null)
            ? list[index]
            : Apply(first: list[index], fieldKey: fieldKey, lambda: lambda, scope: scope, second: JsonValue.Create(value: ((long)index)))
        );
    private static JsonNode? Slice(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count < 2) {
            return Refuse(call: call, message: "slice(array, start, end) takes an array, a start and optionally an end", scope: scope);
        }
        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 3, list: out var list, scope: scope, usage: "slice(array, start, end)")) {
            return null;
        }

        // A negative bound counts from the end, and a bound past either end stops there, so a slice never refuses by range.
        var bounds = new long[] { 0, list.Count };

        for (var which = 0; (which < (args.Count - 1)); ++which) {
            if (!DocumentNumbers.TryInteger(node: DocumentLowering.EvaluateValue(expr: args[(which + 1)].Value, scope: scope), number: out var bound)) {
                return Refuse(call: call, message: "slice's start and end are whole numbers known at compile time", scope: scope);
            }

            bounds[which] = Math.Clamp(value: ((bound < 0) ? (bound + list.Count) : bound), min: 0, max: list.Count);
        }

        var slice = new JsonArray();

        for (var index = bounds[0]; (index < bounds[1]); ++index) {
            slice.AppendNode(item: scope.Budget.Copy(value: list[((int)index)], span: call.Span));
        }

        return slice;
    }
    // `sum(array)`; `sum(array, item => value)` is ListFolds'.
    private static JsonNode? Sum(CallExpressionNode call, DocumentScope scope, string? fieldKey) {
        if (!TryReadPlainList(call: call, fieldKey: fieldKey, list: out var list, scope: scope)) {
            return null;
        }

        scope.Budget.Collection(count: list.Count, span: call.Span);

        JsonNode? total = JsonValue.Create(value: 0L);

        for (var index = 0; (index < list.Count); ++index) {
            // The first item starts the sum, so an array item needs no zero array to start from.
            total = ((index == 0) ? list[index] : Combine(call: call, left: total, op: "+", right: list[index], scope: scope));

            if (total is null) {
                return null;
            }
        }

        return scope.Budget.Copy(value: total, span: call.Span);
    }
    private static JsonNode? Zip(CallExpressionNode call, IReadOnlyList<ArgumentNode> args, DocumentScope scope, string? fieldKey) {
        if (args.Count < 2) {
            return Refuse(call: call, message: "zip(a, b, ...) takes at least two arrays", scope: scope);
        }

        var lists = new List<JsonArray>(capacity: args.Count);

        foreach (var arg in args) {
            if (DocumentLowering.EvaluateValue(expr: arg.Value, scope: scope, fieldKey: fieldKey) is not JsonArray list) {
                return Refuse(call: call, message: "zip reads arrays", scope: scope);
            }
            if ((lists.Count > 0) && (list.Count != lists[0].Count)) {
                return Refuse(call: call, message: $"zip reads arrays of one length, and these have {lists[0].Count.ToString(provider: CultureInfo.InvariantCulture)} and {list.Count.ToString(provider: CultureInfo.InvariantCulture)} elements", scope: scope);
            }

            lists.Add(item: list);
        }

        scope.Budget.Collection(count: (((long)lists[0].Count) * lists.Count), span: call.Span);

        var zipped = new JsonArray();

        for (var index = 0; (index < lists[0].Count); ++index) {
            var tuple = new JsonArray();

            foreach (var list in lists) {
                tuple.AppendNode(item: scope.Budget.Copy(value: list[index], span: call.Span));
            }

            zipped.Add(item: tuple);
        }

        return zipped;
    }
    private static JsonNode? ZeroLike(JsonNode? node) {
        if (node is not JsonArray array) {
            return JsonValue.Create(value: 0L);
        }

        var zero = new JsonArray();

        foreach (var item in array) {
            zero.Add(item: ZeroLike(node: item));
        }

        return zero;
    }

    // The lambda of a list builtin: `item => value` or `(item, index) => value`.
    private static LambdaExpressionNode? ReadItemLambda(ArgumentNode arg, CallExpressionNode call, DocumentScope scope) {
        if (arg.Value is not LambdaExpressionNode lambda) {
            Refuse(call: call, message: $"{call.Name}'s second argument is a lambda", scope: scope);

            return null;
        }
        if (lambda.Parameters.Count is < 1 or > 2) {
            Refuse(call: call, message: $"{call.Name}'s lambda takes the item, or the item and its index", scope: scope);

            return null;
        }

        return lambda;
    }
    // A fold over a list with an optional lambda projecting each item first: `sum(array)`, `scan(array, item => value)`.
    private static bool TryReadFold(IReadOnlyList<ArgumentNode> args, CallExpressionNode call, string? fieldKey, out LambdaExpressionNode? lambda, out JsonArray list, DocumentScope scope, string usage) {
        lambda = null;

        if (!TryReadList(args: args, call: call, fieldKey: fieldKey, maximum: 2, list: out list, scope: scope, usage: usage)) {
            return false;
        }
        if (args.Count == 2) {
            lambda = ReadItemLambda(arg: args[1], call: call, scope: scope);

            return (lambda is not null);
        }

        return true;
    }
    // The array of a fold written without a lambda. A fold written with one is a ListFolds shape, so a second argument
    // that reaches here is not a lambda.
    private static bool TryReadPlainList(CallExpressionNode call, string? fieldKey, out JsonArray list, DocumentScope scope) {
        if (call.Arguments.Count == 2) {
            list = [];
            Refuse(call: call, message: $"{call.Name}'s second argument is a lambda", scope: scope);

            return false;
        }

        return TryReadList(args: call.Arguments, call: call, fieldKey: fieldKey, maximum: 1, list: out list, scope: scope, usage: $"{call.Name}(array) or {call.Name}(array, item => term)");
    }
    // The array a list builtin reads, first in its arguments, refusing a call with none or more than `maximum`.
    private static bool TryReadList(IReadOnlyList<ArgumentNode> args, CallExpressionNode call, string? fieldKey, int maximum, out JsonArray list, DocumentScope scope, string usage) {
        list = [];

        if ((args.Count < 1) || (args.Count > maximum)) {
            Refuse(call: call, message: $"{call.Name} is called as {usage}", scope: scope);

            return false;
        }
        if (DocumentLowering.EvaluateValue(expr: args[0].Value, scope: scope, fieldKey: fieldKey) is not JsonArray read) {
            Refuse(call: call, message: $"{call.Name}'s first argument is an array", scope: scope);

            return false;
        }

        list = read;

        return true;
    }
}
