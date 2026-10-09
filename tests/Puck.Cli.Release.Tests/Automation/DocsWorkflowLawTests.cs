using Puck.Cli.Docs;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Puck.Cli.Release.Tests.Automation;

/// <summary>
/// The documentation workflow starts beside the solution build, before any CLI exists, so it runs DocFX itself rather than
/// through <c>puck docs build</c>. It must run exactly the command the verb runs: a workflow that dropped
/// <c>--warningsAsErrors</c> or named another configuration would publish a site the local verb refuses.
/// </summary>
public sealed class DocsWorkflowLawTests {
    [Fact]
    public void TheDocumentationWorkflowRunsTheDocfxCommandOfDocsBuild() {
        var stream = new YamlStream();

        using (var reader = new StreamReader(path: Path.Combine(path1: RepositoryPaths.RequireRoot(), path2: ".github/workflows/docs.yml"))) { stream.Load(input: reader); }
        var runs = ((YamlMappingNode)stream.Documents.Single().RootNode)["jobs"].AllNodes
            .OfType<YamlMappingNode>()
            .Select(selector: step => (step.Children.TryGetValue(key: new YamlScalarNode(value: "run"), value: out var run) ? ((YamlScalarNode)run).Value!.Trim() : null))
            .Where(predicate: run => ((run?.Contains(comparisonType: StringComparison.Ordinal, value: "docfx") ?? false) && !run.Contains(comparisonType: StringComparison.Ordinal, value: "tool restore")))
            .ToArray();

        Assert.Equal(expected: [("dotnet " + string.Join(separator: ' ', values: DocsBuildCommand.DocfxArguments))], actual: runs);
    }
}
