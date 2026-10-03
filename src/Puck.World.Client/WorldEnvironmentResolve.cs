using System.Numerics;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.SdfVm;

namespace Puck.World.Client;

/// <summary>
/// Resolves a definition's environment for a frame: <c>render.lighting</c>, <c>render.sky</c>, <c>render.atmosphere</c>
/// and <c>render.environment</c> written into the frame's <see cref="SdfLights"/> and <see cref="SdfSky"/> (the atmosphere
/// as <see cref="SdfSky.Atmosphere"/>), every value read
/// through the client's
/// <see cref="WorldStateMirror"/>, the one binding path: a literal as authored, a binding as its slot presents it, and
/// keys at their clock's presented phase (<see cref="WorldKeyResolver"/>). A keyed section is expanded once per
/// definition revision (<see cref="WorldRenderKeys.Expand(WorldRenderSky)"/>), so a section key resolves as the value
/// keys it lands in. Each cloud rate and the star twinkle's rate are integrated to the presented tick
/// (<see cref="WorldStateMirror.Integrate(in BindableScalar, double)"/>), keyed or literal, so the environment carries
/// offsets and a phase that never jump where a key changes a rate.
/// <para>
/// The environment re-resolves only when its definition moves, a slot one of its bindings or state clocks reads
/// moves, or the presented tick moves while it reads a tick clock or a moving anchor, or integrates a rate; every other frame copies
/// the last resolution, and <see cref="Resolutions"/> counts the ones it made. A point light's anchor is resolved fresh
/// every frame.
/// </para>
/// </summary>
public sealed partial class WorldEnvironmentResolve : IDisposable {
    // An unauthored lighting section seeds from the pinned sun and hemisphere; an authored list seeds from nothing.
    private static readonly SdfLights Pinned = SdfLights.Default();
    private static readonly SdfLights Empty = new();
    private static readonly SdfSky Unauthored = new();
    private readonly List<(int Slot, double Value)> m_bound = [];

    private readonly WorldValueDomainGuard m_domains;

    private readonly SdfLights[] m_outputLights = [new SdfLights(), new SdfLights()];
    private readonly SdfSky[] m_outputSky = [new SdfSky(), new SdfSky()];
    private readonly SdfLights m_resolvedLights = new();
    private readonly SdfSky m_resolvedSky = new();

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

    // Each expanded layer's label (WorldSkyLayers.LabelsOf), and where the last resolution wrote it in the stack.
    private string?[] m_skyLabels = [];
    private int[] m_skyLayerIndex = [];

    private PresentedTick m_tick;

    /// <summary>Initializes a new instance of the <see cref="WorldEnvironmentResolve"/> class.</summary>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    public WorldEnvironmentResolve(WorldValueDomainGuard domains) => m_domains = (domains ?? throw new ArgumentNullException(paramName: nameof(domains)));

    /// <summary>Gets how many times this resolver has resolved the environment rather than copying its last
    /// resolution.</summary>
    public int Resolutions => m_resolutions;

    /// <summary>Resolves this frame's lights and sky. The returned instances are reused every other call; a consumer
    /// that must hold them across frames copies them.</summary>
    /// <param name="definition">The live definition.</param>
    /// <param name="revision">The definition revision; a keyed section is expanded once per revision.</param>
    /// <param name="mirror">The state mirror every binding and keyed value reads through.</param>
    /// <param name="resolveLightAnchor">Resolves a positional light anchor to its current pose. Missing targets
    /// return null and disable the light for this frame.</param>
    /// <param name="shadows">The live policy, or the definition's boot policy.</param>
    /// <param name="shadowSelection">A session's complete delivered selection, or this resolver's own subscription.</param>
    /// <param name="skyQuality">The sky's quality tier (<c>world.sky-quality</c>), or <see langword="null"/> for the
    /// definition's boot tier (<c>render.skyQuality</c>).</param>
    /// <returns>The lights and the sky.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="mirror"/> is
    /// <see langword="null"/>.</exception>
    public WorldResolvedEnvironment Resolve(WorldDefinition definition, int revision, WorldStateMirror mirror, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null, WorldShadowSettings? shadows = null, WorldShadowSelection? shadowSelection = null, SdfSkyTier? skyQuality = null) {
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
            m_skyLabels = WorldSkyLayers.LabelsOf(layers: (m_sky?.Layers ?? []));
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
                atmosphere: definition.Render.Atmosphere,
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

        PrepareShadows(mirror, (shadows ?? WorldShadowSettings.From(render: definition.Render)), shadowSelection);

        var lights = m_outputLights[m_outputIndex];
        var sky = m_outputSky[m_outputIndex];

        m_outputIndex ^= 1;
        lights.CopyFrom(source: m_resolvedLights);
        sky.CopyFrom(source: m_resolvedSky);
        sky.Quality = (skyQuality ?? WorldSkyLayers.TierOf(tier: definition.Render.SkyQuality));
        ApplyAnchors(
            lighting: m_lighting,
            output: lights,
            resolveLightAnchor: resolveLightAnchor
        );

        ApplyShadows(lights: lights, mirror: mirror);
        ApplySunDiscLight(lights: lights, sky: sky);
        return new WorldResolvedEnvironment(Lights: lights, Sky: sky);
    }

    // A point light's anchor rides the live dynamic-transform slot every call, never the cached resolution: an anchored
    // placement's pool slot can differ from frame to frame independently of the definition.
    private static void ApplyAnchors(SdfLights output, WorldRenderLighting? lighting, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor) {
        if (lighting?.Lights is not { } lights) {
            return;
        }

        var count = Math.Min(
            val1: lights.Count,
            val2: output.Count
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
            var light = output[index];

            output.Set(
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
    private static int FirstDirectional(SdfLights lights) {
        for (var index = 0; (index < lights.Count); index++) {
            if (lights[index].Kind == SdfLightKind.Directional) {
                return index;
            }
        }

        return -1;
    }
    private static SdfLight PinnedLight(WorldRenderLight light) => (light switch {
        WorldRenderLight.Directional => new SdfLight(
            Kind: SdfLightKind.Directional,
            Direction: SdfLights.DefaultSunDirection,
            Color: Vector3.One,
            Weight: SdfLights.DefaultSunWeight,
            Param: SdfLights.DefaultPenumbraSlope,
            Shadows: false
        ),
        WorldRenderLight.Hemisphere => new SdfLight(
            Kind: SdfLightKind.Hemisphere,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: SdfLights.DefaultAmbientBase,
            Param: SdfLights.DefaultAmbientHemisphere,
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
            Weight: SdfLights.DefaultPointWeight,
            Param: SdfLights.DefaultPointRadius,
            Shadows: false
        ),
        _ => new SdfLight(
            Kind: SdfLightKind.Rim,
            Direction: Vector3.Zero,
            Color: Vector3.One,
            Weight: 0f,
            Param: SdfLights.DefaultRimPower,
            Shadows: false
        ),
    });
    private bool BoundSlotMoved(WorldStateMirror mirror) {
        foreach (var (slot, value) in m_bound) {
            _ = mirror.TryValue(slot: slot, value: out var presented);

            if ((mirror.Changed(slot: slot) > m_resolvedAt) || (presented != value)) {
                return true;
            }
        }

        return false;
    }
    // Notes what a keyed value reads besides its keys: a state clock's slot, the presented tick a tick clock or a moving
    // anchor moves with, or nothing for an anchor held still, which moves only with the definition.
    private void NoteKeys(WorldStateMirror mirror, IWorldKeyTrack? keys) {
        if (keys is null) {
            return;
        }

        var slot = mirror.ClockSlotOf(name: keys.Clock);

        if (slot >= 0) {
            NoteSlot(mirror: mirror, slot: slot);
        } else if (!mirror.ClockHoldsStill(name: keys.Clock)) {
            m_readsTick = true;
        }
    }
    private void NoteBinding(WorldStateMirror mirror, StateBinding? binding, WorldStateConversion conversion) {
        if (binding is { } bound) {
            NoteSlot(mirror: mirror, slot: mirror.SlotOf(
                binding: in bound,
                conversion: conversion
            ));
        }
    }
    // Apply can move a presented number between deliveries without changing the slot's sample revision.
    private void NoteSlot(WorldStateMirror mirror, int slot) {
        _ = mirror.TryValue(slot: slot, value: out var value);
        m_bound.Add(item: (slot, value));
    }
    // Every resolved scalar is mapped into its field's declared domain, so no value a bound row strays to reaches the
    // GPU record; an absent field keeps its fallback, the engine default.
    private float Scalar(WorldStateMirror mirror, BindableScalar? scalar, float fallback, WorldValueField field, in WorldValueSite site) {
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

        return m_domains.Resolve(
            fallback: fallback,
            field: field,
            mirror: mirror,
            scalar: in value,
            site: in site,
            value: mirror.Scalar(
                fallback: fallback,
                scalar: in value
            )
        );
    }
    private float Angle(WorldStateMirror mirror, BindableAngle? angle, float fallback, WorldValueField field, in WorldValueSite site) {
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

        return m_domains.Resolve(
            fallback: fallback,
            field: field,
            mirror: mirror,
            scalar: value.Value,
            site: in site,
            value: mirror.Angle(
                angle: in value,
                fallback: fallback
            )
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
    // The one document-to-records writer. The lights seed from the pinned ones when the world authors no light list (the
    // pinned sun and hemisphere) and from nothing when it does, the sky from the unauthored one, and an absent field
    // takes its kind's default.
    // An absent atmosphere is the default look's (SdfAtmosphere.Default); an authored one is exactly the kinds it states.
    private void Write(WorldStateMirror mirror, WorldRenderLighting? lighting, WorldRenderSky? sky, WorldRenderAtmosphere? atmosphere, WorldRenderEnvironment? environment) {
        var into = m_resolvedLights;

        into.CopyFrom(source: ((lighting?.Lights is null)
            ? Pinned
            : Empty));
        m_resolvedSky.CopyFrom(source: Unauthored);

        if (lighting?.Lights is { } lights) {
            var count = Math.Min(
                val1: lights.Count,
                val2: SdfLights.MaxLights
            );

            for (var index = 0; (index < count); index++) {
                var authored = lights[index];
                var pinned = PinnedLight(light: authored);
                var lightSite = new WorldValueSite(
                    Index: index,
                    Section: "render.lighting.lights"
                );

                into.Set(
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
                                    mirror: mirror,
                                    field: WorldValueFields.DirectionalAngularRadius,
                                    site: lightSite
                                ))
                                : pinned.Param),
                            Shadows = 0u,
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: directional.Weight,
                                field: WorldValueFields.DirectionalWeight,
                                site: lightSite
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
                                scalar: hemisphere.Gradient,
                                field: WorldValueFields.HemisphereGradient,
                                site: lightSite
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: hemisphere.Base,
                                field: WorldValueFields.HemisphereBase,
                                site: lightSite
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
                                scalar: rim.Power,
                                field: WorldValueFields.RimPower,
                                site: lightSite
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: rim.Weight,
                                field: WorldValueFields.RimWeight,
                                site: lightSite
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
                                scalar: occluder.Radius,
                                field: WorldValueFields.OccluderRadius,
                                site: lightSite
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: occluder.Weight,
                                field: WorldValueFields.OccluderWeight,
                                site: lightSite
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
                                scalar: point.Radius,
                                field: WorldValueFields.PointRadius,
                                site: lightSite
                            ),
                            Weight = Scalar(
                                fallback: pinned.Weight,
                                mirror: mirror,
                                scalar: point.Weight,
                                field: WorldValueFields.PointWeight,
                                site: lightSite
                            ),
                        },
                        _ => pinned,
                    })
                );
            }

            into.Count = count;
        }

        if (lighting?.Curvature is { } curvature) {
            var seed = into.Curvature;
            var curvatureSite = new WorldValueSite(Section: "render.lighting.curvature");

            into.Curvature = new SdfCurvature(
                Cavity: Scalar(
                    fallback: seed.Cavity,
                    mirror: mirror,
                    scalar: curvature.Cavity,
                    field: WorldValueFields.CurvatureCavity,
                    site: curvatureSite
                ),
                Rim: Scalar(
                    fallback: seed.Rim,
                    mirror: mirror,
                    scalar: curvature.Rim,
                    field: WorldValueFields.CurvatureRim,
                    site: curvatureSite
                ),
                Ink: Scalar(
                    fallback: seed.Ink,
                    mirror: mirror,
                    scalar: curvature.Ink,
                    field: WorldValueFields.CurvatureInk,
                    site: curvatureSite
                ),
                InkLow: Scalar(
                    fallback: seed.InkLow,
                    mirror: mirror,
                    scalar: curvature.InkLow,
                    field: WorldValueFields.CurvatureInkLow,
                    site: curvatureSite
                ),
                InkHigh: Scalar(
                    fallback: seed.InkHigh,
                    mirror: mirror,
                    scalar: curvature.InkHigh,
                    field: WorldValueFields.CurvatureInkHigh,
                    site: curvatureSite
                ),
                InkColor: Rgb(
                    color: curvature.InkColor,
                    fallback: seed.InkColor,
                    mirror: mirror
                )
            );
        }

        var layers = (sky?.Layers ?? []);

        // An authored gradient replaces the default look's; a stack with none draws over it.
        for (var index = 0; (index < layers.Count); index++) {
            if (layers[index] is WorldRenderSkyLayer.Gradient) {
                m_resolvedSky.ClearLayers();

                break;
            }
        }

        m_resolvedSky.FrameUp = ((sky?.Frame?.Up is { } up) ? ((Vector3)up) : Vector3.UnitY);

        if (m_skyLayerIndex.Length < layers.Count) {
            m_skyLayerIndex = new int[layers.Count];
        }

        for (var index = 0; (index < layers.Count); index++) {
            m_skyLayerIndex[index] = WriteLayer(
                into: m_resolvedSky,
                label: m_skyLabels[index],
                layer: layers[index],
                lights: into,
                mirror: mirror,
                site: new WorldValueSite(
                    Index: index,
                    Section: "render.sky.layers"
                )
            );
        }

        WriteEnvironment(
            environment: environment,
            into: m_resolvedSky,
            mirror: mirror
        );
        WriteAtmosphere(
            atmosphere: atmosphere,
            into: ref m_resolvedSky.Atmosphere,
            mirror: mirror
        );
    }
    private void WriteAtmosphere(WorldStateMirror mirror, WorldRenderAtmosphere? atmosphere, ref SdfAtmosphere into) {
        if (atmosphere is null) {
            into = SdfAtmosphere.Default;

            return;
        }

        var site = new WorldValueSite(Section: "render.atmosphere");

        into = SdfAtmosphere.None;
        if (atmosphere.Fog is { } fog) {
            var fogSite = site with { Inner = "fog" };

            into.FogDensity = Scalar(fallback: SdfSky.DefaultFogDensity, field: WorldValueFields.FogDensity, mirror: mirror, scalar: fog.Density, site: fogSite);
            into.FogColorAuthored = (fog.Color is not null);
            into.FogColor = Rgb(color: fog.Color, fallback: Vector3.Zero, mirror: mirror);
            (into.FogBase, into.FogFalloff) = Height(height: fog.Height, mirror: mirror, site: (fogSite with { Inner = "fog.height" }));
        }
        if (atmosphere.Haze is { } haze) {
            var hazeSite = site with { Inner = "haze" };

            into.HazeAmount = Scalar(fallback: 0f, field: WorldValueFields.HazeAmount, mirror: mirror, scalar: haze.Amount, site: hazeSite);
            into.HazeAnisotropy = Scalar(fallback: SdfAtmosphere.DefaultHazeAnisotropy, field: WorldValueFields.HazeAnisotropy, mirror: mirror, scalar: haze.Anisotropy, site: hazeSite);
            (into.HazeBase, into.HazeFalloff) = Height(height: haze.Height, mirror: mirror, site: (hazeSite with { Inner = "haze.height" }));
        }
        if (atmosphere.Medium is { } medium) {
            var mediumSite = site with { Inner = "medium" };

            into.MediumSurface = Scalar(fallback: 0f, field: WorldValueFields.MediumSurface, mirror: mirror, scalar: medium.Surface, site: mediumSite);
            into.MediumExtinction = Scalar(fallback: SdfAtmosphere.DefaultMediumExtinction, field: WorldValueFields.MediumExtinction, mirror: mirror, scalar: medium.Extinction, site: mediumSite);
            into.MediumColor = Rgb(color: medium.Color, fallback: SdfAtmosphere.DefaultMediumColor, mirror: mirror);
        }

        // An absent profile is a level kind, falloff zero; an authored one takes the default falloff unless it states one.
        (float Base, float Falloff) Height(WorldRenderAirHeight? height, WorldStateMirror mirror, in WorldValueSite site) => ((height is null)
            ? (0f, 0f)
            : (
                Scalar(fallback: 0f, field: WorldValueFields.AirBase, mirror: mirror, scalar: height.Base, site: in site),
                Scalar(fallback: SdfAtmosphere.DefaultFalloff, field: WorldValueFields.AirFalloff, mirror: mirror, scalar: height.Falloff, site: in site)
            ));
    }
    // Writes one document layer into the stack, returning its index there: −1 for a layer past the stack's capacity,
    // which the validator refuses.
    private int WriteLayer(WorldStateMirror mirror, WorldRenderSkyLayer layer, string? label, SdfLights lights, SdfSky into, WorldValueSite site) {
        if ((label is null) || (into.LayerCount >= SdfSky.MaxLayers)) {
            return -1;
        }

        var blend = WorldSkyLayers.BlendOf(layer: layer);
        var visibility = WorldSkyLayers.VisibilityOf(layer: layer);
        var tier = WorldSkyLayers.TierOf(layer: layer);
        var opacity = Scalar(fallback: 1f, field: WorldValueFields.SkyLayerOpacity, mirror: mirror, scalar: layer.Opacity, site: site);
        var at = layer switch {
            WorldRenderSkyLayer.Gradient gradient => into.Add(blend: blend, label: label, opacity: opacity, parameters: GradientOf(gradient: gradient, mirror: mirror, site: site), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.SunDisc disc => into.Add(blend: blend, label: label, opacity: opacity, parameters: DiscOf(disc: disc, lights: lights, mirror: mirror, site: site), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Stars stars => into.Add(blend: blend, label: label, opacity: opacity, parameters: StarsOf(mirror: mirror, site: site, stars: stars), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Clouds clouds => into.Add(blend: blend, label: label, opacity: opacity, parameters: CloudsOf(clouds: clouds, mirror: mirror, site: site), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Aurora aurora => into.Add(blend: blend, label: label, opacity: opacity, parameters: AuroraOf(aurora: aurora, mirror: mirror, site: site), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Noise noise => into.Add(blend: blend, label: label, opacity: opacity, parameters: NoiseOf(mirror: mirror, noise: noise, site: site), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Pattern pattern => into.Add(blend: blend, label: label, opacity: opacity, parameters: PatternOf(mirror: mirror, pattern: pattern), tier: tier, visibility: visibility),
            WorldRenderSkyLayer.Panorama panorama => into.Add(blend: blend, label: label, opacity: opacity, parameters: PanoramaOf(mirror: mirror, panorama: panorama, site: site), tier: tier, visibility: visibility),
            _ => -1,
        };

        if (at < 0) {
            return -1;
        }

        ref var record = ref into.LayerAt(index: at);

        record.Phase = ClockPhase(clock: layer.Clock, mirror: mirror);
        record.Rotation = SdfSkyLayer.RotationOf(
            tilt: Angle(fallback: 0f, field: WorldValueFields.SkyLayerTilt, mirror: mirror, angle: layer.Transform?.Tilt, site: site),
            turn: Angle(fallback: 0f, field: WorldValueFields.SkyLayerTurn, mirror: mirror, angle: layer.Transform?.Turn, site: site)
        );
        WriteMask(mask: layer.Mask, record: ref record);

        return at;
    }
    // A mask's record lanes: an elevation band as heights (the sines of its elevations) with its feather as a height, or
    // a cone as its axis and the cosine of its spread with its feather as the cosine's fall over that angle.
    private static void WriteMask(WorldRenderSkyMask? mask, ref SdfSkyLayer record) {
        if (mask is null) {
            return;
        }

        var feather = Math.Max(val1: (mask.Feather ?? 0d), val2: 0d);

        if (mask.Band is [var low, var high]) {
            record.Mask = SdfSkyMask.Elevation;
            record.MaskBand = new Vector4(x: ((float)Math.Sin(a: low)), y: ((float)Math.Sin(a: high)), z: 0f, w: 0f);
            record.MaskSoftness = ((float)Math.Sin(a: Math.Min(val1: feather, val2: (Math.PI / 2d))));
        } else if (mask.Cone is { } cone) {
            var spread = Math.Clamp(max: Math.PI, min: 0d, value: cone.Spread);
            var axis = ((Vector3)cone.Toward);

            record.Mask = SdfSkyMask.Cone;
            record.MaskBand = new Vector4(value: axis, w: ((float)Math.Cos(d: spread)));
            record.MaskSoftness = ((float)(Math.Cos(d: spread) - Math.Cos(d: Math.Min(val1: (spread + feather), val2: Math.PI))));
        }
    }
    // A layer clock's phase at the presented tick, in cycles in [0, 1), noting what it reads: a state clock's slot, or the
    // tick for a tick clock.
    private float ClockPhase(WorldStateMirror mirror, string? clock) {
        if (clock is null) {
            return 0f;
        }

        var slot = mirror.ClockSlotOf(name: clock);

        if (slot >= 0) {
            NoteSlot(mirror: mirror, slot: slot);
        } else if (!mirror.ClockHoldsStill(name: clock)) {
            m_readsTick = true;
        }

        return (mirror.TryPhase(clock: out _, name: clock, phase: out var phase) ? ((float)phase) : 0f);
    }
    private SdfSkyGradient GradientOf(WorldStateMirror mirror, WorldRenderSkyLayer.Gradient gradient, in WorldValueSite site) {
        if (gradient.Stops is not { } stops) {
            return SdfSky.DefaultGradient;
        }

        var parameters = new SdfSkyGradient();
        var count = Math.Min(
            val1: stops.Count,
            val2: SdfSky.MaxStops
        );
        var stopSite = site with { Inner = "stops" };

        for (var index = 0; (index < count); index++) {
            var stop = stops[index];

            parameters.SetStop(
                color: Rgb(
                    color: stop?.Color,
                    fallback: Vector3.One,
                    mirror: mirror
                ),
                elevation: Scalar(
                    fallback: 0f,
                    mirror: mirror,
                    scalar: stop?.Elevation,
                    field: WorldValueFields.StopElevation,
                    site: stopSite
                ),
                index: index
            );
        }

        parameters.Count = ((uint)count);

        return parameters;
    }
    // A disc's light is its authored one, or the first directional; a disc that names none follows the first shadow slot
    // once the frame's slots are known (ApplySunDiscLight).
    private SdfSkyDisc DiscOf(WorldStateMirror mirror, WorldRenderSkyLayer.SunDisc disc, SdfLights lights, in WorldValueSite site) => new() {
        Color = Rgb(color: disc.Color, fallback: Vector3.One, mirror: mirror),
        Intensity = Scalar(
            fallback: 0f,
            mirror: mirror,
            scalar: disc.Intensity,
            field: WorldValueFields.SunDiscIntensity,
            site: site
        ),
        Light = (disc.Light ?? FirstDirectional(lights: lights)),
        Radius = Angle(
            angle: disc.Radius,
            fallback: SdfSkyDisc.DefaultRadius,
            mirror: mirror,
            field: WorldValueFields.SunDiscRadius,
            site: site
        ),
        Screen = (disc.Texture?.Screen ?? -1),
    };
    private SdfSkyStars StarsOf(WorldStateMirror mirror, WorldRenderSkyLayer.Stars stars, in WorldValueSite site) {
        var parameters = new SdfSkyStars();

        parameters.Density = (stars.Density ?? parameters.Density);
        parameters.Sparsity = (stars.Sparsity ?? parameters.Sparsity);
        parameters.RadiusFraction = (stars.Size ?? parameters.RadiusFraction);
        parameters.Brightness = Scalar(
            fallback: 0f,
            mirror: mirror,
            scalar: stars.Brightness,
            field: WorldValueFields.StarBrightness,
            site: site
        );
        parameters.Seed = (stars.Seed ?? 0u);

        if (stars.Twinkle is not { } twinkle) {
            return parameters;
        }

        var twinkleSite = site with { Inner = "twinkle" };

        parameters.TwinkleShare = Scalar(
            fallback: 0f,
            mirror: mirror,
            scalar: twinkle.Share,
            field: WorldValueFields.TwinkleShare,
            site: twinkleSite
        );
        parameters.TwinkleDepth = Scalar(
            fallback: 0f,
            mirror: mirror,
            scalar: twinkle.Depth,
            field: WorldValueFields.TwinkleDepth,
            site: twinkleSite
        );

        var rate = (twinkle.Rate ?? new BindableScalar(literal: SdfSky.DefaultTwinkleRate));
        var visible = (
            (parameters.Brightness > 0f) &&
            (parameters.Density > 0f) &&
            (parameters.TwinkleShare > 0f) &&
            (parameters.TwinkleDepth > 0f) &&
            (mirror.Scalar(
            fallback: 0f,
            scalar: in rate
        ) > 0f)
        );

        // A sky with no visible twinkle bakes phase zero, so a still frame's record repeats.
        if (visible) {
            var cycles = Integrate(
                mirror: mirror,
                modulus: 1d,
                rate: rate
            );

            parameters.TwinklePhase = ((float)((cycles < 0d)
                ? (cycles + 1d)
                : cycles));
        }

        return parameters;
    }
    private SdfSkyClouds CloudsOf(WorldStateMirror mirror, WorldRenderSkyLayer.Clouds clouds, in WorldValueSite site) {
        var parameters = new SdfSkyClouds();

        parameters.Coverage = Scalar(
            fallback: 0f,
            mirror: mirror,
            scalar: clouds.Coverage,
            field: WorldValueFields.CloudCoverage,
            site: site
        );
        parameters.Softness = Scalar(
            fallback: parameters.Softness,
            mirror: mirror,
            scalar: clouds.Softness,
            field: WorldValueFields.CloudSoftness,
            site: site
        );
        parameters.Scale = Scalar(
            fallback: parameters.Scale,
            mirror: mirror,
            scalar: clouds.Scale,
            field: WorldValueFields.CloudScale,
            site: site
        );
        parameters.Seed = (clouds.Seed ?? 0u);
        parameters.Color = Rgb(
            color: clouds.Color,
            fallback: parameters.Color,
            mirror: mirror
        );
        parameters.Curl = Angle(
            angle: clouds.Curl,
            fallback: 0f,
            mirror: mirror,
            field: WorldValueFields.CloudCurl,
            site: site
        );
        parameters.DriftOffset = Integrate(
            mirror: mirror,
            modulus: SdfVolume.NoisePeriodCells,
            rate: clouds.Drift
        );
        parameters.ShearOffset = Integrate(
            mirror: mirror,
            modulus: SdfVolume.NoisePeriodCells,
            rate: clouds.Shear
        );
        parameters.SpinAngle = ((float)Integrate(
            mirror: mirror,
            modulus: Math.Tau,
            rate: clouds.Spin
        ));
        parameters.Octaves = (clouds.Octaves ?? parameters.Octaves);
        parameters.Warp = (clouds.Warp ?? parameters.Warp);
        parameters.Height = (clouds.Relief ?? parameters.Height);
        parameters.Extinction = (clouds.Extinction ?? parameters.Extinction);

        return parameters;
    }
    // An aurora's angles become the heights the kernel reads: the base's sine, the height its top rises to above it, and
    // the fold's sway at the base's elevation.
    private SdfSkyAurora AuroraOf(WorldStateMirror mirror, WorldRenderSkyLayer.Aurora aurora, in WorldValueSite site) {
        var parameters = new SdfSkyAurora();
        var baseAngle = Angle(angle: aurora.Base, fallback: 0.3f, field: WorldValueFields.AuroraBase, mirror: mirror, site: site);
        var height = Angle(angle: aurora.Height, fallback: 0.35f, field: WorldValueFields.AuroraHeight, mirror: mirror, site: site);
        var fold = Angle(angle: aurora.Fold, fallback: 0.08f, field: WorldValueFields.AuroraFold, mirror: mirror, site: site);
        var baseHeight = Math.Sin(a: baseAngle);

        parameters.Intensity = Scalar(fallback: 0f, field: WorldValueFields.AuroraIntensity, mirror: mirror, scalar: aurora.Intensity, site: site);
        parameters.Color = Rgb(color: aurora.Color, fallback: parameters.Color, mirror: mirror);
        parameters.TopColor = Rgb(color: aurora.Top, fallback: parameters.TopColor, mirror: mirror);
        parameters.Base = ((float)baseHeight);
        parameters.Height = ((float)Math.Max(val1: (Math.Sin(a: Math.Min(val1: (baseAngle + height), val2: (Math.PI / 2d))) - baseHeight), val2: 1e-3d));
        parameters.Fold = ((float)(Math.Cos(d: baseAngle) * fold));
        parameters.Rays = (aurora.Rays ?? parameters.Rays);
        parameters.Waves = (aurora.Waves ?? parameters.Waves);
        parameters.Seed = (aurora.Seed ?? 0u);

        return parameters;
    }
    private SdfSkyNoise NoiseOf(WorldStateMirror mirror, WorldRenderSkyLayer.Noise noise, in WorldValueSite site) {
        var parameters = new SdfSkyNoise();

        parameters.ColorLow = Rgb(color: noise.Low, fallback: parameters.ColorLow, mirror: mirror);
        parameters.ColorHigh = Rgb(color: noise.High, fallback: parameters.ColorHigh, mirror: mirror);
        parameters.Coverage = Scalar(fallback: parameters.Coverage, field: WorldValueFields.NoiseCoverage, mirror: mirror, scalar: noise.Coverage, site: site);
        parameters.Softness = (noise.Softness ?? parameters.Softness);
        parameters.Scale = (noise.Scale ?? parameters.Scale);
        parameters.Octaves = (noise.Octaves ?? parameters.Octaves);
        parameters.Gain = (noise.Gain ?? parameters.Gain);
        parameters.Seed = (noise.Seed ?? 0u);

        return parameters;
    }
    private SdfSkyPattern PatternOf(WorldStateMirror mirror, WorldRenderSkyLayer.Pattern pattern) {
        var parameters = new SdfSkyPattern();

        parameters.Shape = (pattern.Shape switch {
            WorldSkyPatternShape.Stripes => SdfSkyPatternShape.Stripes,
            WorldSkyPatternShape.Grid => SdfSkyPatternShape.Grid,
            _ => SdfSkyPatternShape.Checker,
        });
        parameters.ColorA = Rgb(color: ((pattern.Colors is [var first, ..]) ? first : null), fallback: parameters.ColorA, mirror: mirror);
        parameters.ColorB = Rgb(color: ((pattern.Colors is [_, var second, ..]) ? second : null), fallback: parameters.ColorB, mirror: mirror);
        parameters.Cells = (pattern.Cells ?? parameters.Cells);
        parameters.Line = (pattern.Line ?? parameters.Line);
        parameters.Softness = (pattern.Softness ?? parameters.Softness);

        return parameters;
    }
    private SdfSkyPanorama PanoramaOf(WorldStateMirror mirror, WorldRenderSkyLayer.Panorama panorama, in WorldValueSite site) => new() {
        Intensity = Scalar(fallback: 1f, field: WorldValueFields.PanoramaIntensity, mirror: mirror, scalar: panorama.Intensity, site: site),
        Projection = ((panorama.Projection == WorldSkyProjection.Octahedral) ? SdfSkyProjection.Octahedral : SdfSkyProjection.Equirectangular),
        Screen = (panorama.Screen ?? -1),
    };
    private void WriteEnvironment(WorldStateMirror mirror, WorldRenderEnvironment? environment, SdfSky into) {
        var count = Math.Min(
            val1: (environment?.Softboxes?.Count ?? 0),
            val2: SdfSky.MaxSoftboxes
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
        into.Block.HorizonLow = Rgb(
            color: environment?.Horizon?.Low,
            fallback: Vector3.Zero,
            mirror: mirror
        );
        into.Block.HorizonHigh = Rgb(
            color: environment?.Horizon?.High,
            fallback: Vector3.Zero,
            mirror: mirror
        );
    }
}
/// <summary>A frame's resolved lights and sky (<see cref="WorldEnvironmentResolve.Resolve"/>), which the frame carries as
/// <see cref="SdfFrame.Lights"/> and <see cref="SdfFrame.Sky"/>.</summary>
/// <param name="Lights">The lights table.</param>
/// <param name="Sky">The sky.</param>
public readonly record struct WorldResolvedEnvironment(SdfLights Lights, SdfSky Sky);
