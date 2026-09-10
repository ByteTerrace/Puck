using System.Globalization;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>render.lighting</c>/<c>render.sky</c>/<c>render.environment</c>/<c>render.tonemap</c> read-back:
/// <c>world.lighting</c> reports every authored light by slot, the curvature enrichment, every sky layer, the
/// studio-reflection softbox count and horizon colors, the tonemap mode, and the state row a <c>render.cycle</c>
/// keys them on. The sections are authored through <c>world.row.set render</c>; every field is optional and an
/// absent one reads <c>default</c>, which is the engine's pinned value for that field of that kind, not zero.
/// </summary>
public sealed class WorldLightingCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private static string Describe(float? value) => ((value is { } number)
        ? number.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )
        : "default");
    private static string Describe(bool? value) => ((value is { } flag)
        ? (flag ? "true" : "false")
        : "default");
    private static string Describe(int? value) => ((value is { } number)
        ? number.ToString(provider: CultureInfo.InvariantCulture)
        : "default");
    private static string Describe(uint? value) => ((value is { } number)
        ? number.ToString(provider: CultureInfo.InvariantCulture)
        : "default");
    private static string Describe(BindableColor? color) => (color?.Raw ?? "default");
    private static string Describe(DocumentVector3? vector) => ((vector is { } value)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{value.X:0.####},{value.Y:0.####},{value.Z:0.####}"
        )
        : "default");
    private static string Describe(DocumentVector2? vector) => ((vector is { } value)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{value.X:0.####},{value.Y:0.####}"
        )
        : "default");
    private static CommandEcho DescribeLight(CommandEcho echo, int index, WorldRenderLight light) {
        echo = echo.Head(head: $"lights[{index}]");

        return (light switch {
            WorldRenderLight.Directional directional => echo
                .Field(key: "type", value: "directional")
                .Field(key: "direction", value: Describe(vector: directional.Direction))
                .Field(key: "color", value: Describe(color: directional.Color))
                .Field(key: "weight", value: Describe(value: directional.Weight))
                .Field(key: "angularRadius", value: Describe(value: directional.AngularRadius))
                .Field(key: "shadows", value: Describe(value: directional.Shadows)),
            WorldRenderLight.Hemisphere hemisphere => echo
                .Field(key: "type", value: "hemisphere")
                .Field(key: "color", value: Describe(color: hemisphere.Color))
                .Field(key: "base", value: Describe(value: hemisphere.Base))
                .Field(key: "gradient", value: Describe(value: hemisphere.Gradient)),
            WorldRenderLight.Rim rim => echo
                .Field(key: "type", value: "rim")
                .Field(key: "color", value: Describe(color: rim.Color))
                .Field(key: "weight", value: Describe(value: rim.Weight))
                .Field(key: "power", value: Describe(value: rim.Power)),
            _ => echo.Field(key: "type", value: "unknown"),
        });
    }
    private static CommandEcho DescribeLayer(CommandEcho echo, int index, WorldRenderSkyLayer layer) {
        echo = echo.Head(head: $"sky[{index}]");

        switch (layer) {
            case WorldRenderSkyLayer.Gradient gradient: {
                    echo = echo.Field(key: "type", value: "gradient");

                    if (gradient.Stops is { } stops) {
                        for (var stop = 0; (stop < stops.Count); stop++) {
                            echo = echo.Field(key: $"stop{stop}", value: $"{Describe(value: stops[stop]?.Elevation)}:{Describe(color: stops[stop]?.Color)}");
                        }
                    }

                    return echo;
                }
            case WorldRenderSkyLayer.Fog fog: {
                    return echo
                        .Field(key: "type", value: "fog")
                        .Field(key: "density", value: Describe(value: fog.Density));
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    return echo
                        .Field(key: "type", value: "sunDisc")
                        .Field(key: "light", value: Describe(value: disc.Light))
                        .Field(key: "radius", value: Describe(value: disc.Radius))
                        .Field(key: "intensity", value: Describe(value: disc.Intensity));
                }
            case WorldRenderSkyLayer.Stars stars: {
                    return echo
                        .Field(key: "type", value: "stars")
                        .Field(key: "density", value: Describe(value: stars.Density))
                        .Field(key: "brightness", value: Describe(value: stars.Brightness))
                        .Field(key: "seed", value: Describe(value: stars.Seed))
                        .Field(key: "twinkle", value: ((stars.Twinkle is { } twinkle)
                            ? $"{Describe(value: twinkle.Share)}/{Describe(value: twinkle.Depth)}/{Describe(value: twinkle.Rate)}"
                            : "none"));
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    return echo
                        .Field(key: "type", value: "clouds")
                        .Field(key: "coverage", value: Describe(value: clouds.Coverage))
                        .Field(key: "softness", value: Describe(value: clouds.Softness))
                        .Field(key: "scale", value: Describe(value: clouds.Scale))
                        .Field(key: "seed", value: Describe(value: clouds.Seed))
                        .Field(key: "color", value: Describe(color: clouds.Color))
                        .Field(key: "drift", value: Describe(vector: clouds.Drift))
                        .Field(key: "spin", value: Describe(value: clouds.Spin))
                        .Field(key: "curl", value: Describe(value: clouds.Curl))
                        .Field(key: "shear", value: Describe(vector: clouds.Shear));
                }
            default: {
                    return echo.Field(key: "type", value: "unknown");
                }
        }
    }
    private static string DescribeLighting(WorldDefinition definition) {
        var lighting = definition.Render.Lighting;
        var curvature = lighting?.Curvature;
        var echo = CommandEcho.Open(verb: "world.lighting")
            .Head(head: "lights")
            .Field(key: "count", value: ((lighting?.Lights is { } authored) ? authored.Count.ToString(provider: CultureInfo.InvariantCulture) : "default"));

        if (lighting?.Lights is { } lights) {
            for (var index = 0; (index < lights.Count); index++) {
                echo = DescribeLight(
                    echo: echo.Segment(),
                    index: index,
                    light: lights[index]
                );
            }
        }

        echo = echo
            .Segment()
            .Head(head: "curvature")
            .Field(key: "cavity", value: Describe(value: curvature?.Cavity))
            .Field(key: "rim", value: Describe(value: curvature?.Rim))
            .Field(key: "ink", value: Describe(value: curvature?.Ink))
            .Field(key: "inkLow", value: Describe(value: curvature?.InkLow))
            .Field(key: "inkHigh", value: Describe(value: curvature?.InkHigh))
            .Field(key: "inkColor", value: Describe(color: curvature?.InkColor))
            // The three gains share the runtime gate: all-zero costs the renderer nothing at all.
            .Field(key: "active", value: (((curvature?.Cavity ?? 0f) > 0f) || ((curvature?.Rim ?? 0f) > 0f) || ((curvature?.Ink ?? 0f) > 0f)))
            .Segment()
            .Head(head: "sky")
            .Field(key: "layers", value: ((definition.Render.Sky?.Layers is { } authoredLayers) ? authoredLayers.Count.ToString(provider: CultureInfo.InvariantCulture) : "default"));

        if (definition.Render.Sky?.Layers is { } layers) {
            for (var index = 0; (index < layers.Count); index++) {
                echo = DescribeLayer(
                    echo: echo.Segment(),
                    index: index,
                    layer: layers[index]
                );
            }
        }

        var environment = definition.Render.Environment;

        echo = echo
            .Segment()
            .Head(head: "environment")
            .Field(key: "softboxes", value: ((environment?.Softboxes is { } softboxes) ? softboxes.Count.ToString(provider: CultureInfo.InvariantCulture) : "default"))
            .Field(key: "horizonLow", value: Describe(color: environment?.Horizon?.Low))
            .Field(key: "horizonHigh", value: Describe(color: environment?.Horizon?.High))
            .Segment()
            .Head(head: "tonemap")
            .Field(key: "mode", value: (definition.Render.Tonemap ?? WorldTonemap.None).ToString())
            .Segment()
            .Head(head: "cycle");

        return ((definition.Render.Cycle is { } cycle)
            ? echo
                .Field(key: "state", value: cycle.State)
                .Field(key: "keys", value: cycle.Keys.Count)
                .Close()
            : echo
                .Field(key: "state", value: "none")
                .Close());
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.lighting",
            description: "Reports the render.lighting, render.sky, render.environment, and render.tonemap census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): every light by slot with its kind and fields, the stylized curvature enrichment and whether its runtime gate is open, every sky layer by index, the studio-reflection softbox count and horizon colors, the tonemap mode, and the state row a render.cycle keys them on. An unauthored field reads 'default' — the engine's pinned value for it, not zero.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args: args, verb: "world.lighting") is { } refusal) {
                    return refusal;
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.lighting"
                )) {
                    return error;
                }

                return new CommandResult(Output: DescribeLighting(definition: server.Definition));
            }
        );
    }
}
