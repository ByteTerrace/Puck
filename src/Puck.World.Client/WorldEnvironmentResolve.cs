using System.Numerics;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// Resolves a definition's environment for a frame: <c>render.lighting</c>, <c>render.sky</c> and
/// <c>render.environment</c> written into an <see cref="SdfEnvironment"/>, every value read through the client's
/// <see cref="WorldStateMirror"/>, the one binding path: a literal as authored, a binding as its slot presents it, and
/// keys at their clock's presented phase (<see cref="WorldKeyResolver"/>). A keyed section is expanded once per
/// definition revision (<see cref="WorldRenderKeys.Expand(WorldRenderSky)"/>), so a section key resolves as the value
/// keys it lands in. Each cloud rate and the star twinkle's rate are integrated to the presented tick
/// (<see cref="WorldStateMirror.Integrate(in BindableScalar, double)"/>), keyed or literal, so the environment carries
/// offsets and a phase that never jump where a key changes a rate.
/// <para>
/// The environment re-resolves only when its definition revision moves, a slot one of its bindings or state clocks
/// reads moves, or the presented tick moves while it reads a tick clock or integrates a rate; every other frame copies
/// the last resolution, and <see cref="Resolutions"/> counts the ones it made. A point light's anchor is resolved fresh
/// every frame.
/// </para>
/// </summary>
public sealed class WorldEnvironmentResolve {
    // An unauthored lighting section seeds from the pinned sun and hemisphere; an authored list seeds from nothing.
    private static readonly SdfEnvironment Pinned = SdfEnvironment.Default();
    private static readonly SdfEnvironment Empty = new();
    private readonly List<int> m_bound = [];
    private readonly SdfEnvironment[] m_output = [new SdfEnvironment(), new SdfEnvironment()];
    private readonly SdfEnvironment m_resolved = new();

    private WorldDefinition? m_definition;
    private int m_generation;
    private WorldRenderLighting? m_lighting;
    private WorldStateMirror? m_mirror;
    private int m_outputIndex;
    private bool m_readsTick;
    private int m_resolutions;
    private int m_resolvedAt;

    private int m_revision = -1;

    private WorldRenderSky? m_sky;
    private PresentedTick m_tick;

    /// <summary>Gets how many times this resolver has resolved the environment rather than copying its last
    /// resolution.</summary>
    public int Resolutions => m_resolutions;

    /// <summary>Resolves this frame's environment. The returned instance is reused every other call; a consumer that
    /// must hold one across frames copies it.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="revision">The definition revision; a keyed section is expanded once per revision.</param>
    /// <param name="mirror">The state mirror every binding and keyed value reads through.</param>
    /// <param name="resolveLightAnchor">Resolves a positional light anchor to its current pose. Missing targets
    /// return null and disable the light for this frame.</param>
    /// <returns>The environment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="mirror"/> is
    /// <see langword="null"/>.</exception>
    public SdfEnvironment Resolve(WorldDefinition definition, int revision, WorldStateMirror mirror, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: mirror);

        var moved = false;

        if ((revision != m_revision) || !ReferenceEquals(
            objA: definition,
            objB: m_definition
        )) {
            m_definition = definition;
            m_revision = revision;
            m_lighting = WorldRenderKeys.Expand(lighting: definition.Render.Lighting);
            m_sky = WorldRenderKeys.Expand(sky: definition.Render.Sky);
            moved = true;
        }

        if (
            moved ||
            !ReferenceEquals(
            objA: mirror,
            objB: m_mirror
        ) ||
            (mirror.Generation != m_generation) ||
            (m_readsTick && (mirror.Presented != m_tick)) ||
            BoundSlotMoved(mirror: mirror)
        ) {
            m_mirror = mirror;
            m_bound.Clear();
            m_readsTick = false;
            Write(
                environment: definition.Render.Environment,
                lighting: m_lighting,
                mirror: mirror,
                sky: m_sky
            );
            m_generation = mirror.Generation;
            m_resolvedAt = mirror.Revision;
            m_tick = mirror.Presented;
            m_resolutions++;
        }

        var output = m_output[m_outputIndex];

        m_outputIndex ^= 1;
        output.CopyFrom(source: m_resolved);
        ApplyAnchors(
            lighting: m_lighting,
            output: output,
            resolveLightAnchor: resolveLightAnchor
        );

        return output;
    }

    // A point light's anchor rides the live dynamic-transform slot every call, never the cached resolution: an anchored
    // placement's pool slot can differ from frame to frame independently of the definition.
    private static void ApplyAnchors(SdfEnvironment output, WorldRenderLighting? lighting, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor) {
        if (lighting?.Lights is not { } lights) {
            return;
        }

        var count = Math.Min(
            val1: lights.Count,
            val2: output.LightCount
        );

        for (var index = 0; (index < count); index++) {
            var anchor = lights[index] switch {
                WorldRenderLight.Point point => point.Anchor,
                WorldRenderLight.Occluder occluder => occluder.Anchor,
                _ => null,
            };

            if (anchor is null) {
                continue;
            }

            var pose = resolveLightAnchor?.Invoke(anchor);
            var light = output.GetLight(index: index);

            output.SetLight(
                index: index,
                light: ((pose is { } frame)
                    ? light with {
                        DynamicSlot = SdfProgram.NoDynamicTransformSlot,
                        Direction = (frame.Position + Vector3.Transform(
                            light.Direction,
                            frame.Orientation
                        )),
                    }
                    : light with { DynamicSlot = SdfProgram.NoDynamicTransformSlot, Weight = 0f })
            );
        }
    }
    private static int FirstDirectional(SdfEnvironment environment) {
        for (var index = 0; (index < environment.LightCount); index++) {
            if (environment.GetLight(index: index).Kind == SdfLightKind.Directional) {
                return index;
            }
        }

        return -1;
    }
    private static SdfLight PinnedLight(WorldRenderLight light) => (light switch {
        WorldRenderLight.Directional => new SdfLight(
            Kind: SdfLightKind.Directional,
            Direction: SdfEnvironment.DefaultSunDirection,
            Color: Vector3.One,
            Weight: SdfEnvironment.DefaultSunWeight,
            Param: SdfEnvironment.DefaultPenumbraSlope,
            Shadows: false
        ),
        WorldRenderLight.Hemisphere => new SdfLight(
            Kind: SdfLightKind.Hemisphere,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: SdfEnvironment.DefaultAmbientBase,
            Param: SdfEnvironment.DefaultAmbientHemisphere,
            Shadows: false
        ),
        WorldRenderLight.Occluder => new SdfLight(
            Kind: SdfLightKind.Occluder,
            Direction: Vector3.Zero,
            Color: Vector3.Zero,
            Weight: 0f,
            Param: 1f,
            Shadows: false
        ),
        WorldRenderLight.Point => new SdfLight(
            Kind: SdfLightKind.Point,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: SdfEnvironment.DefaultPointWeight,
            Param: SdfEnvironment.DefaultPointRadius,
            Shadows: false
        ),
        _ => new SdfLight(
            Kind: SdfLightKind.Rim,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: 0f,
            Param: SdfEnvironment.DefaultRimPower,
            Shadows: false
        ),
    });
    private bool BoundSlotMoved(WorldStateMirror mirror) {
        foreach (var slot in m_bound) {
            if (mirror.Changed(slot: slot) > m_resolvedAt) {
                return true;
            }
        }

        return false;
    }
    // Notes what a keyed value reads besides its keys: a state clock's slot, or the presented tick a tick clock moves
    // with.
    private void NoteKeys(WorldStateMirror mirror, IWorldKeyTrack? keys) {
        if (keys is null) {
            return;
        }

        var slot = mirror.ClockSlotOf(name: keys.Clock);

        if (slot >= 0) {
            m_bound.Add(item: slot);
        } else {
            m_readsTick = true;
        }
    }
    private void NoteBinding(WorldStateMirror mirror, StateBinding? binding, WorldStateConversion conversion) {
        if (binding is { } bound) {
            m_bound.Add(item: mirror.SlotOf(
                binding: in bound,
                conversion: conversion
            ));
        }
    }
    private float Scalar(WorldStateMirror mirror, BindableScalar? scalar, float fallback) {
        if (scalar is not { } value) {
            return fallback;
        }

        NoteBinding(
            binding: value.State,
            conversion: WorldStateConversion.Number,
            mirror: mirror
        );
        NoteKeys(
            keys: value.Keys,
            mirror: mirror
        );

        return mirror.Scalar(
            fallback: fallback,
            scalar: in value
        );
    }
    private float Angle(WorldStateMirror mirror, BindableAngle? angle, float fallback) {
        if (angle is not { } value) {
            return fallback;
        }

        NoteBinding(
            binding: value.Value.State,
            conversion: WorldStateConversion.Number,
            mirror: mirror
        );
        NoteKeys(
            keys: value.Value.Keys,
            mirror: mirror
        );

        return mirror.Angle(
            angle: in value,
            fallback: fallback
        );
    }
    private Vector3 Direction(WorldStateMirror mirror, BindableDirection? direction, Vector3 fallback) {
        if (direction is not { } value) {
            return fallback;
        }

        NoteKeys(
            keys: value.Keys,
            mirror: mirror
        );

        return mirror.Direction(
            direction: in value,
            fallback: fallback
        );
    }
    private Vector3 Position(WorldStateMirror mirror, BindableVector3? position, Vector3 fallback) {
        if (position is not { } value) {
            return fallback;
        }

        NoteKeys(
            keys: value.Keys,
            mirror: mirror
        );

        return mirror.Vector(
            fallback: fallback,
            vector: in value
        );
    }
    // The render path is opaque: alpha plays no part in any environment colour, so every bound colour drops it here,
    // the one seam between BindableColor's Vector4 grammar and the lane table's three colour lanes.
    private Vector3 Rgb(WorldStateMirror mirror, BindableColor? color, Vector3 fallback) {
        if (color is not { } value) {
            return fallback;
        }

        NoteBinding(
            binding: value.State,
            conversion: WorldStateConversion.Color,
            mirror: mirror
        );
        NoteKeys(
            keys: value.Keys,
            mirror: mirror
        );

        var resolved = mirror.Color(
            color: in value,
            fallback: new Vector4(
                value: fallback,
                w: 1f
            )
        );

        return new Vector3(
            x: resolved.X,
            y: resolved.Y,
            z: resolved.Z
        );
    }
    // A rate integrated to the presented tick: a literal rate's offset moves with the tick as surely as a keyed one's.
    private double Integrate(WorldStateMirror mirror, BindableScalar? rate, double modulus) {
        if (rate is not { } value) {
            return 0d;
        }

        if ((value.Keys is not null) || ((value.Literal is { } literal) && (literal != 0f))) {
            m_readsTick = true;
        }

        return mirror.Integrate(
            modulus: modulus,
            rate: in value
        );
    }
    private Vector2 Integrate(WorldStateMirror mirror, BindableVector2? rate, double modulus) {
        if (rate is not { } value) {
            return Vector2.Zero;
        }

        if ((value.Keys is not null) || ((value.Literal is { } literal) && (literal != Vector2.Zero))) {
            m_readsTick = true;
        }

        return mirror.Integrate(
            modulus: modulus,
            rate: in value
        );
    }
    // The one document-to-lanes writer. The lanes seed from the pinned environment when the world authors no light
    // list (the pinned sun and hemisphere) and from nothing when it does, and an absent field takes its kind's default.
    // The sky is enabled by any layer that draws (a gradient, the sun disc, stars, clouds), since each is miss-pixel
    // content the shader composites only on the authored path; fog alone leaves the pinned branch, which renders
    // bit-identically to a world with no sky.
    private void Write(WorldStateMirror mirror, WorldRenderLighting? lighting, WorldRenderSky? sky, WorldRenderEnvironment? environment) {
        var into = m_resolved;

        into.CopyFrom(source: ((lighting?.Lights is null)
            ? Pinned
            : Empty));

        if (lighting?.Lights is { } lights) {
            var count = Math.Min(
                val1: lights.Count,
                val2: SdfEnvironment.MaxLights
            );

            for (var index = 0; (index < count); index++) {
                var authored = lights[index];
                var pinned = PinnedLight(light: authored);

                into.SetLight(
                    index: index,
                    light: (authored switch {
                        WorldRenderLight.Directional directional => pinned with {
                            Color = Rgb(
                                color: directional.Color,
                                fallback: pinned.Color,
                                mirror: mirror
                            ),
                            Direction = Direction(
                                direction: directional.Direction,
                                fallback: pinned.Direction,
                                mirror: mirror
                            ),
                            Param = ((directional.AngularRadius is { } angularRadius)
                                ? MathF.Tan(x: Angle(
                                    angle: angularRadius,
                                    fallback: MathF.Atan(x: pinned.Param),
                                    mirror: mirror
                                ))
                                : pinned.Param),
                            Shadows = (directional.Shadows ?? pinned.Shadows),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: directional.Weight
                            ),
                        },
                        WorldRenderLight.Hemisphere hemisphere => pinned with {
                            Color = Rgb(
                                color: hemisphere.Color,
                                fallback: pinned.Color,
                                mirror: mirror
                            ),
                            Param = Scalar(
                                fallback: pinned.Param,
                                mirror: mirror,
                                scalar: hemisphere.Gradient
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: hemisphere.Base
                            ),
                        },
                        WorldRenderLight.Rim rim => pinned with {
                            Color = Rgb(
                                color: rim.Color,
                                fallback: pinned.Color,
                                mirror: mirror
                            ),
                            Param = Scalar(
                                fallback: pinned.Param,
                                mirror: mirror,
                                scalar: rim.Power
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: rim.Weight
                            ),
                        },
                        WorldRenderLight.Occluder occluder => pinned with {
                            Direction = Position(
                                fallback: pinned.Direction,
                                mirror: mirror,
                                position: occluder.Position
                            ),
                            Param = Scalar(
                                fallback: pinned.Param,
                                mirror: mirror,
                                scalar: occluder.Radius
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: occluder.Weight
                            ),
                        },
                        WorldRenderLight.Point point => pinned with {
                            Color = Rgb(
                                color: point.Color,
                                fallback: pinned.Color,
                                mirror: mirror
                            ),
                            Direction = Position(
                                fallback: pinned.Direction,
                                mirror: mirror,
                                position: point.Position
                            ),
                            Param = Scalar(
                                fallback: pinned.Param,
                                mirror: mirror,
                                scalar: point.Radius
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: point.Weight
                            ),
                        },
                        _ => pinned,
                    })
                );
            }

            into.LightCount = count;
        }

        if (lighting?.Curvature is { } curvature) {
            into.CurvatureCavity = Scalar(
                fallback: into.CurvatureCavity,
                mirror: mirror,
                scalar: curvature.Cavity
            );
            into.CurvatureRim = Scalar(
                fallback: into.CurvatureRim,
                mirror: mirror,
                scalar: curvature.Rim
            );
            into.CurvatureInk = Scalar(
                fallback: into.CurvatureInk,
                mirror: mirror,
                scalar: curvature.Ink
            );
            into.CurvatureInkLow = Scalar(
                fallback: into.CurvatureInkLow,
                mirror: mirror,
                scalar: curvature.InkLow
            );
            into.CurvatureInkHigh = Scalar(
                fallback: into.CurvatureInkHigh,
                mirror: mirror,
                scalar: curvature.InkHigh
            );
            into.CurvatureInkColor = Rgb(
                color: curvature.InkColor,
                fallback: into.CurvatureInkColor,
                mirror: mirror
            );
        }

        foreach (var layer in (sky?.Layers ?? [])) {
            WriteLayer(
                into: into,
                layer: layer,
                mirror: mirror
            );
        }

        WriteEnvironment(
            environment: environment,
            into: into,
            mirror: mirror
        );
    }
    private void WriteLayer(WorldStateMirror mirror, WorldRenderSkyLayer layer, SdfEnvironment into) {
        switch (layer) {
            case WorldRenderSkyLayer.Gradient gradient: {
                    if (gradient.Stops is not { } stops) {
                        break;
                    }

                    var count = Math.Min(
                        val1: stops.Count,
                        val2: SdfEnvironment.MaxSkyStops
                    );

                    for (var index = 0; (index < count); index++) {
                        var stop = stops[index];

                        into.SetSkyStop(
                            color: Rgb(
                                color: stop?.Color,
                                fallback: Vector3.One,
                                mirror: mirror
                            ),
                            elevation: Scalar(
                                fallback: 0f,
                                mirror: mirror,
                                scalar: stop?.Elevation
                            ),
                            index: index
                        );
                    }

                    into.SkyStopCount = count;
                    into.SkyEnabled |= (count > 0);

                    break;
                }
            case WorldRenderSkyLayer.Fog fog: {
                    into.FogDensity = Scalar(
                        fallback: into.FogDensity,
                        mirror: mirror,
                        scalar: fog.Density
                    );

                    break;
                }
            case WorldRenderSkyLayer.SunDisc disc: {
                    into.SunDiscLightIndex = (disc.Light ?? ((into.ShadowLightIndex >= 0)
                        ? into.ShadowLightIndex
                        : FirstDirectional(environment: into)));
                    into.SunDiscRadians = Angle(
                        angle: disc.Radius,
                        fallback: into.SunDiscRadians,
                        mirror: mirror
                    );
                    into.SunDiscIntensity = Scalar(
                        fallback: into.SunDiscIntensity,
                        mirror: mirror,
                        scalar: disc.Intensity
                    );
                    into.SkyEnabled = true;

                    break;
                }
            case WorldRenderSkyLayer.Stars stars: {
                    into.StarDensity = (stars.Density ?? into.StarDensity);
                    into.StarBrightness = Scalar(
                        fallback: into.StarBrightness,
                        mirror: mirror,
                        scalar: stars.Brightness
                    );
                    into.StarSeed = (stars.Seed ?? into.StarSeed);
                    into.SkyEnabled = true;

                    if (stars.Twinkle is not { } twinkle) {
                        break;
                    }

                    into.TwinkleShare = Scalar(
                        fallback: into.TwinkleShare,
                        mirror: mirror,
                        scalar: twinkle.Share
                    );
                    into.TwinkleDepth = Scalar(
                        fallback: into.TwinkleDepth,
                        mirror: mirror,
                        scalar: twinkle.Depth
                    );

                    var rate = (twinkle.Rate ?? new BindableScalar(literal: SdfEnvironment.DefaultTwinkleRate));
                    var visible = (
                        (into.StarBrightness > 0f) &&
                        (into.StarDensity > 0f) &&
                        (into.TwinkleShare > 0f) &&
                        (into.TwinkleDepth > 0f) &&
                        (mirror.Scalar(
                        fallback: 0f,
                        scalar: in rate
                    ) > 0f)
                    );

                    // A sky with no visible twinkle bakes phase zero, so a still frame's block repeats.
                    if (visible) {
                        var cycles = Integrate(
                            mirror: mirror,
                            modulus: 1d,
                            rate: rate
                        );

                        into.TwinklePhase = ((float)((cycles < 0d)
                            ? (cycles + 1d)
                            : cycles));
                    }

                    break;
                }
            case WorldRenderSkyLayer.Clouds clouds: {
                    into.CloudCoverage = Scalar(
                        fallback: into.CloudCoverage,
                        mirror: mirror,
                        scalar: clouds.Coverage
                    );
                    into.CloudSoftness = Scalar(
                        fallback: into.CloudSoftness,
                        mirror: mirror,
                        scalar: clouds.Softness
                    );
                    into.CloudScale = Scalar(
                        fallback: into.CloudScale,
                        mirror: mirror,
                        scalar: clouds.Scale
                    );
                    into.CloudSeed = (clouds.Seed ?? into.CloudSeed);
                    into.CloudColor = Rgb(
                        color: clouds.Color,
                        fallback: into.CloudColor,
                        mirror: mirror
                    );
                    into.CloudCurl = Angle(
                        angle: clouds.Curl,
                        fallback: into.CloudCurl,
                        mirror: mirror
                    );
                    into.CloudDriftOffset = Integrate(
                        mirror: mirror,
                        modulus: SdfVolume.NoisePeriodCells,
                        rate: clouds.Drift
                    );
                    into.CloudShearOffset = Integrate(
                        mirror: mirror,
                        modulus: SdfVolume.NoisePeriodCells,
                        rate: clouds.Shear
                    );
                    into.CloudSpinAngle = ((float)Integrate(
                        mirror: mirror,
                        modulus: Math.Tau,
                        rate: clouds.Spin
                    ));
                    into.SkyEnabled = true;

                    break;
                }
        }
    }
    private void WriteEnvironment(WorldStateMirror mirror, WorldRenderEnvironment? environment, SdfEnvironment into) {
        var count = Math.Min(
            val1: (environment?.Softboxes?.Count ?? 0),
            val2: SdfEnvironment.MaxSoftboxes
        );

        for (var index = 0; (index < count); index++) {
            var authored = environment!.Softboxes![index];

            into.SetSoftbox(
                index: index,
                softbox: new SdfSoftbox(
                    Blur: (authored.Blur ?? 0f),
                    Color: Rgb(
                        color: authored.Color,
                        fallback: Vector3.One,
                        mirror: mirror
                    ),
                    Direction: authored.Direction,
                    Size: authored.Size,
                    Weight: (authored.Weight ?? 1f)
                )
            );
        }

        into.SoftboxCount = count;
        into.HorizonLow = Rgb(
            color: environment?.Horizon?.Low,
            fallback: Vector3.Zero,
            mirror: mirror
        );
        into.HorizonHigh = Rgb(
            color: environment?.Horizon?.High,
            fallback: Vector3.Zero,
            mirror: mirror
        );
    }
}
