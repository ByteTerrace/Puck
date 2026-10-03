using System.Collections.Immutable;

namespace Puck.World;

/// <summary>
/// Resolves a keyed <c>render.lighting</c> or <c>render.sky</c> section into the same section with each field its keys
/// state keyed on the section's clock, so a section key and a value's own keys are one mechanism and one resolver
/// (<see cref="WorldKeyResolver"/>). A field's keys are the section keys that state it, in their order, each eased by
/// its key's ease; a field no key states keeps its authored value. A key addresses a light or a layer by name, and a
/// gradient's stops by their place in the layer. The validator expands a section the way every presentation does, so
/// both resolve its keys alike.
/// </summary>
public static class WorldRenderKeys {
    /// <summary>Returns a lighting section with its section keys applied to each field they state.</summary>
    /// <param name="lighting">The section, or <see langword="null"/>.</param>
    /// <returns>The expanded section; the section itself when it carries no keys.</returns>
    public static WorldRenderLighting? Expand(WorldRenderLighting? lighting) {
        if ((lighting?.Keys is not { Count: > 0 } keys) || (lighting.Clock is not { } clock)) {
            return lighting;
        }

        var lights = lighting.Lights?.Select(selector: light => ExpandLight(
            clock: clock,
            keys: keys,
            light: light
        )).ToArray();
        var curvature = ExpandCurvature(
            clock: clock,
            curvature: lighting.Curvature,
            parts: Parts(
                keys: keys.Select(selector: static key => (key.At, key.Ease, key.Curvature)),
                select: static part => part
            )
        );

        return lighting with {
            Clock = null,
            Curvature = curvature,
            Keys = null,
            Lights = lights,
        };
    }
    /// <summary>Returns a sky section with its section keys applied to each field they state.</summary>
    /// <param name="sky">The section, or <see langword="null"/>.</param>
    /// <returns>The expanded section; the section itself when it carries no keys.</returns>
    public static WorldRenderSky? Expand(WorldRenderSky? sky) {
        if ((sky?.Keys is not { Count: > 0 } keys) || (sky.Clock is not { } clock)) {
            return sky;
        }

        var layers = sky.Layers?.Select(selector: layer => ExpandLayer(
            clock: clock,
            keys: keys,
            layer: layer
        )).ToArray();

        return sky with {
            Clock = null,
            Keys = null,
            Layers = layers,
        };
    }

    private static WorldRenderLight ExpandLight(string clock, IReadOnlyList<WorldRenderLightingKey> keys, WorldRenderLight light) {
        if (light?.LightName is not { } name) {
            return light!;
        }

        var stated = keys.Select(selector: key => (key.At, key.Ease, Part: (((key.Lights is { } lights) && lights.TryGetValue(
            key: name,
            value: out var part
        ))
            ? part
            : null)));

        switch (light) {
            case WorldRenderLight.Directional directional: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderLight.Directional)
                    );

                    return directional with {
                        AngularRadius = Angle(
                            authored: directional.AngularRadius,
                            clock: clock,
                            field: static part => part.AngularRadius,
                            parts: parts
                        ),
                        Color = Color(
                            authored: directional.Color,
                            clock: clock,
                            field: static part => part.Color,
                            parts: parts
                        ),
                        Direction = Direction(
                            authored: directional.Direction,
                            clock: clock,
                            field: static part => part.Direction,
                            parts: parts
                        ),
                        Weight = Scalar(
                            authored: directional.Weight,
                            clock: clock,
                            field: static part => part.Weight,
                            parts: parts
                        ),
                    };
                }
            case WorldRenderLight.Hemisphere hemisphere: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderLight.Hemisphere)
                    );

                    return hemisphere with {
                        Base = Scalar(
                            authored: hemisphere.Base,
                            clock: clock,
                            field: static part => part.Base,
                            parts: parts
                        ),
                        Color = Color(
                            authored: hemisphere.Color,
                            clock: clock,
                            field: static part => part.Color,
                            parts: parts
                        ),
                        Gradient = Scalar(
                            authored: hemisphere.Gradient,
                            clock: clock,
                            field: static part => part.Gradient,
                            parts: parts
                        ),
                    };
                }
            case WorldRenderLight.Rim rim: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderLight.Rim)
                    );

                    return rim with {
                        Color = Color(
                            authored: rim.Color,
                            clock: clock,
                            field: static part => part.Color,
                            parts: parts
                        ),
                        Power = Scalar(
                            authored: rim.Power,
                            clock: clock,
                            field: static part => part.Power,
                            parts: parts
                        ),
                        Weight = Scalar(
                            authored: rim.Weight,
                            clock: clock,
                            field: static part => part.Weight,
                            parts: parts
                        ),
                    };
                }
            case WorldRenderLight.Point point: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderLight.Point)
                    );

                    return point with {
                        Position = Vector(
                            authored: point.Position,
                            clock: clock,
                            field: static part => part.Position,
                            parts: parts
                        ),
                        Color = Color(
                            authored: point.Color,
                            clock: clock,
                            field: static part => part.Color,
                            parts: parts
                        ),
                        Radius = Scalar(
                            authored: point.Radius,
                            clock: clock,
                            field: static part => part.Radius,
                            parts: parts
                        ),
                        Weight = Scalar(
                            authored: point.Weight,
                            clock: clock,
                            field: static part => part.Weight,
                            parts: parts
                        ),
                    };
                }
            case WorldRenderLight.Occluder occluder: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderLight.Occluder)
                    );

                    return occluder with {
                        Position = Vector(
                            authored: occluder.Position,
                            clock: clock,
                            field: static part => part.Position,
                            parts: parts
                        ),
                        Radius = Scalar(
                            authored: occluder.Radius,
                            clock: clock,
                            field: static part => part.Radius,
                            parts: parts
                        ),
                        Weight = Scalar(
                            authored: occluder.Weight,
                            clock: clock,
                            field: static part => part.Weight,
                            parts: parts
                        ),
                    };
                }
            default:
                return light;
        }
    }
    private static WorldRenderCurvature? ExpandCurvature(string clock, WorldRenderCurvature? curvature, IReadOnlyList<(double At, WorldEase Ease, WorldRenderCurvature Part)> parts) {
        if (parts.Count == 0) {
            return curvature;
        }

        var authored = (curvature ?? new WorldRenderCurvature());

        return authored with {
            Cavity = Scalar(
                authored: authored.Cavity,
                clock: clock,
                field: static part => part.Cavity,
                parts: parts
            ),
            Ink = Scalar(
                authored: authored.Ink,
                clock: clock,
                field: static part => part.Ink,
                parts: parts
            ),
            InkColor = Color(
                authored: authored.InkColor,
                clock: clock,
                field: static part => part.InkColor,
                parts: parts
            ),
            InkHigh = Scalar(
                authored: authored.InkHigh,
                clock: clock,
                field: static part => part.InkHigh,
                parts: parts
            ),
            InkLow = Scalar(
                authored: authored.InkLow,
                clock: clock,
                field: static part => part.InkLow,
                parts: parts
            ),
            Rim = Scalar(
                authored: authored.Rim,
                clock: clock,
                field: static part => part.Rim,
                parts: parts
            ),
        };
    }
    private static WorldRenderSkyLayer ExpandLayer(string clock, IReadOnlyList<WorldRenderSkyKey> keys, WorldRenderSkyLayer layer) {
        if (layer?.LayerName is not { } name) {
            return layer!;
        }

        var stated = keys.Select(selector: key => (key.At, key.Ease, Part: (((key.Layers is { } layers) && layers.TryGetValue(
            key: name,
            value: out var part
        ))
            ? part
            : null)));

        switch (layer) {
            case WorldRenderSkyLayer.Gradient gradient: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderSkyLayer.Gradient)
                    );

                    if ((parts.Length == 0) || (gradient.Stops is not { } stops)) {
                        return gradient;
                    }

                    var expanded = new WorldRenderSkyStop[stops.Count];

                    for (var index = 0; (index < stops.Count); index++) {
                        var stop = (stops[index] ?? new WorldRenderSkyStop());
                        var stopIndex = index;
                        var stopParts = parts
                            .Select(selector: part => (part.At, part.Ease, Part: (((part.Part.Stops is { } partStops) && (stopIndex < partStops.Count))
                                ? partStops[stopIndex]
                                : null)))
                            .Where(predicate: static part => (part.Part is not null))
                            .Select(selector: static part => (part.At, part.Ease, Part: part.Part!))
                            .ToArray();

                        expanded[index] = stop with {
                            Color = Color(
                                authored: stop.Color,
                                clock: clock,
                                field: static part => part.Color,
                                parts: stopParts
                            ),
                            Elevation = Scalar(
                                authored: stop.Elevation,
                                clock: clock,
                                field: static part => part.Elevation,
                                parts: stopParts
                            ),
                        };
                    }

                    return gradient with { Stops = expanded };
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderSkyLayer.SunDisc)
                    );

                    return disc with {
                        Intensity = Scalar(
                            authored: disc.Intensity,
                            clock: clock,
                            field: static part => part.Intensity,
                            parts: parts
                        ),
                        Radius = Angle(
                            authored: disc.Radius,
                            clock: clock,
                            field: static part => part.Radius,
                            parts: parts
                        ),
                    };
                }
            case WorldRenderSkyLayer.Stars stars: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderSkyLayer.Stars)
                    );
                    var twinkleParts = parts
                        .Where(predicate: static part => (part.Part.Twinkle is not null))
                        .Select(selector: static part => (part.At, part.Ease, Part: part.Part.Twinkle!))
                        .ToArray();
                    var twinkle = (((twinkleParts.Length == 0) && (stars.Twinkle is null))
                        ? null
                        : (stars.Twinkle ?? new WorldRenderSkyTwinkle()) with {
                            Depth = Scalar(
                                authored: stars.Twinkle?.Depth,
                                clock: clock,
                                field: static part => part.Depth,
                                parts: twinkleParts
                            ),
                            Rate = Scalar(
                                authored: stars.Twinkle?.Rate,
                                clock: clock,
                                field: static part => part.Rate,
                                parts: twinkleParts
                            ),
                            Share = Scalar(
                                authored: stars.Twinkle?.Share,
                                clock: clock,
                                field: static part => part.Share,
                                parts: twinkleParts
                            ),
                        }
                    );

                    return stars with {
                        Brightness = Scalar(
                            authored: stars.Brightness,
                            clock: clock,
                            field: static part => part.Brightness,
                            parts: parts
                        ),
                        Twinkle = twinkle,
                    };
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    var parts = Parts(
                        keys: stated,
                        select: static part => (part as WorldRenderSkyLayer.Clouds)
                    );

                    return clouds with {
                        Color = Color(
                            authored: clouds.Color,
                            clock: clock,
                            field: static part => part.Color,
                            parts: parts
                        ),
                        Coverage = Scalar(
                            authored: clouds.Coverage,
                            clock: clock,
                            field: static part => part.Coverage,
                            parts: parts
                        ),
                        Curl = Angle(
                            authored: clouds.Curl,
                            clock: clock,
                            field: static part => part.Curl,
                            parts: parts
                        ),
                        Drift = Vector(
                            authored: clouds.Drift,
                            clock: clock,
                            field: static part => part.Drift,
                            parts: parts
                        ),
                        Scale = Scalar(
                            authored: clouds.Scale,
                            clock: clock,
                            field: static part => part.Scale,
                            parts: parts
                        ),
                        Shear = Vector(
                            authored: clouds.Shear,
                            clock: clock,
                            field: static part => part.Shear,
                            parts: parts
                        ),
                        Softness = Scalar(
                            authored: clouds.Softness,
                            clock: clock,
                            field: static part => part.Softness,
                            parts: parts
                        ),
                        Spin = Scalar(
                            authored: clouds.Spin,
                            clock: clock,
                            field: static part => part.Spin,
                            parts: parts
                        ),
                    };
                }
            default:
                return layer;
        }
    }
    // The keys that state a part of the expected kind, each with its time and ease.
    private static (double At, WorldEase Ease, TPart Part)[] Parts<TSource, TPart>(IEnumerable<(double At, WorldEase? Ease, TSource? Part)> keys, Func<TSource, TPart?> select)
        where TSource : class
        where TPart : class {
        var parts = new List<(double At, WorldEase Ease, TPart Part)>();

        foreach (var (at, ease, source) in keys) {
            if ((source is not null) && (select(arg: source) is { } part)) {
                parts.Add(item: (at, (ease ?? WorldEase.Linear), part));
            }
        }

        return [.. parts];
    }
    private static WorldKeyTrack<TValue>? Track<TPart, TField, TValue>(string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, TField?> field, Func<TField, TValue?> literal)
        where TField : struct
        where TValue : struct {
        var keys = ImmutableArray.CreateBuilder<WorldKey<TValue>>();

        foreach (var (at, ease, part) in parts) {
            if ((field(arg: part) is { } value) && (literal(arg: value) is { } literalValue)) {
                keys.Add(item: new WorldKey<TValue>(
                    At: at,
                    Ease: ease,
                    Value: literalValue
                ));
            }
        }

        return ((keys.Count == 0)
            ? null
            : new WorldKeyTrack<TValue>(
                clock: clock,
                keys: keys.ToImmutable()
            )
        );
    }
    private static BindableScalar? Scalar<TPart>(BindableScalar? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableScalar?> field) => ((Track(
        clock: clock,
        field: field,
        literal: static value => value.Literal,
        parts: parts
    ) is { } track)
        ? new BindableScalar(keys: track)
        : authored
    );
    private static BindableAngle? Angle<TPart>(BindableAngle? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableAngle?> field) => ((Track(
        clock: clock,
        field: field,
        literal: static value => value.Value.Literal,
        parts: parts
    ) is { } track)
        ? new BindableAngle(value: new BindableScalar(keys: track))
        : authored
    );
    private static BindableDirection? Direction<TPart>(BindableDirection? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableDirection?> field) => ((Track(
        clock: clock,
        field: field,
        literal: static value => value.Literal,
        parts: parts
    ) is { } track)
        ? new BindableDirection(keys: track)
        : authored
    );
    private static BindableVector2? Vector<TPart>(BindableVector2? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableVector2?> field) => ((Track(
        clock: clock,
        field: field,
        literal: static value => value.Literal,
        parts: parts
    ) is { } track)
        ? new BindableVector2(keys: track)
        : authored
    );
    private static BindableVector3? Vector<TPart>(BindableVector3? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableVector3?> field) => ((Track(
        clock: clock,
        field: field,
        literal: static value => value.Literal,
        parts: parts
    ) is { } track)
        ? new BindableVector3(keys: track)
        : authored
    );
    private static BindableColor? Color<TPart>(BindableColor? authored, string clock, IReadOnlyList<(double At, WorldEase Ease, TPart Part)> parts, Func<TPart, BindableColor?> field) {
        var keys = ImmutableArray.CreateBuilder<WorldKey<BindableColor>>();

        foreach (var (at, ease, part) in parts) {
            if ((field(arg: part) is { Literal: not null } value)) {
                keys.Add(item: new WorldKey<BindableColor>(
                    At: at,
                    Ease: ease,
                    Value: value
                ));
            }
        }

        return ((keys.Count == 0)
            ? authored
            : new BindableColor(keys: new WorldKeyTrack<BindableColor>(
                clock: clock,
                keys: keys.ToImmutable()
            ))
        );
    }
}
