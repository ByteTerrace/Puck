using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Malformed section keys are refused before expansion; the validator returns their path rather than throwing.</summary>
public sealed class KeyedSectionValidationLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void A_null_section_key_is_refused_by_name(bool lighting) {
        var definition = new WorldDefinition(
            TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock(Name: "day", PeriodSeconds: 1d)]),
            RenderRaw: (lighting
                ? new WorldRenderDefaults(Lighting: new WorldRenderLighting(
                    Clock: "day",
                    Lights: [new WorldRenderLight.Directional(Name: "sun")],
                    Keys: [null!]
                ))
                : new WorldRenderDefaults(Sky: new WorldRenderSky(
                    Clock: "day",
                    Layers: [new WorldRenderSkyLayer.Fog(Name: "haze")],
                    Keys: [null!]
                )))
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: $"render.{(lighting ? "lighting" : "sky")}.keys[0] must be a key");

        var valid = definition with {
            RenderRaw = (lighting
                ? definition.Render with {
                    Lighting = definition.Render.Lighting! with {
                        Keys = [new WorldRenderLightingKey(At: 0d, Lights: new Dictionary<string, WorldRenderLight> {
                        ["sun"] = new WorldRenderLight.Directional(Weight: 0.5f),
                    })],
                    },
                }
                : definition.Render with {
                    Sky = definition.Render.Sky! with {
                        Keys = [new WorldRenderSkyKey(At: 0d, Layers: new Dictionary<string, WorldRenderSkyLayer> {
                        ["haze"] = new WorldRenderSkyLayer.Fog(Density: 0.01f),
                    })],
                    },
                }),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: valid, reason: out var validReason), userMessage: validReason);
    }
}
