using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lsp;

// Reads declarations without evaluating user code or expanding templates during a hover request.
internal static class PuckHoverInfo {
    private static readonly Lazy<PuckSchemaHover> Schema = new(valueFactory: () => new PuckSchemaHover());

    internal static string? Builtin(string word) {
        if (DocumentBuiltins.Collections.TryGetValue(key: word, value: out var collection)) {
            return Card(
                declaration: collection.Signature,
                description: collection.Description,
                title: "Collection function — evaluated at compile time"
            );
        }
        if (ExpressionVocabulary.Functions.TryGetValue(
            key: word,
            value: out var function
        )) {
            var arguments = string.Join(
                separator: ", ",
                values: Enumerable.Range(
                    1,
                    function.Arity
                ).Select(selector: index => $"arg{index}")
            );

            return Card(
                "Expression function",
                $"{word}({arguments})",
                $"Arguments: {function.Arity}. Value domain: {function.Domain}. Operation: {function.Operation}."
            );
        }
        return PuckEmbeddingLsp.GetKeywordHoverCard(word: word);
    }
    internal static string? Declaration(
        string source,
        string? word,
        int offset,
        DocumentVocabularyResolver? resolver = null,
        IReadOnlyList<PuckSymbolResolver.ImportedSymbol>? imported = null,
        string? qualified = null,
        Func<string, string?>? readSource = null,
        DocumentNode? parsedDocument = null
    ) {
        var vocabulary = (resolver?.Resolve(source: source) ?? WorldDocumentVocabulary.Instance);
        var document = (parsedDocument ?? PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            vocabulary: vocabulary
        ).Value);

        if (document is null) {
            return null;
        }
        var path = SyntaxWalk.PathAt(
            offset: offset,
            root: document
        );

        if (
            (word is not null) &&
            (Schema.Value.Describe(
                document: document,
                offset: offset,
                path: path,
                word: word
            ) is { } fieldCard)
        ) {
            return fieldCard;
        }
        if (word is not null) {
            for (var index = (path.Count - 1); (index >= 0); --index) {
                switch (path[index]) {
                    case LambdaExpressionNode lambda when lambda.Parameters.Contains(value: word):
                        return Card(
                            $"{word} — lambda parameter",
                            Slice(
                                node: lambda,
                                source: source
                            )
                        );
                    case TemplateNode template:
                        var parameter = template.Parameters.FirstOrDefault(predicate: item => (item.Name == word));
                        if (parameter is not null) {
                            return Card(
                                $"{word} — template parameter",
                                Slice(
                                    node: parameter,
                                    source: source
                                ),
                                $"Parameter of {template.Name}."
                            );
                        }
                        if (template.Body.Statements.OfType<LetNode>().FirstOrDefault(predicate: item => (item.Name == word)) is { } templateLet) {
                            return Describe(node: templateLet, source: source, title: $"{word} — local constant");
                        }
                        break;
                    case ForStatementNode loop when (((loop.Item == word) || (loop.Index == word)) && !SyntaxWalk.Spans(
                    node: loop.Sequence,
                    offset: offset
                )):
                        return Card(
                            $"{word} — loop variable",
                            $"for {loop.Item}{((loop.Index is null)
                            ? ""
                            : $", {loop.Index}")} in {Slice(
                                source,
                                loop.Sequence
                            )}",
                            ((word == loop.Index)
                            ? "Zero-based element index."
                            : "One element of the source sequence.")
                        );
                    case RepeatStatementNode repeat when ((repeat.Index == word) && !SyntaxWalk.Spans(
                    node: repeat.Count,
                    offset: offset
                )):
                        return Card(
                            $"{word} — loop index",
                            $"repeat {Slice(
                                source,
                                repeat.Count
                            )} as {word}",
                            "Runs from zero to count minus one."
                        );
                    case RuleBlockNode rule:
                        var local = rule.Statements.OfType<LocalStatementNode>().FirstOrDefault(predicate: item => (item.Name == word));
                        if (local is not null) {
                            return Describe(
                                node: local,
                                source: source,
                                title: $"{word} — rule local"
                            );
                        }
                        break;
                    case CallExpressionNode call: {
                            var argument = call.Arguments.FirstOrDefault(predicate: item => ((item.Name == word) && SyntaxWalk.Spans(
                                node: item,
                                offset: offset
                            ) && (offset < item.Value.Offset)));

                            if (argument is null) {
                                break;
                            }
                            if (
                                document.Statements.OfType<TemplateNode>().FirstOrDefault(predicate: item => (item.Name == call.Name)) is { } called &&
                                called.Parameters.FirstOrDefault(predicate: item => (item.Name == argument.Name)) is { } formal
                            ) {
                                return Card(
                                    $"{word} — template parameter",
                                    Slice(
                                        node: formal,
                                        source: source
                                    ),
                                    $"Parameter of {called.Name}."
                                );
                            }
                            if (
                                (imported is not null) &&
                                (imported.FirstOrDefault(predicate: item => ((item.Name == call.Name) && (item.Node is TemplateNode))) is { Node: TemplateNode importedTemplate } sym) &&
                                (importedTemplate.Parameters.FirstOrDefault(predicate: item => (item.Name == argument.Name)) is { } importedFormal)
                            ) {
                                var importedSource = readSource?.Invoke(arg: sym.SourcePath);
                                var decl = ((importedSource is not null) ? Slice(node: importedFormal, source: importedSource) : importedFormal.Name);

                                return Card(
                                    $"{word} — template parameter",
                                    decl,
                                    $"Parameter of {sym.Name} (from `{Path.GetFileName(path: sym.SourcePath)}`)."
                                );
                            }
                            break;
                        }
                }
            }
            foreach (var statement in document.Statements) {
                switch (statement) {
                    case LetNode constant when (constant.Name == word):
                        return Describe(
                            node: constant,
                            source: source,
                            title: $"{word} — compile-time constant"
                        );
                    case TemplateNode template when (template.Name == word):
                        return DescribeTemplate(
                            displayName: word,
                            source: source,
                            template: template,
                            originNote: null
                        );
                    case ImportNode import when (import.Alias == word):
                        return Describe(
                            node: import,
                            source: source,
                            title: $"{word} — import alias"
                        );
                }
            }
        }

        // Cross-import lookup: matches qualified `alias.name` or unaliased `name`.
        var lookupName = (qualified ?? word);

        if ((lookupName is not null) && (imported is not null)) {
            var match = imported.FirstOrDefault(predicate: item => string.Equals(a: item.Name, b: lookupName, comparisonType: StringComparison.Ordinal));

            if (match is not null) {
                var importedSource = readSource?.Invoke(arg: match.SourcePath);
                var origin = $"From `{Path.GetFileName(path: match.SourcePath)}`.";

                return match.Node switch {
                    LetNode importedLet => DescribeImported(
                        fallbackDecl: $"let {match.Name}",
                        node: importedLet,
                        origin: origin,
                        source: importedSource,
                        title: $"{match.Name} — compile-time constant"
                    ),
                    TemplateNode importedTemplate => DescribeTemplate(
                        displayName: match.Name,
                        originNote: origin,
                        source: importedSource,
                        template: importedTemplate
                    ),
                    _ => null,
                };
            }
        }
        return null;
    }

    private static string DescribeTemplate(string displayName, TemplateNode template, string? source, string? originNote) {
        var keyword = (template.IsModule
            ? "module"
            : (template.IsRuleBody
                ? "rule template"
                : (template.IsShade ? "shade template" : "template")));
        var paramList = string.Join(
            separator: ", ",
            values: template.Parameters.Select(selector: parameter => ((source is not null) ? Slice(node: parameter, source: source) : parameter.Name))
        );
        var comments = ((source is not null) ? Comments(node: template, source: source) : null);
        var description = JoinNotes(primary: comments, secondary: originNote);

        return Card(
            declaration: $"{keyword} {displayName}({paramList})",
            description: description,
            title: $"{displayName} — {keyword}"
        );
    }

    private static string DescribeImported(string title, SyntaxNode node, string? source, string fallbackDecl, string origin) {
        var declaration = ((source is not null) ? Slice(node: node, source: source) : fallbackDecl);
        var comments = ((source is not null) ? Comments(node: node, source: source) : null);

        return Card(
            declaration: declaration,
            description: JoinNotes(primary: comments, secondary: origin),
            title: title
        );
    }

    private static string? JoinNotes(string? primary, string? secondary) => (
        (string.IsNullOrWhiteSpace(value: primary), string.IsNullOrWhiteSpace(value: secondary)) switch {
            (true, true) => null,
            (false, true) => primary,
            (true, false) => secondary,
            (false, false) => $"{primary}\n\n{secondary}",
        }
    );
    /// <summary>Returns a hover card: a title, the declaration as a <c>puck</c> code fence long enough that no run of
    /// backquotes inside it closes the fence, and the description after it when there is one.</summary>
    /// <param name="title">The card's first line, as markdown.</param>
    /// <param name="declaration">The source the card shows.</param>
    /// <param name="description">The text after the fence, or <see langword="null"/> for none.</param>
    /// <returns>The card's markdown.</returns>
    internal static string Card(string title, string declaration, string? description = null) {
        var fence = "```";

        while (declaration.Contains(
            comparisonType: StringComparison.Ordinal,
            value: fence
        )) {
            fence += "`";
        }
        return ($"{title}\n\n{fence}puck\n{declaration}\n{fence}" + (string.IsNullOrWhiteSpace(value: description)
            ? ""
            : $"\n\n{description}"));
    }

    private static string? Comments(string source, SyntaxNode node) {
        var lines = source[..node.Offset].Split('\n');
        var comments = new List<string>();

        for (var index = (lines.Length - 2); (index >= 0); --index) {
            var line = lines[index].Trim();

            if (!line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "//"
            )) {
                break;
            }
            comments.Add(item: line[2..].TrimStart(trimChar: '/').Trim());
        }
        comments.Reverse();
        return string.Join(
            separator: "\n",
            values: comments
        );
    }
    private static string Describe(string source, SyntaxNode node, string title) => Card(
        title,
        Slice(
            node: node,
            source: source
        ),
        Comments(
            node: node,
            source: source
        )
    );
    private static string Slice(string source, SyntaxNode node) => source.Substring(
        node.Offset,
        Math.Min(
            val1: node.Length,
            val2: (source.Length - node.Offset)
        )
    ).Trim();
}
