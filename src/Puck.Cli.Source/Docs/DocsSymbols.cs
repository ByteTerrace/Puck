using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Analysis;
using Puck.Cli.Source;

namespace Puck.Cli.Docs;

/// <summary>What a cited C# name resolved to.</summary>
internal enum DocsSymbolVerdict {
    /// <summary>The name resolves: to a Puck declaration, a name the code spells as a string literal, or a type or
    /// member of the runtime.</summary>
    Resolved,
    /// <summary>The name is Puck's and does not resolve: its head is a Puck type or namespace whose member is gone, or its
    /// head is unknown while its member is a name only Puck declares, as after a type's rename.</summary>
    Missing,
    /// <summary>The name is neither Puck's nor the runtime's (a package's type, a permission scope, a shorthand), so
    /// nothing current can say whether it is stale.</summary>
    Unknown,
}
/// <summary>
/// What a document's code span may cite as C#: every type and member declared under <c>src/</c> and <c>tests/</c>, read
/// off the syntax index <c>puck declarations</c> uses (<see cref="DeclarationsWalker"/>), the projects and the package
/// ids their project files declare, and the code's own string literals, which hold the dotted names the code reads
/// (an AppContext switch) and the dates a protocol names its revisions by. A cited name that is not Puck's resolves
/// against the public types of the runtime the CLI runs on, so a citation of the base library is not mistaken for a
/// stale one.
/// </summary>
internal sealed class DocsSymbols {
    // A dotted C# name: two or more segments, each opening with a capital. Lowercase heads are other vocabularies (a
    // console verb, a document field path, a file name), which other checks own.
    private static readonly Regex DottedName = new(
        options: RegexOptions.Compiled,
        pattern: @"^[A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)+$"
    );
    private static readonly Regex LiteralName = new(
        options: RegexOptions.Compiled,
        pattern: @"""([A-Z][A-Za-z0-9_]*(?:\.[A-Z][A-Za-z0-9_]*)+)"""
    );
    private static readonly Regex PackageId = new(
        options: RegexOptions.Compiled,
        pattern: @"<PackageId>\s*([^<\s]+)\s*</PackageId>"
    );
    private static readonly Regex IsoDate = new(
        options: RegexOptions.Compiled,
        pattern: @"\b(?:19|20)\d\d-(?:0[1-9]|1[0-2])-(?:0[1-9]|[12]\d|3[01])\b"
    );

    private readonly Dictionary<string, List<string>> m_declaredBySimpleName;
    private readonly HashSet<string> m_typeNames;
    private readonly HashSet<string> m_rootNamespaces;
    private readonly Lazy<RuntimeTypes> m_runtimeTypes = new(valueFactory: RuntimeTypes.Load);
    private readonly HashSet<string> m_scopes;

    private DocsSymbols(Dictionary<string, List<string>> declaredBySimpleName, HashSet<string> typeNames, HashSet<string> rootNamespaces, HashSet<string> scopes, HashSet<string> literalDates, int declarations) {
        m_declaredBySimpleName = declaredBySimpleName;
        m_typeNames = typeNames;
        m_rootNamespaces = rootNamespaces;
        m_scopes = scopes;
        LiteralDates = literalDates;
        DeclarationCount = declarations;
    }

    /// <summary>Gets the number of declarations the index read.</summary>
    public int DeclarationCount { get; }
    /// <summary>Gets every ISO date spelled inside a string literal under <c>src/</c>: a revision name the code speaks,
    /// such as a protocol version.</summary>
    public IReadOnlySet<string> LiteralDates { get; }

    // A declared name with its type parameters and parameter list removed: `Foo<T>.Bar(int)` is `Foo.Bar`.
    private static string StripGroups(string name) {
        var builder = new StringBuilder(capacity: name.Length);
        var depth = 0;

        foreach (var character in name) {
            if (character is '<' or '(' or '[') {
                depth++;
            } else if (character is '>' or ')' or ']') {
                depth = Math.Max(
                    val1: 0,
                    val2: (depth - 1)
                );
            } else if (depth == 0) {
                builder.Append(value: character);
            }
        }

        return builder.ToString();
    }
    // The dotted names and dates one source file spells inside its string literals.
    private static void SweepLiterals(ParsedFile file, HashSet<string> names, HashSet<string> dates) {
        foreach (Match match in LiteralName.Matches(input: file.Text)) {
            lock (names) {
                _ = names.Add(item: match.Groups[1].Value);
            }
        }

        foreach (Match match in IsoDate.Matches(input: file.Text)) {
            if (file.Root.FindToken(position: match.Index).Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.Utf8StringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken or SyntaxKind.SingleLineRawStringLiteralToken) {
                lock (dates) {
                    _ = dates.Add(item: match.Value);
                }
            }
        }
    }

    /// <summary>Reduces a code span to the C# name it cites, or <see langword="null"/> when the span is not shaped
    /// like one: a dotted name whose every segment opens with a capital, optionally followed by an argument list, with
    /// any type arguments dropped (<c>RuleCapacity.MaxWorkUnitsPerTick</c>, <c>DocsCommand.Create()</c>,
    /// <c>List&lt;T&gt;.Count</c>). An argument list before the last segment makes the span an expression
    /// (<c>Matches(...).Count</c>), not a name. A segment with no lowercase letter longer than three characters is a
    /// native or foreign constant (<c>D3D12_RESOURCE_STATES</c>), and a one-letter head is a type parameter
    /// (<c>T.CreateTruncating</c>); neither is a Puck name.</summary>
    /// <param name="span">The code span's content.</param>
    /// <returns>The cited name, or <see langword="null"/>.</returns>
    public static string? CitedName(string span) {
        var trimmed = span.Trim();

        if ((trimmed.Length < 3) || !char.IsAsciiLetterUpper(c: trimmed[0]) || (trimmed[1] == '.')) {
            return null;
        }

        if (trimmed.EndsWith(value: ')') && (trimmed.IndexOf(value: '(') is var open and > 0)) {
            trimmed = trimmed[..open];
        }

        if (trimmed.Contains(value: '(')) {
            return null;
        }

        var name = StripGroups(name: trimmed);

        if (!DottedName.IsMatch(input: name)) {
            return null;
        }

        foreach (var segment in name.Split(separator: '.')) {
            if ((segment.Length > 3) && !segment.Any(predicate: char.IsAsciiLetterLower)) {
                return null;
            }
        }

        return name;
    }
    /// <summary>Builds the index over every C# file under <c>src/</c> and <c>tests/</c>, parsed once. A page may also
    /// cite, like a namespace, a project, the package id its project file declares, or a source file by its stem
    /// (<c>WorldViewPaneMappingLawTests.Desktop</c> for a partial class's file).</summary>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <returns>The index.</returns>
    public static DocsSymbols Build(string repositoryRoot) {
        var roots = new[] { "src", "tests" }
            .Select(selector: tree => Path.Combine(
                path1: repositoryRoot,
                path2: tree
            ))
            .Where(predicate: Directory.Exists)
            .ToArray();
        var files = (FileWalk.Enumerate(
            exclude: [],
            extension: ".cs",
            include: [],
            roots: roots,
            verb: "docs links"
        ) ?? []);
        var projectFiles = (FileWalk.Enumerate(
            exclude: [],
            extension: ".csproj",
            include: [],
            roots: roots,
            verb: "docs links"
        ) ?? []);
        var corpus = SourceCorpus.Parse(
            files: files,
            relativeTo: repositoryRoot
        );
        var records = DeclarationsWalker.Collect(
            corpus: corpus,
            options: new DeclarationsOptions {
                Attribute = null,
                Base = null,
                Doc = false,
                Exclude = [],
                Include = [],
                Json = false,
                Kinds = new HashSet<string>(comparer: StringComparer.Ordinal),
                Members = true,
                Name = null,
                Quiet = true,
                Roots = [],
            }
        );
        var bySimpleName = new Dictionary<string, List<string>>(comparer: StringComparer.Ordinal);
        var typeNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var rootNamespaces = new HashSet<string>(comparer: StringComparer.Ordinal);
        var scopes = new HashSet<string>(
            collection: projectFiles.Select(selector: static path => Path.GetFileNameWithoutExtension(path: path))
                .Concat(second: projectFiles.SelectMany(selector: static path => PackageId.Matches(input: File.ReadAllText(path: path)).Select(selector: static match => match.Groups[1].Value)))
                .Concat(second: files.Select(selector: static path => Path.GetFileNameWithoutExtension(path: path))),
            comparer: StringComparer.Ordinal
        );

        foreach (var record in records) {
            var path = StripGroups(name: record.Name);

            if (path.Contains(value: ' ') || path.Contains(value: '~')) {
                continue;
            }

            var simpleName = path[(path.LastIndexOf(value: '.') + 1)..];

            if (!bySimpleName.TryGetValue(
                key: simpleName,
                value: out var paths
            )) {
                paths = [];
                bySimpleName[simpleName] = paths;
            }

            paths.Add(item: path);

            // Every scope that encloses a type, namespaces and outer types alike, is a name a page may cite whole. A type's
            // simple name and its root namespace are the heads a Puck citation opens with.
            if (DeclarationsWalker.TypeKinds.Contains(item: record.Kind)) {
                _ = typeNames.Add(item: simpleName);
                _ = rootNamespaces.Add(item: path[..Math.Max(
                    val1: 0,
                    val2: path.IndexOf(value: '.')
                )]);

                for (var dot = path.IndexOf(value: '.'); (dot >= 0); dot = path.IndexOf(
                    startIndex: (dot + 1),
                    value: '.'
                )) {
                    _ = scopes.Add(item: path[..dot]);
                }
            }
        }

        var dates = new HashSet<string>(comparer: StringComparer.Ordinal);

        Parallel.ForEach(
            body: file => SweepLiterals(
                dates: dates,
                file: file,
                names: scopes
            ),
            source: corpus.Files.Where(predicate: static file => file.Relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "src/"
            ))
        );

        return new DocsSymbols(
            declarations: records.Count,
            declaredBySimpleName: bySimpleName,
            literalDates: dates,
            rootNamespaces: rootNamespaces,
            scopes: scopes,
            typeNames: typeNames
        );
    }

    // Whether Puck declares the name: a scope cited whole, a declaration whose path ends with it, or a member of a
    // namespace's static class cited through the namespace, the way a caller reaches an extension method
    // (`Puck.Platform.AddPlatformWindowing`).
    private bool Declares(string name) {
        if (m_scopes.Contains(item: name)) {
            return true;
        }

        var lastDot = name.LastIndexOf(value: '.');

        if (lastDot < 0) {
            return m_declaredBySimpleName.ContainsKey(key: name);
        }

        var scope = name[..lastDot];
        var simpleName = name[(lastDot + 1)..];

        return (m_declaredBySimpleName.TryGetValue(
            key: simpleName,
            value: out var paths
        ) && paths.Any(predicate: path => (string.Equals(
            a: path,
            b: name,
            comparisonType: StringComparison.Ordinal
        ) || (path.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: name
        ) && (path[((path.Length - name.Length) - 1)] == '.')) || (m_scopes.Contains(item: scope) && path.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (scope + ".")
        ) && (path.IndexOf(
            startIndex: (scope.Length + 1),
            value: '.'
        ) == ((path.Length - simpleName.Length) - 1))))));
    }

    /// <summary>Resolves a cited name. A Puck declaration, a literal the code spells, or a runtime type resolves it. A
    /// chain through a member (<c>WorldDefinition.Simulation.RateHz</c>) resolves when its longest declared prefix is
    /// followed only by names something declares, since the syntax index does not know a member's type. Otherwise a
    /// type cited twice is its constructor; a head that is a Puck type makes it <see cref="DocsSymbolVerdict.Missing"/>;
    /// a head the runtime names as a type or namespace leaves it <see cref="DocsSymbolVerdict.Unknown"/> (a package's
    /// member, say); a head that is a Puck root namespace makes it Missing; a head Puck declares only as a member is a
    /// chain through it, resolved when each later segment is declared; and a head nothing declares makes it Missing when
    /// its last segment is a member name only Puck declares (a renamed type). What remains is
    /// <see cref="DocsSymbolVerdict.Unknown"/>.</summary>
    /// <param name="name">The name, as <see cref="CitedName"/> reduced it.</param>
    /// <returns>The verdict.</returns>
    public DocsSymbolVerdict Resolve(string name) {
        if (Declares(name: name)) {
            return DocsSymbolVerdict.Resolved;
        }

        var segments = name.Split(separator: '.');
        var runtime = m_runtimeTypes.Value;

        for (var length = (segments.Length - 1); (length >= 2); length--) {
            if (Declares(name: string.Join(
                separator: '.',
                value: segments[..length]
            ))) {
                if (segments[length..].All(predicate: segment => (m_declaredBySimpleName.ContainsKey(key: segment) || runtime.DeclaresMember(name: segment)))) {
                    return DocsSymbolVerdict.Resolved;
                }

                break;
            }
        }

        if (runtime.Resolves(name: name)) {
            return DocsSymbolVerdict.Resolved;
        }

        var head = segments[0];

        // A constructor is cited by its type's name twice (`CaptureFrame.CaptureFrame`), a primary constructor included,
        // which declares no member of its own.
        if ((segments.Length >= 2) && (segments[^1] == segments[^2]) && Declares(name: string.Join(
            separator: '.',
            value: segments[..^1]
        ))) {
            return DocsSymbolVerdict.Resolved;
        }

        // A Puck type has no such member even where the runtime has a type of that name, since the runtime did not
        // resolve it either; a root namespace the runtime shares (`System`, where Puck declares a polyfill) is the
        // runtime's to judge, and it found nothing.
        if (m_typeNames.Contains(item: head)) {
            return DocsSymbolVerdict.Missing;
        }

        if (runtime.NamesType(name: head) || runtime.NamesNamespace(name: head)) {
            return DocsSymbolVerdict.Unknown;
        }

        if (m_rootNamespaces.Contains(item: head)) {
            return DocsSymbolVerdict.Missing;
        }

        var last = segments[^1];

        // A head Puck declares only as a member is a chain through that member (`Arena.Scratch`), whose type the syntax
        // index does not know; a head nothing declares, ahead of a member only Puck declares, is a renamed type.
        return (m_declaredBySimpleName.ContainsKey(key: head)
            ? (segments.Skip(count: 1).All(predicate: segment => (m_declaredBySimpleName.ContainsKey(key: segment) || runtime.DeclaresMember(name: segment)))
                ? DocsSymbolVerdict.Resolved
                : DocsSymbolVerdict.Missing)
            : ((m_declaredBySimpleName.ContainsKey(key: last) && !runtime.DeclaresMember(name: last))
                ? DocsSymbolVerdict.Missing
                : DocsSymbolVerdict.Unknown));
    }

    // The visible types of every assembly the CLI's runtime trusts, Puck's own excluded so the source stays the one
    // truth for Puck names, read from metadata without loading any assembly: each keyed by simple name with any generic
    // arity dropped, with the names of the members and nested types it declares.
    private sealed class RuntimeTypes {
        private readonly HashSet<string> m_namespaces;
        private readonly HashSet<string> m_memberNames;
        private readonly Dictionary<string, List<RuntimeType>> m_bySimpleName;

        private RuntimeTypes(Dictionary<string, List<RuntimeType>> bySimpleName, HashSet<string> namespaces, HashSet<string> memberNames) {
            m_bySimpleName = bySimpleName;
            m_namespaces = namespaces;
            m_memberNames = memberNames;
        }

        private static string SimpleName(MetadataReader reader, StringHandle name) {
            var text = reader.GetString(handle: name);
            var tick = text.IndexOf(value: '`');

            return ((tick < 0)
                ? text
                : text[..tick]);
        }
        // Every type a caller outside the assembly can name: public at the top level, or public or protected when nested
        // in such a type.
        private static List<RuntimeType> Read(string path) {
            var types = new List<RuntimeType>();

            try {
                using var stream = File.OpenRead(path: path);
                using var portable = new PEReader(peStream: stream);

                if (!portable.HasMetadata) {
                    return types;
                }

                var reader = portable.GetMetadataReader();

                foreach (var handle in reader.TypeDefinitions) {
                    var definition = reader.GetTypeDefinition(handle: handle);
                    var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
                    var @namespace = reader.GetString(handle: definition.Namespace);

                    if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem)) {
                        continue;
                    }

                    for (var outer = definition; (outer.GetDeclaringType() is { IsNil: false } declaring);) {
                        outer = reader.GetTypeDefinition(handle: declaring);
                        @namespace = reader.GetString(handle: outer.Namespace);
                    }

                    var members = new HashSet<string>(comparer: StringComparer.Ordinal);

                    foreach (var method in definition.GetMethods()) {
                        _ = members.Add(item: reader.GetString(handle: reader.GetMethodDefinition(handle: method).Name));
                    }

                    foreach (var field in definition.GetFields()) {
                        _ = members.Add(item: reader.GetString(handle: reader.GetFieldDefinition(handle: field).Name));
                    }

                    foreach (var property in definition.GetProperties()) {
                        _ = members.Add(item: reader.GetString(handle: reader.GetPropertyDefinition(handle: property).Name));
                    }

                    foreach (var @event in definition.GetEvents()) {
                        _ = members.Add(item: reader.GetString(handle: reader.GetEventDefinition(handle: @event).Name));
                    }

                    foreach (var nested in definition.GetNestedTypes()) {
                        _ = members.Add(item: SimpleName(
                            name: reader.GetTypeDefinition(handle: nested).Name,
                            reader: reader
                        ));
                    }

                    types.Add(item: new RuntimeType(
                        Members: members,
                        Name: SimpleName(
                            name: definition.Name,
                            reader: reader
                        ),
                        Namespace: @namespace
                    ));
                }
            } catch (Exception exception) when ((exception is BadImageFormatException or IOException or UnauthorizedAccessException)) {
                return [];
            }

            return types;
        }

        public static RuntimeTypes Load() {
            var trusted = ((AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty).Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: Path.PathSeparator
            ).Where(predicate: static path => !Path.GetFileName(path: path).StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: "Puck."
            )).ToArray();
            var read = new List<RuntimeType>[trusted.Length];

            Parallel.For(
                body: index => read[index] = Read(path: trusted[index]),
                fromInclusive: 0,
                toExclusive: trusted.Length
            );

            var bySimpleName = new Dictionary<string, List<RuntimeType>>(comparer: StringComparer.Ordinal);
            var namespaces = new HashSet<string>(comparer: StringComparer.Ordinal);
            var memberNames = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var type in read.SelectMany(selector: static types => types)) {
                if (!bySimpleName.TryGetValue(
                    key: type.Name,
                    value: out var list
                )) {
                    list = [];
                    bySimpleName[type.Name] = list;
                }

                list.Add(item: type);
                memberNames.UnionWith(other: type.Members);

                if (type.Namespace.Length != 0) {
                    _ = namespaces.Add(item: type.Namespace);

                    for (var dot = type.Namespace.IndexOf(value: '.'); (dot >= 0); dot = type.Namespace.IndexOf(
                        startIndex: (dot + 1),
                        value: '.'
                    )) {
                        _ = namespaces.Add(item: type.Namespace[..dot]);
                    }
                }
            }

            return new RuntimeTypes(
                bySimpleName: bySimpleName,
                memberNames: memberNames,
                namespaces: namespaces
            );
        }
        // Whether a member of some runtime type has this name.
        public bool DeclaresMember(string name) => m_memberNames.Contains(item: name);
        // Whether some runtime namespace is spelled this way.
        public bool NamesNamespace(string name) => m_namespaces.Contains(item: name);
        // Whether some runtime type has this simple name.
        public bool NamesType(string name) => m_bySimpleName.ContainsKey(key: name);
        // A namespace, or a type (namespace-qualified or not) followed by at most one member or nested type it declares;
        // what follows that is not checked. Inherited members are not read, so a member cited through a derived type
        // resolves when any runtime type declares a member of that name.
        public bool Resolves(string name) {
            if (m_namespaces.Contains(item: name)) {
                return true;
            }

            var segments = name.Split(separator: '.');

            for (var index = 0; (index < segments.Length); index++) {
                var scope = string.Join(
                    separator: '.',
                    value: segments[..index]
                );

                if ((index > 0) && !m_namespaces.Contains(item: scope)) {
                    return false;
                }

                if (!m_bySimpleName.TryGetValue(
                    key: segments[index],
                    value: out var types
                )) {
                    continue;
                }

                var candidates = types.Where(predicate: type => ((index == 0) || string.Equals(
                    a: type.Namespace,
                    b: scope,
                    comparisonType: StringComparison.Ordinal
                ))).ToArray();

                if (candidates.Length == 0) {
                    continue;
                }

                if ((index + 1) == segments.Length) {
                    return true;
                }

                var member = segments[(index + 1)];

                return (candidates.Any(predicate: type => type.Members.Contains(item: member)) || m_memberNames.Contains(item: member));
            }

            return false;
        }

        private sealed record RuntimeType(string Namespace, string Name, HashSet<string> Members);
    }
}
