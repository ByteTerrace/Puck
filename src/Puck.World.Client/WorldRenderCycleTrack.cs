using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>Resolves a definition's environment each frame: the static <c>render.lighting</c>/<c>render.sky</c>
/// written into an <see cref="SdfEnvironment"/>, or, when it authors a <c>render.cycle</c>, the two keys bracketing
/// the state row's live value (through the one state read every consumer shares) blended lane by lane. Statics and
/// keys are resolved once per definition revision — a live edit to either section, or to a state cell a colour binds
/// to, lands on the next frame — each key holding every lane the previous key left it, starting from the statics,
/// the last wrapping into the first.</summary>
public sealed class WorldRenderCycleTrack {
    private readonly SdfEnvironment[] m_output = [new SdfEnvironment(), new SdfEnvironment()];
    private readonly SdfEnvironment m_statics = new();
    private readonly float[] m_blend = new float[SdfEnvironment.LaneCount];
    private float[][] m_keys = [];
    private float[] m_keyAts = [];
    private int m_outputIndex;
    private int m_revision = -1;
    private string? m_stateRow;

    // The render path is opaque — alpha plays no part in any environment colour, so every bound colour drops it here,
    // the one seam between BindableColor's Vector4 grammar and the lane table's three colour lanes.
    private static Vector3 Rgb(BindableColor? color, WorldDefinition definition, Vector3 fallback) {
        if (color is not { } bound) {
            return fallback;
        }

        var resolved = bound.Resolve(
            definition: definition,
            fallback: new Vector4(fallback, 1f)
        );

        return new Vector3(resolved.X, resolved.Y, resolved.Z);
    }
    private static int FirstDirectional(SdfEnvironment environment) {
        for (var index = 0; (index < environment.LightCount); index++) {
            if (environment.GetLight(index: index).Kind == SdfLightKind.Directional) {
                return index;
            }
        }

        return -1;
    }
    // The ONE document-to-lanes writer. With `carry` false the target is seeded from the pinned environment (absent
    // lighting = the pinned sun and hemisphere; an authored list = exactly those lights; absent sky = the pinned
    // two-stop gradient), and an absent field takes its kind's default; with `carry` true the target already holds
    // the previous key's lanes and an absent field keeps them. The sky is ENABLED by any layer that draws — a
    // gradient, the sun disc, stars, clouds — since each is miss-pixel content the shader composites only on the
    // authored path; fog alone leaves the pinned branch, which renders bit-identically to a world with no sky.
    private static void Write(WorldDefinition definition, WorldRenderLighting? lighting, WorldRenderSky? sky, SdfEnvironment into, bool carry) {
        if (!carry) {
            into.CopyFrom(source: ((lighting?.Lights is null)
                ? SdfEnvironment.Default()
                : new SdfEnvironment()));
        }

        if (lighting?.Lights is { } lights) {
            var count = Math.Min(val1: lights.Count, val2: SdfEnvironment.MaxLights);

            for (var index = 0; (index < count); index++) {
                var authored = lights[index];
                var previous = (carry
                    ? into.GetLight(index: index)
                    : PinnedLight(light: authored));

                into.SetLight(
                    index: index,
                    light: (authored switch {
                        WorldRenderLight.Directional directional => previous with {
                            Kind = SdfLightKind.Directional,
                            Direction = (directional.Direction ?? previous.Direction),
                            Color = Rgb(color: directional.Color, definition: definition, fallback: previous.Color),
                            Weight = (directional.Weight ?? previous.Weight),
                            Param = ((directional.AngularRadius is { } angularRadius) ? MathF.Tan(x: angularRadius) : previous.Param),
                            Shadows = (directional.Shadows ?? previous.Shadows),
                        },
                        WorldRenderLight.Hemisphere hemisphere => previous with {
                            Kind = SdfLightKind.Hemisphere,
                            Direction = Vector3.Zero,
                            Color = Rgb(color: hemisphere.Color, definition: definition, fallback: previous.Color),
                            Weight = (hemisphere.Base ?? previous.Weight),
                            Param = (hemisphere.Gradient ?? previous.Param),
                            Shadows = false,
                        },
                        WorldRenderLight.Rim rim => previous with {
                            Kind = SdfLightKind.Rim,
                            Direction = Vector3.Zero,
                            Color = Rgb(color: rim.Color, definition: definition, fallback: previous.Color),
                            Weight = (rim.Weight ?? previous.Weight),
                            Param = (rim.Power ?? previous.Param),
                            Shadows = false,
                        },
                        WorldRenderLight.Occluder occluder => previous with {
                            Kind = SdfLightKind.Occluder,
                            Direction = occluder.Position ?? previous.Direction,
                            Color = Vector3.Zero,
                            Weight = occluder.Weight ?? previous.Weight,
                            Param = occluder.Radius ?? previous.Param,
                            Shadows = false,
                            DynamicSlot = -1,
                        },
                        WorldRenderLight.Point point => previous with {
                            Kind = SdfLightKind.Point,
                            Direction = (point.Position ?? previous.Direction),
                            Color = Rgb(color: point.Color, definition: definition, fallback: previous.Color),
                            Weight = (point.Weight ?? previous.Weight),
                            Param = (point.Radius ?? previous.Param),
                            Shadows = false,
                            // Never carried: a live anchor is resolved fresh every frame by ApplyAnchors, after the
                            // statics/keys this method writes are cached for the revision.
                            DynamicSlot = -1,
                        },
                        _ => previous,
                    })
                );
            }

            if (!carry) {
                into.LightCount = count;
            }
        }

        if (lighting?.Curvature is { } curvature) {
            into.CurvatureCavity = (curvature.Cavity ?? into.CurvatureCavity);
            into.CurvatureRim = (curvature.Rim ?? into.CurvatureRim);
            into.CurvatureInk = (curvature.Ink ?? into.CurvatureInk);
            into.CurvatureInkLow = (curvature.InkLow ?? into.CurvatureInkLow);
            into.CurvatureInkHigh = (curvature.InkHigh ?? into.CurvatureInkHigh);
            into.CurvatureInkColor = Rgb(color: curvature.InkColor, definition: definition, fallback: into.CurvatureInkColor);
        }

        if (sky?.Layers is not { } layers) {
            return;
        }

        foreach (var layer in layers) {
            switch (layer) {
                case WorldRenderSkyLayer.Gradient gradient: {
                        var stops = gradient.Stops;

                        if (stops is null) {
                            break;
                        }

                        var count = Math.Min(val1: stops.Count, val2: SdfEnvironment.MaxSkyStops);

                        for (var index = 0; (index < count); index++) {
                            var stop = stops[index];
                            var (previousColor, previousElevation) = (carry
                                ? into.GetSkyStop(index: index)
                                : (Vector3.One, 0f));

                            into.SetSkyStop(
                                index: index,
                                color: Rgb(color: stop?.Color, definition: definition, fallback: previousColor),
                                elevation: (stop?.Elevation ?? previousElevation)
                            );
                        }

                        if (!carry) {
                            into.SkyStopCount = count;
                        }

                        into.SkyEnabled |= (count > 0);

                        break;
                    }
                case WorldRenderSkyLayer.Fog fog: {
                        into.FogDensity = (fog.Density ?? into.FogDensity);

                        break;
                    }
                case WorldRenderSkyLayer.SunDisc disc: {
                        into.SunDiscLightIndex = (disc.Light ?? ((carry && (into.SunDiscLightIndex >= 0))
                            ? into.SunDiscLightIndex
                            : ((into.ShadowLightIndex >= 0) ? into.ShadowLightIndex : FirstDirectional(environment: into))));
                        into.SunDiscRadians = (disc.Radius ?? into.SunDiscRadians);
                        into.SunDiscIntensity = (disc.Intensity ?? into.SunDiscIntensity);
                        into.SkyEnabled = true;

                        break;
                    }
                case WorldRenderSkyLayer.Stars stars: {
                        into.StarDensity = (stars.Density ?? into.StarDensity);
                        into.StarBrightness = (stars.Brightness ?? into.StarBrightness);
                        into.StarSeed = (stars.Seed ?? into.StarSeed);
                        into.SkyEnabled = true;

                        if (stars.Twinkle is { } twinkle) {
                            into.TwinkleShare = (twinkle.Share ?? into.TwinkleShare);
                            into.TwinkleDepth = (twinkle.Depth ?? into.TwinkleDepth);
                            into.TwinkleRate = (twinkle.Rate ?? into.TwinkleRate);
                        }

                        break;
                    }
                case WorldRenderSkyLayer.Clouds clouds: {
                        into.CloudCoverage = (clouds.Coverage ?? into.CloudCoverage);
                        into.CloudSoftness = (clouds.Softness ?? into.CloudSoftness);
                        into.CloudScale = (clouds.Scale ?? into.CloudScale);
                        into.CloudSeed = (clouds.Seed ?? into.CloudSeed);
                        into.CloudColor = Rgb(color: clouds.Color, definition: definition, fallback: into.CloudColor);
                        into.CloudDrift = (clouds.Drift ?? into.CloudDrift);
                        into.CloudSpin = (clouds.Spin ?? into.CloudSpin);
                        into.CloudCurl = (clouds.Curl ?? into.CloudCurl);
                        into.CloudShear = (clouds.Shear ?? into.CloudShear);
                        into.SkyEnabled = true;

                        break;
                    }
            }
        }
    }
    // render.environment/render.tonemap are NOT part of a render.cycle key (WorldRenderCycleKey carries no
    // environment/tonemap field), so they are written directly onto the statics once per revision rather than
    // through Write's per-key carry — every cycle key inherits the same value via CopyFrom, so blending two
    // identical lane values (whatever SdfEnvironment.BlendOf classifies them as) is exact.
    private static void WriteEnvironment(WorldDefinition definition, WorldRenderEnvironment? environment, SdfEnvironment into, WorldTonemap? tonemap) {
        into.Tonemap = ((tonemap ?? WorldTonemap.None) switch {
            WorldTonemap.None => SdfTonemapMode.None,
            WorldTonemap.Filmic => SdfTonemapMode.Filmic,
            var other => throw new ArgumentOutOfRangeException(paramName: nameof(tonemap), actualValue: other, message: "render.tonemap names a mode the environment lane table does not carry."),
        });

        var count = Math.Min(val1: (environment?.Softboxes?.Count ?? 0), val2: SdfEnvironment.MaxSoftboxes);

        for (var index = 0; (index < count); index++) {
            var authored = environment!.Softboxes![index];

            into.SetSoftbox(
                index: index,
                softbox: new SdfSoftbox(
                    Direction: authored.Direction,
                    Color: Rgb(color: authored.Color, definition: definition, fallback: Vector3.One),
                    Weight: (authored.Weight ?? 1f),
                    Size: authored.Size,
                    Blur: (authored.Blur ?? 0f)
                )
            );
        }

        into.SoftboxCount = count;
        into.HorizonLow = Rgb(color: environment?.Horizon?.Low, definition: definition, fallback: Vector3.Zero);
        into.HorizonHigh = Rgb(color: environment?.Horizon?.High, definition: definition, fallback: Vector3.Zero);

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
        WorldRenderLight.Occluder => new SdfLight(SdfLightKind.Occluder, Vector3.Zero, Vector3.Zero, 0f, 1f, false),
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
    private void Rebuild(WorldDefinition definition, WorldRenderCycle cycle) {
        var keys = cycle.Keys;
        var resolved = new float[keys.Count][];
        var ats = new float[keys.Count];
        var carried = new SdfEnvironment();

        carried.CopyFrom(source: m_statics);

        // Two passes: the second lets the first key inherit whatever the last key left, so the wrap holds lanes too.
        for (var pass = 0; (pass < 2); pass++) {
            for (var index = 0; (index < keys.Count); index++) {
                Write(
                    carry: true,
                    definition: definition,
                    into: carried,
                    lighting: keys[index].Lighting,
                    sky: keys[index].Sky
                );
                resolved[index] = carried.Lanes.ToArray();
                ats[index] = keys[index].At;
            }
        }

        m_keys = resolved;
        m_keyAts = ats;
        m_stateRow = cycle.State;
    }

    // A point light's anchor rides the resolver's live dynamic-transform slot every call (never cached with the
    // statics/keys above, which move only once per revision) — an anchored placement's pool slot can differ from
    // frame to frame independently of the definition. definition.Render.Lighting is read directly rather than
    // through the cached SdfEnvironment because only the document carries which light is a Point with an anchor;
    // a cycle key may not change a light's kind (validated), so the statics' kind at each index holds for every key.
    private static void ApplyAnchors(SdfEnvironment output, WorldDefinition definition, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor) {
        if (definition.Render.Lighting?.Lights is not { } lights) {
            return;
        }

        var count = Math.Min(val1: lights.Count, val2: output.LightCount);

        for (var index = 0; (index < count); index++) {
            var anchor = lights[index] switch {
                WorldRenderLight.Point point => point.Anchor,
                WorldRenderLight.Occluder occluder => occluder.Anchor,
                _ => null,
            };
            if (anchor is null) { continue; }
            var pose = resolveLightAnchor?.Invoke(anchor);
            var light = output.GetLight(index);
            output.SetLight(index, pose is { } frame
                ? light with { DynamicSlot = -1, Direction = frame.Position + Vector3.Transform(light.Direction, frame.Orientation) }
                : light with { DynamicSlot = -1, Weight = 0f });
        }
    }
    /// <summary>Resolves this frame's environment: the cycle's interpolation at the state row's live value, or the
    /// statics when the definition authors no cycle or the row cannot be read. The returned instance is reused every
    /// other call; a consumer that must hold one across frames copies it.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="revision">The definition revision (statics and keys are resolved once per revision).</param>
    /// <param name="tick">The tick to read the state row as of.</param>
    /// <param name="resolveLightAnchor">Resolves a positional light anchor to its current pose.
    /// Missing targets return null and disable the light for this frame.</param>
    public SdfEnvironment Resolve(WorldDefinition definition, int revision, ulong tick, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var cycle = definition.Render.Cycle;

        if (revision != m_revision) {
            m_revision = revision;
            Write(
                carry: false,
                definition: definition,
                into: m_statics,
                lighting: definition.Render.Lighting,
                sky: definition.Render.Sky
            );
            WriteEnvironment(
                definition: definition,
                environment: definition.Render.Environment,
                into: m_statics,
                tonemap: definition.Render.Tonemap
            );

            if (cycle is { Keys.Count: >= 2 }) {
                Rebuild(
                    cycle: cycle,
                    definition: definition
                );
            }
        }

        var output = m_output[m_outputIndex];

        m_outputIndex ^= 1;

        if (cycle is not { Keys.Count: >= 2 }) {
            output.CopyFrom(source: m_statics);
            ApplyAnchors(output: output, definition: definition, resolveLightAnchor: resolveLightAnchor);

            return output;
        }

        if (
            !WorldStateReader.TryRead(
                definition: definition,
                key: null,
                rawValue: out var rawValue,
                row: out var row,
                rowName: m_stateRow!,
                text: out _,
                tick: tick
            ) ||
            (rawValue is not { } raw)
        ) {
            output.CopyFrom(source: m_statics);
            ApplyAnchors(output: output, definition: definition, resolveLightAnchor: resolveLightAnchor);

            return output;
        }

        var value = ((row.Kind == CellKind.Fixed)
            ? ((double)FixedQ4816.FromRawBits(value: raw))
            : ((double)raw)
        );
        var fraction = ((float)(value - Math.Floor(d: value)));
        var count = m_keys.Length;
        var index = 0;

        while (
            ((index + 1) < count) &&
            (m_keyAts[(index + 1)] <= fraction)
        ) {
            index++;
        }

        // Before the first key, or after the last: the segment from the last key wrapping round to the first.
        var fromIndex = ((fraction < m_keyAts[0])
            ? (count - 1)
            : index);
        var toIndex = ((fromIndex + 1) % count);
        var fromAt = m_keyAts[fromIndex];
        var toAt = m_keyAts[toIndex];
        var span = ((toIndex == 0)
            ? ((1f - fromAt) + toAt)
            : (toAt - fromAt));
        var offset = ((fraction >= fromAt)
            ? (fraction - fromAt)
            : ((1f - fromAt) + fraction));
        var t = ((span > 0f)
            ? Math.Clamp(
                max: 1f,
                min: 0f,
                value: (offset / span)
            )
            : 0f);
        SdfEnvironment.Blend(
            from: m_keys[fromIndex],
            into: m_blend,
            t: t,
            to: m_keys[toIndex]
        );
        output.CopyFrom(lanes: m_blend);
        ApplyAnchors(output: output, definition: definition, resolveLightAnchor: resolveLightAnchor);

        return output;
    }
}
