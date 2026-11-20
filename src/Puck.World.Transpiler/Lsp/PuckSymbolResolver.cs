using Puck.Abstractions;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Editing;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;

namespace Puck.World.Transpiler.Lsp;

/// <summary>Resolves symbols, imported declarations, definitions and AST-level state rows for language-server
/// requests without compiling the buffer synchronously.</summary>
internal static class PuckSymbolResolver {
    /// <summary>A declaration brought in through one or more library <c>import</c> statements.</summary>
    /// <param name="Name">The name in the importer's scope (prefixed with <c>alias.</c> when aliased).</param>
    /// <param name="OriginalName">The declaration's own name in <paramref name="SourcePath"/>.</param>
    /// <param name="SourcePath">The full path of the library file that defines <paramref name="Node"/>.</param>
    /// <param name="Node">The defining <see cref="LetNode"/> or <see cref="TemplateNode"/>, carrying its original span.</param>
    internal sealed record ImportedSymbol(string Name, string OriginalName, string SourcePath, StatementNode Node);

    /// <summary>The word and optional dotted qualified name under a cursor position.</summary>
    /// <param name="Word">The bare identifier under the cursor, or <see langword="null"/> when preceded by <c>.</c> or inside a comment/string.</param>
    /// <param name="Qualified">The full dotted name ending at the cursor (for example <c>water.riverChannel</c>), or <see langword="null"/> when not dotted.</param>
    /// <param name="StartOffset">The 0-based start offset of the bare identifier segment.</param>
    /// <param name="EndOffset">The 0-based end offset of the bare identifier segment.</param>
    internal readonly record struct WordContext(string? Word, string? Qualified, int StartOffset, int EndOffset);

    /// <summary>A resolved definition location.</summary>
    /// <param name="Uri">The document URI of the target file.</param>
    /// <param name="Span">The target span in that file.</param>
    /// <param name="SourceText">The target file's text when available, for multi-line span calculation.</param>
    internal sealed record DefinitionTarget(string Uri, SourceSpan Span, string? SourceText);

    /// <summary>A state row or enum discovered directly from the parsed AST.</summary>
    /// <param name="Kind">The row's declared or inferred kind, or <c>"enum"</c> for an enum.</param>
    /// <param name="Keys">The row's authored cell keys or enum members.</param>
    internal sealed record AstStateRow(string Kind, IReadOnlyList<string> Keys);

    /// <summary>Collects every <c>let</c> and <c>template</c> declaration reachable from <paramref name="document"/>'s
    /// library <c>import</c>s through <paramref name="sources"/> (<see cref="PuckSourceTable"/>).</summary>
    public static IReadOnlyList<ImportedSymbol> CollectImportedDeclarations(
        DocumentNode? document,
        string? sourcePath,
        PuckSourceTable sources,
        IDocumentVocabulary vocabulary
    ) {
        if ((document is null) || string.IsNullOrWhiteSpace(value: sourcePath)) {
            return [];
        }

        var fullRoot = Path.GetFullPath(path: sourcePath);
        var rootDir = (Path.GetDirectoryName(path: fullRoot) ?? string.Empty);
        var visited = new HashSet<(string Path, string Prefix)>();
        var results = new List<ImportedSymbol>();

        WalkImports(
            currentDir: rootDir,
            document: document,
            prefix: string.Empty,
            results: results,
            sources: sources,
            visited: visited,
            vocabulary: vocabulary
        );

        return results;
    }

    private static void WalkImports(
        DocumentNode document,
        string currentDir,
        string prefix,
        PuckSourceTable sources,
        IDocumentVocabulary vocabulary,
        HashSet<(string Path, string Prefix)> visited,
        List<ImportedSymbol> results
    ) {
        foreach (var import in document.Statements.OfType<ImportNode>()) {
            if (import.NamesFile) {
                continue;
            }
            if (
                !vocabulary.TryResolveImport(
                    directory: currentDir,
                    library: out var library,
                    name: import.Path,
                    reason: out _
                ) ||
                (library is null)
            ) {
                continue;
            }

            var fullPath = Path.GetFullPath(path: library);
            var nextPrefix = (string.IsNullOrEmpty(value: prefix)
                ? (import.Alias ?? string.Empty)
                : (string.IsNullOrEmpty(value: import.Alias) ? prefix : $"{prefix}.{import.Alias}"));

            if (!visited.Add(item: (PuckPaths.Key(path: fullPath), nextPrefix))) {
                continue;
            }

            DocumentNode importedDoc;

            try {
                importedDoc = sources.Read(
                    parsed: out _,
                    path: fullPath,
                    vocabulary: vocabulary
                ).Document;
            } catch {
                continue;
            }

            var nextDir = (Path.GetDirectoryName(path: fullPath) ?? string.Empty);

            WalkImports(
                currentDir: nextDir,
                document: importedDoc,
                prefix: nextPrefix,
                results: results,
                sources: sources,
                visited: visited,
                vocabulary: vocabulary
            );

            foreach (var statement in importedDoc.Statements) {
                var originalName = (statement switch {
                    LetNode letNode => letNode.Name,
                    TemplateNode templateNode => templateNode.Name,
                    _ => null,
                });

                if (originalName is null) {
                    continue;
                }

                var exposedName = (string.IsNullOrEmpty(value: nextPrefix)
                    ? originalName
                    : $"{nextPrefix}.{originalName}");

                results.Add(item: new ImportedSymbol(
                    Name: exposedName,
                    Node: statement,
                    OriginalName: originalName,
                    SourcePath: fullPath
                ));
            }
        }
    }

    /// <summary>Resolves an <c>import</c> or <c>basis</c> name beside <paramref name="directory"/> to a source file
    /// path when one exists on disk.</summary>
    public static string? ResolveImportOrBasisFile(string directory, string name, IDocumentVocabulary vocabulary) {
        if (string.IsNullOrWhiteSpace(value: directory) || string.IsNullOrWhiteSpace(value: name)) {
            return null;
        }
        if (
            vocabulary.TryResolveImport(
                directory: directory,
                library: out var library,
                name: name,
                reason: out _
            ) &&
            (library is not null)
        ) {
            return Path.GetFullPath(path: library);
        }

        foreach (var suffix in new[] { ".puck", ".world.puck", ".world.json" }) {
            var candidate = Path.GetFullPath(path: Path.Combine(path1: directory, path2: (name + suffix)));

            if (File.Exists(path: candidate)) {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Extracts the bare word and optional dotted qualified name at a 0-based LSP line/character.</summary>
    public static WordContext WordContextAt(string text, int targetLine, int targetCol) {
        var lines = text.Split('\n');

        if ((targetLine < 0) || (targetLine >= lines.Length)) {
            return default;
        }

        var lineText = lines[targetLine];

        if ((targetCol < 0) || (targetCol >= lineText.Length)) {
            return default;
        }

        var absolute = LspJson.Offset(character: targetCol, line: targetLine, source: text);

        for (var index = 0; (index <= absolute); ++index) {
            var lexicalEnd = SourceLexemes.End(offset: index, source: text);

            if (lexicalEnd > index) {
                if (absolute < lexicalEnd) {
                    if ((text[index] == '`') && (lexicalEnd > (index + 1)) && (text[(lexicalEnd - 1)] == '`')) {
                        var backquoted = text.Substring(length: ((lexicalEnd - index) - 2), startIndex: (index + 1));

                        return new WordContext(EndOffset: lexicalEnd, Qualified: null, StartOffset: index, Word: backquoted);
                    }
                    return default;
                }
                index = (lexicalEnd - 1);
            }
        }

        if (!IdentifierSpelling.IsNameCharacter(character: lineText[targetCol])) {
            return default;
        }

        var start = targetCol;

        while ((start > 0) && IdentifierSpelling.IsNameCharacter(character: lineText[(start - 1)])) {
            start--;
        }

        var end = targetCol;

        while ((end < lineText.Length) && IdentifierSpelling.IsNameCharacter(character: lineText[end])) {
            end++;
        }

        if (start >= end) {
            return default;
        }

        var lineOffset = (absolute - targetCol);
        var segment = lineText[start..end];

        if ((start > 0) && (lineText[(start - 1)] == '.')) {
            var qStart = (start - 1);

            while (
                (qStart > 0) &&
                (IdentifierSpelling.IsNameCharacter(character: lineText[(qStart - 1)]) || (lineText[(qStart - 1)] == '.'))
            ) {
                qStart--;
            }

            var qualified = lineText[qStart..end];

            return new WordContext(
                EndOffset: (lineOffset + end),
                Qualified: (qualified.StartsWith(value: '.') ? null : qualified),
                StartOffset: (lineOffset + start),
                Word: null
            );
        }

        return new WordContext(
            EndOffset: (lineOffset + end),
            Qualified: null,
            StartOffset: (lineOffset + start),
            Word: segment
        );
    }

    /// <summary>Resolves <c>textDocument/definition</c> at the cursor position, searching local scopes, local
    /// declarations, imported library declarations via <see cref="PuckSourceTable"/>, and <c>import</c>/<c>basis</c>
    /// targets.</summary>
    public static DefinitionTarget? FindDefinition(
        string source,
        string? sourcePath,
        string uri,
        int line,
        int character,
        DocumentNode? document,
        PuckSourceTable sources,
        IDocumentVocabulary vocabulary,
        Func<string, string> pathToUri,
        Func<string, string?> readSource
    ) {
        if (document is null) {
            return null;
        }

        var offset = LspJson.Offset(character: character, line: line, source: source);
        var sourceDir = (string.IsNullOrWhiteSpace(value: sourcePath) ? null : Path.GetDirectoryName(path: Path.GetFullPath(path: sourcePath)));

        // 1. Cursor inside a `basis: "..."` header or `import "..."` statement path.
        if (
            (document.Basis is { Length: > 0 } basisName) &&
            (sourceDir is not null) &&
            ContainsSpan(span: document.BasisSpan, offset: offset) &&
            (ResolveImportOrBasisFile(directory: sourceDir, name: basisName, vocabulary: vocabulary) is { } basisFile)
        ) {
            return new DefinitionTarget(
                SourceText: readSource(arg: basisFile),
                Span: new SourceSpan(Column: 1, Length: 1, Line: 1, Offset: 0),
                Uri: pathToUri(arg: basisFile)
            );
        }

        var path = SyntaxWalk.PathAt(offset: offset, root: document);

        for (var index = (path.Count - 1); (index >= 0); --index) {
            if (
                (path[index] is ImportNode importNode) &&
                (sourceDir is not null) &&
                !importNode.NamesFile &&
                (ResolveImportOrBasisFile(directory: sourceDir, name: importNode.Path, vocabulary: vocabulary) is { } importedFile)
            ) {
                return new DefinitionTarget(
                    SourceText: readSource(arg: importedFile),
                    Span: new SourceSpan(Column: 1, Length: 1, Line: 1, Offset: 0),
                    Uri: pathToUri(arg: importedFile)
                );
            }
        }

        var wordContext = WordContextAt(targetCol: character, targetLine: line, text: source);
        var word = wordContext.Word;
        var qualified = wordContext.Qualified;

        if ((word is null) && (qualified is null)) {
            return null;
        }

        var imported = CollectImportedDeclarations(
            document: document,
            sourcePath: sourcePath,
            sources: sources,
            vocabulary: vocabulary
        );

        // 2. Lexical scopes walking outward from the cursor.
        if (word is not null) {
            for (var index = (path.Count - 1); (index >= 0); --index) {
                switch (path[index]) {
                    case LambdaExpressionNode lambda when lambda.Parameters.Contains(value: word):
                        return new DefinitionTarget(SourceText: source, Span: lambda.Span, Uri: uri);

                    case TemplateNode template:
                        if (template.Parameters.FirstOrDefault(predicate: item => (item.Name == word)) is { } param) {
                            return new DefinitionTarget(SourceText: source, Span: param.Span, Uri: uri);
                        }
                        if (template.Body.Statements.OfType<LetNode>().FirstOrDefault(predicate: item => (item.Name == word)) is { } templateLet) {
                            return new DefinitionTarget(SourceText: source, Span: templateLet.Span, Uri: uri);
                        }
                        break;

                    case ForStatementNode loop when (((loop.Item == word) || (loop.Index == word)) && !SyntaxWalk.Spans(node: loop.Sequence, offset: offset)):
                        return new DefinitionTarget(SourceText: source, Span: loop.Span, Uri: uri);

                    case ForStatementNode loop:
                        if (loop.Body.OfType<LetNode>().FirstOrDefault(predicate: item => (item.Name == word)) is { } loopLet) {
                            return new DefinitionTarget(SourceText: source, Span: loopLet.Span, Uri: uri);
                        }
                        break;

                    case RepeatStatementNode repeat when ((repeat.Index == word) && !SyntaxWalk.Spans(node: repeat.Count, offset: offset)):
                        return new DefinitionTarget(SourceText: source, Span: repeat.Span, Uri: uri);

                    case RuleBlockNode rule:
                        if (rule.Statements.OfType<LocalStatementNode>().FirstOrDefault(predicate: item => (item.Name == word)) is { } local) {
                            return new DefinitionTarget(SourceText: source, Span: local.Span, Uri: uri);
                        }
                        break;

                    case BlockNode block:
                        if (block.Statements.OfType<LetNode>().FirstOrDefault(predicate: item => (item.Name == word)) is { } blockLet) {
                            return new DefinitionTarget(SourceText: source, Span: blockLet.Span, Uri: uri);
                        }
                        break;

                    case CallExpressionNode call: {
                            var argument = call.Arguments.FirstOrDefault(predicate: item => ((item.Name == word) && SyntaxWalk.Spans(node: item, offset: offset) && (offset < item.Value.Offset)));

                            if (argument is not null) {
                                if (document.Statements.OfType<TemplateNode>().FirstOrDefault(predicate: item => (item.Name == call.Name)) is { } localTemplate &&
                                    localTemplate.Parameters.FirstOrDefault(predicate: item => (item.Name == argument.Name)) is { } localParam) {
                                    return new DefinitionTarget(SourceText: source, Span: localParam.Span, Uri: uri);
                                }
                                if (imported.FirstOrDefault(predicate: item => ((item.Name == call.Name) && (item.Node is TemplateNode))) is { Node: TemplateNode importedTemplate } symbol &&
                                    importedTemplate.Parameters.FirstOrDefault(predicate: item => (item.Name == argument.Name)) is { } importedParam) {
                                    return new DefinitionTarget(
                                        SourceText: readSource(arg: symbol.SourcePath),
                                        Span: importedParam.Span,
                                        Uri: pathToUri(arg: symbol.SourcePath)
                                    );
                                }
                            }
                            break;
                        }
                }
            }
        }

        // 3. Top-level local declarations (`let`, `template`, `import` alias, `enum`, `record`, state rows, named blocks).
        if (word is not null) {
            foreach (var statement in document.Statements) {
                switch (statement) {
                    case LetNode letNode when (letNode.Name == word):
                        return new DefinitionTarget(SourceText: source, Span: letNode.Span, Uri: uri);
                    case TemplateNode templateNode when (templateNode.Name == word):
                        return new DefinitionTarget(SourceText: source, Span: templateNode.Span, Uri: uri);
                    case EnumDeclarationNode enumNode when (enumNode.Name == word):
                        return new DefinitionTarget(SourceText: source, Span: enumNode.Span, Uri: uri);
                    case RecordDeclarationNode recordNode when (recordNode.Name == word):
                        return new DefinitionTarget(SourceText: source, Span: recordNode.Span, Uri: uri);
                    case ImportNode importNode when (importNode.Alias == word):
                        if (
                            (sourceDir is not null) &&
                            !importNode.NamesFile &&
                            (ResolveImportOrBasisFile(directory: sourceDir, name: importNode.Path, vocabulary: vocabulary) is { } aliasTarget)
                        ) {
                            return new DefinitionTarget(
                                SourceText: readSource(arg: aliasTarget),
                                Span: new SourceSpan(Column: 1, Length: 1, Line: 1, Offset: 0),
                                Uri: pathToUri(arg: aliasTarget)
                            );
                        }
                        return new DefinitionTarget(SourceText: source, Span: importNode.Span, Uri: uri);
                }
            }

            if (FindNamedDeclarationInStatements(name: word, statements: document.Statements) is { } localNamed) {
                return new DefinitionTarget(SourceText: source, Span: localNamed.Span, Uri: uri);
            }
        }

        // 4. Imported library declarations (qualified `alias.name` or unaliased `name`).
        var lookupName = (qualified ?? word);

        if (lookupName is not null) {
            var match = imported.FirstOrDefault(predicate: symbol => string.Equals(a: symbol.Name, b: lookupName, comparisonType: StringComparison.Ordinal));

            if (match is not null) {
                return new DefinitionTarget(
                    SourceText: readSource(arg: match.SourcePath),
                    Span: match.Node.Span,
                    Uri: pathToUri(arg: match.SourcePath)
                );
            }
        }

        return null;
    }

    /// <summary>Finds a declared state row or enum directly in the parsed AST without running a compile.</summary>
    public static AstStateRow? FindStateRowInAst(DocumentNode? document, string rowName) {
        if ((document is null) || string.IsNullOrEmpty(value: rowName)) {
            return null;
        }

        return FindStateRowInStatements(rowName: rowName, statements: document.Statements);
    }

    private static AstStateRow? FindStateRowInStatements(IReadOnlyList<StatementNode> statements, string rowName) {
        foreach (var statement in statements) {
            switch (statement) {
                case EnumDeclarationNode enumNode when (enumNode.Name == rowName):
                    return new AstStateRow(
                        Keys: [.. enumNode.Members.Select(selector: static member => member.Name)],
                        Kind: "enum"
                    );
                case StateTableDeclarationNode table when (table.Name == rowName):
                    return new AstStateRow(
                        Keys: [.. table.Cells.Select(selector: static cell => cell.Key)],
                        Kind: (string.IsNullOrEmpty(value: table.Kind) ? InferKind(value: table.Cells.FirstOrDefault()?.Value) : table.Kind)
                    );
                case StateSlotDeclarationNode slot when (slot.Name == rowName):
                    return new AstStateRow(
                        Keys: [],
                        Kind: (string.IsNullOrEmpty(value: slot.Kind) ? InferKind(value: slot.Value) : slot.Kind)
                    );
                case StatePileDeclarationNode pile when (pile.Name == rowName):
                    return new AstStateRow(
                        Keys: [.. pile.Tokens.Select(selector: static token => token.Key)],
                        Kind: "Int"
                    );
                case StateGridDeclarationNode grid when (grid.Name == rowName):
                    return new AstStateRow(
                        Keys: [.. grid.Cells.Select(selector: static cell => cell.Key)],
                        Kind: (string.IsNullOrEmpty(value: grid.Kind) ? InferKind(value: grid.Cells.FirstOrDefault()?.Value) : grid.Kind)
                    );
                case BlockNode { Identifier: "row" } rowBlock: {
                        if (TryExtractRowObject(properties: rowBlock.Statements.OfType<PropertyNode>(), rowName: rowName) is { } extracted) {
                            return extracted;
                        }
                        break;
                    }
                case PropertyNode { Name: "world" or "body" or "identity", Value: ArrayExpressionNode array }: {
                        foreach (var element in array.Elements) {
                            if ((element is ObjectExpressionNode obj) && (TryExtractRowObject(properties: obj.Properties, rowName: rowName) is { } extracted)) {
                                return extracted;
                            }
                        }
                        break;
                    }
                case BlockNode block: {
                        if (FindStateRowInStatements(rowName: rowName, statements: block.Statements) is { } nested) {
                            return nested;
                        }
                        break;
                    }
            }
        }

        return null;
    }

    private static AstStateRow? TryExtractRowObject(IEnumerable<PropertyNode> properties, string rowName) {
        string? name = null;
        string? kind = null;
        var keys = new List<string>();

        foreach (var prop in properties) {
            if ((prop.Name == "name") && (prop.Value is LiteralExpressionNode { Value: string s })) {
                name = s;
            } else if ((prop.Name == "kind") && (prop.Value is IdentifierExpressionNode id)) {
                kind = id.Name;
            } else if ((prop.Name == "kind") && (prop.Value is LiteralExpressionNode { Value: string ks })) {
                kind = ks;
            } else if ((prop.Name == "cells") && (prop.Value is ArrayExpressionNode cells)) {
                ExtractCellKeys(cells: cells, keys: keys);
            }
        }

        return ((name == rowName) ? new AstStateRow(Keys: keys, Kind: (kind ?? "?")) : null);
    }

    private static void ExtractCellKeys(ArrayExpressionNode cells, List<string> keys) {
        foreach (var element in cells.Elements) {
            if (element is ObjectExpressionNode cellObj) {
                foreach (var prop in cellObj.Properties) {
                    if ((prop.Name == "key") && (prop.Value is LiteralExpressionNode { Value: string k })) {
                        keys.Add(item: k);
                    }
                }
            }
        }
    }

    private static string InferKind(ExpressionNode? value) => (value switch {
        LiteralExpressionNode { Value: bool } => "Bool",
        LiteralExpressionNode { Value: string } => "Text",
        LiteralExpressionNode { Value: double or decimal } => "Fixed",
        LiteralExpressionNode { Value: long or int } => "Int",
        _ => "?",
    });

    private static SyntaxNode? FindNamedDeclarationInStatements(IReadOnlyList<StatementNode> statements, string name) {
        foreach (var statement in statements) {
            switch (statement) {
                case StateTableDeclarationNode table when (table.Name == name): return table;
                case StateSlotDeclarationNode slot when (slot.Name == name): return slot;
                case StatePileDeclarationNode pile when (pile.Name == name): return pile;
                case StateGridDeclarationNode grid when (grid.Name == name): return grid;
                case StatePoolDeclarationNode pool when (pool.Name == name): return pool;
                case StatePairPoolDeclarationNode pairPool when (pairPool.Name == name): return pairPool;
                case BlockNode block when ((block.Name == name) && (block.Identifier is "prototype" or "placement" or "camera" or "spawn" or "layout" or "material" or "space")):
                    return block;
                case BlockNode block:
                    if (FindNamedDeclarationInStatements(name: name, statements: block.Statements) is { } nested) {
                        return nested;
                    }
                    break;
            }
        }

        return null;
    }

    private static bool ContainsSpan(SourceSpan span, int offset) => ((span.Length > 0) && (offset >= span.Offset) && (offset < (span.Offset + span.Length)));
}
