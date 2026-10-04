using System.Globalization;
using Puck.Commands;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>The shared authored lighting, sky and atmosphere readout used by the console and the inspector.</summary>
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
    private static string Describe(BindableColor? color, string change = "lighting-visible") => ((color is { } value)
        ? ((value.Keys is { } keys) ? $"{keys} class={change}" : value.ToString())
        : "default"
    );
    private static string Describe(BindableScalar? value, string change = "lighting-visible") => (value switch {
        null => "default",
        { Keys: { } keys } => $"{keys} class={change}",
        { Binding: { } binding } => binding,
        { Literal: { } literal } => Describe(value: ((float?)literal)),
        _ => "default",
    });
    private static string Describe(BindableAngle? value, string change = "lighting-visible") => Describe(value: value?.Value, change: change);
    private static string Describe(BindableDirection? direction, string change = "lighting-visible") => ((direction is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####},{literal.Z:0.####}"
            )
            : ((value.Keys is not null) ? $"{value} class={change}" : value.ToString()))
        : "default"
    );
    private static string Describe(BindableVector2? vector, string change = "visual-only") => ((vector is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####}"
            )
            : ((value.Keys is not null) ? $"{value} class={change}" : value.ToString()))
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
    private static string Describe(BindableVector3? vector, string change = "lighting-visible") => ((vector is { } value)
        ? ((value.Literal is { } literal)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{literal.X:0.####},{literal.Y:0.####},{literal.Z:0.####}"
            )
            : ((value.Keys is not null) ? $"{value} class={change}" : value.ToString()))
        : "default"
    );
    // A layer's fields, then what every layer of the stack carries as the engine resolves it: its blend, opacity,
    // visibility, lowest tier, clock, mask and transform.
    private static string LayerChange(WorldRenderSkyLayer layer) => (((layer is not WorldRenderSkyLayer.SunDisc) && ((WorldSkyLayers.VisibilityOf(layer: layer) & SdfSkyVisibility.Lighting) != 0)) ? "lighting-visible" : "visual-only");
    private static CommandEcho DescribeLayer(CommandEcho echo, int index, WorldRenderSkyLayer layer) {
        var change = LayerChange(layer: layer);

        echo = DescribeKind(echo: echo.Head(head: $"sky[{index}]").Field(key: "name", value: (layer.LayerName ?? "none")), layer: layer);

        return echo
            .Field(key: "blend", value: WorldSkyLayers.BlendOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "opacity", value: ((layer.Opacity is null) ? "1" : Describe(value: layer.Opacity, change: change)))
            .Field(key: "visibility", value: WorldSkyLayers.VisibilityOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "tier", value: WorldSkyLayers.TierOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "clock", value: (layer.Clock ?? "none"))
            .Field(key: "mask", value: ((layer.Mask?.Band is not null) ? "band" : ((layer.Mask?.Cone is not null) ? "cone" : "none")))
            .Field(key: "transform", value: ((layer.Transform is { } transform) ? $"{Describe(value: transform.Turn, change: change)}/{Describe(value: transform.Tilt, change: change)}" : "none"));
    }
    private static CommandEcho DescribeKind(CommandEcho echo, WorldRenderSkyLayer layer) {
        var change = LayerChange(layer: layer);

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
                                value: $"{Describe(value: stops[stop]?.Elevation, change: change)}:{Describe(color: stops[stop]?.Color, change: change)}"
                            );
                        }
                    }

                    return echo;
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
                        value: Describe(value: disc.Radius, change: change)
                    )
                        .Field(
                        key: "intensity",
                        value: Describe(value: disc.Intensity, change: change)
                    )
                        .Field(key: "texture", value: ((disc.Texture is { } texture) ? Describe(value: texture.Screen) : "none"));
                }
            case WorldRenderSkyLayer.Stars stars: {
                    return echo
                        .Field(
                        key: "type",
                        value: "stars"
                    )
                        .Field(
                        key: "density",
                        value: Describe(value: stars.Density, change: change)
                    )
                        .Field(
                        key: "brightness",
                        value: Describe(value: stars.Brightness, change: change)
                    )
                        .Field(
                        key: "seed",
                        value: Describe(value: stars.Seed)
                    )
                        .Field(
                        key: "twinkle",
                        value: ((stars.Twinkle is { } twinkle)
                        ? $"{Describe(value: twinkle.Share, change: change)}/{Describe(value: twinkle.Depth, change: change)}/{Describe(value: twinkle.Rate, change: change)}"
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
                        value: Describe(value: clouds.Coverage, change: change)
                    )
                        .Field(
                        key: "softness",
                        value: Describe(value: clouds.Softness, change: change)
                    )
                        .Field(
                        key: "scale",
                        value: Describe(value: clouds.Scale, change: change)
                    )
                        .Field(
                        key: "seed",
                        value: Describe(value: clouds.Seed)
                    )
                        .Field(
                        key: "color",
                        value: Describe(color: clouds.Color, change: change)
                    )
                        .Field(
                        key: "drift",
                        value: Describe(vector: clouds.Drift, change: change)
                    )
                        .Field(
                        key: "spin",
                        value: Describe(value: clouds.Spin, change: change)
                    )
                        .Field(
                        key: "curl",
                        value: Describe(value: clouds.Curl, change: change)
                    )
                        .Field(
                        key: "shear",
                        value: Describe(vector: clouds.Shear, change: change)
                    );
                }
            case WorldRenderSkyLayer.Aurora aurora: {
                    return echo
                        .Field(key: "type", value: "aurora")
                        .Field(key: "intensity", value: Describe(value: aurora.Intensity, change: change))
                        .Field(key: "color", value: Describe(color: aurora.Color, change: change))
                        .Field(key: "top", value: Describe(color: aurora.Top, change: change))
                        .Field(key: "base", value: Describe(value: aurora.Base, change: change))
                        .Field(key: "height", value: Describe(value: aurora.Height, change: change))
                        .Field(key: "fold", value: Describe(value: aurora.Fold, change: change));
                }
            case WorldRenderSkyLayer.Noise noise: {
                    return echo
                        .Field(key: "type", value: "noise")
                        .Field(key: "low", value: Describe(color: noise.Low, change: change))
                        .Field(key: "high", value: Describe(color: noise.High, change: change))
                        .Field(key: "coverage", value: Describe(value: noise.Coverage, change: change))
                        .Field(key: "scale", value: Describe(value: noise.Scale, change: change))
                        .Field(key: "octaves", value: Describe(value: noise.Octaves));
                }
            case WorldRenderSkyLayer.Pattern pattern: {
                    return echo
                        .Field(key: "type", value: "pattern")
                        .Field(key: "shape", value: (pattern.Shape ?? WorldSkyPatternShape.Checker).ToString().ToLowerInvariant())
                        .Field(key: "cells", value: Describe(value: pattern.Cells));
                }
            case WorldRenderSkyLayer.Panel panel: {
                    return echo.Field(key: "type", value: "panel").Field(key: "color", value: Describe(color: panel.Color, change: change))
                        .Field(key: "intensity", value: Describe(value: panel.Intensity, change: change))
                        .Field(key: "blur", value: Describe(value: panel.Blur, change: change))
                        .Field(key: "direction", value: (panel.Direction?.ToString() ?? "default"))
                        .Field(key: "size", value: (panel.Size?.ToString() ?? "default"));
                }
            case WorldRenderSkyLayer.Panorama panorama: {
                    return echo
                        .Field(key: "type", value: "panorama")
                        .Field(key: "screen", value: Describe(value: panorama.Screen))
                        .Field(key: "projection", value: (panorama.Projection ?? WorldSkyProjection.Equirect).ToString().ToLowerInvariant())
                        .Field(key: "intensity", value: Describe(value: panorama.Intensity, change: change));
                }
            case WorldRenderSkyLayer.View view: {
                    return echo
                        .Field(key: "type", value: "view")
                        .Field(key: "destination", value: (view.Destination ?? "none"))
                        .Field(key: "scale", value: Describe(value: view.Scale))
                        .Field(key: "refresh", value: Describe(value: view.Refresh));
                }
            case WorldRenderSkyLayer.Far far: {
                    return echo
                        .Field(key: "type", value: "far")
                        .Field(key: "prototypes", value: string.Join(separator: ",", values: (far.Prototypes ?? [])))
                        .Field(key: "scale", value: Describe(value: far.Scale))
                        .Field(key: "refresh", value: Describe(value: far.Refresh));
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
            value: Describe(direction: directional.Direction, change: ((directional.Shadow is WorldShadowMode.Always or WorldShadowMode.Auto) ? "shadow-direction" : "lighting-visible"))
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
            value: Describe(value: directional.AngularRadius, change: ((directional.Shadow is WorldShadowMode.Always or WorldShadowMode.Auto) ? "shadow-direction" : "lighting-visible"))
        )
                .Field(
            key: "shadow",
            value: (directional.Shadow ?? WorldShadowMode.Never).ToString().ToLowerInvariant()
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
    // The atmosphere's kinds, each "none" when an authored section leaves it out and "default" when the section is absent.
    private static CommandEcho DescribeAtmosphere(CommandEcho echo, WorldRenderAtmosphere? atmosphere) {
        static string Height(WorldRenderAirHeight? height) => ((height is { } profile)
            ? $"{Describe(value: profile.Base)}/{Describe(value: profile.Falloff)}"
            : "level");

        echo = echo.Segment().Head(head: "atmosphere");
        if (atmosphere is null) {
            return echo.Field(key: "fog", value: "default").Field(key: "haze", value: "none").Field(key: "medium", value: "none");
        }

        echo = ((atmosphere.Fog is { } fog)
            ? echo
                .Field(key: "fog", value: "on")
                .Field(key: "fogDensity", value: Describe(value: fog.Density))
                .Field(key: "fogColor", value: ((fog.Color is null) ? "sky" : Describe(color: fog.Color)))
                .Field(key: "fogHeight", value: Height(height: fog.Height))
            : echo.Field(key: "fog", value: "none"));
        echo = ((atmosphere.Haze is { } haze)
            ? echo
                .Field(key: "haze", value: "on")
                .Field(key: "hazeAmount", value: Describe(value: haze.Amount))
                .Field(key: "hazeAnisotropy", value: Describe(value: haze.Anisotropy))
                .Field(key: "hazeHeight", value: Height(height: haze.Height))
            : echo.Field(key: "haze", value: "none"));

        return ((atmosphere.Medium is { } medium)
            ? echo
                .Field(key: "medium", value: "on")
                .Field(key: "mediumSurface", value: Describe(value: medium.Surface))
                .Field(key: "mediumExtinction", value: Describe(value: medium.Extinction))
                .Field(key: "mediumColor", value: Describe(color: medium.Color))
            : echo.Field(key: "medium", value: "none"));
    }
    private static string DescribeLightAnchor(WorldAnchor? anchor) => anchor switch {
        WorldAnchor.Entity entity => $"entity:{entity.Index}",
        WorldAnchor.EntityPart part => $"entityPart:{part.Index}/{part.PartId}",
        WorldAnchor.Placement placement => $"placement:{placement.PlacementId}/{placement.ShapeId}",
        _ => "none",
    };

    /// <summary>Returns the authored lighting, sky, atmosphere and environment echo.</summary>
    /// <param name="definition">The live document.</param>
    /// <returns>The complete lighting echo.</returns>
    public static string Describe(WorldDefinition definition) {
        var lighting = WorldRenderKeys.Expand(lighting: definition.Render.Lighting);
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
            value: Describe(value: curvature?.Cavity, change: "geometry-or-camera")
        )
            .Field(
            key: "rim",
            value: Describe(value: curvature?.Rim, change: "geometry-or-camera")
        )
            .Field(
            key: "ink",
            value: Describe(value: curvature?.Ink, change: "geometry-or-camera")
        )
            .Field(
            key: "inkLow",
            value: Describe(value: curvature?.InkLow, change: "geometry-or-camera")
        )
            .Field(
            key: "inkHigh",
            value: Describe(value: curvature?.InkHigh, change: "geometry-or-camera")
        )
            .Field(
            key: "inkColor",
            value: Describe(color: curvature?.InkColor, change: "geometry-or-camera")
        )
            // The three gains share the runtime gate: all-zero costs the renderer nothing at all.
            .Field(
            key: "active",
            value: (MayBePositive(value: curvature?.Cavity) || MayBePositive(value: curvature?.Rim) || MayBePositive(value: curvature?.Ink))
        );
        echo = AppendSky(echo: echo.Segment(), sky: definition.Render.Sky, atmosphere: definition.Render.Atmosphere);
        var environment = definition.Render.Environment;

        echo = echo
            .Segment()
            .Head(head: "environment")
            .Field(key: "ambient", value: Describe(value: environment?.Ambient))
            .Field(key: "reflection", value: Describe(value: environment?.Reflection))
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
                clock: definition.Render.Lighting?.Clock,
                count: (definition.Render.Lighting?.Keys?.Count ?? 0)
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
    /// <summary>Returns exactly the sky and atmosphere segments of the lighting echo, before inspector wrapping.</summary>
    /// <param name="sky">The authored sky, or null for the engine defaults.</param>
    /// <param name="atmosphere">The authored atmosphere, or null for its defaults.</param>
    /// <returns>The shared sky and atmosphere text.</returns>
    public static string DescribeSky(WorldRenderSky? sky, WorldRenderAtmosphere? atmosphere) =>
        AppendSky(CommandEcho.Open(verb: "world.lighting"), sky, atmosphere).Close()["[world.lighting: ".Length..^1];

    private static CommandEcho AppendSky(CommandEcho echo, WorldRenderSky? sky, WorldRenderAtmosphere? atmosphere) {
        echo = echo.Head(head: "sky").Field(key: "layers", value: (sky?.Layers?.Count.ToString(provider: CultureInfo.InvariantCulture) ?? "default"));
        if (WorldRenderKeys.Expand(sky: sky)?.Layers is { } layers) {
            for (var index = 0; (index < layers.Count); index++) {
                echo = DescribeLayer(echo: echo.Segment(), index: index, layer: layers[index]);
            }
        }
        return DescribeAtmosphere(atmosphere: atmosphere, echo: echo);
    }
}
