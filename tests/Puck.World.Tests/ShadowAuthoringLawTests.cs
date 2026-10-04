using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The document load boundary owns shadow identities, policy bounds, and handoff progress refusals.</summary>
public sealed class ShadowAuthoringLawTests {
    [Fact]
    public void ZeroDirectionalAngularRadiusRefusesWhileAPositivePenumbraLoads() {
        var document = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Name: "sun", Shadow: WorldShadowMode.Always)])),
        };
        Accepts(document: document);
        var json = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: document))!;
        json["render"]!["lighting"]!["lights"]![0]!["angularRadius"] = 0;
        Assert.False(condition: WorldDefinitionLoader.TryLoad(utf8: Encoding.UTF8.GetBytes(s: json.ToJsonString()), sourceName: "light-view.world.json", definition: out _, reason: out var reason));
        Assert.Contains(expectedSubstring: "angularRadius", actualString: reason, comparisonType: StringComparison.Ordinal);
    }
    private static WorldQualityPreset Policy(int slots = 1, int fades = 1, uint ticks = 8,
        WorldShadowOverflow overflow = WorldShadowOverflow.Instant) => new(
            Shadows: ShadowTier.High, AmbientOcclusion: false, RenderScale: 1f,
            ShadowLights: slots, ShadowFadeSlots: fades, ShadowFadeTicks: ticks, ShadowOverflow: overflow);
    private static WorldDefinition Document(string row, WorldQualityPreset policy) {
        var render = row switch {
            "render" => new WorldRenderDefaults(ShadowLights: policy.ShadowLights, ShadowFadeSlots: policy.ShadowFadeSlots,
                ShadowFadeTicks: policy.ShadowFadeTicks, ShadowOverflow: policy.ShadowOverflow),
            "render.low" => new WorldRenderDefaults(LowRaw: policy),
            "render.medium" => new WorldRenderDefaults(MediumRaw: policy),
            "render.high" => new WorldRenderDefaults(HighRaw: policy),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(row)),
        };

        return Fixtures.BuildDocument() with { RenderRaw = render };
    }
    private static bool Load(WorldDefinition document, out WorldDefinition? loaded, out string reason) =>
        WorldDefinitionLoader.TryLoad(utf8: WorldDefinitionSerialization.Serialize(definition: document),
            sourceName: "s60-shadow-authoring.world.json", definition: out loaded, reason: out reason);
    private static void Refuses(WorldDefinition document, string message) {
        Assert.False(condition: Load(document: document, loaded: out var loaded, reason: out var reason));
        Assert.Null(@object: loaded);
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: message);
    }
    private static void Accepts(WorldDefinition document) {
        Assert.True(condition: Load(document: document, loaded: out var loaded, reason: out var reason), userMessage: reason);
        Assert.NotNull(@object: loaded);
    }

    [InlineData("render")]
    [InlineData("render.low")]
    [InlineData("render.medium")]
    [InlineData("render.high")]
    [Theory]
    public void PositiveFadeWithoutAFadeSlotRefusesAtEveryReachableTier(string row) {
        Refuses(document: Document(row, Policy(fades: 0)),
            message: $"{row}: shadowLights 1 with shadowFadeSlots 0 and shadowFadeTicks 8 never fades; give it a fade slot (shadowFadeSlots 1 or 2) or set shadowFadeTicks to 0 for instant handoffs.");
        Accepts(document: Document(row, Policy()));
    }
    [InlineData("render")]
    [InlineData("render.low")]
    [InlineData("render.medium")]
    [InlineData("render.high")]
    [Theory]
    public void FadeSlotWithoutTicksRefusesAtEveryReachableTier(string row) {
        Refuses(document: Document(row, Policy(ticks: 0)),
            message: $"{row}: shadowFadeSlots 1 with shadowFadeTicks 0 reserves fade slots that never fade; set shadowFadeTicks above 0, or shadowFadeSlots to 0 for instant handoffs.");
        Accepts(document: Document(row, Policy()));
    }
    [InlineData("render")]
    [InlineData("render.low")]
    [InlineData("render.medium")]
    [InlineData("render.high")]
    [Theory]
    public void QueueWithoutProgressRefusesAtEveryReachableTier(string row) {
        foreach (var (fades, ticks) in new (int, uint)[] { (0, 0), (0, 8), (1, 0) }) {
            Refuses(document: Document(row, Policy(fades: fades, ticks: ticks, overflow: WorldShadowOverflow.Queue)),
                message: $"{row}: shadowOverflow queue with shadowLights 1 never progresses while shadowFadeSlots is {fades} and shadowFadeTicks is {ticks}; give it a fade slot and a positive shadowFadeTicks, or set shadowOverflow to instant.");
        }
        Accepts(document: Document(row, Policy(overflow: WorldShadowOverflow.Queue)));
    }
    [InlineData("render")]
    [InlineData("render.low")]
    [InlineData("render.medium")]
    [InlineData("render.high")]
    [Theory]
    public void SlotBoundsRefuseByNameAtEveryReachableTier(string row) {
        foreach (var slots in new[] { -1, 5 }) {
            Refuses(document: Document(row, Policy(slots: slots)), message: $"{row}.shadowLights must be between 0 and 4.");
        }
        foreach (var fades in new[] { -1, 3 }) {
            Refuses(document: Document(row, Policy(fades: fades)), message: $"{row}.shadowFadeSlots must be between 0 and 2.");
        }
        Accepts(document: Document(row, Policy(slots: 4, fades: 2)));
    }
    [InlineData("render")]
    [InlineData("render.low")]
    [InlineData("render.medium")]
    [InlineData("render.high")]
    [Theory]
    public void AnInertPolicyAndAnInstantPolicyNeedNoFadeCapacity(string row) {
        Accepts(document: Document(row, Policy(slots: 0, fades: 0, ticks: 0)));
        Accepts(document: Document(row, Policy(slots: 1, fades: 0, ticks: 0)));
        Accepts(document: Document(row, Policy(fades: 0, overflow: WorldShadowOverflow.Queue, slots: 0, ticks: 0)));
    }
    [InlineData(WorldShadowMode.Always)]
    [InlineData(WorldShadowMode.Auto)]
    [Theory]
    public void AShadowCandidateNeedsANameAtLoad(WorldShadowMode mode) {
        var document = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Lighting: new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Shadow: mode),
            ])),
        };

        Refuses(document: document,
            message: "render.lighting.lights[0].name is required when shadow is always or auto; a shadow-capable light needs a name.");
        Accepts(document: document with {
            RenderRaw = document.Render with {
                Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Shadow: mode, Name: "sun")]),
            },
        });
    }
    [Fact]
    public void OmittedOverflowDefaultsToInstantAtBootAndEveryPreset() {
        foreach (var row in new[] { "render", "render.low", "render.medium", "render.high" }) {
            var document = Document(row, Policy(fades: 0, ticks: 0));
            var json = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: document))!;
            var render = json["render"]!.AsObject();
            var policy = ((row == "render") ? render : render[row[7..]]!.AsObject());

            Assert.True(condition: policy.Remove(propertyName: "shadowOverflow"));
            Assert.True(condition: WorldDefinitionLoader.TryLoad(utf8: Encoding.UTF8.GetBytes(s: json.ToJsonString()),
                sourceName: "s60-shadow-default.world.json", definition: out var loaded, reason: out var reason), userMessage: reason);
            var overflow = row switch {
                "render" => loaded!.Render.ShadowOverflow,
                "render.low" => loaded!.Render.LowRaw!.Value.ShadowOverflow,
                "render.medium" => loaded!.Render.MediumRaw!.Value.ShadowOverflow,
                _ => loaded!.Render.HighRaw!.Value.ShadowOverflow,
            };

            Assert.Equal(actual: overflow, expected: WorldShadowOverflow.Instant);
        }
    }
    [Fact]
    public void MultipleNamedCandidatesAndUnnamedNeverLightsLoadTogether() {
        var document = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Lighting: new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Shadow: WorldShadowMode.Always, Name: "sun"),
                new WorldRenderLight.Directional(Shadow: WorldShadowMode.Auto, Name: "moon"),
                new WorldRenderLight.Directional(Shadow: WorldShadowMode.Never),
                new WorldRenderLight.Directional(),
            ])),
        };

        Assert.True(condition: Load(document: document, loaded: out var loaded, reason: out var reason), userMessage: reason);
        Assert.Equal(document.Render.Lighting!.Lights, loaded!.Render.Lighting!.Lights);
    }
    [Fact]
    public void TheRetiredDirectionalShadowsFieldRefusesByName() {
        var document = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Lighting: new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Shadow: WorldShadowMode.Always, Name: "sun"),
            ])),
        };
        var json = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: document))!;

        json["render"]!["lighting"]!["lights"]![0]!["shadows"] = true;

        Assert.False(condition: WorldDefinitionLoader.TryLoad(utf8: Encoding.UTF8.GetBytes(s: json.ToJsonString()),
            sourceName: "s60-shadow-authoring.world.json", definition: out _, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "shadows");
        Accepts(document: document);
    }
    [InlineData(WorldShadowMode.Always, "always", WorldShadowOverflow.Queue, "queue")]
    [InlineData(WorldShadowMode.Auto, "auto", WorldShadowOverflow.Instant, "instant")]
    [InlineData(WorldShadowMode.Never, "never", WorldShadowOverflow.Instant, "instant")]
    [Theory]
    public void ShadowEnumsWriteTheirAuthoredSpelling(WorldShadowMode mode, string modeName,
        WorldShadowOverflow overflow, string overflowName) {
        var document = Document("render", Policy(overflow: overflow));

        document = document with {
            RenderRaw = document.Render with {
                Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Shadow: mode, Name: "sun")]),
            },
        };
        var json = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: document))!;

        Assert.Equal(modeName, json["render"]!["lighting"]!["lights"]![0]!["shadow"]!.GetValue<string>());
        Assert.Equal(overflowName, json["render"]!["shadowOverflow"]!.GetValue<string>());
        Accepts(document: document);
    }
}
