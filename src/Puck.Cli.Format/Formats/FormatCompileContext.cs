using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Puck.Cli.Formats;

/// <summary>
/// The trees a format closure compiles, each as its project compiles it. The closure is one compilation over every
/// project's sources, where a <c>global using</c> would reach every project's files; so each file instead receives its own
/// project's project-level usings as file-scoped directives: the <c>Using</c> items its project's evaluation declares
/// (implicit usings included) and the <c>global using</c> directives its project's files state, which leave the file that
/// states them. A directive the file already states is not written twice.
/// </summary>
public sealed class FormatCompileContext {
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(version: LanguageVersion.Preview);

    private readonly FormatShapeSources m_sources;
    private readonly IReadOnlyDictionary<string, SyntaxTree> m_trees;
    private readonly Dictionary<string, SyntaxList<UsingDirectiveSyntax>> m_projects = new(comparer: StringComparer.Ordinal);

    /// <summary>Reads each project's directives: its evaluated <c>Using</c> items and every <c>global using</c> its files
    /// state, one spelling each.</summary>
    /// <param name="sources">The sources and their compile context.</param>
    /// <param name="trees">Each source file's syntax tree as parsed, by its path.</param>
    public FormatCompileContext(FormatShapeSources sources, IReadOnlyDictionary<string, SyntaxTree> trees) {
        m_sources = sources;
        m_trees = trees;

        var directives = sources.Projects.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static pair => new SortedSet<string>(collection: pair.Value.Select(selector: static item => item.Directive), comparer: StringComparer.Ordinal),
            keySelector: static pair => pair.Key
        );

        foreach (var path in sources.Files.Keys.Order(comparer: StringComparer.Ordinal)) {
            if ((sources.ProjectOf(path: path) is not { } project) || !directives.TryGetValue(key: project, value: out var stated)) { continue; }

            foreach (var directive in trees[path].GetCompilationUnitRoot().Usings.Where(predicate: static directive => !directive.GlobalKeyword.IsKind(kind: SyntaxKind.None))) {
                stated.Add(item: directive.WithGlobalKeyword(globalKeyword: default).WithoutTrivia().NormalizeWhitespace().ToFullString());
            }
        }
        foreach (var (project, stated) in directives) {
            var text = new StringBuilder();

            foreach (var directive in stated) { text.Append(value: directive).Append(value: '\n'); }

            m_projects[project] = CSharpSyntaxTree.ParseText(options: ParseOptions, text: text.ToString()).GetCompilationUnitRoot().Usings;
        }
    }

    /// <summary>The trees of the files a closure covers, in the files' own order, each with its project's usings.</summary>
    /// <returns>Each file's tree as its project compiles it, by its path.</returns>
    public Dictionary<string, SyntaxTree> Units() => m_sources.Files.Keys.AsParallel().AsOrdered().Select(selector: path => (Path: path, Tree: WithUsings(prelude: Project(project: m_sources.ProjectOf(path: path)), tree: m_trees[path]))).ToArray().ToDictionary(
        comparer: StringComparer.Ordinal,
        elementSelector: static pair => pair.Tree,
        keySelector: static pair => pair.Path
    );
    /// <summary>The trees that bind names and are never units: each file a project links in from outside <c>src/</c>,
    /// with that project's usings, and one <c>FormatShapes</c> class per namespace that declares a format, holding a
    /// constant per format as the generated file does. The constants' values are left empty, so a shape never reads the
    /// shapes the generated files record.</summary>
    /// <param name="declared">Every format the sources declare.</param>
    /// <returns>The binding trees, in ordinal order of their paths.</returns>
    public IEnumerable<SyntaxTree> Bindings(IReadOnlyList<FormatRef> declared) {
        foreach (var (path, (text, project)) in m_sources.Linked.OrderBy(keySelector: static pair => pair.Key, comparer: StringComparer.Ordinal)) {
            yield return WithUsings(
                prelude: Project(project: project),
                tree: CSharpSyntaxTree.ParseText(options: ParseOptions, path: path, text: text)
            );
        }

        var constants = new SortedDictionary<string, SortedSet<string>>(comparer: StringComparer.Ordinal);

        foreach (var format in declared) {
            var space = NamespaceOf(tree: m_trees[format.Source]);

            if (!constants.TryGetValue(key: space, value: out var names)) { constants[space] = names = new SortedSet<string>(comparer: StringComparer.Ordinal); }

            names.Add(item: FormatShapesFiles.ConstantOf(id: format.Id));
        }
        foreach (var (space, names) in constants) {
            var body = $"internal static class {FormatShapesFiles.ClassName} {{ {string.Concat(values: names.Select(selector: static name => $"public const string {name} = \"\"; "))}}}";

            yield return CSharpSyntaxTree.ParseText(
                options: ParseOptions,
                path: $"<{FormatShapesFiles.ClassName}>/{space}.cs",
                text: ((space.Length == 0) ? body : $"namespace {space} {{ {body} }}")
            );
        }
    }
    /// <summary>Every using directive a file compiles under in its project: its project's directives and its own.</summary>
    /// <param name="path">The file's repository-relative path.</param>
    /// <returns>The directives, its project's first.</returns>
    public IEnumerable<UsingDirectiveSyntax> Scope(string path) => Project(project: m_sources.ProjectOf(path: path)).Concat(second: m_trees[path].GetCompilationUnitRoot().Usings.Where(predicate: static directive => directive.GlobalKeyword.IsKind(kind: SyntaxKind.None)));

    private static string NamespaceOf(SyntaxTree tree) => (tree.GetRoot().DescendantNodes(descendIntoChildren: static node => (node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)).OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty);
    private SyntaxList<UsingDirectiveSyntax> Project(string? project) => (((project is not null) && m_projects.TryGetValue(key: project, value: out var directives)) ? directives : default);
    // A directive's identity in one compilation unit: an alias by its name, any other by what it names and how.
    private static string Key(UsingDirectiveSyntax directive) => ((directive.Alias is { } alias)
        ? $"alias {alias.Name.Identifier.ValueText}"
        : $"{(directive.StaticKeyword.IsKind(kind: SyntaxKind.None) ? "using" : "static")} {Named(directive: directive)}");

    /// <summary>What a directive names, spelled without <c>global::</c> or whitespace.</summary>
    /// <param name="directive">A using directive.</param>
    /// <returns>The namespace or type it names.</returns>
    public static string Named(UsingDirectiveSyntax directive) => (directive.NamespaceOrType?.WithoutTrivia().NormalizeWhitespace().ToFullString().Replace(newValue: string.Empty, oldValue: "global::") ?? string.Empty);

    // The file with its project's directives written ahead of its own, and its own global usings left to the project. The
    // file's leading trivia (a header, a #define) stays on its first token.
    private static SyntaxTree WithUsings(SyntaxList<UsingDirectiveSyntax> prelude, SyntaxTree tree) {
        var root = tree.GetCompilationUnitRoot();

        if ((prelude.Count == 0) && root.Usings.All(predicate: static directive => directive.GlobalKeyword.IsKind(kind: SyntaxKind.None))) { return tree; }

        var first = root.GetFirstToken(includeZeroWidth: true);
        var leading = first.LeadingTrivia;

        root = root.ReplaceToken(newToken: first.WithLeadingTrivia(), oldToken: first);

        var own = root.Usings.Where(predicate: static directive => directive.GlobalKeyword.IsKind(kind: SyntaxKind.None)).ToArray();
        var stated = own.Select(selector: Key).ToHashSet(comparer: StringComparer.Ordinal);

        root = root.WithUsings(usings: SyntaxFactory.List(nodes: prelude.Where(predicate: directive => !stated.Contains(item: Key(directive: directive))).Concat(second: own)));

        var opening = root.GetFirstToken(includeZeroWidth: true);

        return tree.WithRootAndOptions(options: ParseOptions, root: root.ReplaceToken(newToken: opening.WithLeadingTrivia(trivia: leading), oldToken: opening));
    }
}
