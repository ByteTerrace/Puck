using Puck.Cli.Refusals;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the CLI's source census of the refusals counts exactly the projects whose sources declare the
/// refusals the World loads: the <c>src/</c> projects in <c>Puck.World</c>'s reference closure that tag an enum member
/// with <c>[Refusal(…)]</c>. A project dropped from the census, or one the census counts that declares none, fails here,
/// so the census canary's agreement with the World's scan cannot pass by both sides dropping the same door.
/// </summary>
public sealed class RefusalCensusLawTests {
    [Fact]
    public void TheCensusCountsExactlyTheProjectsThatDeclareTheWorldsRefusals() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.Equal(
            expected: RefusalDeclaringProjects.Of(repositoryRoot: repositoryRoot),
            actual: [.. RefusalCensus.Projects.Order(comparer: StringComparer.Ordinal)]
        );

        var census = RefusalCensus.Count(repositoryRoot: repositoryRoot);

        Assert.True(condition: (census.Refusals > census.Doors), userMessage: $"{census}");
    }
}
