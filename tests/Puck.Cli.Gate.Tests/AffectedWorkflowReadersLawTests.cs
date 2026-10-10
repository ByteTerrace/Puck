using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a change to a GitHub Actions workflow or composite action selects exactly the suites whose laws
/// read that tree, so <c>puck gate</c> runs them for a workflow-only change. A project declares what it reads under
/// <c>.github/</c> with an item in its project file that includes it (<c>PuckAffectedInput</c>), and only that
/// declaration reaches it: a source that names a workflow in a comment, or as a path a test compares, selects nothing.
/// A suite reads the tree when its C# names <c>.github</c> as a path segment in a string outside an attribute argument;
/// an attribute argument is a datum a test compares, and a law that opens a file names its path in its own code.
/// </summary>
public sealed partial class AffectedWorkflowReadersLawTests {
    private static readonly string Root = RepositoryPaths.RequireRoot();

    [GeneratedRegex(pattern: "<ProjectReference Include=\"(?<path>[^\"]+)\"")]
    private static partial Regex ProjectReference();
    [GeneratedRegex(pattern: "<Compile Include=\"(?<path>[^\"]+\\.cs)\"")]
    private static partial Regex LinkedSource();
    [GeneratedRegex(pattern: @"(^|[/\\])\.github($|[/\\])")]
    private static partial Regex WorkflowTree();
    // Every project under src/ and tests/, as selection sees it, with its project file.
    private static (AffectedProject Project, string File)[] Projects(string root) => [
        .. ((string[])["src", "tests"]).Where(predicate: parent => Directory.Exists(path: Path.Combine(path1: root, path2: parent))).SelectMany(selector: parent => Directory.EnumerateDirectories(path: Path.Combine(path1: root, path2: parent))
            .Select(selector: directory => Path.Combine(path1: directory, path2: (Path.GetFileName(path: directory) + ".csproj")))
            .Where(predicate: File.Exists)
            .Order(comparer: StringComparer.Ordinal)
            .Select(selector: file => (new AffectedProject(
                Directory: $"{parent}/{Path.GetFileName(path: Path.GetDirectoryName(path: file))}",
                IsSuite: (parent == "tests"),
                Name: Path.GetFileNameWithoutExtension(path: file),
                References: [.. ProjectReference().Matches(input: File.ReadAllText(path: file)).Select(selector: match => Path.GetFileNameWithoutExtension(path: match.Groups["path"].Value))]
            ), file))),
    ];
    // Whether a source names .github in a string a law could open: outside comments and outside attribute arguments.
    private static bool ReadsWorkflows(string source) => CSharpSyntaxTree.ParseText(text: source).GetRoot().DescendantNodes()
        .Where(predicate: node => (((node is LiteralExpressionSyntax literal) && literal.Token.IsKind(kind: SyntaxKind.StringLiteralToken)) || (node is InterpolatedStringTextSyntax)))
        .Where(predicate: node => !node.Ancestors().OfType<AttributeArgumentSyntax>().Any())
        .Any(predicate: node => WorkflowTree().IsMatch(input: ((node is LiteralExpressionSyntax literal) ? literal.Token.ValueText : ((InterpolatedStringTextSyntax)node).TextToken.ValueText)));
    // The C# a project compiles: its own sources and those its project file links in from elsewhere.
    private static IEnumerable<string> Sources(string root, AffectedProject project, string file) =>
        Directory.EnumerateFiles(path: Path.Combine(path1: root, path2: project.Directory), searchOption: SearchOption.AllDirectories, searchPattern: "*.cs")
            .Where(predicate: source => (!source.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !source.Contains(comparisonType: StringComparison.Ordinal, value: $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
            .Concat(second: LinkedSource().Matches(input: File.ReadAllText(path: file)).Select(selector: match => Path.GetFullPath(path: Path.Combine(path1: Path.GetDirectoryName(path: file)!, path2: match.Groups["path"].Value))).Where(predicate: File.Exists));
    // The suites a change under .github must select: every suite whose sources read it.
    private static string[] Readers(string root, (AffectedProject Project, string File)[] projects) => [
        .. projects.Where(predicate: entry => (entry.Project.IsSuite && Sources(file: entry.File, project: entry.Project, root: root).Any(predicate: source => ReadsWorkflows(source: File.ReadAllText(path: source)))))
            .Select(selector: entry => entry.Project.Name)
            .Order(comparer: StringComparer.Ordinal),
    ];
    private static AffectedPlan Select(string root, AffectedProject[] projects, string changed) => AffectedSelection.Select(
        canaries: [],
        canariesReaching: _ => new HashSet<string>(),
        catalogInputs: (_, _) => false,
        changed: [changed],
        consumersOf: AffectedConsumers.Search(projects: projects, repositoryRoot: root),
        coverage: new Dictionary<string, IReadOnlySet<string>>(),
        declaresTests: _ => false,
        linkedBy: AffectedConsumers.Linking(projects: projects, repositoryRoot: root),
        projects: projects,
        standInsFor: _ => [],
        worldClosure: new HashSet<string>(),
        worldInput: _ => false
    );

    [Fact]
    public void EveryWorkflowChangeSelectsExactlyTheSuitesThatReadWorkflows() {
        var projects = Projects(root: Root);
        var readers = Readers(projects: projects, root: Root);
        var tree = Path.Combine(path1: Root, path2: ".github");

        Assert.NotEmpty(collection: readers);
        foreach (var file in Directory.EnumerateFiles(path: tree, searchOption: SearchOption.AllDirectories, searchPattern: "*").Order(comparer: StringComparer.Ordinal)) {
            var changed = Path.GetRelativePath(path: file, relativeTo: Root).Replace(newChar: '/', oldChar: '\\');

            Assert.Equal(expected: readers, actual: Select(changed: changed, projects: [.. projects.Select(selector: entry => entry.Project)], root: Root).Suites);
        }
    }
    [Fact]
    public void AWorkflowNamedInProseOrAsADatumSelectsNothing() {
        using var scratch = new TemporaryDirectory();

        _ = scratch.WriteText(name: ".github/workflows/build.yml", text: "name: Build\n");
        _ = scratch.WriteText(name: "src/Format/Format.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _ = scratch.WriteText(name: "src/Format/Selection.cs", text: "// KEEP IN SYNC with .github/workflows/build.yml.\nclass Selection { }");
        _ = scratch.WriteText(name: "tests/Format.Tests/Format.Tests.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"../../src/Format/Format.csproj\" /></ItemGroup></Project>");
        _ = scratch.WriteText(name: "tests/Format.Tests/SelectionTests.cs", text: "class SelectionTests { [InlineData(\".github/workflows/build.yml\")] void Excluded(string path) { } }");
        _ = scratch.WriteText(name: "tests/Graph.Tests/Graph.Tests.csproj", text: "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PuckAffectedInput Include=\"../../.github/**\" /></ItemGroup></Project>");
        _ = scratch.WriteText(name: "tests/Graph.Tests/GraphLaws.cs", text: "class GraphLaws { string Tree => System.IO.Path.Combine(\"root\", \".github\"); }");

        var projects = Projects(root: scratch.RootPath);

        Assert.Equal(expected: ["Graph.Tests"], actual: Readers(projects: projects, root: scratch.RootPath));
        Assert.Equal(expected: ["Graph.Tests"], actual: Select(changed: ".github/workflows/build.yml", projects: [.. projects.Select(selector: entry => entry.Project)], root: scratch.RootPath).Suites);
    }
}
