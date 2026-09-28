using System.Numerics;
using System.Text.Json;

using Puck.SignedDistance;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>render.lighting</c>/<c>render.sky</c> resolution (<see cref="WorldEnvironmentResolve"/>):
/// absence resolves to the pinned environment bit-exactly, an authored list is exactly the lights it names, every
/// authored field threads through untouched, a state-bound colour reads its cell live, and the validator refuses the
/// shapes the lane table cannot carry.</summary>
public sealed class WorldRenderLightingSkyLawTests {
    private static WorldRenderDefaults BaseDefaults() => WorldRenderDefaults.Absent;
    private static WorldDefinition ClockCycle(WorldRenderDefaults defaults) => (Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(
            Name: CellName.Parse(candidate: "clock"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 0L)
                )]
        )]) with {
        RenderRaw = defaults,
        TimelineRaw = new WorldTimelineSection([new WorldClock("clock", State: "clock")]),
    });
    private static WorldStateRow ColorsRow(string hex) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "sun"),
                Value: CellValue.Text(value: hex)
            )]
    );
    private static SdfEnvironment Resolve(WorldRenderDefaults defaults, IReadOnlyList<WorldStateRow>? state = null, int revision = 0, WorldEnvironmentResolve? track = null, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null) {
        var definition = (Fixtures.BuildDocument().WithWorldState(rows: (state ?? [])) with { RenderRaw = defaults, TimelineRaw = new WorldTimelineSection([new WorldClock("clock", State: "clock")]) });

        return (track ?? new WorldEnvironmentResolve()).Resolve(
            definition: definition,
            mirror: ClientFixtures.StateMirror(definition: definition),
            resolveLightAnchor: resolveLightAnchor,
            revision: revision
        );
    }
    private static WorldRenderSoftbox Softbox(float x = 1f, float y = 1f, float z = 1f, float width = 0.3f, float height = 0.4f) => new(
        Direction: new Vector3(
            x: x,
            y: y,
            z: z
        ),
        Size: new Vector2(
            x: width,
            y: height
        ),
        Color: new BindableColor(Raw: "#FFFFFF"),
        Weight: 0.5f
    );
    private static WorldRenderLighting SunAndSky(BindableColor? sunColor = null) => new(Lights: [
        new WorldRenderLight.Directional(
            Color: sunColor,
            Shadows: true
        ),
        new WorldRenderLight.Hemisphere(),
    ]);
    private static WorldRenderSky ThreeStopSky() => new(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
        new WorldRenderSkyStop(
                Elevation: -1f,
                Color: new BindableColor(Raw: "#0B0D14")
            ),
        new WorldRenderSkyStop(
                Elevation: 0f,
                Color: new BindableColor(Raw: "#1B2350")
            ),
        new WorldRenderSkyStop(
                Elevation: 1f,
                Color: new BindableColor(Raw: "#2B3360")
            ),
    ])]);
    private static bool TryValidateLocal(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );
    private static WorldRenderSky TwoStopSky(string top = "#1B2350", string bottom = "#0B0D14") => new(Layers: [
        new WorldRenderSkyLayer.Gradient(Stops: [
            new WorldRenderSkyStop(
                Elevation: -1f,
                Color: new BindableColor(Raw: bottom)
            ),
            new WorldRenderSkyStop(
                Elevation: 1f,
                Color: new BindableColor(Raw: top)
            ),
        ]),
    ]);

    [Fact]
    public void AbsentEnvironment_ResolvesToNoSoftboxesAndABlackHorizon() {
        var resolved = Resolve(defaults: BaseDefaults());

        Assert.Equal(
            expected: 0,
            actual: resolved.SoftboxCount
        );
        Assert.Equal(
            expected: Vector3.Zero,
            actual: resolved.HorizonLow
        );
        Assert.Equal(
            expected: Vector3.Zero,
            actual: resolved.HorizonHigh
        );
    }
    [Fact]
    public void AbsentLightingAndSky_ResolveToThePinnedEnvironmentBitExact() {
        var resolved = Resolve(defaults: BaseDefaults());
        var pinned = SdfEnvironment.Default();

        Assert.True(condition: pinned.Lanes.SequenceEqual(other: resolved.Lanes));
        Assert.Equal(
            expected: 2,
            actual: resolved.LightCount
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.ShadowLightIndex
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultSunDirection,
            actual: resolved.GetLight(index: 0).Direction
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultPenumbraSlope,
            actual: resolved.GetLight(index: 0).Param
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultAmbientBase,
            actual: resolved.GetLight(index: 1).Weight
        );
        Assert.False(condition: resolved.SkyEnabled);
        Assert.Equal(
            expected: SdfEnvironment.DefaultFogDensity,
            actual: resolved.FogDensity
        );
    }
    [Fact]
    public void AngularRadius_PastThePenumbraCeiling_RefusesByName_ControlInRangeClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.angular-radius",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(
                    AngularRadius: 0.5f,
                    Shadows: true
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(
                    AngularRadius: 0.1f,
                    Shadows: true
                )]),
                },
            }))
        );
    }
    [Fact]
    public void AuthoredEnvironment_SoftboxesAndHorizonThreadThroughAndUnsetFieldsTakeTheirDefaults() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Environment = new WorldRenderEnvironment(
            Softboxes: [
                    new WorldRenderSoftbox(
                    Direction: new Vector3(
                        x: 0f,
                        y: 1f,
                        z: 0f
                    ),
                    Size: new Vector2(
                        x: 0.32f,
                        y: 0.48f
                    ),
                    Color: new BindableColor(Raw: "#FFD9A6"),
                    Weight: 0.8f,
                    Blur: 0.1f
                ),
                    new WorldRenderSoftbox(
                    Direction: new Vector3(
                        x: 1f,
                        y: 0f,
                        z: 0f
                    ),
                    Size: new Vector2(
                        x: 0.19f,
                        y: 0.52f
                    )
                ),
                ],
            Horizon: new WorldRenderHorizon(
                Low: new BindableColor(Raw: "#0B0D14"),
                High: new BindableColor(Raw: "#1B2350")
            )
        ),
        });

        Assert.Equal(
            expected: 2,
            actual: resolved.SoftboxCount
        );

        var first = resolved.GetSoftbox(index: 0);
        var second = resolved.GetSoftbox(index: 1);

        Assert.Equal(
            expected: new Vector3(
                x: 0f,
                y: 1f,
                z: 0f
            ),
            actual: first.Direction
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0xFF / 255f),
                y: (0xD9 / 255f),
                z: (0xA6 / 255f)
            ),
            actual: first.Color
        );
        Assert.Equal(
            expected: 0.8f,
            actual: first.Weight
        );
        Assert.Equal(
            expected: new Vector2(
                x: 0.32f,
                y: 0.48f
            ),
            actual: first.Size
        );
        Assert.Equal(
            expected: 0.1f,
            actual: first.Blur
        );

        // Unset weight/blur/color take their engine defaults (white, weight 1, blur 0), not the previous softbox's.
        Assert.Equal(
            expected: Vector3.One,
            actual: second.Color
        );
        Assert.Equal(
            expected: 1f,
            actual: second.Weight
        );
        Assert.Equal(
            expected: 0f,
            actual: second.Blur
        );

        Assert.Equal(
            expected: new Vector3(
                x: (0x0B / 255f),
                y: (0x0D / 255f),
                z: (0x14 / 255f)
            ),
            actual: resolved.HorizonLow
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0x1B / 255f),
                y: (0x23 / 255f),
                z: (0x50 / 255f)
            ),
            actual: resolved.HorizonHigh
        );
    }
    [Fact]
    public void AuthoredLights_AreExactlyTheListAuthored_UnsetFieldsTakeTheirKindsDefaults() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: 0.3f,
                    y: 0.6f,
                    z: -0.5f
                ),
                Weight: 0.42f,
                Color: new BindableColor(Raw: "#FFD9A6"),
                AngularRadius: 0.2f,
                Shadows: true
            ),
                new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: -1f,
                    y: 0f,
                    z: 0f
                ),
                Weight: 0.3f
            ),
                new WorldRenderLight.Rim(
                Weight: 0.7f,
                Power: 5f,
                Color: new BindableColor(Raw: "#88AAFF")
            ),
            ]),
        });

        Assert.Equal(
            expected: 3,
            actual: resolved.LightCount
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.ShadowLightIndex
        );

        var key = resolved.GetLight(index: 0);
        var fill = resolved.GetLight(index: 1);
        var rim = resolved.GetLight(index: 2);

        Assert.InRange(Vector3.Distance(Vector3.Normalize(new Vector3(0.3f, 0.6f, -0.5f)), key.Direction), 0f, 1e-7f);
        Assert.Equal(
            expected: 0.42f,
            actual: key.Weight
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0xFF / 255f),
                y: (0xD9 / 255f),
                z: (0xA6 / 255f)
            ),
            actual: key.Color
        );
        Assert.Equal(
            expected: MathF.Tan(x: 0.2f),
            actual: key.Param
        );
        Assert.True(condition: key.Shadows);
        Assert.Equal(
            expected: SdfLightKind.Directional,
            actual: fill.Kind
        );
        Assert.False(condition: fill.Shadows);
        Assert.Equal(
            expected: Vector3.One,
            actual: fill.Color
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultPenumbraSlope,
            actual: fill.Param
        );
        Assert.Equal(
            expected: SdfLightKind.Rim,
            actual: rim.Kind
        );
        Assert.Equal(
            expected: 0.7f,
            actual: rim.Weight
        );
        Assert.Equal(
            expected: 5f,
            actual: rim.Param
        );
    }
    [Fact]
    public void AuthoredSky_EveryLayerThreadsThroughAndEnablesTheAuthoredGradient() {
        var sky = new WorldRenderSky(Layers: [
            new WorldRenderSkyLayer.Gradient(Stops: [
                new WorldRenderSkyStop(
                    Elevation: -1f,
                    Color: new BindableColor(Raw: "#0B0D14")
                ),
                new WorldRenderSkyStop(
                    Elevation: 0f,
                    Color: new BindableColor(Raw: "#E08F6B")
                ),
                new WorldRenderSkyStop(
                    Elevation: 1f,
                    Color: new BindableColor(Raw: "#1B2350")
                ),
            ]),
            new WorldRenderSkyLayer.Fog(Density: 0.02f),
            new WorldRenderSkyLayer.SunDisc(
                Radius: 0.045f,
                Intensity: 6f
            ),
            new WorldRenderSkyLayer.Stars(
                Density: 64f,
                Brightness: 0.85f,
                Seed: 1337u
            ),
        ]);
        var resolved = Resolve(defaults: BaseDefaults() with { Lighting = SunAndSky(), Sky = sky });

        Assert.True(condition: resolved.SkyEnabled);
        Assert.Equal(
            expected: 3,
            actual: resolved.SkyStopCount
        );
        Assert.Equal(
            expected: (new Vector3(
                x: (0xE0 / 255f),
                y: (0x8F / 255f),
                z: (0x6B / 255f)
            ), 0f),
            actual: resolved.GetSkyStop(index: 1)
        );
        Assert.Equal(
            expected: 0.02f,
            actual: resolved.FogDensity
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.SunDiscLightIndex
        );
        Assert.Equal(
            expected: 0.045f,
            actual: resolved.SunDiscRadians
        );
        Assert.Equal(
            expected: 6f,
            actual: resolved.SunDiscIntensity
        );
        Assert.Equal(
            expected: 64f,
            actual: resolved.StarDensity
        );
        Assert.Equal(
            expected: 0.85f,
            actual: resolved.StarBrightness
        );
        Assert.Equal(
            expected: 1337u,
            actual: resolved.StarSeed
        );
    }
    [Fact]
    public void AuthoredSky_FogAlone_LeavesThePinnedGradient() {
        var resolved = Resolve(defaults: BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: 0.05f)]) });

        Assert.False(condition: resolved.SkyEnabled);
        Assert.Equal(
            expected: 0.05f,
            actual: resolved.FogDensity
        );
    }
    [Fact]
    public void AuthoredSky_StarsAlone_DrawOverThePinnedGradient() {
        // A drawn layer without an authored gradient enables the sky, and the gradient it draws over is the pinned
        // two-stop one seeded into every environment — not a zeroed stop table.
        var resolved = Resolve(defaults: BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Brightness: 1f)]) });

        Assert.True(condition: resolved.SkyEnabled);
        Assert.Equal(
            expected: 1f,
            actual: resolved.StarBrightness
        );
        Assert.Equal(
            expected: 2,
            actual: resolved.SkyStopCount
        );
        Assert.Equal(
            expected: (SdfEnvironment.DefaultSkyGroundColor, -1f),
            actual: resolved.GetSkyStop(index: 0)
        );
        Assert.Equal(
            expected: (SdfEnvironment.DefaultSkyZenithColor, 1f),
            actual: resolved.GetSkyStop(index: 1)
        );
    }
    [Fact]
    public void CurvatureInkBand_Inverted_RefusesByName_ControlOrderedClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.curvature-ink-band",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
                Ink: 1f,
                InkLow: 12f,
                InkHigh: 4f
            )),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
                Ink: 1f,
                InkLow: 4f,
                InkHigh: 12f
            )),
                },
            }))
        );
    }
    [Fact]
    public void CurvatureInkBand_OneEndPastTheDefaultOther_RefusesByName_ControlBelowClean() {
        // inkHigh absent resolves to the engine default: a low equal to it gives smoothstep a zero-width band.
        Laws.RefusalWithControl(
            lawId: "render.lighting.curvature-ink-band-default",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
                Ink: 1f,
                InkLow: SdfEnvironment.DefaultCurvatureInkHigh
            )),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
                Ink: 1f,
                InkLow: (SdfEnvironment.DefaultCurvatureInkHigh - 1f)
            )),
                },
            }))
        );
    }
    private static WorldSectionKey SectionKey(double at, string fields) => new(at) {
        Values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!,
    };
    private static WorldRenderLighting NamedLighting() => new([
        new WorldRenderLight.Directional(Shadows: true) { Name = "sun" },
        new WorldRenderLight.Hemisphere() { Name = "fill" },
    ]);

    [Theory]
    [InlineData("{\"lights\":{\"sun\":{\"shadows\":false}}}", "shadows")]
    [InlineData("{\"lights\":{\"sun\":{\"$type\":\"rim\"}}}", "$type")]
    [InlineData("{\"lights\":{\"extra\":{\"weight\":1}}}", "extra")]
    public void Section_keys_cannot_change_light_topology_or_shadow_ownership(string fields, string refused) {
        var definition = ClockCycle(BaseDefaults() with { Lighting = NamedLighting() with {
            Keys = new WorldSectionKeys("clock", [SectionKey(0d, "{}"), SectionKey(0.5d, fields)]),
        } });
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains(refused, reason);
        var control = definition with { RenderRaw = definition.Render with { Lighting = NamedLighting() with {
            Keys = new WorldSectionKeys("clock", [SectionKey(0d, "{}"), SectionKey(0.5d, """{"lights":{"sun":{"weight":1}}}""")]),
        } } };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(control, out reason), reason);
    }

    [Theory]
    [InlineData(10f, false)]
    [InlineData(7f, true)]
    public void Section_ink_band_inherits_omitted_endpoints_through_the_wrap(float low, bool accepted) {
        var definition = ClockCycle(BaseDefaults() with { Lighting = new WorldRenderLighting(
            Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: 6f, InkHigh: 16f)) {
            Keys = new WorldSectionKeys("clock", [SectionKey(0d, """{"curvature":{"inkHigh":8}}"""),
                SectionKey(0.5d, FormattableString.Invariant($"{{\"curvature\":{{\"inkLow\":{low}}}}}"))]),
        } });
        Assert.Equal(accepted, WorldDefinitionValidator.TryValidateLocally(definition, out _));
    }

    [Fact]
    public void A_named_light_blends_direction_on_the_arc_and_weight_linearly() {
        var direction = new BindableDirection(new WorldKeys<BindableDirection>("clock", [new(0d, Vector3.UnitX), new(0.5d, Vector3.UnitY)]));
        var weight = new BindableScalar(new WorldKeys<BindableScalar>("clock", [new(0d, 0f), new(0.5d, 1f)]));
        var defaults = BaseDefaults() with { Lighting = new([new WorldRenderLight.Directional(direction, Weight: weight, Shadows: true) { Name = "sun" }]) };
        var state = new WorldStateRow(CellName.Parse("clock"), CellKind.Fixed, Cells: [
            new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(Puck.Maths.FixedQ4816.FromDouble(0.25d).Value)),
        ]);
        var key = Resolve(defaults, state: [state]).GetLight(0);
        Assert.Equal(0.5f, key.Weight, precision: 5);
        Assert.Equal(1f, key.Direction.Length(), precision: 4);
        Assert.Equal(MathF.Sqrt(0.5f), key.Direction.X, precision: 4);
        Assert.Equal(MathF.Sqrt(0.5f), key.Direction.Y, precision: 4);
        Assert.True(key.Shadows);
    }

    [Fact]
    public void Curvature_only_lighting_keeps_the_pinned_lights_and_a_disc_can_name_the_pinned_sun() {
        var definition = ClockCycle(BaseDefaults() with {
            Lighting = new(Curvature: new(Ink: 1f)),
            Sky = new([new WorldRenderSkyLayer.SunDisc(Light: 0, Radius: 0.05f, Intensity: 1f)]),
        });
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var resolved = Resolve(definition.Render);
        Assert.Equal(2, resolved.LightCount);
        Assert.Equal(SdfLightKind.Directional, resolved.GetLight(0).Kind);
        Assert.Equal(0, resolved.SunDiscLightIndex);
    }

    [Fact]
    public void Section_keys_require_an_authored_named_light_instead_of_implicitly_addressing_a_pinned_slot() {
        var definition = ClockCycle(BaseDefaults() with { Lighting = new WorldRenderLighting {
            Keys = new WorldSectionKeys("clock", [SectionKey(0d, "{}"), SectionKey(0.5d, """{"lights":{"sun":{"weight":1}}}""")]),
        } });
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains("lights", reason);
    }

    [Fact]
    public void Named_gradient_stops_carry_omitted_elevations_through_the_wrap() {
        var definition = ClockCycle(BaseDefaults() with { Sky = new WorldRenderSky([
            new WorldRenderSkyLayer.Gradient([
                new WorldRenderSkyStop(-1f, new BindableColor("#000000")) { Name = "lower" },
                new WorldRenderSkyStop(0f, new BindableColor("#777777")) { Name = "middle" },
                new WorldRenderSkyStop(1f, new BindableColor("#FFFFFF")) { Name = "upper" },
            ]) { Name = "gradient" },
        ]) {
            Keys = new WorldSectionKeys("clock", [
                SectionKey(0d, """{"layers":{"gradient":{"stops":{"lower":{"elevation":0.9}}}}}"""),
                SectionKey(0.5d, """{"layers":{"gradient":{"stops":{"middle":{"elevation":0.95}}}}}"""),
            ]),
        } });
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var resolved = new WorldEnvironmentResolve().Resolve(definition, 0, ClientFixtures.StateMirror(definition));
        Assert.Equal(0.9f, resolved.GetSkyStop(0).Elevation);
        Assert.Equal(0.95f, resolved.GetSkyStop(1).Elevation);
        Assert.Equal(1f, resolved.GetSkyStop(2).Elevation);
    }
    [Fact]
    public void DirectionalDirection_Zero_RefusesByName_ControlNonzeroClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.direction",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Direction: Vector3.Zero)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Direction: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ))]),
                },
            }))
        );
    }
    [Fact]
    public void EightAuthoredLights_TheEighthReachesThePackedLanes() {
        var lights = new WorldRenderLight[8];

        for (var index = 0; (index < 8); index++) {
            lights[index] = new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: 0f,
                    y: 0f,
                    z: 1f
                ),
                Weight: (0.1f * (index + 1))
            );
        }

        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: lights),
        });

        Assert.Equal(
            expected: 8,
            actual: resolved.LightCount
        );

        var eighth = resolved.GetLight(index: 7);

        Assert.Equal(
            expected: 0.8f,
            actual: eighth.Weight,
            precision: 5
        );
    }
    [Fact]
    public void Environment_MoreThanFourSoftboxes_RefusesByName_ControlFourClean() {
        Laws.RefusalWithControl(
            lawId: "render.environment.softbox-count",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox(), Softbox(), Softbox(), Softbox(), Softbox()]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox(), Softbox(), Softbox(), Softbox()]) },
            }))
        );
    }
    [Fact]
    public void Environment_NonPositiveSoftboxSize_RefusesByName_ControlPositiveClean() {
        Laws.RefusalWithControl(
            lawId: "render.environment.softbox-size",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox(width: 0f)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox(width: 0.1f)]) },
            }))
        );
    }
    [Fact]
    public void Environment_ZeroSoftboxDirection_RefusesByName_ControlNonzeroClean() {
        Laws.RefusalWithControl(
            lawId: "render.environment.softbox-direction",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Environment = new WorldRenderEnvironment(Softboxes: [Softbox(
                    x: 0f,
                    y: 0f,
                    z: 0f
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox()]) },
            }))
        );
    }
    [Fact]
    public void FiveAuthoredLights_AllFiveReachThePackedLanes_IncludingPastTheFourthSlot() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                Weight: 0.1f,
                Shadows: true
            ),
                new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: 1f,
                    y: 0f,
                    z: 0f
                ),
                Weight: 0.2f
            ),
                new WorldRenderLight.Hemisphere(Base: 0.3f),
                new WorldRenderLight.Rim(
                Weight: 0.4f,
                Power: 2f
            ),
                new WorldRenderLight.Directional(
                Direction: new Vector3(
                    x: 0f,
                    y: 0f,
                    z: 1f
                ),
                Weight: 0.9f
            ),
            ]),
        });

        Assert.Equal(
            expected: 5,
            actual: resolved.LightCount
        );

        var fifth = resolved.GetLight(index: 4);

        Assert.Equal(
            expected: SdfLightKind.Directional,
            actual: fifth.Kind
        );
        Assert.Equal(
            expected: new Vector3(
                x: 0f,
                y: 0f,
                z: 1f
            ),
            actual: fifth.Direction
        );
        Assert.Equal(
            expected: 0.9f,
            actual: fifth.Weight
        );

        // The packed lane table (what the engine uploads, row for row) must carry the fifth light's direction and
        // weight too — not only what GetLight's own row math reads back.
        var lanes = resolved.Lanes;
        var row = (SdfEnvironment.LightsRow + (4 * SdfEnvironment.RowsPerLight));

        Assert.Equal(
            expected: 0f,
            actual: lanes[((row * 4) + 0)]
        );
        Assert.Equal(
            expected: 0f,
            actual: lanes[((row * 4) + 1)]
        );
        Assert.Equal(
            expected: 1f,
            actual: lanes[((row * 4) + 2)]
        );
        Assert.Equal(
            expected: 0.9f,
            actual: lanes[((row * 4) + 3)]
        );
    }
    [Fact]
    public void LightColor_MalformedHex_RefusesByName_ControlWellFormedClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.light-color",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = SunAndSky(sunColor: new BindableColor(Raw: "orange")) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = SunAndSky(sunColor: new BindableColor(Raw: "#FFD9A6")) },
            }))
        );
    }
    [Fact]
    public void OccluderRefusesInvalidStrengthAndRadius() {
        var valid = BaseDefaults() with {
            Lighting = new(Lights: [new WorldRenderLight.Occluder(
                Radius: 2f,
                Weight: 0.5f
            )]),
        };

        Assert.True(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with { RenderRaw = valid }));
        Assert.False(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with {
            RenderRaw = valid with { Lighting = new(Lights: [new WorldRenderLight.Occluder(Radius: 0f)]) },
        }));
        Assert.False(condition: TryValidateLocal(definition: Fixtures.BuildDocument() with {
            RenderRaw = valid with { Lighting = new(Lights: [new WorldRenderLight.Occluder(Weight: 1.1f)]) },
        }));
    }
    [Fact]
    public void OccluderUsesTheGeneralLightTableAndMissingAnchorsDisableIt() {
        var defaults = BaseDefaults() with {
            Lighting = new(Lights: [
            new WorldRenderLight.Occluder(
                Position: new Vector3(
                    x: 2f,
                    y: 3f,
                    z: 4f
                ),
                Radius: 2f,
                Anchor: new WorldAnchor.Entity(Index: 0),
                Weight: 0.6f
            )
        ]),
        };
        var track = new WorldEnvironmentResolve();
        var missing = Resolve(
            defaults,
            track: track
        ).GetLight(index: 0);

        Assert.Equal(
            0f,
            missing.Weight
        );
        var live = Resolve(
            defaults,
            track: track,
            resolveLightAnchor: _ => new SdfAnchor(
                new Vector3(
                    x: 7f,
                    y: 0f,
                    z: 0f
                ),
                Quaternion.Identity
            )
        ).GetLight(index: 0);

        Assert.Equal(
            0.6f,
            live.Weight
        );
        Assert.Equal(
            -1,
            live.DynamicSlot
        );
        Assert.Equal(
            SdfLightKind.Occluder,
            live.Kind
        );
        Assert.Equal(
            new Vector3(
                x: 9f,
                y: 3f,
                z: 4f
            ),
            live.Direction
        );
    }
    [Fact]
    public void PointLightAnchorComposesItsOffsetIntoTheResolvedPose() {
        var resolved = Resolve(
            defaults: BaseDefaults() with {
                Lighting = new WorldRenderLighting(Lights: [
                    new WorldRenderLight.Point(
                    Position: new Vector3(
                        x: 0f,
                        y: 2f,
                        z: 4f
                    ),
                    Radius: 1.5f,
                    Weight: 1.2f,
                    Anchor: new WorldAnchor.Placement(
                        PlacementId: "nozzle",
                        ShapeId: null
                    )
                ),
                    new WorldRenderLight.Point(Position: new Vector3(
                    x: 1f,
                    y: 1f,
                    z: 1f
                )),
                ]),
            },
            resolveLightAnchor: static anchor => ((anchor is WorldAnchor.Placement { PlacementId: "nozzle" })
            ? new SdfAnchor(
                    new Vector3(
                        x: 7f,
                        y: 0f,
                        z: 0f
                    ),
                    Quaternion.Identity
                )
            : null)
        );

        Assert.Equal(
            expected: 2,
            actual: resolved.LightCount
        );
        Assert.Equal(
            expected: SdfLightKind.Point,
            actual: resolved.GetLight(index: 0).Kind
        );
        Assert.Equal(
            expected: -1,
            actual: resolved.GetLight(index: 0).DynamicSlot
        );
        Assert.Equal(
            new Vector3(
                x: 7f,
                y: 2f,
                z: 4f
            ),
            resolved.GetLight(index: 0).Direction
        );
        Assert.Equal(
            expected: SdfLightKind.Point,
            actual: resolved.GetLight(index: 1).Kind
        );
        Assert.Equal(
            expected: -1,
            actual: resolved.GetLight(index: 1).DynamicSlot
        );
    }
    [Fact]
    public void PointLightAnchor_SeatRelative_RefusesByName_ControlStaticClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.point-light-anchor-kind",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Point(Anchor: new WorldAnchor.Seat())]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildGradientUpDocument(gradientUp: false) with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Point(Anchor: new WorldAnchor.Placement(
                    PlacementId: "ball",
                    ShapeId: null
                ))]),
                },
            }))
        );
    }
    [Fact]
    public void A_point_light_with_keyed_position_is_admitted() {
        var position = new BindableVector3(new WorldKeys<BindableVector3>("clock", [
            new(0d, new Vector3(10f, 2f, 3f)), new(0.5d, new Vector3(20f, 4f, 6f)),
        ]));
        Assert.True(TryValidateLocal(ClockCycle(BaseDefaults() with { Lighting = new([new WorldRenderLight.Point(Position: position)]) })));
    }    [Fact]
    public void PointLight_AnchorWithNoResolver_ResolvesToMinusOne() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Point(Anchor: new WorldAnchor.Placement(
                PlacementId: "nozzle",
                ShapeId: null
            )),
            ]),
        });

        Assert.Equal(
            expected: -1,
            actual: resolved.GetLight(index: 0).DynamicSlot
        );
    }
    [InlineData(SdfLightKind.Point)]
    [InlineData(SdfLightKind.Occluder)]
    [Theory]
    public void PositionKindsInterpolateLinearlyWithoutNormalization(SdfLightKind kind) {
        var position = new BindableVector3(new WorldKeys<BindableVector3>("clock", [new(0d, new Vector3(10f, 20f, 30f)), new(0.5d, new Vector3(30f, 40f, 50f))]));
        var radius = new BindableScalar(new WorldKeys<BindableScalar>("clock", [new(0d, 1f), new(0.5d, 3f)]));
        WorldRenderLight light = kind == SdfLightKind.Point ? new WorldRenderLight.Point(Position: position, Radius: radius)
            : new WorldRenderLight.Occluder(Position: position, Radius: radius);
        var state = new WorldStateRow(CellName.Parse("clock"), CellKind.Fixed, Cells: [
            new StateCell(WorldStateRow.SlotKey, CellValue.Fixed(Puck.Maths.FixedQ4816.FromDouble(0.25d).Value)),
        ]);
        var result = Resolve(BaseDefaults() with { Lighting = new([light]) }, state: [state]);
        Assert.Equal(new Vector3(20f, 30f, 40f), result.GetLight(0).Direction);
        Assert.Equal(2f, result.GetLight(0).Param);
    }    [Fact]
    public void SkyFogDensity_Negative_RefusesByName_ControlNonNegativeClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.fog-density-negative",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: -0.01f)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: 0.01f)]) },
            }))
        );
    }
    [Fact]
    public void SkyLayerKind_Repeated_RefusesByName_ControlOnceClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.layer-once",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: 0.01f), new WorldRenderSkyLayer.Fog(Density: 0.02f)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: 0.01f)]) },
            }))
        );
    }
    [Fact]
    public void SkyStarsDensity_NonPositive_RefusesByName_ControlPositiveClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.stars-density-positive",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(
                    Density: 0f,
                    Brightness: 0.5f,
                    Seed: 1u
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(
                    Density: 32f,
                    Brightness: 0.5f,
                    Seed: 1u
                )]),
                },
            }))
        );
    }
    [Fact]
    public void SkyStopColor_MalformedHex_RefusesByName_ControlWellFormedClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.stop-color",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = TwoStopSky(top: "not-a-color") },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = TwoStopSky() },
            }))
        );
    }
    [Fact]
    public void SkyStops_OutOfOrder_RefuseByName_ControlAscendingClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.stops-ascending",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
                        new WorldRenderSkyStop(
                        Elevation: 1f,
                        Color: new BindableColor(Raw: "#1B2350")
                    ),
                        new WorldRenderSkyStop(
                        Elevation: -1f,
                        Color: new BindableColor(Raw: "#0B0D14")
                    ),
                    ])]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = TwoStopSky() },
            }))
        );
    }
    [Fact]
    public void SkySunDiscLight_NotADirectional_RefusesByName_ControlDirectionalClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.sun-disc-light",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = SunAndSky(),
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(
                    Intensity: 1f,
                    Light: 1,
                    Radius: 0.05f
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = SunAndSky(),
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(
                    Intensity: 1f,
                    Light: 0,
                    Radius: 0.05f
                )]),
                },
            }))
        );
    }
    [Fact]
    public void SkySunDiscRadius_OutOfRange_RefusesByName_ControlInRangeClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.sun-disc-radius-range",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(
                    Radius: 0f,
                    Intensity: 1f
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(
                    Radius: 0.05f,
                    Intensity: 1f
                )]),
                },
            }))
        );
    }
    [Fact]
    public void Stylization_Absent_ResolvesToThePinnedDefaults() {
        var resolved = Resolve(defaults: BaseDefaults());

        Assert.Equal(
            expected: 0f,
            actual: resolved.CurvatureCavity
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.CurvatureRim
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.CurvatureInk
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultCurvatureInkLow,
            actual: resolved.CurvatureInkLow
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultCurvatureInkHigh,
            actual: resolved.CurvatureInkHigh
        );
        Assert.Equal(
            expected: SdfEnvironment.DefaultCurvatureInkColor,
            actual: resolved.CurvatureInkColor
        );
    }
    [Fact]
    public void Stylization_Authored_ThreadsThroughUntouched() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
            Cavity: 0.6f,
            Rim: 0.2f,
            Ink: 0.9f,
            InkLow: 3f,
            InkHigh: 11f,
            InkColor: new BindableColor(Raw: "#101018")
        )),
        });

        Assert.Equal(
            expected: 0.6f,
            actual: resolved.CurvatureCavity
        );
        Assert.Equal(
            expected: 0.9f,
            actual: resolved.CurvatureInk
        );
        Assert.Equal(
            expected: 11f,
            actual: resolved.CurvatureInkHigh
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0x10 / 255f),
                y: (0x10 / 255f),
                z: (0x18 / 255f)
            ),
            actual: resolved.CurvatureInkColor
        );
        // Curvature alone leaves the pinned lights in place.
        Assert.Equal(
            expected: 2,
            actual: resolved.LightCount
        );
    }
    [Fact]
    public void SunColor_BoundToStateTextCell_ResolvesToTheCell_AndFollowsItsRowWithoutARevisionMove() {
        var track = new WorldEnvironmentResolve();
        var lighting = SunAndSky(sunColor: new BindableColor(Raw: "state.colors.sun"));
        var definition = (Fixtures.BuildDocument().WithWorldState(rows: [ColorsRow(hex: "#FFD9A6")]) with { RenderRaw = BaseDefaults() with { Lighting = lighting } });
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => definition));

        mirror.Install(
            engineTick: 0UL,
            tick: 0UL
        );

        var first = track.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        ).GetLight(index: 0).Color;

        // A value-only delivery: the definition revision holds, the mirror refreshes the moved row.
        definition = (Fixtures.BuildDocument().WithWorldState(rows: [ColorsRow(hex: "#4C5C8C")]) with { RenderRaw = BaseDefaults() with { Lighting = lighting } });
        mirror.Refresh(stamp: new WorldStateStamp(
            EngineTick: 0UL,
            Everything: true,
            MovedRows: default,
            Tick: 1UL
        ));

        var second = track.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        ).GetLight(index: 0).Color;
        var held = track.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        ).GetLight(index: 0).Color;

        Assert.Equal(
            expected: new Vector3(
                x: (0xFF / 255f),
                y: (0xD9 / 255f),
                z: (0xA6 / 255f)
            ),
            actual: first
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0x4C / 255f),
                y: (0x5C / 255f),
                z: (0x8C / 255f)
            ),
            actual: second
        );
        Assert.Equal(
            actual: held,
            expected: second
        );
    }
    [Fact]
    public void SunColor_BoundToUndeclaredCell_RefusesByName_ControlDeclaredTextCellClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.light-color-binding",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = SunAndSky(sunColor: new BindableColor(Raw: "state.colors.moon")) },
                StateRaw = new WorldStateSection(World: [ColorsRow(hex: "#FFD9A6")]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = SunAndSky(sunColor: new BindableColor(Raw: "state.colors.sun")) },
                StateRaw = new WorldStateSection(World: [ColorsRow(hex: "#FFD9A6")]),
            }))
        );
    }
    [Fact]
    public void TwoShadowingLights_RefuseByName_ControlOneClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.one-shadow-light",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [
                        new WorldRenderLight.Directional(Shadows: true),
                        new WorldRenderLight.Directional(
                    Direction: new Vector3(
                        x: -1f,
                        y: 1f,
                        z: 0f
                    ),
                    Shadows: true
                ),
                    ]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [
                        new WorldRenderLight.Directional(Shadows: true),
                        new WorldRenderLight.Directional(Direction: new Vector3(
                    x: -1f,
                    y: 1f,
                    z: 0f
                )),
                    ]),
                },
            }))
        );
    }
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void Weights_NonFiniteOrNegative_RefuseByName(float weight) {
        Assert.False(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: weight)]) },
        })));
        Assert.False(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Rim(Weight: weight)]) },
        })));
        Assert.False(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Cavity: weight)) },
        })));
        // The control: the same three surfaces with an admissible weight validate.
        Assert.True(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with {
                Lighting = new WorldRenderLighting(
            Lights: [new WorldRenderLight.Directional(Weight: 0.5f), new WorldRenderLight.Rim(Weight: 0.5f)],
            Curvature: new WorldRenderCurvature(Cavity: 0.5f)
        ),
            },
        })));
    }
}
