using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class WorldCostSourceTests {
    [Fact]
    public void ExpandedContributorsKeepSeparateInstancesAndTheirDefiningLine() {
        var compilation = WorldCompiler.Compile(source: """
            module counter() {
              state { world { slot score = false } }
              rule tick {
                when score == false
                score = true
              }
            }
            use counter as first()
            use counter as second()
            """, defaultSchema: WorldDefinition.SchemaVersion, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport(""));
        var report = WorldCostAnalysis.Generate(compilation: compilation);

        Assert.Equal(2, report.Contributors.Count);
        Assert.Equal(["first$tick", "second$tick"], report.ContributorSources.Select(selector: static source => source.Name));
        Assert.Equal(["first", "second"], report.ContributorSources.Select(selector: static source => source.ModuleInstancePath));
        Assert.Equal(["/rules/0", "/rules/1"], report.ContributorSources.Select(selector: static source => source.JsonPointer));
        Assert.All(report.ContributorSources, static source => Assert.Equal(3, source.Line));
        Assert.True(condition: report.TotalBound.IsUnmodeled);
    }
}
