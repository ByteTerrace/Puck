using Puck.Cli.Docs;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// The documentation workflow starts beside the solution build, before any CLI exists, so it runs DocFX itself rather than
/// through <c>puck docs build</c>. It must run exactly the command the verb runs: a workflow that dropped
/// <c>--warningsAsErrors</c> or named another configuration would publish a site the local verb refuses. Every job that
/// takes the site downloads it as its zip and leaves the extraction to <c>puck docs build --site</c>: the download action's
/// streaming extraction of the site's twelve thousand files fails on the Windows runner every time.
/// </summary>
public sealed class DocsWorkflowLawTests {
    private static IEnumerable<YamlMappingNode> Steps(string workflow) {
        var stream = new YamlStream();

        using (var reader = new StreamReader(path: Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: workflow))) { stream.Load(input: reader); }
        return ((YamlMappingNode)stream.Documents.Single().RootNode)["jobs"].AllNodes.OfType<YamlMappingNode>();
    }
    private static string? Value(YamlMappingNode node, string key) => (node.Children.TryGetValue(key: new YamlScalarNode(value: key), value: out var value) ? (value as YamlScalarNode)?.Value : null);

    [Fact]
    public void TheDocumentationWorkflowRunsTheDocfxCommandOfDocsBuild() {
        var runs = Steps(workflow: ".github/workflows/docs.yml")
            .Select(selector: step => Value(key: "run", node: step)?.Trim())
            .Where(predicate: run => ((run?.Contains(comparisonType: StringComparison.Ordinal, value: "docfx") ?? false) && !run.Contains(comparisonType: StringComparison.Ordinal, value: "tool restore")))
            .ToArray();

        Assert.Equal(expected: [("dotnet " + string.Join(separator: ' ', values: DocsBuildCommand.DocfxArguments))], actual: runs);
    }
    [Fact]
    public void EveryDownloadOfTheDocumentationSiteLeavesItsArchiveUnextracted() {
        var downloads = Directory.EnumerateFiles(path: Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: ".github/workflows"), searchPattern: "*.yml")
            .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: RepositoryPaths.RequireRoot()).Replace(newChar: '/', oldChar: '\\'))
            .SelectMany(selector: workflow => Steps(workflow: workflow).Select(selector: step => (workflow, step)))
            .Where(predicate: entry => ((Value(key: "uses", node: entry.step)?.StartsWith(comparisonType: StringComparison.Ordinal, value: "actions/download-artifact@") ?? false)
                && (entry.step.Children.TryGetValue(key: new YamlScalarNode(value: "with"), value: out var with) && (Value(key: "name", node: ((YamlMappingNode)with)) == "documentation"))))
            .ToArray();

        Assert.NotEmpty(collection: downloads);
        foreach (var (workflow, step) in downloads) {
            Assert.True(
                condition: (Value(key: "skip-decompress", node: ((YamlMappingNode)step["with"])) == "true"),
                userMessage: $"{workflow}: the download of the documentation site extracts it in the action rather than leaving the archive to puck docs build --site."
            );
        }
    }
}
