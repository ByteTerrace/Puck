using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Puck.Cli.Format;
using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Formats;

/// <summary>A format's boundary was drawn dishonestly: a seam that gives no reason.</summary>
public sealed class FormatBoundaryException(IReadOnlyList<string> problems) : Exception(message: string.Join(separator: '\n', values: problems)) {
    /// <summary>Every refusal, each naming its fix.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}
/// <summary>A format as a closure sees it.</summary>
/// <param name="Source">The declaring file.</param>
/// <param name="Owner">The declaring type's name.</param>
/// <param name="Member">The token member's name.</param>
/// <param name="Id">The ledger id, with the <c>@path</c> that tells two files' identical <c>Type.Member</c> apart.</param>
public readonly record struct FormatRef(string Source, string Owner, string Member, string Id);
/// <summary>What the closure of one format holds.</summary>
/// <param name="Shape">Sixteen lowercase hexadecimal digits of a SHA-256 of the closure's canonical syntax.</param>
/// <param name="Units">Every unit the shape covers.</param>
/// <param name="Open">Every repository member the closure calls that is neither covered nor marked, by documentation-comment
/// id in ordinal order: the calls the shape does not see.</param>
/// <param name="Outside">Every name the closure's units write that binds to nothing and names no repository type, in
/// ordinal order: a package's type or member, which the closure compiles without by design.</param>
public sealed record FormatClosure(string Shape, IReadOnlyList<FormatShapeClosure.Unit> Units, IReadOnlyList<string> Open, IReadOnlyList<string> Outside);
/// <summary>The names one format's closure writes that bind to nothing.</summary>
/// <param name="Repository">Each name of a type the repository declares, which the closure failed to see, in ordinal
/// order. <see cref="FormatShapeClosure.Of"/> refuses a closure that has one.</param>
/// <param name="Outside">Each other name, a package's, in ordinal order.</param>
public sealed record FormatUnbound(IReadOnlyList<string> Repository, IReadOnlyList<string> Outside);
/// <summary>
/// The shape of a format: a digest of the canonical syntax of everything its codec's read and write paths cover inside the
/// repository, computed with the Roslyn semantic model over <em>units</em>: a type's layout (its header and its data
/// members: fields, constants, enum members, auto-properties, static constructors and primary-constructor parameters) and
/// each code member (a method, constructor, operator, or property or event with a body) on its own.
/// <para>
/// The boundary is explicit, because a closure over every call reaches the whole engine (a world codec's reaches ten
/// thousand units) and would move every fingerprint on any edit. The roots are where encoding is: the layouts of the file that
/// declares the token and of its partial siblings, those files' members that touch bytes (a byte buffer, stream or binary
/// reader or writer, a <c>u8</c> literal, a <c>[FormatLeaf]</c> member), and each unit anywhere that names the token. The
/// rest of those files is neighbouring code, reached only when a covered member calls it: a method that drives the engine
/// from decoded data is not the format's shape. A unit covers:
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
/// open calls, so one that joins or leaves is a reviewable ledger diff and <c>--check</c> reports it as drift. Platform and
/// package members are outside the repository and outside the digest.
/// </para>
/// <para>
/// The closure compiles the repository's sources against the shared framework alone, never against the assemblies the
/// computing process happens to load, so a shape is a function of the source: the puck tool, a test host and another
/// machine on the same framework close the same source to the same shape. A call into a package member binds to nothing,
/// so its arguments are digested in the order they are written.
/// </para>
/// <para>
/// Each file compiles with the usings its own project compiles it with (<see cref="FormatShapeSources"/>): the project's
/// <c>Using</c> items and implicit usings, and the <c>global using</c> directives the project's files state, written into
/// each of that project's files as file-scoped directives, so one project's usings never reach another's files in the
/// one compilation. The files projects link in from outside <c>src/</c>, and a <c>FormatShapes</c> class for each
/// namespace that declares a format, bind names and are never units. A name a unit writes that still binds to nothing
/// while a repository type of that name exists is refused: the shape would be blind to that type.
/// </para>
/// <para>
/// Formatting, trivia and local renames never move a digest; operator grouping, argument binding, evaluation order and
/// serialized member names do.
/// </para>
/// </summary>
public sealed class FormatShapeClosure {
    /// <summary>What a unit holds.</summary>
    public enum UnitKind {
        /// <summary>A type's header and data members.</summary>
        Layout,
        /// <summary>One member with a body.</summary>
        Code,
    }
    /// <summary>One piece of the closure.</summary>
    /// <param name="Path">The repository-relative path of the declaring file.</param>
    /// <param name="Key">The declaring symbol's documentation-comment id.</param>
    /// <param name="Kind">What the unit holds.</param>
    public readonly record struct Unit(string Path, string Key, UnitKind Kind);

    private enum Mark {
        None,
        Leaf,
        Seam,
    }
    private sealed record Call(string Callee, Mark Mark, Unit[] Targets, Unit[] Dispatch);
    private sealed class Reached {
        public List<Call> Calls { get; } = [];
        public List<(Unit Unit, bool Expand)> Layouts { get; } = [];
        // The layouts a layout unit's own headers and member types name: followed only when the unit is expanded, which
        // is the one-level depth limit. A layout's initializers are never deep.
        public List<(Unit Unit, bool Expand)> DeepLayouts { get; } = [];

        public bool Deep { get; set; }
        // Whether the unit touches bytes: a wire reader or writer, a byte buffer, a [FormatLeaf] member.
        public bool Encodes { get; set; }

        public SortedSet<string> Refusals { get; } = new(comparer: StringComparer.Ordinal);
        // The names the unit writes that bind to nothing, as written: a type name, or an attribute's name.
        public SortedSet<string> Unbound { get; } = new(comparer: StringComparer.Ordinal);
    }
    // Drops every token's trivia in one walk of the tree. ReplaceTokens over every token is quadratic in the file: it tests
    // each node's span against the span of every token it replaces.
    private sealed class TriviaStripper : CSharpSyntaxRewriter {
        public override SyntaxToken VisitToken(SyntaxToken token) => token.WithoutTrivia();
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
    private readonly FormatCompileContext m_context;
    private readonly IReadOnlyDictionary<string, string> m_files;
    private readonly Dictionary<string, SyntaxTree> m_current;

    private readonly ConcurrentDictionary<string, FileIndex> m_index = new(comparer: StringComparer.Ordinal);
    private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> m_models = new();
    private readonly Dictionary<string, CanonicalFile> m_canonical = new(comparer: StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, HashSet<Unit>>> m_namers = new(comparer: StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Unit, Reached> m_reach = new();

    private readonly Dictionary<string, SyntaxTree> m_trees;

    private CSharpCompilation m_canonicalCompilation;
    private Dictionary<string, List<ISymbol>>? m_implementers;

    // The assemblies every closure compiles against, read once per process: a reference holds its metadata, so every
    // compilation after the first reuses what the first one read. They are the shared framework's alone, the trusted
    // assemblies that sit beside the core library, never the host's own application assemblies: the same source closes
    // to the same shape in every process that runs on the framework. A package member binds to nothing here, so a call
    // into one is digested as written.
    private static readonly Lazy<MetadataReference[]> FrameworkReferences = new(valueFactory: static () => {
        var framework = Path.GetDirectoryName(path: typeof(object).Assembly.Location)!;

        return [.. ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(separator: Path.PathSeparator)
            .Where(predicate: path => string.Equals(comparisonType: StringComparison.OrdinalIgnoreCase, a: Path.GetDirectoryName(path: path), b: framework))
            .Order(comparer: StringComparer.OrdinalIgnoreCase)
            .Select(selector: static path => MetadataReference.CreateFromFile(path: path))];
    });

    /// <summary>Parses every file once, on every core.</summary>
    /// <param name="files">Every source file's text, by repository-relative path with forward slashes.</param>
    /// <returns>Each file's syntax tree, by its path.</returns>
    public static Dictionary<string, SyntaxTree> Parse(IReadOnlyDictionary<string, string> files) => files.AsParallel().ToDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static pair => CSharpSyntaxTree.ParseText(
            options: ParseOptions,
            path: pair.Key,
            text: pair.Value
        ),
        keySelector: static pair => pair.Key
    );

    /// <summary>Closes formats over trees already parsed (<see cref="Parse"/>).</summary>
    /// <param name="sources">The sources and each one's compile context.</param>
    /// <param name="trees">Each source file's syntax tree, by its path.</param>
    /// <param name="declared">Every format the sources declare, whose shape constants the codecs read.</param>
    public FormatShapeClosure(FormatShapeSources sources, IReadOnlyDictionary<string, SyntaxTree> trees, IReadOnlyList<FormatRef> declared) {
        m_files = sources.Files;
        m_context = new FormatCompileContext(sources: sources, trees: trees);
        // The compilation holds the trees in the files' own order, whatever order they were parsed in, so symbols declared
        // across partial files enumerate as they always have.
        m_trees = m_context.Units();
        m_current = new Dictionary<string, SyntaxTree>(collection: m_trees, comparer: StringComparer.Ordinal);
        m_compilation = CSharpCompilation.Create(
            assemblyName: "FormatShapes",
            options: new CSharpCompilationOptions(
                allowUnsafe: true,
                outputKind: OutputKind.DynamicallyLinkedLibrary
            ),
            references: FrameworkReferences.Value,
            syntaxTrees: m_trees.Values.Concat(second: m_context.Bindings(declared: declared))
        );
        m_canonicalCompilation = m_compilation;
    }

    /// <summary>Closes each format over its boundary and digests what the closure covers.</summary>
    /// <param name="formats">Each format's declaring file, declaring type, token member and ledger id.</param>
    /// <param name="allIds">The ledger ids of every format, when <paramref name="formats"/> holds only some, so a [FormatPart] naming another is not mistaken for naming none.</param>
    /// <returns>One closure per format, in order.</returns>
    /// <exception cref="FormatBoundaryException">A seam gives no reason.</exception>
    public FormatClosure[] Of(IReadOnlyList<FormatRef> formats, IReadOnlyCollection<string>? allIds = null) {
        var known = (allIds ?? formats.Select(selector: static format => format.Id).ToArray()).ToHashSet(comparer: StringComparer.Ordinal);
        var misnamed = PartDeclarations().Where(predicate: part => ((part.Id is null) || !known.Contains(item: part.Id))).Select(selector: part => ((part.Id is null)
            ? $"part without a format: {KeyOf(symbol: part.Symbol)} carries [FormatPart] with an argument that is not a constant string; name the format's ledger id"
            : $"part of no format: {KeyOf(symbol: part.Symbol)} carries [FormatPart(\"{part.Id}\")], which is no format in the ledger; spell its id as FormatVersions.json does, with the @path when two files share a Type.Member")).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        if (misnamed.Length != 0) { throw new FormatBoundaryException(problems: misnamed); }

        // Each format's analysis reads only caches whose entries are whole functions of their keys, so the formats are
        // analyzed side by side and joined in their own order.
        var analyses = formats.AsParallel().AsOrdered().Select(selector: format => Analyze(format: format)).ToArray();
        var refusals = analyses.SelectMany(selector: static analysis => analysis.Refusals).Concat(second: formats.Zip(second: analyses).SelectMany(selector: static pair => pair.Second.Unbound.Repository.Select(selector: name => $"unresolved: {pair.First.Id} names {name}, a repository type its closure cannot bind; the shape cannot see it until the closure compiles that file with the usings its project does (FormatShapeSources)"))).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        if (refusals.Length != 0) { throw new FormatBoundaryException(problems: refusals); }

        Canonicalize(paths: analyses.SelectMany(selector: static analysis => analysis.Units).Select(selector: static unit => unit.Path).Distinct(comparer: StringComparer.Ordinal).ToArray());

        return [.. analyses.Select(selector: analysis => new FormatClosure(
            Open: analysis.Open,
            Outside: analysis.Unbound.Outside,
            Shape: Digest(units: analysis.Units),
            Units: analysis.Units
        ))];
    }
    /// <summary>The names each format's closure writes that bind to nothing, without refusing any: what <see cref="Of"/>
    /// would refuse, and what it leaves outside the repository.</summary>
    /// <param name="formats">Each format's declaring file, declaring type, token member and ledger id.</param>
    /// <returns>Each format's unbound names, in the order of <paramref name="formats"/>.</returns>
    public FormatUnbound[] UnboundOf(IReadOnlyList<FormatRef> formats) => [.. formats.AsParallel().AsOrdered().Select(selector: format => Analyze(format: format).Unbound)];

    private sealed record Analysis(IReadOnlyList<Unit> Units, IReadOnlyList<string> Open, IReadOnlyList<string> Refusals, FormatUnbound Unbound);

    private Analysis Analyze(FormatRef format) {
        var (roots, candidates) = Roots(format: format);
        var seen = new HashSet<Unit>(collection: roots);
        var pending = new Queue<(Unit Unit, bool Expand)>(collection: roots.Select(selector: static root => (root, true)));
        var scanned = new HashSet<Unit>();
        var expanded = new HashSet<Unit>();
        var unfollowed = new List<Call>();
        var refusals = new SortedSet<string>(comparer: StringComparer.Ordinal);

        void Follow(Unit unit, bool expand) {
            seen.Add(item: unit);

            if (!scanned.Contains(item: unit) || (expand && !expanded.Contains(item: unit))) { pending.Enqueue(item: (unit, expand)); }
        }

        while (pending.TryDequeue(result: out var next)) {
            var (unit, expand) = next;
            var reach = Reach(unit: unit);

            if (scanned.Add(item: unit)) {
                foreach (var refusal in reach.Refusals) { refusals.Add(item: refusal); }
                foreach (var (layout, expandLayout) in reach.Layouts) { Follow(expand: expandLayout, unit: layout); }
                foreach (var call in reach.Calls) {
                    if (call.Mark is Mark.Seam) { continue; }
                    if ((call.Mark is Mark.Leaf) || call.Targets.Concat(second: call.Dispatch).Any(predicate: seen.Contains) || call.Targets.Any(predicate: candidates.Contains) || IsPlainAccessor(call: call)) {
                        foreach (var target in call.Targets.Concat(second: call.Dispatch)) { Follow(expand: false, unit: target); }
                    } else {
                        unfollowed.Add(item: call);
                    }
                }
            }
            if (expand && expanded.Add(item: unit)) {
                foreach (var (layout, expandLayout) in reach.DeepLayouts) { Follow(expand: expandLayout, unit: layout); }
            }
        }

        // A call whose units were reached some other way is covered, not open.
        var open = unfollowed.Where(predicate: call => !call.Targets.Concat(second: call.Dispatch).Any(predicate: seen.Contains)).Select(selector: static call => call.Callee).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray();

        // A name that binds to nothing is a package's, unless its file's project scope holds a repository type of that name:
        // then the closure failed to see a type the build sees, and the shape is blind to it.
        var repository = new SortedSet<string>(comparer: StringComparer.Ordinal);
        var outside = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var unit in seen) {
            foreach (var name in Reach(unit: unit).Unbound) { (InScope(name: name, unit: unit) ? repository : outside).Add(item: name); }
        }

        outside.ExceptWith(other: repository);

        return new Analysis(
            Open: open,
            Refusals: [.. refusals],
            Unbound: new FormatUnbound(Outside: [.. outside], Repository: [.. repository]),
            Units: [.. seen.OrderBy(comparer: StringComparer.Ordinal, keySelector: static unit => unit.Key).ThenBy(keySelector: static unit => unit.Kind).ThenBy(comparer: StringComparer.Ordinal, keySelector: static unit => unit.Path)]
        );
    }

    private Dictionary<string, HashSet<string>>? m_containers;

    private readonly ConcurrentDictionary<Unit, (HashSet<string> Containers, Dictionary<string, (string Container, string Name)> Aliases)> m_scopes = new();

    // Whether a repository type of this name is in the unit's scope as its project compiles it: declared in a namespace its
    // usings or enclosing namespaces name, nested in a type its static usings or enclosing types name, or the target of an
    // alias its usings declare. An attribute is written without its suffix.
    private bool InScope(string name, Unit unit) {
        var containers = Containers();

        var (scope, aliases) = ScopeOf(unit: unit);

        bool Declared(string type) => (containers.TryGetValue(key: type, value: out var holders) && holders.Overlaps(other: scope));

        return (Declared(type: name) || Declared(type: $"{name}Attribute") || (aliases.TryGetValue(key: name, value: out var target) && containers.TryGetValue(key: target.Name, value: out var named) && named.Contains(item: target.Container)));
    }
    // Each type's simple name, with every container that declares a type of that name: its namespace (empty for the
    // global namespace), or its containing type.
    private Dictionary<string, HashSet<string>> Containers() {
        if (Volatile.Read(location: ref m_containers) is { } built) { return built; }

        var containers = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var pending = new Stack<INamespaceOrTypeSymbol>();

        pending.Push(item: m_compilation.Assembly.GlobalNamespace);

        while (pending.TryPop(result: out var next)) {
            foreach (var member in next.GetMembers()) {
                if (member is not INamespaceOrTypeSymbol inner) { continue; }

                pending.Push(item: inner);

                if (inner is INamedTypeSymbol type) {
                    if (!containers.TryGetValue(key: type.Name, value: out var holders)) { containers[type.Name] = holders = new HashSet<string>(comparer: StringComparer.Ordinal); }

                    holders.Add(item: ((type.ContainingType is { } outer)
                        ? outer.OriginalDefinition.ToDisplayString()
                        : (type.ContainingNamespace.IsGlobalNamespace ? string.Empty : type.ContainingNamespace.ToDisplayString())));
                }
            }
        }

        return (Interlocked.CompareExchange(comparand: null, location1: ref m_containers, value: containers) ?? containers);
    }
    // The containers a unit's names are looked up in, and the aliases it can use, as its project compiles its file.
    private (HashSet<string> Containers, Dictionary<string, (string Container, string Name)> Aliases) ScopeOf(Unit unit) {
        if (m_scopes.TryGetValue(key: unit, value: out var scope)) { return scope; }

        var containers = new HashSet<string>(comparer: StringComparer.Ordinal) { string.Empty };
        var aliases = new Dictionary<string, (string Container, string Name)>(comparer: StringComparer.Ordinal);

        foreach (var directive in m_context.Scope(path: unit.Path)) {
            if (directive.Alias is { } alias) {
                // An alias names a repository type when the container its target is qualified by declares a type of that name.
                if (directive.NamespaceOrType is QualifiedNameSyntax qualified) { aliases[alias.Name.Identifier.ValueText] = (qualified.Left.WithoutTrivia().NormalizeWhitespace().ToFullString().Replace(newValue: string.Empty, oldValue: "global::"), qualified.Right.Identifier.ValueText); }
            } else {
                containers.Add(item: FormatCompileContext.Named(directive: directive));
            }
        }

        var node = Index(path: unit.Path).Nodes[(unit.Key, unit.Kind)];
        var model = Model(tree: m_trees[unit.Path]);

        foreach (var ancestor in node.AncestorsAndSelf()) {
            switch (ancestor) {
                case BaseNamespaceDeclarationSyntax space when (model.GetDeclaredSymbol(space) is INamespaceSymbol declared):
                    for (var current = declared; !current.IsGlobalNamespace; current = current.ContainingNamespace) { containers.Add(item: current.ToDisplayString()); }
                    break;
                case BaseTypeDeclarationSyntax type when (model.GetDeclaredSymbol(type) is INamedTypeSymbol declared):
                    for (var current = declared; (current is not null); current = current.BaseType) { containers.Add(item: current.OriginalDefinition.ToDisplayString()); }
                    break;
            }
        }

        return m_scopes.GetOrAdd(key: unit, value: (containers, aliases));
    }
    // Whether a name stands where a type or a value is looked up by name in scope: not as a member after a dot, a named
    // argument's or initializer's label, or an alias's own name.
    private static bool IsLookup(SimpleNameSyntax name) => name.Parent switch {
        MemberAccessExpressionSyntax access => (access.Expression == name),
        QualifiedNameSyntax qualified => (qualified.Left == name),
        AliasQualifiedNameSyntax => false,
        MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax or NameMemberCrefSyntax => false,
        AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } assignment => (assignment.Left != name),
        TypeConstraintSyntax => (name.Identifier.ValueText is not ("notnull" or "unmanaged")),
        _ => true,
    };
    // A property whose body calls nothing in the repository only reads data: its body is covered, not left open.
    private bool IsPlainAccessor(Call call) => (call.Callee.StartsWith(comparisonType: StringComparison.Ordinal, value: "P:") && (call.Targets.Length > 0) && (call.Dispatch.Length == 0) && call.Targets.All(predicate: target => (Reach(unit: target).Calls.Count == 0)));
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
    // The units that start a closure: the layouts of the declaring file and its partial siblings, their members that
    // encode, and each unit anywhere that names the token. The rest of those files are candidates a covered call may reach.
    private (IReadOnlyList<Unit> Roots, HashSet<Unit> Candidates) Roots(FormatRef format) {
        var directory = format.Source[..(format.Source.LastIndexOf(value: '/') + 1)];
        var name = format.Source[directory.Length..];
        var stem = name[..name.IndexOf(value: '.')];
        var candidates = new HashSet<Unit>();
        var roots = new HashSet<Unit>();

        foreach (var path in m_files.Keys) {
            if (
                (path == format.Source) || (
                    path.StartsWith(comparisonType: StringComparison.Ordinal, value: directory) &&
                    (path.IndexOf(startIndex: directory.Length, value: '/') < 0) &&
                    path[directory.Length..].StartsWith(comparisonType: StringComparison.Ordinal, value: $"{stem}.")
                )
            ) {
                foreach (var (key, kind) in Index(path: path).Nodes.Keys) {
                    var unit = new Unit(Key: key, Kind: kind, Path: path);

                    candidates.Add(item: unit);

                    // The codec's read and write paths: its layouts and the members that touch bytes. A member that only drives
                    // the engine from decoded data is the codec file's neighbour, not its shape; it joins when an encoding member calls it.
                    if ((kind == UnitKind.Layout) || Reach(unit: unit).Encodes) { roots.Add(item: unit); }
                }
            }
        }
        foreach (var unit in Parts(id: format.Id)) { roots.Add(item: unit); }

        if (TokenKey(format: format) is { } token) {
            foreach (var unit in (Naming(member: format.Member, owner: format.Owner).GetValueOrDefault(key: token) ?? [])) { roots.Add(item: unit); }
        }

        foreach (var unit in Implementations(roots: roots).ToArray()) { roots.Add(item: unit); }

        return ([.. roots], candidates);
    }

    private sealed record PartDeclaration(string Path, SemanticModel Model, MemberDeclarationSyntax Declaration, ISymbol Symbol, string? Id, AttributeSyntax Attribute);

    private List<PartDeclaration>? m_parts;

    // Every [FormatPart(...)] in the repository, with the format id its argument evaluates to as a constant string, or null
    // when it does not: an escaped or verbatim literal and a named constant all name a format, so none is read as text.
    private List<PartDeclaration> PartDeclarations() {
        if (m_parts is not null) { return m_parts; }

        var found = new List<PartDeclaration>();

        foreach (var (path, text) in m_files) {
            if (!text.Contains(comparisonType: StringComparison.Ordinal, value: "FormatPart")) { continue; }

            var tree = m_trees[path];
            var model = Model(tree: tree);

            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()) {
                foreach (var attribute in declaration.AttributeLists.SelectMany(selector: static list => list.Attributes)) {
                    var name = ((attribute.Name is QualifiedNameSyntax qualified) ? qualified.Right : attribute.Name).ToString();

                    if (name is not ("FormatPart" or "FormatPartAttribute")) { continue; }
                    if (declaration is BaseFieldDeclarationSyntax field) {
                        foreach (var variable in field.Declaration.Variables) {
                            if (model.GetDeclaredSymbol(variable) is { } fieldSymbol) { found.Add(item: new PartDeclaration(Path: path, Model: model, Declaration: declaration, Symbol: fieldSymbol, Id: PartId(attribute: attribute, model: model), Attribute: attribute)); }
                        }

                        continue;
                    }
                    if (model.GetDeclaredSymbol(declaration) is { } symbol) { found.Add(item: new PartDeclaration(Path: path, Model: model, Declaration: declaration, Symbol: symbol, Id: PartId(attribute: attribute, model: model), Attribute: attribute)); }
                }
            }
        }

        m_parts = found;

        return found;
    }
    private static string? PartId(AttributeSyntax attribute, SemanticModel model) => (((attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } expression) && (model.GetConstantValue(expression) is { HasValue: true, Value: string id }))
        ? id
        : null);
    // The units of every declaration marked [FormatPart("<id>")]: the codecs a format governs that none of its own code
    // calls. A type roots all its units, in every partial file; a member with a body roots its body, with a partial method's
    // implementation and every override or implementation of a slot; a data member roots its type's layout.
    private IEnumerable<Unit> Parts(string id) {
        foreach (var part in PartDeclarations().Where(predicate: part => (part.Id == id))) {
            var symbol = part.Symbol;

            if (symbol is INamedTypeSymbol type) {
                var name = KeyOf(symbol: type)[2..];

                foreach (var file in m_files.Keys) {
                    if (!m_files[file].Contains(comparisonType: StringComparison.Ordinal, value: type.Name)) { continue; }

                    foreach (var (unitKey, kind) in Index(path: file).Nodes.Keys) {
                        if ((unitKey.Length > 2) && ((unitKey[2..] == name) || unitKey[2..].StartsWith(comparisonType: StringComparison.Ordinal, value: $"{name}.") || unitKey[2..].StartsWith(comparisonType: StringComparison.Ordinal, value: $"{name}+"))) {
                            yield return new Unit(Key: unitKey, Kind: kind, Path: file);
                        }
                    }
                }

                continue;
            }

            var found = false;

            foreach (var variant in new[] { symbol, (symbol as IMethodSymbol)?.PartialImplementationPart, (symbol as IMethodSymbol)?.PartialDefinitionPart }.OfType<ISymbol>()) {
                var key = KeyOf(symbol: variant);

                foreach (var path in Paths(symbol: variant)) {
                    if (Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Code))) {
                        found = true;

                        yield return new Unit(Key: key, Kind: UnitKind.Code, Path: path);
                    }
                }
            }
            if (!found && (symbol.ContainingType is { } owner)) {
                var ownerKey = KeyOf(symbol: owner);

                foreach (var path in Paths(symbol: owner)) {
                    if (Index(path: path).Nodes.ContainsKey(key: (ownerKey, UnitKind.Layout))) { yield return new Unit(Key: ownerKey, Kind: UnitKind.Layout, Path: path); }
                }
            }
        }
    }
    // A rooted slot dispatches: every override and implementation of a rooted virtual, abstract or interface member is rooted.
    private IEnumerable<Unit> Implementations(IEnumerable<Unit> roots) {
        foreach (var root in roots.Where(predicate: static root => (root.Kind == UnitKind.Code)).ToArray()) {
            foreach (var slot in DocumentationCommentId.GetSymbolsForDeclarationId(compilation: m_compilation, id: root.Key)) {
                if (!(slot.IsVirtual || slot.IsAbstract || slot.IsOverride || (slot.ContainingType?.TypeKind == TypeKind.Interface))) { continue; }
                if (!Implementers().TryGetValue(key: root.Key, value: out var implementers)) { continue; }

                foreach (var implementer in implementers) {
                    var key = KeyOf(symbol: implementer.OriginalDefinition);

                    foreach (var path in Paths(symbol: implementer.OriginalDefinition)) {
                        if (Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Code))) { yield return new Unit(Key: key, Kind: UnitKind.Code, Path: path); }
                    }
                }
            }
        }
    }
    private string? TokenKey(FormatRef format) {
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

        named = m_namers.GetOrAdd(key: $"{owner}.{member}", value: named);

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
        if (!m_models.TryGetValue(key: tree, value: out var model)) { model = m_models.GetOrAdd(key: tree, value: m_compilation.GetSemanticModel(syntaxTree: tree)); }

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

        index = m_index.GetOrAdd(key: path, value: index);

        return index;
    }
    // What one unit's names lead to, whatever format asks.
    private Reached Reach(Unit unit) {
        if (m_reach.TryGetValue(key: unit, value: out var reach)) { return reach; }

        reach = new Reached();

        var model = Model(tree: m_trees[unit.Path]);
        var root = Index(path: unit.Path).Nodes[(unit.Key, unit.Kind)];

        foreach (var node in Region(root: root)) {
            // What a layout's headers and member types name is one level deep; what its initializers read is part of the
            // value and is followed whole.
            reach.Deep = ((unit.Kind == UnitKind.Layout) && !InInitializer(node: node, root: root));

            if (node is ArrayTypeSyntax { ElementType: PredefinedTypeSyntax { Keyword.RawKind: ((int)SyntaxKind.ByteKeyword) } } or LiteralExpressionSyntax { RawKind: ((int)SyntaxKind.Utf8StringLiteralExpression) }) { reach.Encodes = true; }

            switch (node) {
                case IdentifierNameSyntax or GenericNameSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax
                    or ConstructorInitializerSyntax or BinaryExpressionSyntax or PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax
                    or CastExpressionSyntax or ElementAccessExpressionSyntax or AssignmentExpressionSyntax:
                    var info = model.GetSymbolInfo(node);

                    Resolve(symbol: info.Symbol, reach: reach);

                    if (info.Symbol is null) {
                        foreach (var candidate in info.CandidateSymbols) { Resolve(reach: reach, symbol: candidate); }
                        if ((info.CandidateSymbols.Length == 0) && (node is SimpleNameSyntax name) && IsLookup(name: name) && (SyntaxFacts.GetContextualKeywordKind(text: name.Identifier.ValueText) == SyntaxKind.None)) { reach.Unbound.Add(item: name.Identifier.ValueText); }
                    }

                    if (node is AssignmentExpressionSyntax assignment) { Deconstruction(info: model.GetDeconstructionInfo(assignment: assignment), reach: reach); }

                    break;
                case CommonForEachStatementSyntax loop:
                    // The enumerator pattern a foreach lowers to is called implicitly: no name in the syntax carries it.
                    var iteration = model.GetForEachStatementInfo(forEachStatement: loop);

                    Resolve(symbol: iteration.GetEnumeratorMethod, reach: reach);
                    Resolve(symbol: iteration.MoveNextMethod, reach: reach);
                    Resolve(symbol: iteration.CurrentProperty, reach: reach);
                    Resolve(symbol: iteration.DisposeMethod, reach: reach);
                    Resolve(symbol: iteration.ElementConversion.MethodSymbol, reach: reach);
                    Resolve(symbol: iteration.CurrentConversion.MethodSymbol, reach: reach);

                    if (loop is ForEachVariableStatementSyntax variables) { Deconstruction(info: model.GetDeconstructionInfo(@foreach: variables), reach: reach); }

                    break;
                case AwaitExpressionSyntax awaited:
                    var awaiting = model.GetAwaitExpressionInfo(awaitExpression: awaited);

                    Resolve(symbol: awaiting.GetAwaiterMethod, reach: reach);
                    Resolve(symbol: awaiting.IsCompletedProperty, reach: reach);
                    Resolve(symbol: awaiting.GetResultMethod, reach: reach);
                    break;
                case UsingStatementSyntax { Declaration: { } declaration }:
                    Disposal(type: model.GetTypeInfo(declaration.Type).Type, reach: reach);
                    break;
                case UsingStatementSyntax { Expression: { } resource }:
                    Disposal(type: model.GetTypeInfo(resource).Type, reach: reach);
                    break;
                case LocalDeclarationStatementSyntax { UsingKeyword.RawKind: not 0 } local:
                    Disposal(type: model.GetTypeInfo(local.Declaration.Type).Type, reach: reach);
                    break;
            }
            if (
                (node is ExpressionSyntax) &&
                (node.Parent is ArgumentSyntax or EqualsValueClauseSyntax or ReturnStatementSyntax or ArrowExpressionClauseSyntax or AssignmentExpressionSyntax or CastExpressionSyntax or YieldStatementSyntax or InitializerExpressionSyntax)
            ) {
                var conversion = model.GetConversion(node);

                if (conversion.IsUserDefined) { Resolve(symbol: conversion.MethodSymbol, reach: reach); }
            }
            // Implicit calls the syntax does not name: method-group conversions, collection-initializer adds, operators,
            // property reads and the copy a with-expression takes.
            if (IsBodyRoot(node: node) && (model.GetOperation(node) is { } operation)) {
                foreach (var child in operation.DescendantsAndSelf()) {
                    switch (child) {
                        case IInvocationOperation invocation: Resolve(symbol: invocation.TargetMethod, reach: reach); break;
                        case IObjectCreationOperation creation: Resolve(symbol: creation.Constructor, reach: reach); break;
                        case IWithOperation with: Resolve(symbol: with.CloneMethod, reach: reach); break;
                        case IMethodReferenceOperation methodReference: Resolve(symbol: methodReference.Method, reach: reach); break;
                        case IMemberReferenceOperation member: Resolve(symbol: member.Member, reach: reach); break;
                        case IConversionOperation conversion: Resolve(symbol: conversion.OperatorMethod, reach: reach); break;
                        case IBinaryOperation binary: Resolve(symbol: binary.OperatorMethod, reach: reach); break;
                        case IUnaryOperation unary: Resolve(symbol: unary.OperatorMethod, reach: reach); break;
                        case IIncrementOrDecrementOperation increment: Resolve(symbol: increment.OperatorMethod, reach: reach); break;
                        case ICompoundAssignmentOperation compound: Resolve(symbol: compound.OperatorMethod, reach: reach); break;
                    }
                }
            }
        }

        reach.Deep = false;
        reach.Calls.RemoveAll(match: call => (Array.IndexOf(array: call.Targets, value: unit) >= 0));

        return m_reach.GetOrAdd(key: unit, value: reach);
    }
    // The nodes whose operation tree carries a body or an initializer: a member's block or expression body and each
    // initializer, which Roslyn binds as one unit.
    private static bool IsBodyRoot(SyntaxNode node) => node switch {
        BlockSyntax block => (block.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax),
        ArrowExpressionClauseSyntax or EqualsValueClauseSyntax => true,
        ConstructorInitializerSyntax => true,
        _ => false,
    };
    private static bool InInitializer(SyntaxNode node, SyntaxNode root) {
        for (var current = node; ((current is not null) && (current != root)); current = current.Parent) {
            if (current is EqualsValueClauseSyntax or ArrowExpressionClauseSyntax or BaseMethodDeclarationSyntax or AccessorDeclarationSyntax) { return true; }
        }

        return false;
    }
    private void Deconstruction(DeconstructionInfo info, Reached reach) {
        Resolve(symbol: info.Method, reach: reach);
        Resolve(symbol: info.Conversion?.MethodSymbol, reach: reach);

        if (!info.Nested.IsDefault) {
            foreach (var nested in info.Nested) { Deconstruction(info: nested, reach: reach); }
        }
    }
    // A using's disposal is a call the syntax does not name: every Dispose a resource type or its bases declare.
    private void Disposal(ITypeSymbol? type, Reached reach) {
        for (var current = type; (current is not null); current = current.BaseType) {
            foreach (var member in current.GetMembers().Where(predicate: static item => (item is IMethodSymbol { Name: "Dispose" or "DisposeAsync" }))) { Resolve(reach: reach, symbol: member); }
        }
    }
    // Bytes are what encoding touches: byte buffers and streams, the platform's binary readers and writers, and any member the
    // codec's own boundary marks [FormatLeaf].
    private bool IsEncoding(ISymbol symbol, Reached reach) {
        static bool Bytes(ITypeSymbol? type) => (type switch {
            IArrayTypeSymbol array => (array.ElementType.SpecialType == SpecialType.System_Byte),
            INamedTypeSymbol { Name: "Span" or "ReadOnlySpan" or "Memory" or "ReadOnlyMemory" } named => ((named.TypeArguments.Length == 1) && (named.TypeArguments[0].SpecialType == SpecialType.System_Byte)),
            INamedTypeSymbol { Name: "Stream" or "MemoryStream" or "BinaryReader" or "BinaryWriter" or "BinaryPrimitives" or "IBufferWriter" or "ArrayBufferWriter" or "Encoding" or "Utf8" } platform => (platform.ContainingNamespace.ToDisplayString().StartsWith(comparisonType: StringComparison.Ordinal, value: "System")),
            _ => false,
        });

        return symbol switch {
            ITypeSymbol type => (Bytes(type: type) || (MarkOf(reach: reach, symbol: symbol) is Mark.Leaf)),
            IMethodSymbol method => (Bytes(type: method.ContainingType) || (MarkOf(reach: reach, symbol: symbol) is Mark.Leaf)),
            IPropertySymbol or IFieldSymbol or IEventSymbol => (Bytes(type: symbol.ContainingType) || (MarkOf(reach: reach, symbol: symbol) is Mark.Leaf)),
            _ => false,
        };
    }
    private void Resolve(ISymbol? symbol, Reached reach) {
        if ((symbol is not null) && !reach.Encodes && IsEncoding(reach: reach, symbol: symbol)) { reach.Encodes = true; }

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
            if (Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Layout))) { (reach.Deep ? reach.DeepLayouts : reach.Layouts).Add(item: (new Unit(Key: key, Kind: UnitKind.Layout, Path: path), expand)); }
        }
    }
    private void Member(ISymbol symbol, Reached reach) {
        var key = KeyOf(symbol: symbol);
        var targets = Paths(symbol: symbol).Where(predicate: path => Index(path: path).Nodes.ContainsKey(key: (key, UnitKind.Code))).Select(selector: path => new Unit(Key: key, Kind: UnitKind.Code, Path: path)).ToArray();
        var dispatch = new List<Unit>();

        // A slot dispatches whether or not its own declaration has a body: an abstract or interface member, an auto-property
        // an override replaces, and a static abstract or virtual interface member all reach their overrides and
        // implementations.
        if (symbol.IsVirtual || symbol.IsAbstract || symbol.IsOverride || (symbol.ContainingType?.TypeKind == TypeKind.Interface)) {
            if (Implementers().TryGetValue(key: key, value: out var implementers)) {
                foreach (var implementer in implementers) {
                    var implementerKey = KeyOf(symbol: implementer.OriginalDefinition);

                    foreach (var path in Paths(symbol: implementer.OriginalDefinition)) {
                        if (Index(path: path).Nodes.ContainsKey(key: (implementerKey, UnitKind.Code))) { dispatch.Add(item: new Unit(Key: implementerKey, Kind: UnitKind.Code, Path: path)); }
                    }
                }
            }
        }
        if (targets.Length == 0) {
            if (symbol.ContainingType is { } owner) { Layout(type: owner.OriginalDefinition, reach: reach); }
        }
        if ((targets.Length == 0) && (dispatch.Count == 0)) { return; }

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
        if (Volatile.Read(location: ref m_implementers) is { } built) { return built; }

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
                    foreach (var slot in contract.GetMembers().Where(predicate: static item => ((item is IMethodSymbol or IPropertySymbol or IEventSymbol) && (!item.IsStatic || item.IsAbstract || item.IsVirtual)))) {
                        if ((type.FindImplementationForInterfaceMember(interfaceMember: slot) is { } implementation) && (implementation.DeclaringSyntaxReferences.Length > 0)) {
                            Add(key: KeyOf(symbol: slot.OriginalDefinition), implementer: implementation);
                        }
                    }
                }
            }
        }

        // Formats analyzed side by side may each build the index; every build is the same, and the first one published stands.
        return (Interlocked.CompareExchange(comparand: null, location1: ref m_implementers, value: index) ?? index);

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
        var texts = new ConcurrentDictionary<string, string>(comparer: StringComparer.Ordinal);

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
        root = new TriviaStripper().Visit(node: root)!.NormalizeWhitespace();
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
