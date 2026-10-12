using Puck.SdfVm;
using Puck.Testing;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The independent field scale reaches live cameras and authored presets without changing scene resolution.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed class WorldSkyFieldScaleLawTests {
    [InlineData(0f)]
    [InlineData(.75f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void InvalidBootPresetAndLiveFractionsRefuse(float fraction) {
        foreach (var render in new WorldRenderDefaults[] {
            new(SkyFieldScale: fraction), new(LowRaw: new(ShadowTier.Off, false, 1f, SkyFieldScale: fraction)),
        }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidate(Fixtures.BuildDocument() with { RenderRaw = render },
                neighbours: null, reason: out var reason));
            Assert.Contains(actualString: reason, expectedSubstring: "skyFieldScale");
        }
        var settings = new WorldRenderSettings(defaults: WorldRenderDefaults.Absent);

        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => settings.SkyFieldScale = fraction);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => _ = new SdfViewQuality { SkyFieldScale = ((fraction == 0f) ? -1f : fraction) }.SkyFieldFraction);
        Assert.Equal(1f, settings.SkyFieldScale);
    }
    [Fact]
    public void HalfFieldRestrictionAndAuthoredDslSurviveComposition() {
        var half = new SdfViewQuality { SkyFieldScale = .5f };

        Assert.Equal(.5f, half.Restrict(other: default).SkyFieldFraction);
        Assert.Equal(.5f, default(SdfViewQuality).Restrict(other: half).SkyFieldFraction);
        Assert.Equal(1f, default(SdfViewQuality).SkyFieldFraction);
        using var files = new TemporaryDirectory(prefix: "sky-field-dsl-");
        var source = Path.Combine(path1: files.RootPath, path2: "field.puck");

        File.WriteAllText(contents: """
            schema: "puck.world.definition.v1"
            documentId: "field"
            render {
              skyFieldScale: 0.5
              low { shadows: Off ambientOcclusion: false renderScale: 1 skyFieldScale: 0.5 }
            }
            """, path: source);
        var compilation = WorldCompiler.CompileFile(path: source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport(sourceText: File.ReadAllText(path: source), filePath: source));
        Assert.Equal(.5m, compilation.RequireJson()["render"]!["skyFieldScale"]!.GetValue<decimal>());
        Assert.Equal(.5m, compilation.RequireJson()["render"]!["low"]!["skyFieldScale"]!.GetValue<decimal>());
    }
}
