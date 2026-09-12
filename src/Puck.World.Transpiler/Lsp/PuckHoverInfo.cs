using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;

namespace Puck.World.Transpiler.Lsp;

// Reads declarations without evaluating user code or expanding templates during a hover request.
internal static class PuckHoverInfo {
    internal static string? Declaration(string source, string word, int offset) {
        var document = PuckParser.ParseDocumentWithDiagnostics(source).Value;

        if (document is null) {
            return null;
        }
        var path = new List<SyntaxNode>();

        FindPath(node: document, offset: offset, path: path);
        for (var index = (path.Count - 1); (index >= 0); --index) {
            switch (path[index]) {
                case LambdaExpressionNode lambda when lambda.Parameters.Contains(value: word):
                    return Card($"{word} — lambda parameter", Slice(node: lambda, source: source));
                case TemplateNode template:
                    var parameter = template.Parameters.FirstOrDefault(predicate: item => (item.Name == word));
                    if (parameter is not null) {
                        return Card($"{word} — template parameter", Slice(node: parameter, source: source), $"Parameter of {template.Name}.");
                    }
                    break;
                case ForStatementNode loop when (((loop.Item == word) || (loop.Index == word)) && !Contains(node: loop.Sequence, offset: offset)):
                    return Card($"{word} — loop variable", $"for {loop.Item}{((loop.Index is null) ? "" : $", {loop.Index}")} in {Slice(source, loop.Sequence)}",
                        ((word == loop.Index) ? "Zero-based element index." : "One element of the source sequence."));
                case RepeatStatementNode repeat when ((repeat.Index == word) && !Contains(node: repeat.Count, offset: offset)):
                    return Card($"{word} — loop index", $"repeat {Slice(source, repeat.Count)} as {word}", "Runs from zero to count minus one.");
                case RuleBlockNode rule:
                    var binding = rule.Statements.OfType<BindStatementNode>().FirstOrDefault(predicate: item => (item.Name == word));
                    if (binding is not null) {
                        return Describe(source, binding, $"{word} — rule binding ({binding.Kind})");
                    }
                    break;
                case CallExpressionNode call:
                    var called = document.Statements.OfType<TemplateNode>().FirstOrDefault(predicate: item => (item.Name == call.Name));
                    var argument = call.Arguments.FirstOrDefault(predicate: item => ((item.Name == word) && Contains(node: item, offset: offset) && (offset < item.Value.Offset)));
                    var formal = called?.Parameters.FirstOrDefault(predicate: item => (item.Name == argument?.Name));
                    if (formal is not null) {
                        return Card($"{word} — template parameter", Slice(node: formal, source: source), $"Parameter of {called!.Name}.");
                    }
                    break;
            }
        }
        foreach (var statement in document.Statements) {
            switch (statement) {
                case LetNode constant when (constant.Name == word):
                    return Describe(node: constant, source: source, title: $"{word} — compile-time constant");
                case TemplateNode template when (template.Name == word):
                    return Card($"{word} — template", $"template {word}({string.Join(separator: ", ", values: template.Parameters.Select(selector: parameter => Slice(node: parameter, source: source)))})", Comments(node: template, source: source));
                case ImportNode import when (import.Alias == word):
                    return Describe(node: import, source: source, title: $"{word} — import alias");
            }
        }
        return null;
    }
    internal static string? Builtin(string word) {
        var collection = word switch {
            "range" => ("range(start, count)", "Produces count consecutive integers beginning at start. Count must be nonnegative."),
            "length" => ("length(value)", "Returns the number of array elements, object properties, or UTF-16 string code units."),
            "concat" => ("concat(values...)", "Concatenates arrays and scalar values into one array."),
            "map" => ("map(array, (item, index) => value)", "Transforms each element. The index parameter is optional."),
            "filter" => ("filter(array, (item, index) => condition)", "Keeps elements whose condition is truthy. The index parameter is optional."),
            "reduce" => ("reduce(array, initial, (accumulator, item) => value)", "Folds elements into an accumulated value starting with initial."),
            "distinct" => ("distinct(array)", "Removes duplicate values, preserving the first occurrence."),
            "sort" => ("sort(array, item => key)", "Stably sorts an array. The key lambda is optional."),
            "groupBy" => ("groupBy(array, (item, index) => key)", "Groups elements into an object of arrays by key. The index parameter is optional."),
            _ => (((string, string)?)null)
        };

        if (collection is { } info) {
            return Card(declaration: info.Item1, description: info.Item2, title: "Collection function — evaluated at compile time");
        }
        if (ExpressionVocabulary.Functions.TryGetValue(key: word, value: out var function)) {
            var arguments = string.Join(separator: ", ", values: Enumerable.Range(1, function.Arity).Select(selector: index => $"arg{index}"));

            return Card("Expression function", $"{word}({arguments})", $"Arguments: {function.Arity}. Value domain: {function.Domain}. Operation: {function.Operation}.");
        }
        return null;
    }

    private static string Describe(string source, SyntaxNode node, string title) => Card(title, Slice(node: node, source: source), Comments(node: node, source: source));
    private static string Card(string title, string declaration, string? description = null) {
        var fence = "```";

        while (declaration.Contains(comparisonType: StringComparison.Ordinal, value: fence)) {
            fence += "`";
        }
        return ($"{title}\n\n{fence}puck\n{declaration}\n{fence}" + (string.IsNullOrWhiteSpace(value: description) ? "" : $"\n\n{description}"));
    }
    private static string Slice(string source, SyntaxNode node) => source.Substring(node.Offset, Math.Min(val1: node.Length, val2: (source.Length - node.Offset))).Trim();
    private static string? Comments(string source, SyntaxNode node) {
        var lines = source[..node.Offset].Split('\n');
        var comments = new List<string>();

        for (var index = (lines.Length - 2); (index >= 0); --index) {
            var line = lines[index].Trim();

            if (!line.StartsWith(comparisonType: StringComparison.Ordinal, value: "//")) {
                break;
            }
            comments.Add(item: line[2..].TrimStart(trimChar: '/').Trim());
        }
        comments.Reverse();
        return string.Join(separator: "\n", values: comments);
    }
    private static bool Contains(SyntaxNode node, int offset) => ((offset >= node.Offset) && (offset < (node.Offset + node.Length)));
    private static bool FindPath(SyntaxNode node, int offset, List<SyntaxNode> path) {
        if (!Contains(node: node, offset: offset)) {
            return false;
        }
        path.Add(item: node);
        foreach (var child in Children(node: node)) {
            if (FindPath(node: child, offset: offset, path: path)) {
                break;
            }
        }
        return true;
    }
    private static IEnumerable<SyntaxNode> Children(SyntaxNode node) => node switch {
        DocumentNode document => document.Statements,
        BlockNode block => block.Statements,
        TemplateNode template => [.. template.Parameters, template.Body],
        TemplateParameterNode { DefaultValue: { } value } => [value],
        LetNode constant => [constant.Value],
        PropertyNode property => [property.Value],
        ExpressionStatementNode statement => [statement.Expression],
        ForStatementNode loop => [loop.Sequence, .. loop.Body],
        RepeatStatementNode repeat => [repeat.Count, .. repeat.Body],
        RuleBlockNode rule => rule.Statements,
        DecisionBlockNode decision => decision.Statements,
        OptionBlockNode option => option.Statements,
        OnNoChoiceBlockNode fallback => fallback.Effects,
        TransactionStatementNode transaction => [.. transaction.MainEffects, .. (transaction.OnFailureEffects ?? [])],
        IfStatementNode branch => [.. branch.Then, .. (branch.Else ?? [])],
        CallExpressionNode call => call.Arguments,
        ArgumentNode argument => [argument.Value],
        LambdaExpressionNode lambda => [lambda.Body],
        ArrayExpressionNode array => array.Elements,
        ObjectExpressionNode obj => obj.Properties,
        BinaryExpressionNode binary => [binary.Left, binary.Right],
        UnaryExpressionNode unary => [unary.Operand],
        IndexExpressionNode index => [index.Target, index.Index],
        MemberAccessExpressionNode member => [member.Target],
        RangeExpressionNode range => [range.Start, range.End],
        _ => []
    };
}
