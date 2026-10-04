using System.Text.RegularExpressions;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Full host and scene probe laws run alone. Their reserved instruction streams are large even when the
/// live world is empty; parallel class fixtures multiply that temporary storage by the worker count.</summary>
[Collection(AllocationCollection.Name)]
public sealed partial class WorldRenderProbeAllocationLawTests(ITestOutputHelper output) {
    [Fact]
    public void NoCpuSceneProbeClassCanRunBesideAnotherCollection() {
        var assembly = typeof(WorldRenderProbeAllocationLawTests).Assembly;
        var classes = assembly.GetTypes().Where(predicate: type => (!type.IsNested && type.GetMethods().Any(predicate: method =>
            method.GetCustomAttributesData().Any(predicate: attribute => typeof(FactAttribute).IsAssignableFrom(c: attribute.AttributeType)))))
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        var definitions = assembly.GetTypes().SelectMany(selector: type => type.GetCustomAttributesData())
            .Where(predicate: attribute => (attribute.AttributeType == typeof(CollectionDefinitionAttribute)))
            .ToDictionary(attribute => ((string)attribute.ConstructorArguments[0].Value!), attribute =>
                attribute.NamedArguments.Any(predicate: argument => ((argument.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
                    && (argument.TypedValue.Value is true))), StringComparer.Ordinal);
        var probes = new HashSet<Type>();

        foreach (var path in Directory.EnumerateFiles(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.World.Tests"), searchPattern: "*.cs")) {
            var name = Path.GetFileName(path: path).Split('.')[0];

            if (!classes.TryGetValue(key: name, value: out var type) || type.GetCustomAttributesData().Any(predicate: attribute =>
                ((attribute.AttributeType == typeof(TraitAttribute)) && (attribute.ConstructorArguments[0].Value is "Category")
                    && (attribute.ConstructorArguments[1].Value is "Gpu")))) {
                continue;
            }
            var code = CommentsPattern().Replace(input: File.ReadAllText(path: path), replacement: string.Empty);

            if (ProbeOwnerPattern().IsMatch(input: code)) { probes.Add(item: type); }
        }

        Assert.Contains(expected: typeof(WorldBootCompositionLawTests), set: probes);
        Assert.Contains(expected: typeof(WorldRenderLeverFrameLawTests), set: probes);
        Assert.Contains(expected: typeof(WorldRenderEnvelopeLawTests), set: probes);
        var parallel = probes.Where(predicate: type => {
            var collection = type.GetCustomAttributesData().SingleOrDefault(predicate: attribute => (attribute.AttributeType == typeof(CollectionAttribute)));

            return ((collection is null) || !definitions.TryGetValue(key: ((string)collection.ConstructorArguments[0].Value!), value: out var runsAlone) || !runsAlone);
        }).Select(selector: type => type.Name).Order(comparer: StringComparer.Ordinal).ToArray();

        output.WriteLine(message: $"CPU probe classes: {probes.Count}; classes allowed beside another collection: {parallel.Length}; ceiling 0");
        Assert.Empty(collection: parallel);
    }

    [GeneratedRegex(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentsPattern();
    // Type references include target-typed construction and fixture fields, not just explicit constructor calls.
    [GeneratedRegex(@"\b(?:WorldBootHarness|WorldSceneEmitter|WorldFramePresenter|SdfCompositionFrameSource|ComposedSdfWorldFixture)\b")]
    private static partial Regex ProbeOwnerPattern();
}
