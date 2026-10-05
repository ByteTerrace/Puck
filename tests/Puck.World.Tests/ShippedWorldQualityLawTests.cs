using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.Abstractions.Presentation;
using Puck.Testing;
using Puck.SignedDistance;
using Puck.World.Transpiler;

using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: every world the game ships answers <c>world.quality</c> from one preset table. The
/// table is authored once, in <c>quality.puck</c>, which the standard world imports and every presenting world reaches
/// through its basis or an import of its own, so each composes exactly that table. The table authors nothing but the
/// three presets, and a document merges member by member, so importing it moves none of a world's own render levers:
/// a world boots on the render it booted on before, and only a chosen tier changes it.</summary>
public sealed class ShippedWorldQualityLawTests(ShippedWorldQualityLawTests.Staging staging) : IClassFixture<ShippedWorldQualityLawTests.Staging> {
    private const string QualitySource = $"{ShippedWorldDocuments.WorldDirectory}/quality.puck";

    // The one table, as its source compiles.
    private static JsonObject QualityDocument() {
        var compilation = WorldCompiler.CompileFile(
            allowMultiple: true,
            path: RepositoryPaths.Resolve(relativePath: QualitySource)
        );

        Assert.True(
            condition: compilation.Success,
            userMessage: $"{QualitySource} does not compile."
        );

        return compilation.RequireJson();
    }
    private static WorldRenderDefaults QualityTable() {
        Assert.True(
            condition: WorldJsonPayload.TryParse(
                error: out var error,
                info: ((JsonTypeInfo<WorldRenderDefaults>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldRenderDefaults))),
                json: QualityDocument()["render"]!.ToJsonString(),
                value: out var table
            ),
            userMessage: error
        );

        return table;
    }

    public static TheoryData<string> Shipped() => [.. WorldDocumentCorpus.ShippedDocuments()
        .Where(predicate: static path => path.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: $"{ShippedWorldDocuments.WorldDirectory}/"
        ))
        .Order(comparer: StringComparer.Ordinal)];
    [Fact]
    public void TheTableAuthorsTheThreePresetsAndNothingElse() {
        var document = QualityDocument();

        Assert.Equal(
            actual: document.Select(selector: static member => member.Key),
            expected: ["render"]
        );
        Assert.Equal(
            actual: document["render"]!.AsObject().Select(selector: static member => member.Key).Order(comparer: StringComparer.Ordinal),
            expected: QualityTiers.Names.Order(comparer: StringComparer.Ordinal)
        );

        var table = QualityTable();

        Assert.Equal(
            actual: table.Preset(tier: QualityTier.Low),
            expected: new WorldQualityPreset(
                AmbientOcclusion: false,
                RenderScale: WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.Half),
                Shadows: ShadowTier.Off,
                Indirect: SdfIndirectTier.Off,
                Sky: WorldSkyTier.Low,
                Temporal: false
            )
        );
        Assert.True(condition: (table.Preset(tier: QualityTier.Medium)?.Temporal ?? false));
        Assert.True(condition: (table.Preset(tier: QualityTier.High)?.Temporal ?? false));
        Assert.Equal(SdfIndirectTier.Medium, table.Preset(tier: QualityTier.Medium)?.Indirect);
        Assert.Equal(SdfIndirectTier.High, table.Preset(tier: QualityTier.High)?.Indirect);
        Assert.Equal(
            actual: (table with { LowRaw = null, MediumRaw = null, HighRaw = null }),
            expected: WorldRenderDefaults.Absent
        );
    }
    [MemberData(nameof(Shipped))]
    [Theory]
    public void EveryPresentingShippedWorldAnswersEachTierFromTheOneTable(string relativePath) {
        if (!WorldDocumentCorpus.TryBoot(
            path: RepositoryPaths.Resolve(relativePath: relativePath),
            reason: out var refusal,
            stagingDirectory: staging.For(relativePath: relativePath),
            worlds: out var worlds
        )) {
            Assert.True(
                condition: ((refusal == WorldDocumentCorpus.ModuleLibrary) || WorldDocumentCorpus.IsFragment(relativePath: relativePath)),
                userMessage: $"{relativePath} does not boot, and it is neither a module library nor a fragment: {refusal}"
            );

            return;
        }

        var table = QualityTable();

        foreach (var (source, definition) in worlds) {
            if (definition.Host.Presentation == WorldHostPresentation.None) {
                continue;
            }

            foreach (var tier in QualityTiers.All) {
                Assert.True(
                    condition: (definition.Render.Preset(tier: tier) == table.Preset(tier: tier)),
                    userMessage: $"{Path.GetFileName(path: source)} answers world.quality {QualityTiers.Name(tier: tier)} with {(definition.Render.Preset(tier: tier)?.ToString() ?? "no preset")}, not the shared table's."
                );
            }
        }
    }

    /// <summary>The directory composition sources stage their worlds into, deleted on dispose whatever the laws'
    /// outcome.</summary>
    public sealed class Staging : IDisposable {
        private readonly TemporaryDirectory m_directory = new();

        /// <inheritdoc/>
        public void Dispose() => m_directory.Dispose();
        /// <summary>Returns a fresh directory one boot of a composition source stages its worlds into.</summary>
        /// <param name="relativePath">The repository-relative path of the composition source.</param>
        /// <returns>The full path of the staging directory, created.</returns>
        public string For(string relativePath) => Directory.CreateDirectory(path: m_directory.PathOf(name: $"{relativePath}/{Guid.NewGuid():N}")).FullName;
    }
}
