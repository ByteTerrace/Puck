using System.Text.Json.Nodes;

using Json.Schema;

using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Pins the generated schema's completeness: every array declares <c>items</c>, every closed object's
/// property is typed (a residual gap is named explicitly, never silent), the row substrate's own schema tracks
/// <see cref="StateRowJsonConverter{TRow}.Shape"/>, the root and silo self-identify, and the composed island plus
/// every shipped fragment validates against the bundle.</summary>
public sealed class WorldSchemaCompletenessLawTests {
    private static readonly Lazy<JsonObject> BundleHolder = new(valueFactory: BuildBundle);
    private static readonly Lazy<JsonSchema> CompiledBundleHolder = new(valueFactory: () => SchemaVerdicts.Build(schema: Bundle));
    private static readonly Lazy<JsonObject> ComposedIslandHolder = new(valueFactory: ComposeIsland);

    private static JsonObject Bundle => BundleHolder.Value;
    private static JsonSchema CompiledBundle => CompiledBundleHolder.Value;
    // Composed once for every law that reads it; a law that changes it works on its own deep clone.
    private static JsonObject ComposedIsland => ComposedIslandHolder.Value;

    private static void AssertValidatesAgainstBundle(JsonNode instance, string label) {
        if (!CompiledBundle.Admits(instance: instance)) {
            Assert.Fail(message: $"{label} failed schema validation:\n{CompiledBundle.Explain(instance: instance)}");
        }
    }
    private static JsonObject BuildBundle() {
        var split = Puck.World.WorldSchema.Export(postProcessPackages: []);

        return Puck.World.WorldSchema.Bundle(split: split);
    }
    private static IEnumerable<object[]> FragmentPaths(string folder) =>
        ShippedWorldDocuments.Files(
            directory: Path.Combine(
                path1: RepositoryPaths.Resolve(relativePath: ShippedWorldDocuments.WorldDirectory),
                path2: folder
            ),
            option: SearchOption.TopDirectoryOnly
        ).Select(selector: static path => new object[] { path });
    private static JsonObject ComposeIsland() {
        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                documents: PuckDocumentComposer.Instance,
                path: IslandPath(),
                tree: out var tree,
                reason: out var reason
            ),
            userMessage: reason
        );

        return tree!;
    }
    private static string IslandPath() =>
        RepositoryPaths.Resolve(relativePath: $"{ShippedWorldDocuments.WorldDirectory}/{WorldDocumentName.DocumentFile(name: "puck")}");
    private static JsonNode ParseFragment(string path) =>
        (JsonNode.Parse(utf8Json: ShippedWorldDocuments.Read(path: path)) ?? throw new InvalidDataException(message: $"{path} did not parse as JSON."));
    // A document the old, fully-inlined bundle refused stays refused: additionalProperties:false at the root
    // (preserved verbatim by both the old inlining and the new $defs form) rejects a key the document model never
    // declared.
    [Fact]
    public void ADocumentTheOldBundleRefusedStillRefuses() {
        var corrupted = ((JsonObject)ComposedIsland.DeepClone());

        corrupted["thisPropertyWasNeverDeclaredByTheDocumentModel"] = true;

        Assert.False(
            condition: CompiledBundle.Admits(instance: corrupted),
            userMessage: "an undeclared top-level property validated — additionalProperties:false regressed."
        );
    }
    [MemberData(memberName: nameof(GameFragmentPaths))]
    [Theory]
    public void EveryGameFragmentValidatesAgainstTheBundle(string path) =>
        AssertValidatesAgainstBundle(
            instance: ParseFragment(path: path),
            label: path
        );
    [MemberData(memberName: nameof(ModuleFragmentPaths))]
    [Theory]
    public void EveryModuleFragmentValidatesAgainstTheBundle(string path) =>
        AssertValidatesAgainstBundle(
            instance: ParseFragment(path: path),
            label: path
        );
    public static IEnumerable<object[]> GameFragmentPaths() => FragmentPaths(folder: "games");
    public static IEnumerable<object[]> ModuleFragmentPaths() => FragmentPaths(folder: "modules");
    [Fact]
    public void TheComposedIslandValidatesAgainstTheBundle() =>
        AssertValidatesAgainstBundle(
            instance: ComposedIsland,
            label: IslandPath()
        );
}
