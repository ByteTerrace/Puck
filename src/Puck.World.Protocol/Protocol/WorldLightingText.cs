using System.Globalization;
using Puck.Commands;

namespace Puck.World.Client;

/// <summary>The shared authored lighting and sky readout used by the console and the inspector.</summary>
public static class WorldLightingText {
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
    private static string Describe(BindableColor? color) => ((color is { } value)
        ? value.ToString()
        : "default"
    );
    private static string Describe(BindableScalar? value) => (value switch {
        null => "default",
        { Keys: { } keys } => keys.ToString(),
        { Binding: { } binding } => binding,
        { Literal: { } literal } => Describe(value: ((float?)literal)),
        _ => "default",
    });
    private static string Describe(BindableAngle? value) => Describe(value: value?.Value);
    private static string Describe(BindableDirection? direction) => ((direction is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####},{literal.Z:0.####}"
            )
            : value.ToString())
        : "default"
    );
    private static string Describe(BindableVector2? vector) => ((vector is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####}"
            )
            : value.ToString())
        : "default"
    );
    // Whether a gain can be positive: a literal or any key above zero, or a binding, whose value the echo cannot know.
    private static bool MayBePositive(BindableScalar? value) => (
        (value?.Binding is not null) ||
        (value?.AuthoredValues().Any(predicate: static number => (number > 0f)) ?? false)
    );
    private static string DescribeKeys(string? clock, int count) => ((clock is not null)
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{clock}/{count}"
        )
        : "none"
    );
    private static string Describe(BindableVector3? vector) => ((vector is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####},{literal.Z:0.####}"
            )
            : value.ToString())
        : "default"
    );
    private static CommandEcho DescribeLayer(CommandEcho echo, int index, WorldRenderSkyLayer layer) {
        echo = echo
            .Head(head: $"sky[{index}]")
            .Field(
            key: "name",
            value: (layer.LayerName ?? "none")
        );

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
        echo = echo
            .Head(head: $"lights[{index}]")
            .Field(
            key: "name",
            value: (light.LightName ?? "none")
        );

        return (light switch {
            WorldRenderLight.Directional directional => echo
                .Field(
            key: "type",
            value: "directional"
        )
                .Field(
            key: "direction",
            value: Describe(direction: directional.Direction)
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
            key: "shadow",
            value: (directional.Shadow ?? WorldShadowMode.Never).ToString().ToLowerInvariant()
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
    /// <summary>Returns the authored lighting, sky, fog and environment census.</summary>
    /// <param name="definition">The definition being inspected.</param>
    /// <returns>The console echo.</returns>
    public static string Describe(WorldDefinition definition) {
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
            value: (MayBePositive(value: curvature?.Cavity) || MayBePositive(value: curvature?.Rim) || MayBePositive(value: curvature?.Ink))
        );
        echo = AppendSky(echo.Segment(), definition.Render.Sky);

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
            .Head(head: "keys")
            .Field(
            key: "lighting",
            value: DescribeKeys(
                clock: lighting?.Clock,
                count: (lighting?.Keys?.Count ?? 0)
            )
        )
            .Field(
            key: "sky",
            value: DescribeKeys(
                clock: definition.Render.Sky?.Clock,
                count: (definition.Render.Sky?.Keys?.Count ?? 0)
            )
        );

        return echo.Close();
    }

    /// <summary>Returns exactly the sky and air segment of the lighting echo, before inspector wrapping.</summary>
    /// <param name="sky">The authored sky, or null for the engine defaults.</param>
    /// <returns>The shared sky text.</returns>
    public static string DescribeSky(WorldRenderSky? sky) => AppendSky(CommandEcho.Open("world.lighting"), sky).Close()["[world.lighting: ".Length..^1];

    private static CommandEcho AppendSky(CommandEcho echo, WorldRenderSky? sky) {
        echo = echo.Head("sky").Field("layers", sky?.Layers?.Count.ToString(CultureInfo.InvariantCulture) ?? "default")
            .Field("air", sky?.Layers?.Any(static layer => layer is WorldRenderSkyLayer.Fog) == true ? "fog" : "default");
        if (sky?.Layers is { } layers) {
            for (var index = 0; index < layers.Count; index++) {
                echo = DescribeLayer(echo.Segment(), index, layers[index]);
            }
        }
        return echo;
    }

}
