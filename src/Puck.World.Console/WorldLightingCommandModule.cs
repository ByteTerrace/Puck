using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>render.lighting</c>/<c>render.sky</c>/<c>render.environment</c>/<c>render.grounding</c>/
/// <c>render.tonemap</c> read-back: <c>world.lighting</c> reports every authored light by slot, the curvature
/// enrichment, every sky layer, the studio-reflection softbox count and horizon colors, the grounding
/// strength/radius, the tonemap mode, and the clock and key count of each keyed section; a keyed value reads as its
/// clock and key count. The sections are authored through <c>world.row.set render</c>; every field is optional and an
/// absent one reads <c>default</c>, which is the engine's pinned value for that field of that kind, not zero.
/// </summary>
public sealed class WorldLightingCommandModule(IWorldConsoleAuthority authority, Func<WorldDefinition, string?>? shadowReport = null) : ICommandModule {
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
    // A layer's fields, then what every layer of the stack carries as the engine resolves it: its blend, opacity,
    // visibility, lowest tier, clock, mask and transform.
    private static CommandEcho DescribeLayer(CommandEcho echo, int index, WorldRenderSkyLayer layer) {
        echo = DescribeKind(echo: echo.Head(head: $"sky[{index}]").Field(key: "name", value: (layer.LayerName ?? "none")), layer: layer);

        if (layer is WorldRenderSkyLayer.Fog) {
            return echo;
        }

        return echo
            .Field(key: "blend", value: WorldSkyLayers.BlendOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "opacity", value: ((layer.Opacity is null) ? "1" : Describe(value: layer.Opacity)))
            .Field(key: "visibility", value: WorldSkyLayers.VisibilityOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "tier", value: WorldSkyLayers.TierOf(layer: layer).ToString().ToLowerInvariant())
            .Field(key: "clock", value: (layer.Clock ?? "none"))
            .Field(key: "mask", value: ((layer.Mask?.Band is not null) ? "band" : ((layer.Mask?.Cone is not null) ? "cone" : "none")))
            .Field(key: "transform", value: ((layer.Transform is { } transform) ? $"{Describe(value: transform.Turn)}/{Describe(value: transform.Tilt)}" : "none"));
    }
    private static CommandEcho DescribeKind(CommandEcho echo, WorldRenderSkyLayer layer) {
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
            case WorldRenderSkyLayer.Aurora aurora: {
                    return echo
                        .Field(key: "type", value: "aurora")
                        .Field(key: "intensity", value: Describe(value: aurora.Intensity))
                        .Field(key: "color", value: Describe(color: aurora.Color))
                        .Field(key: "top", value: Describe(color: aurora.Top))
                        .Field(key: "base", value: Describe(value: aurora.Base))
                        .Field(key: "height", value: Describe(value: aurora.Height))
                        .Field(key: "fold", value: Describe(value: aurora.Fold));
                }
            case WorldRenderSkyLayer.Noise noise: {
                    return echo
                        .Field(key: "type", value: "noise")
                        .Field(key: "low", value: Describe(color: noise.Low))
                        .Field(key: "high", value: Describe(color: noise.High))
                        .Field(key: "coverage", value: Describe(value: noise.Coverage))
                        .Field(key: "scale", value: Describe(value: noise.Scale))
                        .Field(key: "octaves", value: Describe(value: noise.Octaves));
                }
            case WorldRenderSkyLayer.Pattern pattern: {
                    return echo
                        .Field(key: "type", value: "pattern")
                        .Field(key: "shape", value: (pattern.Shape ?? WorldSkyPatternShape.Checker).ToString().ToLowerInvariant())
                        .Field(key: "cells", value: Describe(value: pattern.Cells));
                }
            case WorldRenderSkyLayer.Panorama panorama: {
                    return echo
                        .Field(key: "type", value: "panorama")
                        .Field(key: "screen", value: Describe(value: panorama.Screen))
                        .Field(key: "projection", value: (panorama.Projection ?? WorldSkyProjection.Equirect).ToString().ToLowerInvariant())
                        .Field(key: "intensity", value: Describe(value: panorama.Intensity));
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
    private static string DescribeLighting(WorldDefinition definition) {
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

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return authority.CreateServerQueryCommand(
            description: "Reports the render.lighting, render.sky, render.environment, render.grounding, and render.tonemap census (Immediate; the stdin barrier makes it read the settled state after any pending mutation): every light by slot with its kind and fields, the last presented named shadow holders with their selection ranks and transition state, the stylized curvature enrichment and whether its runtime gate is open, every sky layer by index, the studio-reflection softbox count and horizon colors, the grounding strength/radius, the tonemap mode, and the clock and key count of each keyed section. A keyed value reads keys(clock: <name>, <n> keys); an unauthored field reads 'default' — the engine's pinned value for it, not zero.",
            describe: server => ((DescribeLighting(definition: server.Definition) + " | ") + (shadowReport?.Invoke(server.Definition) ?? "shadowSlots unavailable: no presented frame of this authority")),
            name: "world.lighting"
        );
    }
}
