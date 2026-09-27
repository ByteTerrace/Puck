using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// The light stage reaches every light through one interface (<c>shade/sdf-light.hlsli</c>): each light, an environment
/// light or a bound screen's, is one <c>SdfLight</c>, and <c>sdfLightResponse</c> answers what it adds. So no other kernel
/// source branches on a light's kind, and the response answers every kind the instruction set generates.
/// </summary>
public sealed partial class SdfLightInterfaceLawTests {
    private const string Interface = "shade/sdf-light.hlsli";

    private static string Root => RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory);

    [Fact]
    public void OnlyTheLightInterfaceBranchesOnALightsKind() {
        var readers = Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
            .Where(predicate: static path => !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "isa/"))
            .Where(predicate: path => LightKindPattern().IsMatch(input: CodeOf(path: path)))
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: readers,
            expected: [Interface]
        );
    }
    [Fact]
    public void TheResponseAnswersEveryGeneratedLightKind() {
        var kinds = LightKindDefinitionPattern().Matches(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: "isa", path3: "sdf-isa.hlsli")))
            .Select(selector: static match => match.Groups[1].Value)
            .ToArray();
        var response = CodeOf(path: Interface);

        Assert.NotEmpty(collection: kinds);
        Assert.All(
            action: kind => Assert.Contains(
                actualString: response,
                expectedSubstring: $"light.kind == SDF_LIGHT_{kind}"
            ),
            collection: kinds
        );
    }

    // A source's code with its line comments removed, so a comment naming a kind is no branch on it.
    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"\bSDF_LIGHT_[A-Z_]+\b")]
    private static partial Regex LightKindPattern();
    [GeneratedRegex(pattern: @"#define\s+SDF_LIGHT_([A-Z_]+)\s")]
    private static partial Regex LightKindDefinitionPattern();
}
