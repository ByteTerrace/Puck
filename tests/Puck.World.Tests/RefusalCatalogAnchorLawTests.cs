using System.Text.RegularExpressions;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the World's refusal catalog scans exactly the assemblies whose sources declare its refusals: the
/// <c>src/</c> projects in <c>Puck.World</c>'s reference closure that tag an enum member with <c>[Refusal(…)]</c>. An
/// assembly dropped from the catalog's anchors, or one anchored that declares none, fails here, and the catalog lists
/// as many refusals as those sources tag.
/// </summary>
public sealed partial class RefusalCatalogAnchorLawTests {
    [GeneratedRegex(pattern: @"^\s*\[Refusal\(", options: RegexOptions.Multiline)]
    private static partial Regex RefusalTag();

    [Fact]
    public void TheCatalogAnchorsExactlyTheProjectsThatDeclareTheWorldsRefusals() {
        var root = RepositoryPaths.RequireRoot();
        var projects = RefusalDeclaringProjects.Of(repositoryRoot: root);

        Assert.Equal(
            expected: projects,
            actual: [.. RefusalCatalog.AnchoredAssemblies.Select(selector: static assembly => assembly.GetName().Name!).Order(comparer: StringComparer.Ordinal)]
        );

        var tagged = projects.Sum(selector: project => Directory
            .EnumerateFiles(path: Path.Combine(path1: root, path2: "src", path3: project), searchPattern: "*.cs", searchOption: SearchOption.AllDirectories)
            .Where(predicate: static file => (!file.Replace(newChar: '/', oldChar: '\\').Contains(comparisonType: StringComparison.Ordinal, value: "/obj/") && !file.Replace(newChar: '/', oldChar: '\\').Contains(comparisonType: StringComparison.Ordinal, value: "/bin/")))
            .Sum(selector: static file => RefusalTag().Count(input: File.ReadAllText(path: file))));

        Assert.Equal(expected: tagged, actual: RefusalCatalog.All().Count);
    }
}
