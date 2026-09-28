using System.Globalization;
using Puck.Commands;
using Puck.Hosting;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>render.lighting</c>/<c>render.sky</c>/<c>render.environment</c>/
/// <c>render.tonemap</c> read-back: <c>world.lighting</c> reports every authored light by slot, the curvature
/// enrichment, every sky layer, the studio-reflection softbox count and horizon colors, and the tonemap mode.
/// Clock-keyed fields report their clock and key count. The sections are
/// authored through <c>world.row.set render</c>; every field is optional and an absent one reads <c>default</c>,
/// which is the engine's pinned value for that field of that kind, not zero.
/// </summary>
public sealed class WorldLightingCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private static string Describe(float? value) => ((value is { } number)
        ? number.ToString(
            format: "0.####",
            provider: CultureInfo.InvariantCulture
        )
        : "default"
    );
    private static string Describe(bool? value) => ((value is { } flag)
        ? (flag
            ? "true"
            : "false")
        : "default"
    );
    private static string Describe(int? value) => ((value is { } number)
        ? number.ToString(provider: CultureInfo.InvariantCulture)
        : "default"
    );
    private static string Describe(uint? value) => ((value is { } number)
        ? number.ToString(provider: CultureInfo.InvariantCulture)
        : "default"
    );
    private static string Keyed<T>(WorldKeys<T> keys) => $"keys({keys.Clock},{keys.Keys.Count})";
    private static string Describe(BindableScalar? value) => value?.Keys is { } keys ? Keyed(keys)
        : value?.Literal is { } literal ? Describe((float?)literal) : value?.Binding ?? "default";
    private static string Describe(BindableAngle? value) => Describe(value?.Value);
    private static string Describe(BindableColor? color) => color?.Keys is { } keys ? Keyed(keys) : color?.Raw ?? "default";
    private static string Describe(BindableVector3? vector) => vector?.Keys is { } keys ? Keyed(keys)
        : vector is { } value ? $"{Describe(value.X)},{Describe(value.Y)},{Describe(value.Z)}" : "default";
    private static string Describe(BindableDirection? vector) => vector?.Keys is { } keys ? Keyed(keys) : Describe(vector?.Value);
    private static string Describe(BindableVector2? vector) => vector?.Keys is { } keys ? Keyed(keys)
        : vector is { } value ? $"{Describe(value.X)},{Describe(value.Y)}" : "default";
    private static CommandEcho DescribeLayer(CommandEcho echo, int index, WorldRenderSkyLayer layer) {
        echo = echo.Head(head: $"sky[{index}]");

        switch (layer) {
            case WorldRenderSkyLayer.Gradient gradient: {
                    echo = echo.Field(
                        key: "type",
                        value: "gradient"
                    );

                    if (gradient.Stops is { } stops) {
                        for (var stop = 0; (stop < stops.Count); stop++) {
                            echo = echo.Field(
                                key: $"stop{stop}",
                                value: $"{Describe(value: stops[stop]?.Elevation)}:{Describe(color: stops[stop]?.Color)}"
                            );
                        }
                    }

                    return echo;
                }
            case WorldRenderSkyLayer.Fog fog: {
                    return echo
                        .Field(
                        key: "type",
                        value: "fog"
                    )
                        .Field(
                        key: "density",
                        value: Describe(value: fog.Density)
                    );
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    return echo
                        .Field(
                        key: "type",
                        value: "sunDisc"
                    )
                        .Field(
                        key: "light",
                        value: Describe(value: disc.Light)
                    )
                        .Field(
                        key: "radius",
                        value: Describe(value: disc.Radius)
                    )
                        .Field(
                        key: "intensity",
                        value: Describe(value: disc.Intensity)
                    );
                }
            case WorldRenderSkyLayer.Stars stars: {
                    return echo
                        .Field(
                        key: "type",
                        value: "stars"
                    )
                        .Field(
                        key: "density",
                        value: Describe(value: stars.Density)
                    )
                        .Field(
                        key: "brightness",
                        value: Describe(value: stars.Brightness)
                    )
                        .Field(
                        key: "seed",
                        value: Describe(value: stars.Seed)
                    )
                        .Field(
                        key: "twinkle",
                        value: ((stars.Twinkle is { } twinkle)
                        ? $"{Describe(value: twinkle.Share)}/{Describe(value: twinkle.Depth)}/{Describe(value: twinkle.Rate)}"
                        : "none")
                    );
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    return echo
                        .Field(
                        key: "type",
                        value: "clouds"
                    )
                        .Field(
                        key: "coverage",
                        value: Describe(value: clouds.Coverage)
                    )
                        .Field(
                        key: "softness",
                        value: Describe(value: clouds.Softness)
                    )
                        .Field(
                        key: "scale",
                        value: Describe(value: clouds.Scale)
                    )
                        .Field(
                        key: "seed",
                        value: Describe(value: clouds.Seed)
                    )
                        .Field(
                        key: "color",
                        value: Describe(color: clouds.Color)
                    )
                        .Field(
                        key: "drift",
                        value: Describe(vector: clouds.Drift)
                    )
                        .Field(
                        key: "spin",
                        value: Describe(value: clouds.Spin)
                    )
                        .Field(
                        key: "curl",
                        value: Describe(value: clouds.Curl)
                    )
                        .Field(
                        key: "shear",
                        value: Describe(vector: clouds.Shear)
                    );
                }
            default: {
                    return echo.Field(
                        key: "type",
                        value: "unknown"
                    );
                }
        }
    }
    private static CommandEcho DescribeLight(CommandEcho echo, int index, WorldRenderLight light) {
        echo = echo.Head(head: $"lights[{index}]");

        return (light switch {
            WorldRenderLight.Directional directional => echo
                .Field(
            key: "type",
            value: "directional"
        )
                .Field(
            key: "direction",
            value: Describe(vector: directional.Direction)
        )
                .Field(
            key: "color",
            value: Describe(color: directional.Color)
        )
                .Field(
            key: "weight",
            value: Describe(value: directional.Weight)
        )
                .Field(
            key: "angularRadius",
            value: Describe(value: directional.AngularRadius)
        )
                .Field(
            key: "shadows",
            value: Describe(value: directional.Shadows)
        ),
            WorldRenderLight.Hemisphere hemisphere => echo
                .Field(
            key: "type",
            value: "hemisphere"
        )
                .Field(
            key: "color",
            value: Describe(color: hemisphere.Color)
        )
                .Field(
            key: "base",
            value: Describe(value: hemisphere.Base)
        )
                .Field(
            key: "gradient",
            value: Describe(value: hemisphere.Gradient)
        ),
            WorldRenderLight.Rim rim => echo
                .Field(
            key: "type",
            value: "rim"
        )
                .Field(
            key: "color",
            value: Describe(color: rim.Color)
        )
                .Field(
            key: "weight",
            value: Describe(value: rim.Weight)
        )
                .Field(
            key: "power",
            value: Describe(value: rim.Power)
        ),
            WorldRenderLight.Occluder occluder => echo
                .Field(
            key: "type",
            value: "occluder"
        )
                .Field(
            key: "position",
            value: Describe(vector: occluder.Position)
        )
                .Field(
            key: "radius",
            value: Describe(value: occluder.Radius)
        )
                .Field(
            key: "weight",
            value: Describe(value: occluder.Weight)
        )
                .Field(
            key: "anchor",
            value: DescribeLightAnchor(anchor: occluder.Anchor)
        ),
            WorldRenderLight.Point point => echo
                .Field(
            key: "type",
            value: "point"
        )
                .Field(
            key: "position",
            value: Describe(vector: point.Position)
        )
                .Field(
            key: "radius",
            value: Describe(value: point.Radius)
        )
                .Field(
            key: "color",
            value: Describe(color: point.Color)
        )
                .Field(
            key: "weight",
            value: Describe(value: point.Weight)
        )
                .Field(
            key: "anchor",
            value: DescribeLightAnchor(anchor: point.Anchor)
        ),
            _ => echo.Field(
            key: "type",
            value: "unknown"
        ),
        });
    }
    private static string DescribeLightAnchor(WorldAnchor? anchor) => anchor switch {
        WorldAnchor.Entity entity => $"entity:{entity.Index}",
        WorldAnchor.EntityPart part => $"entityPart:{part.Index}/{part.PartId}",
        WorldAnchor.Placement placement => $"placement:{placement.PlacementId}/{placement.ShapeId}",
        _ => "none",
    };
    private static string DescribeLighting(WorldDefinition definition, ulong tick) {
        var rates = WorldPresentationRates.Of(definition).Cost;
        definition = WorldPresentationValues.Of(definition).Definition;
        var values = new WorldValueResolver(definition, new PresentedTick(tick, 0d));
        var lighting = definition.Render.Lighting;
        var curvature = lighting?.Curvature;
        var echo = CommandEcho.Open(verb: "world.lighting")
            .Head(head: "lights")
            .Field(
            key: "count",
            value: ((lighting?.Lights is { } authored)
            ? authored.Count.ToString(provider: CultureInfo.InvariantCulture)
            : "default")
        );

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
            .Field(
            key: "cavity",
            value: Describe(value: curvature?.Cavity)
        )
            .Field(
            key: "rim",
            value: Describe(value: curvature?.Rim)
        )
            .Field(
            key: "ink",
            value: Describe(value: curvature?.Ink)
        )
            .Field(
            key: "inkLow",
            value: Describe(value: curvature?.InkLow)
        )
            .Field(
            key: "inkHigh",
            value: Describe(value: curvature?.InkHigh)
        )
            .Field(
            key: "inkColor",
            value: Describe(color: curvature?.InkColor)
        )
            // The three gains share the runtime gate: all-zero costs the renderer nothing at all.
            .Field(
            key: "active",
            value: ((values.Scalar(curvature?.Cavity ?? 0f, 0d) > 0d) || (values.Scalar(curvature?.Rim ?? 0f, 0d) > 0d) || (values.Scalar(curvature?.Ink ?? 0f, 0d) > 0d))
        )
            .Segment()
            .Head(head: "sky")
            .Field(
            key: "layers",
            value: ((definition.Render.Sky?.Layers is { } authoredLayers)
            ? authoredLayers.Count.ToString(provider: CultureInfo.InvariantCulture)
            : "default")
        );

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
            .Field(
            key: "softboxes",
            value: ((environment?.Softboxes is { } softboxes)
            ? softboxes.Count.ToString(provider: CultureInfo.InvariantCulture)
            : "default")
        )
            .Field(
            key: "horizonLow",
            value: Describe(color: environment?.Horizon?.Low)
        )
            .Field(
            key: "horizonHigh",
            value: Describe(color: environment?.Horizon?.High)
        )
            .Segment()
            .Head(head: "tonemap")
            .Field(
            key: "mode",
            value: (definition.Render.Tonemap ?? WorldTonemap.None).ToString()
        )
            .Segment()
            .Head(head: "timeline")
            .Field(key: "pieces", value: rates.Pieces)
            .Field(key: "coefficients", value: rates.Coefficients)
            .Field(key: "coefficient-bytes", value: rates.CoefficientBytes)
            .Field(key: "rate-degree", value: rates.Degree);

        return echo.Close();
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return authority.CreateServerQueryCommand(
            description: "Reports the render.lighting, render.sky, render.environment, and render.tonemap census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): every light by slot with its kind and fields, the stylized curvature enrichment and whether its runtime gate is open, every sky layer by index, the studio-reflection softbox count and horizon colors, and the tonemap mode. Keyed fields name their clock and key count. An unauthored field reads 'default' — the engine's pinned value for it, not zero.",
            describe: server => DescribeLighting(definition: server.Definition, tick: server.CompletedEngineTicks),
            name: "world.lighting"
        );
    }
}
