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
    /// <param name="SourcePath">The full path of the library file that defines <paramref name="Node"/>.</param>
    /// <param name="Node">The defining <see cref="LetNode"/> or <see cref="TemplateNode"/>, carrying its span in
    /// <paramref name="SourcePath"/>.</param>
    internal sealed record ImportedSymbol(string Name, string SourcePath, StatementNode Node);

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

    /// <summary>Collects every <c>let</c> and <c>template</c> declaration <paramref name="document"/>'s library
    /// <c>import</c>s hand it, under the names its compile gives them
    /// (<see cref="ModuleResolver.ImportedDeclarations"/>).</summary>
    public static IReadOnlyList<ImportedSymbol> CollectImportedDeclarations(
        DocumentNode? document,
        string? sourcePath,
        PuckSourceTable sources,
        IDocumentVocabulary vocabulary
    ) {
        if ((document is null) || string.IsNullOrWhiteSpace(value: sourcePath)) {
            return [];
        }

        return [.. ModuleResolver.ImportedDeclarations(rootDoc: document, rootPath: sourcePath, sources: sources, vocabulary: vocabulary).Select(selector: static statement => (statement switch {
            LetNode constant => new ImportedSymbol(Name: constant.Name, Node: constant, SourcePath: constant.DefinitionPath!),
            var declaration => new ImportedSymbol(Name: ((TemplateNode)declaration).Name, Node: declaration, SourcePath: ((TemplateNode)declaration).DefinitionPath!),
        }))];
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

        // 2. A name some enclosing node binds, or the parameter a call's named argument fills.
        if ((word is not null) && (ResolveLexical(document: document, imported: imported, offset: offset, path: path, word: word) is { } lexical)) {
            return ((lexical.Origin is { } origin)
                ? new DefinitionTarget(SourceText: readSource(arg: origin.SourcePath), Span: lexical.Binder.Declaration.Span, Uri: pathToUri(arg: origin.SourcePath))
                : new DefinitionTarget(SourceText: source, Span: lexical.Binder.Declaration.Span, Uri: uri)
            );
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

    /// <summary>Resolves a bare word read at the innermost position of <paramref name="path"/> to the declaration
    /// inside the tree it names: a name an enclosing node binds (<see cref="SyntaxScopes.Resolve"/>), or, on a call's
    /// named argument, the called template's parameter. Hover and go-to-definition both answer from it, so they agree
    /// with each other and with what lowering binds.</summary>
    /// <param name="document">The parsed document.</param>
    /// <param name="path">The nodes the position stands in, outermost first.</param>
    /// <param name="word">The word under the cursor.</param>
    /// <param name="offset">The 0-based source offset of the cursor.</param>
    /// <param name="imported">The declarations the document's library imports hand it.</param>
    /// <returns>The binder, with the imported template that declares it when it is another file's; otherwise
    /// <see langword="null"/>.</returns>
    internal static (SyntaxBinder Binder, ImportedSymbol? Origin)? ResolveLexical(
        DocumentNode document,
        IReadOnlyList<SyntaxNode> path,
        string word,
        int offset,
        IReadOnlyList<ImportedSymbol>? imported
    ) {
        if ((path.Count >= 2) && (path[^1] is ArgumentNode argument) && (argument.Name == word) && (offset < argument.Value.Offset) && (path[^2] is CallExpressionNode call)) {
            var origin = ((document.Statements.OfType<TemplateNode>().Any(predicate: item => (item.Name == call.Name)))
                ? null
                : imported?.FirstOrDefault(predicate: item => ((item.Name == call.Name) && (item.Node is TemplateNode))));
            var called = (((TemplateNode?)origin?.Node) ?? document.Statements.OfType<TemplateNode>().FirstOrDefault(predicate: item => (item.Name == call.Name)));

            if (called?.Parameters.FirstOrDefault(predicate: item => (item.Name == word)) is { } formal) {
                return (new SyntaxBinder(Declaration: formal, Kind: SyntaxBinderKind.Parameter, Name: word), origin);
            }
        }
        return ((SyntaxScopes.Resolve(name: word, path: path) is { } binder) ? (binder, null) : null);
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
