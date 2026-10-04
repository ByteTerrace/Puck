using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

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
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 1d)]),
        };
        using var row = HostRow.Build(name: "p18-12-inspector", definition: definition);
        var registry = new CommandRegistry(modules: [new WorldLightingCommandModule(new Authority(row.Instance))]);
        var lighting = registry.Submit("world.lighting");
        Assert.False(lighting.IsError, lighting.Output);
        var first = lighting.Output!.IndexOf("sky layers=", StringComparison.Ordinal);
        var last = lighting.Output.IndexOf(" | environment", first, StringComparison.Ordinal);
        var sky = lighting.Output[first..last];
        Assert.Equal(WorldLightingText.DescribeSky(definition.Render.Sky, definition.Render.Atmosphere), sky);
        var mirror = ClientFixtures.StateMirror(row.Server.Definition);
        Assert.True(mirror.ControlClock("day", WorldTimelineOperation.At, 12600, 1d, out var refusal), refusal);
        var snapshot = new WorldInspectorSnapshot { Definition = row.Server.Definition, Mirror = mirror, ReloadError = "none" };
        var formatter = new WorldInspectorText();
        void Format() { formatter.Format(in snapshot); formatter.Finish(); }
        Format();
        var text = new string(formatter.Text);
        Assert.Contains("sky layers=default | atmosphere fog=on fogDensity=0.0125", text);
        Assert.Contains("fog=default", WorldLightingText.DescribeSky(null, null));
        Assert.Contains("timeline clocks=1", text);
        Assert.Contains("held=True", text);
        Assert.Contains("tick=12600+0 phase=0.25", text);
        Assert.False(formatter.Refused);
        Assert.Equal(0L, AllocationWindow.Least(Format));

        // Replacing only atmosphere must invalidate the shared inspector text as well.
        var changed = snapshot with { Definition = definition with { RenderRaw = definition.Render with {
            Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.025f)),
        } } };
        formatter.Format(in changed);
        formatter.Finish();
        Assert.Contains("fogDensity=0.025", new string(formatter.Text));
    }
}
