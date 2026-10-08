using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed class WorldSkyInspectorLawTests {
    private sealed class Authority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;
            return true;
        }
    }

    [Fact]
    public void InspectorSkyIsTheLightingEchoAndIncludesAirAndHeldTimelineWithoutSteadyAllocation() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new WorldRenderDefaults(Atmosphere: new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.0125f))),
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 1d)]),
        };
        using var row = HostRow.Build(definition: definition, name: "p18-12-inspector");
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(instance: row.Instance))]);
        var lighting = registry.Submit(line: "world.lighting");

        Assert.False(condition: lighting.IsError, userMessage: lighting.Output);
        var first = lighting.Output!.IndexOf(comparisonType: StringComparison.Ordinal, value: "sky layers=");
        var last = lighting.Output.IndexOf(comparisonType: StringComparison.Ordinal, startIndex: first, value: " | indirect");
        var sky = lighting.Output[first..last];

        Assert.Equal(WorldLightingText.DescribeSky(definition.Render.Sky, definition.Render.Atmosphere), sky);
        var mirror = ClientFixtures.StateMirror(row.Server.Definition);

        Assert.True(condition: mirror.ControlClock(name: "day", operation: WorldTimelineOperation.At, rate: 1d, refusal: out var refusal, tick: 12600), userMessage: refusal);
        var snapshot = new WorldInspectorSnapshot { Definition = row.Server.Definition, Mirror = mirror, ReloadError = "none" };
        var formatter = new WorldInspectorText();

        void Format() { formatter.Format(snapshot: in snapshot); formatter.Finish(); }
        Format();
        var text = new string(value: formatter.Text);

        Assert.Contains(actualString: text, expectedSubstring: "sky layers=default | atmosphere fog=on fogDensity=0.0125");
        Assert.Contains("fog=default", WorldLightingText.DescribeSky(atmosphere: null, sky: null));
        Assert.Contains(actualString: text, expectedSubstring: "timeline clocks=1");
        Assert.Contains(actualString: text, expectedSubstring: "held=True");
        Assert.Contains(actualString: text, expectedSubstring: "tick=12600+0 phase=0.25");
        Assert.Equal(0L, AllocationWindow.Least(Format));

        // Replacing only atmosphere must invalidate the shared inspector text as well.
        var changed = snapshot with {
            Definition = definition with {
                RenderRaw = definition.Render with {
                    Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.025f)),
                },
            },
        };

        formatter.Format(snapshot: in changed);
        formatter.Finish();
        Assert.Contains("fogDensity=0.025", new string(value: formatter.Text));
    }
}
