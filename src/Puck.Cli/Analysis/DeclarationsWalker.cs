using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Source;

namespace Puck.Cli.Analysis;

// The syntax tier of the analysis verbs: what a parsed file declares, read straight off the tree with no
// compilation, no project load and no restore. It therefore sees every .cs file on disk — including the ones
// no project compiles, which the semantic tier is blind to — and in exchange it knows names, not symbols.
internal static class DeclarationsWalker {
    public static readonly IReadOnlySet<string> TypeKinds =
        new HashSet<string>(collection: ["class", "delegate", "enum", "interface", "record", "struct"], comparer: StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> MemberKinds =
        new HashSet<string>(collection: ["ctor", "event", "field", "method", "property"], comparer: StringComparer.Ordinal);

    // Every record the options select, ordered by (path, line, column, relation, name) — a total order,
    // so two runs over an unchanged tree emit byte-identical bytes.
    public static List<AnalysisRecord> Collect(SourceCorpus corpus, DeclarationsOptions options) {
        var records = new List<AnalysisRecord>();

        foreach (var file in corpus.Files) {
            foreach (var node in file.Root.DescendantNodes()) {
                CollectDeclarations(file: file, node: node, options: options, records: records);
            }

            if (options.Doc) {
                CollectCrefs(file: file, options: options, records: records);
            }
        }

        records.Sort(comparison: Compare);

        return records;
    }

    private static int Compare(AnalysisRecord left, AnalysisRecord right) {
        var byPath = string.CompareOrdinal(strA: left.Path, strB: right.Path);

        if (byPath != 0) {
            return byPath;
        }

        if (left.Line != right.Line) {
            return left.Line.CompareTo(value: right.Line);
        }

        if (left.Column != right.Column) {
            return left.Column.CompareTo(value: right.Column);
        }

        var byRelation = string.CompareOrdinal(strA: left.Relation, strB: right.Relation);

        return ((byRelation != 0) ? byRelation : string.CompareOrdinal(strA: left.Name, strB: right.Name));
    }
    private static void CollectDeclarations(ParsedFile file, SyntaxNode node, DeclarationsOptions options, List<AnalysisRecord> records) {
        switch (node) {
            // An extension block is a type declaration that names nothing, so there is no declaration to
            // report; its members are attributed to the enclosing static class (see QualifiedName).
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax when (Identifier(node: node).Text.Length == 0):
                break;
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax when options.WantTypes:
                Add(file: file, node: node, kind: TypeKind(node: node), identifier: Identifier(node: node), simpleName: SimpleName(node: node), options: options, records: records);

                break;
            case BaseFieldDeclarationSyntax field when options.WantMembers:
                // One record per declarator: `int a, b;` declares two fields, and the attributes and any
                // filter apply to the whole declaration.
                foreach (var declarator in field.Declaration.Variables) {
                    Add(
                        file: file,
                        node: field,
                        kind: ((field is EventFieldDeclarationSyntax) ? "event" : "field"),
                        identifier: declarator.Identifier,
                        simpleName: declarator.Identifier.Text,
                        options: options,
                        records: records);
                }

                break;
            case BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax when options.WantMembers:
                Add(file: file, node: node, kind: MemberKind(node: node), identifier: Identifier(node: node), simpleName: SimpleName(node: node), options: options, records: records);

                break;
            default:
                break;
        }
    }
    // `<see cref="..."/>` and `<seealso cref="..."/>` targets. Documentation lives in trivia, so the walk
    // has to descend into it — a tree walk that does not will call a doc-referenced symbol unmentioned.
    private static void CollectCrefs(ParsedFile file, DeclarationsOptions options, List<AnalysisRecord> records) {
        foreach (var attribute in file.Root.DescendantNodes(descendIntoTrivia: true).OfType<XmlCrefAttributeSyntax>()) {
            var target = Flatten(node: attribute.Cref);

            if ((options.Name is { } fragment) && !target.Contains(comparisonType: StringComparison.Ordinal, value: fragment)) {
                continue;
            }

            var position = attribute.Cref.GetLocation().GetLineSpan().StartLinePosition;

            records.Add(
                item: new AnalysisRecord(
                    Path: file.Relative,
                    Line: (position.Line + 1),
                    Column: (position.Character + 1),
                    Relation: "cref",
                    Kind: string.Empty,
                    Name: target,
                    Detail: null));
        }
    }
    private static void Add(
        ParsedFile file,
        SyntaxNode node,
        string kind,
        SyntaxToken identifier,
        string simpleName,
        DeclarationsOptions options,
        List<AnalysisRecord> records
    ) {
        if ((options.Kinds.Count > 0) && !options.Kinds.Contains(item: kind)) {
            return;
        }

        if ((options.Name is { } nameFragment) && !simpleName.Contains(comparisonType: StringComparison.Ordinal, value: nameFragment)) {
            return;
        }

        var bases = Bases(node: node);

        if ((options.Base is { } baseFragment) && ((bases is null) || !bases.Contains(comparisonType: StringComparison.Ordinal, value: baseFragment))) {
            return;
        }

        if ((options.Attribute is { } attributeFragment) && !HasAttribute(fragment: attributeFragment, node: node)) {
            return;
        }

        var position = identifier.GetLocation().GetLineSpan().StartLinePosition;

        records.Add(
            item: new AnalysisRecord(
                Path: file.Relative,
                Line: (position.Line + 1),
                Column: (position.Character + 1),
                Relation: "decl",
                Kind: kind,
                Name: QualifiedName(node: node, simpleName: (simpleName + Signature(node: node))),
                Detail: bases));
    }
    // The base list a type declares, each entry flattened and joined canonically — the emitter supplies
    // the leading colon. Null when the declaration has no base list.
    private static string? Bases(SyntaxNode node) =>
        (((node as BaseTypeDeclarationSyntax)?.BaseList is { } list)
            ? string.Join(separator: ", ", values: list.Types.Select(selector: static type => Flatten(node: type)))
            : null);
    // A record is one line; the syntax it renders (a base list, parameter list, or cref) may span several
    // source lines with comments between tokens. Flatten renders tokens only, collapsing any original
    // separation to exactly one space, so `--name`/`--base` filtering matches what a record actually prints.
    private static string Flatten(SyntaxNode node) {
        var builder = new StringBuilder();
        var tokens = node.DescendantTokens().ToList();

        for (var index = 0; (index < tokens.Count); index++) {
            if ((index > 0) && Separates(left: tokens[(index - 1)].TrailingTrivia, right: tokens[index].LeadingTrivia)) {
                _ = builder.Append(value: ' ');
            }

            _ = builder.Append(value: tokens[index].Text);
        }

        return string.Join(separator: ' ', values: builder.ToString().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: default(char[])));
    }
    // Whether the trivia between two tokens stands for a space. An empty gap does not. Neither does one
    // holding the `///` exterior of a documentation comment: that marker opens a CONTINUED line, so a cref
    // broken across two of them names one dotted path, not two words.
    private static bool Separates(SyntaxTriviaList left, SyntaxTriviaList right) =>
        (((left.Count > 0) || (right.Count > 0)) && !Continues(trivia: left) && !Continues(trivia: right));
    private static bool Continues(SyntaxTriviaList trivia) =>
        trivia.Any(predicate: static item => item.IsKind(kind: SyntaxKind.DocumentationCommentExteriorTrivia));
    private static bool HasAttribute(SyntaxNode node, string fragment) {
        if (node is not MemberDeclarationSyntax member) {
            return false;
        }

        foreach (var list in member.AttributeLists) {
            foreach (var attribute in list.Attributes) {
                if (attribute.Name.ToString().Contains(comparisonType: StringComparison.Ordinal, value: fragment)) {
                    return true;
                }
            }
        }

        return false;
    }
    private static string TypeKind(SyntaxNode node) =>
        node switch {
            RecordDeclarationSyntax => "record",
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            EnumDeclarationSyntax => "enum",
            _ => "delegate",
        };
    private static string MemberKind(SyntaxNode node) =>
        node switch {
            ConstructorDeclarationSyntax => "ctor",
            EventDeclarationSyntax => "event",
            PropertyDeclarationSyntax or IndexerDeclarationSyntax => "property",
            _ => "method",
        };
    // The declared simple name, spelled the way the declaration does: an operator keeps its keyword, an
    // indexer is `this`, a finalizer keeps its tilde.
    private static string SimpleName(SyntaxNode node) =>
        node switch {
            OperatorDeclarationSyntax op => $"operator {op.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax conversion => $"operator {Flatten(node: conversion.Type)}",
            IndexerDeclarationSyntax => "this",
            DestructorDeclarationSyntax destructor => $"~{destructor.Identifier.Text}",
            _ => Identifier(node: node).Text,
        };
    // The token a record's line and column point at — the name, not the modifiers or the doc comment.
    private static SyntaxToken Identifier(SyntaxNode node) =>
        node switch {
            BaseTypeDeclarationSyntax type => type.Identifier,
            DelegateDeclarationSyntax d => d.Identifier,
            MethodDeclarationSyntax method => method.Identifier,
            ConstructorDeclarationSyntax constructor => constructor.Identifier,
            DestructorDeclarationSyntax destructor => destructor.Identifier,
            OperatorDeclarationSyntax op => op.OperatorToken,
            ConversionOperatorDeclarationSyntax conversion => conversion.OperatorKeyword,
            PropertyDeclarationSyntax property => property.Identifier,
            EventDeclarationSyntax @event => @event.Identifier,
            IndexerDeclarationSyntax indexer => indexer.ThisKeyword,
            _ => node.GetFirstToken(),
        };
    // Type parameters and, for the callable forms, the parameter types as written — enough to tell two
    // overloads apart without a semantic model.
    private static string Signature(SyntaxNode node) {
        var typeParameters = node switch {
            TypeDeclarationSyntax type => TypeParameters(list: type.TypeParameterList),
            DelegateDeclarationSyntax d => TypeParameters(list: d.TypeParameterList),
            MethodDeclarationSyntax method => TypeParameters(list: method.TypeParameterList),
            _ => null,
        };

        var parameters = node switch {
            BaseMethodDeclarationSyntax callable => callable.ParameterList,
            DelegateDeclarationSyntax d => d.ParameterList,
            IndexerDeclarationSyntax indexer => (indexer.ParameterList as BaseParameterListSyntax),
            _ => null,
        };

        var rendered = ((parameters is null)
            ? string.Empty
            : $"({string.Join(separator: ", ", values: parameters.Parameters.Select(selector: static parameter => ((parameter.Type is { } type) ? Flatten(node: type) : string.Empty)))})");

        return ((typeParameters ?? string.Empty) + rendered);
    }
    // `<T, U>` as written, flattened. Null when the declaration takes no type parameters.
    private static string? TypeParameters(TypeParameterListSyntax? list) =>
        ((list is null) ? null : Flatten(node: list));
    // The declaration's dotted path: enclosing namespaces, then enclosing types, then the name itself.
    private static string QualifiedName(SyntaxNode node, string simpleName) {
        var parts = new List<string> { simpleName };

        for (var current = node.Parent; (current is not null); current = current.Parent) {
            switch (current) {
                // A nameless enclosing type — an extension block — contributes no segment, so its members
                // read as members of the static class holding it rather than picking up an empty one.
                case TypeDeclarationSyntax { Identifier.Text.Length: 0 }:
                    break;
                case TypeDeclarationSyntax type:
                    parts.Add(item: (type.Identifier.Text + (TypeParameters(list: type.TypeParameterList) ?? string.Empty)));

                    break;
                case BaseNamespaceDeclarationSyntax @namespace:
                    parts.Add(item: Flatten(node: @namespace.Name));

                    break;
                default:
                    break;
            }
        }

        parts.Reverse();

        return string.Join(separator: '.', values: parts);
    }
}
