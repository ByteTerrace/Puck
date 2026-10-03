using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Puck.Cli.Format;
using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Formats;

/// <summary>A format's boundary was drawn dishonestly: a seam that gives no reason.</summary>
internal sealed class FormatBoundaryException(IReadOnlyList<string> problems) : Exception(message: string.Join(separator: '\n', values: problems)) {
    /// <summary>Every refusal, each naming its fix.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}
/// <summary>What the closure of one format holds.</summary>
/// <param name="Shape">Sixteen lowercase hexadecimal digits of a SHA-256 of the closure's canonical syntax.</param>
/// <param name="Units">Every unit the shape covers.</param>
/// <param name="Open">Every repository member the closure calls that is neither covered nor marked, by documentation-comment
/// id in ordinal order: the calls the shape does not see.</param>
internal sealed record FormatClosure(string Shape, IReadOnlyList<FormatShapeClosure.Unit> Units, IReadOnlyList<string> Open);
/// <summary>
/// The shape of a format: a digest of the canonical syntax of everything its codec's read and write paths cover inside the
/// repository, computed with the Roslyn semantic model over <em>units</em>: a type's layout (its header and its data
/// members: fields, constants, enum members, auto-properties, static constructors and primary-constructor parameters) and
/// each code member (a method, constructor, operator, or property or event with a body) on its own.
/// <para>
/// The boundary is explicit, because a closure over every call reaches the whole engine (a world codec's reaches ten
/// thousand units) and would move every fingerprint on any edit. The roots are every unit of the file that declares the
/// token, of its partial siblings, and each unit anywhere that names the token. A unit covers:
/// </para>
/// <list type="bullet">
/// <item>an enum named anywhere in it, whole, and every constant it reads: the values are the wire;</item>
/// <item>any other repository type it names, one level deep: that type's header and data members, never the types those
/// members name in turn;</item>
/// <item>a repository member it calls that is marked <c>[FormatLeaf]</c>, directly or through its type, with that member's
/// own units in turn, and every override or implementation of a covered virtual or interface member.</item>
/// </list>
/// <para>
/// A call into a repository member that is none of those, nor marked <c>[FormatSeam("its behaviour sets no byte because
/// …")]</c> with a reason, is <em>open</em>: the shape cannot see what it does. <c>puck formats</c> records each format's
/// open calls and refuses a call that is not already recorded, so the boundary only tightens. Platform and package members
/// are outside the repository and outside the digest.
/// </para>
/// <para>
/// Formatting, trivia and local renames never move a digest; operator grouping, argument binding, evaluation order and
/// serialized member names do.
/// </para>
/// </summary>
internal sealed class FormatShapeClosure {
    /// <summary>What a unit holds.</summary>
    internal enum UnitKind {
        /// <summary>A type's header and data members.</summary>
        Layout,
        /// <summary>One member with a body.</summary>
        Code,
    }
    /// <summary>One piece of the closure.</summary>
    /// <param name="Path">The repository-relative path of the declaring file.</param>
    /// <param name="Key">The declaring symbol's documentation-comment id.</param>
    /// <param name="Kind">What the unit holds.</param>
    internal readonly record struct Unit(string Path, string Key, UnitKind Kind);

    private enum Mark {
        None,
        Leaf,
        Seam,
    }
    private sealed record Call(string Callee, Mark Mark, Unit[] Targets, Unit[] Dispatch);
    private sealed class Reached {
        public List<Call> Calls { get; } = [];
        public List<(Unit Unit, bool Expand)> Layouts { get; } = [];
        public SortedSet<string> Refusals { get; } = new(comparer: StringComparer.Ordinal);
    }
    private sealed class FileIndex {
        public Dictionary<(string Key, UnitKind Kind), SyntaxNode> Nodes { get; } = [];
    }
    private sealed class CanonicalFile {
        public Dictionary<(string Key, UnitKind Kind), string> Fragments { get; } = [];
        public required SemanticModel Model { get; init; }
        public required Dictionary<(string Key, UnitKind Kind), SyntaxNode> Nodes { get; init; }
    }

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview);

    private readonly CSharpCompilation m_compilation;
    private readonly IReadOnlyDictionary<string, string> m_files;
    private readonly Dictionary<string, SyntaxTree> m_current;

    private readonly Dictionary<string, FileIndex> m_index = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<SyntaxTree, SemanticModel> m_models = [];
    private readonly Dictionary<string, CanonicalFile> m_canonical = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, HashSet<Unit>>> m_namers = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<Unit, Reached> m_reach = [];

    private readonly Dictionary<string, SyntaxTree> m_trees;

    private CSharpCompilation m_canonicalCompilation;
    private Dictionary<string, List<ISymbol>>? m_implementers;

    /// <summary>Parses every file once.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    public FormatShapeClosure(IReadOnlyDictionary<string, string> files) {
        m_files = files;
        m_trees = files.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: pair => CSharpSyntaxTree.ParseText(
                options: ParseOptions,
                path: pair.Key,
                text: pair.Value
            ),
            keySelector: pair => pair.Key
        );
        m_current = new Dictionary<string, SyntaxTree>(collection: m_trees, comparer: StringComparer.Ordinal);
        m_compilation = CSharpCompilation.Create(
            assemblyName: "FormatShapes",
            options: new CSharpCompilationOptions(
                allowUnsafe: true,
                outputKind: OutputKind.DynamicallyLinkedLibrary
            ),
            references: ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(separator: Path.PathSeparator)
                .Select(selector: path => MetadataReference.CreateFromFile(path: path)),
            syntaxTrees: m_trees.Values.Append(element: CSharpSyntaxTree.ParseText(
                options: ParseOptions,
                text: "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;"
            ))
        );
        m_canonicalCompilation = m_compilation;
    }

    /// <summary>Closes each format over its boundary and digests what the closure covers.</summary>
    /// <param name="formats">Each format's declaring file, declaring type and token member.</param>
    /// <returns>One closure per format, in order.</returns>
    /// <exception cref="FormatBoundaryException">A seam gives no reason.</exception>
    public FormatClosure[] Of(IReadOnlyList<(string Source, string Owner, string Member)> formats) {
        var analyses = formats.Select(selector: format => Analyze(format: format)).ToArray();
        var refusals = analyses.SelectMany(selector: static analysis => analysis.Refusals).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        if (refusals.Length != 0) { throw new FormatBoundaryException(problems: refusals); }

        Canonicalize(paths: analyses.SelectMany(selector: static analysis => analysis.Units).Select(selector: static unit => unit.Path).Distinct(comparer: StringComparer.Ordinal).ToArray());

        return [.. analyses.Select(selector: analysis => new FormatClosure(
            Open: analysis.Open,
            Shape: Digest(units: analysis.Units),
            Units: analysis.Units
        ))];
    }

    private sealed record Analysis(IReadOnlyList<Unit> Units, IReadOnlyList<string> Open, IReadOnlyList<string> Refusals);

    private Analysis Analyze((string Source, string Owner, string Member) format) {
        var seen = new HashSet<Unit>(collection: Roots(format: format));
        var pending = new Queue<Unit>(collection: seen);
        var unfollowed = new List<Call>();
        var refusals = new SortedSet<string>(comparer: StringComparer.Ordinal);

        while (pending.TryDequeue(result: out var unit)) {
            var reach = Reach(unit: unit);

            foreach (var refusal in reach.Refusals) { refusals.Add(item: refusal); }
            foreach (var (layout, expand) in reach.Layouts) {
                if (seen.Add(item: layout) && expand) { pending.Enqueue(item: layout); }
            }
            foreach (var call in reach.Calls) {
                if (call.Mark is Mark.Seam) { continue; }
                if ((call.Mark is Mark.Leaf) || call.Targets.Any(predicate: seen.Contains) || IsPlainAccessor(call: call)) {
                    foreach (var target in call.Targets.Concat(second: call.Dispatch)) {
                        if (seen.Add(item: target)) { pending.Enqueue(item: target); }
                    }
                } else {
                    unfollowed.Add(item: call);
                }
            }
        }

        var open = unfollowed.Where(predicate: call => !call.Targets.Any(predicate: seen.Contains)).Select(selector: static call => call.Callee).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        return new Analysis(
            Open: open,
            Refusals: [.. refusals],
            Units: [.. seen.OrderBy(comparer: StringComparer.Ordinal, keySelector: static unit => unit.Key).ThenBy(keySelector: static unit => unit.Kind).ThenBy(comparer: StringComparer.Ordinal, keySelector: static unit => unit.Path)]
        );
    }
    // A property whose body calls nothing in the repository only reads data: its body is covered, not left open.
    private bool IsPlainAccessor(Call call) => (call.Callee.StartsWith(comparisonType: StringComparison.Ordinal, value: "P:") && call.Targets.All(predicate: target => (Reach(unit: target).Calls.Count == 0)));
    private string Digest(IReadOnlyList<Unit> units) {
        var builder = new StringBuilder();

        foreach (var pair in units
            .Select(selector: unit => (Unit: unit, Fragment: Fragment(unit: unit)))
            .OrderBy(comparer: StringComparer.Ordinal, keySelector: static pair => pair.Unit.Key)
            .ThenBy(keySelector: static pair => pair.Unit.Kind)
            .ThenBy(comparer: StringComparer.Ordinal, keySelector: static pair => pair.Fragment)) {
            builder.Append(value: pair.Unit.Key).Append(value: '\u0002').Append(value: pair.Fragment).Append(value: '\u0001');
        }

        return Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: builder.ToString())))[..16].ToLowerInvariant();
    }
    // The units that start a closure: every unit of the declaring file and its partial siblings, and each unit anywhere
    // that names the token.
    private IEnumerable<Unit> Roots((string Source, string Owner, string Member) format) {
        var directory = format.Source[..(format.Source.LastIndexOf(value: '/') + 1)];
        var name = format.Source[directory.Length..];
        var stem = name[..name.IndexOf(value: '.')];
        var roots = new HashSet<Unit>();

        foreach (var path in m_files.Keys) {
            if (
                (path == format.Source) || (
                    path.StartsWith(comparisonType: StringComparison.Ordinal, value: directory) &&
                    (path.IndexOf(startIndex: directory.Length, value: '/') < 0) &&
                    path[directory.Length..].StartsWith(comparisonType: StringComparison.Ordinal, value: $"{stem}.")
                )
            ) {
                foreach (var (key, kind) in Index(path: path).Nodes.Keys) { roots.Add(item: new Unit(Key: key, Kind: kind, Path: path)); }
            }
        }
        if (TokenKey(format: format) is { } token) {
            foreach (var unit in (Naming(member: format.Member, owner: format.Owner).GetValueOrDefault(key: token) ?? [])) { roots.Add(item: unit); }
        }

        return roots;
    }
    private string? TokenKey((string Source, string Owner, string Member) format) {
        var tree = m_trees[format.Source];
        var model = Model(tree: tree);

        foreach (var declarator in tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(predicate: item => (item.Identifier.ValueText == format.Member))) {
            if (
                (model.GetDeclaredSymbol(declarator) is IFieldSymbol field) &&
                (field.ContainingType.Name == format.Owner)
            ) {
                return KeyOf(symbol: field);
            }
        }

        return null;
    }
    // Every unit that names a field called member in a file that mentions its owner, grouped by the field it resolves to:
    // a use of a type's field is either inside the type, where its name is on the page, or qualified by it.
    private Dictionary<string, HashSet<Unit>> Naming(string member, string owner) {
        if (m_namers.TryGetValue(key: $"{owner}.{member}", value: out var named)) { return named; }

        named = new Dictionary<string, HashSet<Unit>>(comparer: StringComparer.Ordinal);

        foreach (var (path, tree) in m_trees) {
            if (!m_files[path].Contains(comparisonType: StringComparison.Ordinal, value: member) || !m_files[path].Contains(comparisonType: StringComparison.Ordinal, value: owner)) { continue; }

            var model = Model(tree: tree);

            foreach (var name in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(predicate: item => (item.Identifier.ValueText == member))) {
                if (model.GetSymbolInfo(name).Symbol is not IFieldSymbol field) { continue; }

                var key = KeyOf(symbol: field.OriginalDefinition);
                var unit = EnclosingUnit(model: model, node: name, path: path);

                if (unit is null) { continue; }
                if (!named.TryGetValue(key: key, value: out var units)) { named[key] = units = []; }

                units.Add(item: unit.Value);
            }
        }

        m_namers[$"{owner}.{member}"] = named;

        return named;
    }
    private Unit? EnclosingUnit(SemanticModel model, SyntaxNode node, string path) {
        var index = Index(path: path);

        foreach (var ancestor in node.Ancestors()) {
            if ((ancestor is MemberDeclarationSyntax member) && IsCode(member: member) && (model.GetDeclaredSymbol(member) is { } declared) && index.Nodes.ContainsKey(key: (KeyOf(symbol: declared), UnitKind.Code))) {
                return new Unit(Key: KeyOf(symbol: declared), Kind: UnitKind.Code, Path: path);
            }
            if ((ancestor is BaseTypeDeclarationSyntax type) && (model.GetDeclaredSymbol(type) is { } owner) && index.Nodes.ContainsKey(key: (KeyOf(symbol: owner), UnitKind.Layout))) {
                return new Unit(Key: KeyOf(symbol: owner), Kind: UnitKind.Layout, Path: path);
            }
        }

        return null;
    }
    private SemanticModel Model(SyntaxTree tree) {
        if (!m_models.TryGetValue(key: tree, value: out var model)) { m_models[tree] = model = m_compilation.GetSemanticModel(syntaxTree: tree); }

        return model;
    }
    private static string KeyOf(ISymbol symbol) => (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(format: SymbolDisplayFormat.CSharpErrorMessageFormat));
    private static bool IsType(SyntaxNode node) => (node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
    private static bool IsCode(MemberDeclarationSyntax member) => member switch {
        MethodDeclarationSyntax or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax => true,
        ConstructorDeclarationSyntax constructor => !constructor.Modifiers.Any(kind: SyntaxKind.StaticKeyword),
        PropertyDeclarationSyntax { ExpressionBody: not null } => true,
        IndexerDeclarationSyntax { ExpressionBody: not null } => true,
        BasePropertyDeclarationSyntax property => (property.AccessorList?.Accessors.Any(predicate: static accessor => ((accessor.Body is not null) || (accessor.ExpressionBody is not null))) ?? false),
        _ => false,
    };
    // A member of a type that its layout unit leaves to another unit: code, and nested types.
    private static bool LeavesLayout(SyntaxNode node) => (
        (node is MemberDeclarationSyntax member) &&
        (node.Parent is BaseTypeDeclarationSyntax) &&
        (IsType(node: node) || IsCode(member: member))
    );
    private static IEnumerable<SyntaxNode> Region(SyntaxNode root) {
        var pending = new Stack<SyntaxNode>();

        pending.Push(item: root);

        while (pending.TryPop(result: out var node)) {
            yield return node;

            foreach (var child in node.ChildNodes()) {
                if (!LeavesLayout(node: child)) { pending.Push(item: child); }
            }
        }
    }
    private static IEnumerable<(string Key, UnitKind Kind, SyntaxNode Node)> Units(SyntaxNode node, SemanticModel model) {
        switch (node) {
            case CompilationUnitSyntax unit:
                foreach (var member in unit.Members) {
                    foreach (var item in Units(model: model, node: member)) { yield return item; }
                }
                break;
            case BaseNamespaceDeclarationSyntax space:
                foreach (var member in space.Members) {
                    foreach (var item in Units(model: model, node: member)) { yield return item; }
                }
                break;
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                if (model.GetDeclaredSymbol(node) is { } symbol) { yield return (KeyOf(symbol: symbol), UnitKind.Layout, node); }
                if (node is TypeDeclarationSyntax type) {
                    foreach (var member in type.Members) {
                        if (IsType(node: member)) {
                            foreach (var item in Units(model: model, node: member)) { yield return item; }
                        } else if (IsCode(member: member) && (model.GetDeclaredSymbol(member) is { } declared)) {
                            yield return (KeyOf(symbol: declared), UnitKind.Code, member);
                        }
                    }
                }
                break;
        }
    }
    private FileIndex Index(string path) {
        if (m_index.TryGetValue(key: path, value: out var index)) { return index; }

        index = new FileIndex();

        var tree = m_trees[path];

        foreach (var (key, kind, node) in Units(node: tree.GetRoot(), model: Model(tree: tree))) { index.Nodes.TryAdd(key: (key, kind), value: node); }

        m_index[path] = index;

        return index;
    }
    // What one unit's names lead to, whatever format asks.
    private Reached Reach(Unit unit) {
        if (m_reach.TryGetValue(key: unit, value: out var reach)) { return reach; }

        reach = new Reached();
        m_reach[unit] = reach;

        var model = Model(tree: m_trees[unit.Path]);

        foreach (var node in Region(root: Index(path: unit.Path).Nodes[(unit.Key, unit.Kind)])) {
            switch (node) {
                case IdentifierNameSyntax or GenericNameSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                    or ConstructorInitializerSyntax or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or CastExpressionSyntax or ElementAccessExpressionSyntax or AssignmentExpressionSyntax or ForEachStatementSyntax:
                    var info = model.GetSymbolInfo(node);

                    Resolve(symbol: info.Symbol, reach: reach);

                    if (info.Symbol is null) {
                        foreach (var candidate in info.CandidateSymbols) { Resolve(reach: reach, symbol: candidate); }
                    }

                    break;
            }
            if (
                (node is ExpressionSyntax) &&
                (node.Parent is ArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax or ArrowExpressionClauseSyntax or AssignmentExpressionSyntax or CastExpressionSyntax or YieldStatementSyntax or InitializerExpressionSyntax)
            ) {
                var conversion = model.GetConversion(node);

                if (conversion.IsUserDefined) { Resolve(symbol: conversion.MethodSymbol, reach: reach); }
            }
        }

        reach.Calls.RemoveAll(match: call => (Array.IndexOf(array: call.Targets, value: unit) >= 0));

        return reach;
    }
    private void Resolve(ISymbol? symbol, Reached reach) {
        switch (symbol) {
            case null:
                return;
            case IAliasSymbol alias:
                Resolve(symbol: alias.Target, reach: reach);
                return;
            case IArrayTypeSymbol array:
                Resolve(symbol: array.ElementType, reach: reach);
                return;
            case IPointerTypeSymbol pointer:
                Resolve(symbol: pointer.PointedAtType, reach: reach);
                return;
            case INamedTypeSymbol type:
                Layout(type: type.OriginalDefinition, reach: reach);
                return;
            case IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction }:
                return;
            case IMethodSymbol method:
                method = (method.ReducedFrom ?? method);

                if (method.AssociatedSymbol is { } associated) {
                    Resolve(reach: reach, symbol: associated);

                    return;
                }

                Member(symbol: method.OriginalDefinition, reach: reach);
                return;
            case IPropertySymbol or IEventSymbol or IFieldSymbol:
                Member(symbol: symbol.OriginalDefinition, reach: reach);
                return;
        }
    }
    private void Layout(INamedTypeSymbol type, Reached reach) {
        var key = KeyOf(symbol: type);
        var expand = ((type.TypeKind is TypeKind.Enum) || (MarkOf(reach: reach, symbol: type) is Mark.Leaf));

        foreach (var path in Paths(symbol: type)) {
            if (Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Layout))) { reach.Layouts.Add(item: (new Unit(Key: key, Kind: UnitKind.Layout, Path: path), expand)); }
        }
    }
    private void Member(ISymbol symbol, Reached reach) {
        var key = KeyOf(symbol: symbol);
        var targets = Paths(symbol: symbol).Where(predicate: path => Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Code))).Select(selector: path => new Unit(Key: key, Kind: UnitKind.Code, Path: path)).ToArray();

        if (targets.Length == 0) {
            if (symbol.ContainingType is { } owner) { Layout(type: owner.OriginalDefinition, reach: reach); }

            return;
        }

        var dispatch = new List<Unit>();

        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride || ((symbol.ContainingType?.TypeKind == TypeKind.Interface) && !symbol.IsStatic)) {
            if (Implementers().TryGetValue(key: key, value: out var implementers)) {
                foreach (var implementer in implementers) {
                    var implementerKey = KeyOf(symbol: implementer.OriginalDefinition);

                    foreach (var path in Paths(symbol: implementer.OriginalDefinition)) {
                        if (Index(path: path).Nodes.ContainsKey(key: (implementerKey, UnitKind.Code))) { dispatch.Add(item: new Unit(Key: implementerKey, Kind: UnitKind.Code, Path: path)); }
                    }
                }
            }
        }

        reach.Calls.Add(item: new Call(
            Callee: key,
            Dispatch: [.. dispatch],
            Mark: MarkOf(reach: reach, symbol: symbol),
            Targets: targets
        ));
    }
    // The repository files that declare a symbol.
    private IEnumerable<string> Paths(ISymbol symbol) => symbol.DeclaringSyntaxReferences.Select(selector: static reference => reference.SyntaxTree.FilePath).Where(predicate: path => m_trees.ContainsKey(key: path)).Distinct(comparer: StringComparer.Ordinal);
    // [FormatLeaf] and [FormatSeam] are read from the declarations' syntax, on the member or any type that holds it.
    private static Mark MarkOf(ISymbol symbol, Reached reach) {
        var mark = Mark.None;

        for (var current = symbol; (current is not null); current = current.ContainingType) {
            foreach (var reference in current.DeclaringSyntaxReferences) {
                if (reference.GetSyntax() is not MemberDeclarationSyntax declaration) { continue; }

                foreach (var attribute in declaration.AttributeLists.SelectMany(selector: static list => list.Attributes)) {
                    var name = ((attribute.Name is QualifiedNameSyntax qualified) ? qualified.Right : attribute.Name) switch {
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                        _ => string.Empty,
                    };

                    if (name is "FormatLeaf" or "FormatLeafAttribute") {
                        mark = ((mark is Mark.Seam) ? mark : Mark.Leaf);
                    } else if (name is "FormatSeam" or "FormatSeamAttribute") {
                        if ((attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { RawKind: ((int)SyntaxKind.StringLiteralExpression) } reason) || string.IsNullOrWhiteSpace(value: reason.Token.ValueText)) {
                            reach.Refusals.Add(item: $"seam without a reason: {KeyOf(symbol: current)} is marked [FormatSeam] with no reason; say why its behaviour sets no byte, as [FormatSeam(\"its behaviour sets no byte because …\")], or remove the mark");
                        }

                        mark = Mark.Seam;
                    }
                }
            }
        }

        return mark;
    }
    // Each virtual or interface member's overrides and implementations the repository declares, by the member's key.
    private Dictionary<string, List<ISymbol>> Implementers() {
        if (m_implementers is not null) { return m_implementers; }

        var index = new Dictionary<string, List<ISymbol>>(comparer: StringComparer.Ordinal);
        var types = new Stack<INamespaceOrTypeSymbol>();

        types.Push(item: m_compilation.Assembly.GlobalNamespace);

        while (types.TryPop(result: out var next)) {
            foreach (var member in next.GetMembers()) {
                if (member is not INamespaceOrTypeSymbol inner) { continue; }

                types.Push(item: inner);

                if ((inner is not INamedTypeSymbol type) || (type.TypeKind is TypeKind.Interface)) { continue; }

                foreach (var own in type.GetMembers().Where(predicate: static item => (item is IMethodSymbol or IPropertySymbol or IEventSymbol))) {
                    if (own.IsOverride) {
                        for (var basis = Overridden(symbol: own); (basis is not null); basis = Overridden(symbol: basis)) { Add(key: KeyOf(symbol: basis.OriginalDefinition), implementer: own); }
                    }
                }
                foreach (var contract in type.AllInterfaces.Where(predicate: static item => (item.DeclaringSyntaxReferences.Length > 0))) {
                    foreach (var slot in contract.GetMembers().Where(predicate: static item => ((item is IMethodSymbol or IPropertySymbol or IEventSymbol) && !item.IsStatic))) {
                        if ((type.FindImplementationForInterfaceMember(interfaceMember: slot) is { } implementation) && (implementation.DeclaringSyntaxReferences.Length > 0)) {
                            Add(key: KeyOf(symbol: slot.OriginalDefinition), implementer: implementation);
                        }
                    }
                }
            }
        }

        m_implementers = index;

        return index;

        void Add(string key, ISymbol implementer) {
            if (!index.TryGetValue(key: key, value: out var list)) { index[key] = list = []; }

            list.Add(item: implementer);
        }
        static ISymbol? Overridden(ISymbol symbol) => symbol switch {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null,
        };
    }
    // Canonical text of each file, parsed into one compilation so every unit's local names and argument bindings resolve.
    private void Canonicalize(IReadOnlyList<string> paths) {
        var fresh = paths.Where(predicate: path => !m_canonical.ContainsKey(key: path)).ToArray();

        if (fresh.Length == 0) { return; }

        var replacements = new Dictionary<string, SyntaxTree>(comparer: StringComparer.Ordinal);

        var models = fresh.ToDictionary(comparer: StringComparer.Ordinal, elementSelector: path => Model(tree: m_trees[path]), keySelector: static path => path);
        var texts = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(comparer: StringComparer.Ordinal);

        Parallel.ForEach(source: fresh, body: path => texts[path] = CanonicalText(model: models[path], tree: m_trees[path]));

        foreach (var path in fresh) {
            replacements[path] = CSharpSyntaxTree.ParseText(
                options: ParseOptions,
                path: path,
                text: texts[path]
            );
        }

        m_canonicalCompilation = m_canonicalCompilation
            .RemoveSyntaxTrees(trees: fresh.Select(selector: path => m_current[path]))
            .AddSyntaxTrees(trees: replacements.Values);

        foreach (var (path, tree) in replacements) {
            var model = m_canonicalCompilation.GetSemanticModel(syntaxTree: tree);
            var nodes = new Dictionary<(string Key, UnitKind Kind), SyntaxNode>();

            foreach (var (key, kind, node) in Units(node: tree.GetRoot(), model: model)) { nodes.TryAdd(key: (key, kind), value: node); }

            m_current[path] = tree;
            m_canonical[path] = new CanonicalFile { Model = model, Nodes = nodes };
        }
    }
    private static string CanonicalText(SemanticModel model, SyntaxTree tree) {
        var root = new NullPatternRewriter(model: model).Visit(node: tree.GetRoot())!;

        // Run the existing syntactic normalizers on a trivia-free copy. A comment cannot decide whether a
        // declaration or initializer is sorted in the fingerprint.
        root = root.ReplaceTokens(root.DescendantTokens(), static (token, _) => token.WithoutTrivia()).NormalizeWhitespace();
        foreach (var pass in FormatPasses.All.Where(predicate: pass => (pass.Default && pass.Syntactic))) {
            root = pass.Apply(node: root);
            root = CSharpSyntaxTree.ParseText(root.ToFullString(), ParseOptions).GetRoot();
        }

        return root.ToFullString();
    }
    private string Fragment(Unit unit) {
        var file = m_canonical[unit.Path];

        if (file.Fragments.TryGetValue(key: (unit.Key, unit.Kind), value: out var fragment)) { return fragment; }
        if (!file.Nodes.TryGetValue(key: (unit.Key, unit.Kind), value: out var node)) {
            // A canonical form that no longer declares the symbol (a pass rewrote its signature) still has to move the digest.
            return $"unbound:{unit.Key}";
        }

        var names = new Dictionary<ISymbol, string>(comparer: SymbolEqualityComparer.Default);

        foreach (var inner in Region(root: node)) {
            if ((inner is ParameterSyntax) && (inner.Parent?.Parent is RecordDeclarationSyntax)) { continue; }
            if ((inner is ParameterSyntax or VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax or CatchDeclarationSyntax)
                && (file.Model.GetDeclaredSymbol(inner) is ILocalSymbol or IParameterSymbol)) {
                var symbol = file.Model.GetDeclaredSymbol(inner)!;

                names.TryAdd(key: symbol, value: $"local{names.Count.ToString(provider: CultureInfo.InvariantCulture)}");
            }
        }

        var builder = new StringBuilder();

        AppendShape(node, builder, file.Model, names);
        fragment = builder.ToString();
        file.Fragments[(unit.Key, unit.Kind)] = fragment;

        return fragment;
    }
    private static void AppendShape(SyntaxNode node, StringBuilder builder, SemanticModel model, IReadOnlyDictionary<ISymbol, string> names) {
        if ((node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            && (model.GetConstantValue(node) is { HasValue: true, Value: string nameOf })) {
            builder.Append(value: "nameof:").Append(value: nameOf.Length.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
            AppendText(builder: builder, text: nameOf);
            return;
        }
        if (node is ParenthesizedExpressionSyntax parentheses) {
            AppendShape(parentheses.Expression, builder, model, names);
            return;
        }
        builder.Append(value: node.RawKind.ToString(provider: CultureInfo.InvariantCulture)).Append(value: '{');
        if ((node is ArgumentListSyntax list) && (ArgumentBindings(list: list, model: model) is { } bindings)) {
            var arguments = bindings.AsEnumerable();

            if (!arguments.Any(predicate: binding => ExpressionSafety.HasSideEffect(expression: binding.Argument.Expression, model: model))) {
                arguments = arguments.OrderBy(keySelector: binding => binding.Ordinal);
            }
            foreach (var (argument, ordinal) in arguments) {
                builder.Append(value: ordinal.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
                builder.Append(value: argument.RefKindKeyword.ValueText).Append(value: ':');
                AppendShape(argument.Expression, builder, model, names);
            }
        } else {
            foreach (var child in node.ChildNodesAndTokens()) {
                if (child.IsNode) {
                    if (!LeavesLayout(node: child.AsNode()!)) { AppendShape(child.AsNode()!, builder, model, names); }
                } else {
                    var token = child.AsToken();

                    if (token.IsKind(kind: SyntaxKind.CommaToken)) { continue; }
                    var text = token.ValueText;

                    if (token.IsKind(kind: SyntaxKind.IdentifierToken)) {
                        var symbol = (model.GetDeclaredSymbol(node) ?? model.GetSymbolInfo(node).Symbol);

                        if ((symbol is not null) && names.TryGetValue(key: symbol, value: out var name)) { text = name; }
                    }
                    builder.Append(value: token.RawKind.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':')
                        .Append(value: text.Length.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ':');
                    AppendText(builder: builder, text: text);
                    builder.Append(value: ';');
                }
            }
        }
        builder.Append(value: '}');
    }
    private static void AppendText(string text, StringBuilder builder) {
        foreach (var character in text) { builder.Append(value: ((int)character).ToString(format: "x4", provider: CultureInfo.InvariantCulture)); }
    }
    private static IReadOnlyList<(ArgumentSyntax Argument, int Ordinal)>? ArgumentBindings(ArgumentListSyntax list, SemanticModel model) {
        if (model.GetSymbolInfo(list.Parent!).Symbol is not IMethodSymbol method) { return null; }

        var bindings = new List<(ArgumentSyntax, int)>();

        for (var index = 0; (index < list.Arguments.Count); index++) {
            var argument = list.Arguments[index];
            var parameter = ((argument.NameColon is { } name)
                ? method.Parameters.FirstOrDefault(predicate: parameter => (parameter.Name == name.Name.Identifier.ValueText))
                : method.Parameters.ElementAtOrDefault(index: index));

            if ((parameter is null) || parameter.IsParams) { return null; }

            bindings.Add(item: (argument, parameter.Ordinal));
        }

        return bindings;
    }
}
