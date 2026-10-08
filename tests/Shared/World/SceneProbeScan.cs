using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Puck.World.Testing;

/// <summary>The scan every suite that owns full hosts or scene probes holds its classes to: each class whose source
/// names a probe owner (<c>WorldBootHarness</c>, <c>WorldSceneEmitter</c>, <c>WorldFramePresenter</c>,
/// <c>SdfCompositionFrameSource</c> or <c>ComposedSdfWorldFixture</c>) sits in <see cref="SceneProbeCollection"/>, which
/// runs one class at a time beside the other parallel collections, or in a collection that disables parallelization and
/// so runs alone. Their reserved instruction streams are large even when the live world is empty, so parallel probe
/// classes multiply that temporary storage by the worker count.</summary>
internal static partial class SceneProbeScan {
    [GeneratedRegex(@"//[^\r\n]*|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentsPattern();
    // Type references include target-typed construction and fixture fields, not just explicit constructor calls.
    [GeneratedRegex(@"\b(?:WorldBootHarness|WorldSceneEmitter|WorldFramePresenter|SdfCompositionFrameSource|ComposedSdfWorldFixture)\b")]
    private static partial Regex ProbeOwnerPattern();

    /// <summary>Asserts that no two CPU scene-probe classes of <paramref name="assembly"/> run at once.</summary>
    /// <param name="assembly">The suite's assembly.</param>
    /// <param name="suiteDirectory">The suite's directory, repository-relative, whose sources the scan reads.</param>
    /// <param name="controls">Probe classes the scan must find, which prove it reads the suite it judges.</param>
    /// <param name="output">Where the count is reported.</param>
    public static void AssertProbesRunOneAtATime(Assembly assembly, string suiteDirectory, IReadOnlyList<Type> controls, ITestOutputHelper output) {
        var classes = assembly.GetTypes().Where(predicate: type => (!type.IsNested && type.GetMethods().Any(predicate: method =>
            method.GetCustomAttributesData().Any(predicate: attribute => typeof(FactAttribute).IsAssignableFrom(c: attribute.AttributeType)))))
            .ToDictionary(type => type.Name, StringComparer.Ordinal);
        var definitions = assembly.GetTypes().SelectMany(selector: type => type.GetCustomAttributesData())
            .Where(predicate: attribute => (attribute.AttributeType == typeof(CollectionDefinitionAttribute)))
            .ToDictionary(attribute => ((string)attribute.ConstructorArguments[0].Value!), attribute =>
                attribute.NamedArguments.Any(predicate: argument => ((argument.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization))
                    && (argument.TypedValue.Value is true))), StringComparer.Ordinal);
        var probes = new HashSet<Type>();

        foreach (var path in Directory.EnumerateFiles(path: RepositoryPaths.Resolve(relativePath: suiteDirectory), searchPattern: "*.cs")) {
            var name = Path.GetFileName(path: path).Split('.')[0];

            if (!classes.TryGetValue(key: name, value: out var type) || type.GetCustomAttributesData().Any(predicate: attribute =>
                ((attribute.AttributeType == typeof(TraitAttribute)) && (attribute.ConstructorArguments[0].Value is "Category")
                    && (attribute.ConstructorArguments[1].Value is "Gpu")))) {
                continue;
            }
            var code = CommentsPattern().Replace(input: File.ReadAllText(path: path), replacement: string.Empty);

            if (ProbeOwnerPattern().IsMatch(input: code)) { probes.Add(item: type); }
        }

        Assert.All(collection: controls, action: control => Assert.Contains(expected: control, set: probes));
        var parallel = probes.Where(predicate: type => {
            var collection = type.GetCustomAttributesData().SingleOrDefault(predicate: attribute => (attribute.AttributeType == typeof(CollectionAttribute)));

            if (collection is null) {
                return true;
            }
            var name = ((string)collection.ConstructorArguments[0].Value!);

            return ((name != SceneProbeCollection.Name) && (!definitions.TryGetValue(key: name, value: out var runsAlone) || !runsAlone));
        }).Select(selector: type => type.Name).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.False(condition: definitions[SceneProbeCollection.Name], userMessage: "the scene-probe collection runs beside the parallel collections, one class at a time");
        output.WriteLine(message: $"CPU probe classes: {probes.Count}; classes allowed beside another probe: {parallel.Length}; ceiling 0");
        Assert.Empty(collection: parallel);
    }
}
