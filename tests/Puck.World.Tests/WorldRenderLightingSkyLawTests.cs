using System.Numerics;

using Puck.SignedDistance;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.World.Protocol;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>render.lighting</c>/<c>render.sky</c> resolution (<see cref="WorldEnvironmentResolve"/>):
/// absence resolves to the pinned lights and sky bit-exactly, an authored list is exactly the lights it names, every
/// authored field threads through untouched, a state-bound colour reads its cell live, and the validator refuses the
/// shapes the lights table and the sky's tables cannot carry.</summary>
public sealed partial class WorldRenderLightingSkyLawTests {
    private static WorldRenderDefaults BaseDefaults() => WorldRenderDefaults.Absent with { ShadowLights = 1 };
    private static WorldStateRow ColorsRow(string hex) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Cells: [new StateCell(
                Key: CellName.Parse(candidate: "sun"),
                Value: CellValue.Text(value: hex)
            )]
    );
    private static WorldResolvedEnvironment Resolve(WorldRenderDefaults defaults, IReadOnlyList<WorldStateRow>? state = null, int revision = 0, WorldEnvironmentResolve? track = null, Func<WorldAnchor, SdfAnchor?>? resolveLightAnchor = null) {
        var definition = (Fixtures.BuildDocument().WithWorldState(rows: (state ?? [])) with { RenderRaw = defaults });

        return (track ?? new WorldEnvironmentResolve(domains: new WorldValueDomainGuard())).Resolve(
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
            Shadow: WorldShadowMode.Always, Name: "sun"
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
            actual: resolved.Sky.SoftboxCount
        );
        Assert.Equal(
            expected: Vector3.Zero,
            actual: resolved.Sky.Block.HorizonLow
        );
        Assert.Equal(
            expected: Vector3.Zero,
            actual: resolved.Sky.Block.HorizonHigh
        );
    }
    [Fact]
    public void AbsentLightingAndSky_ResolveToThePinnedEnvironmentBitExact() {
        var resolved = Resolve(defaults: BaseDefaults());
        var pinned = SdfLights.Default();
        var unauthored = new SdfSky();

        Assert.True(condition: pinned.Records.SequenceEqual(other: resolved.Lights.Records));
        Assert.Equal(expected: unauthored.Block, actual: resolved.Sky.Block);
        Assert.True(condition: unauthored.Layers.SequenceEqual(other: resolved.Sky.Layers));
        Assert.Equal(
            expected: 2,
            actual: resolved.Lights.Count
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.Lights.ShadowSlots[0]
        );
        Assert.Equal(
            expected: SdfLights.DefaultSunDirection,
            actual: resolved.Lights[0].Direction
        );
        Assert.Equal(
            expected: SdfLights.DefaultPenumbraSlope,
            actual: resolved.Lights[0].Param
        );
        Assert.Equal(
            expected: SdfLights.DefaultAmbientBase,
            actual: resolved.Lights[1].Weight
        );
        Assert.Equal(expected: 2u, actual: resolved.Sky.First<SdfSkyGradient>().Count);
        Assert.Equal(
            expected: SdfSky.DefaultFogDensity,
            actual: resolved.Sky.Atmosphere.FogDensity
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
                    Shadow: WorldShadowMode.Always, Name: "sun"
                )]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(
                    AngularRadius: 0.1f,
                    Shadow: WorldShadowMode.Always, Name: "sun"
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
            actual: resolved.Sky.SoftboxCount
        );

        var first = resolved.Sky.Softboxes[0];
        var second = resolved.Sky.Softboxes[1];

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
            actual: first.Weight,
            expected: 0.8f
        );
        Assert.Equal(
            expected: new Vector2(
                x: 0.32f,
                y: 0.48f
            ),
            actual: first.Size
        );
        Assert.Equal(
            actual: first.Blur,
            expected: 0.1f
        );

        // Unset weight/blur/color take their engine defaults (white, weight 1, blur 0), not the previous softbox's.
        Assert.Equal(
            expected: Vector3.One,
            actual: second.Color
        );
        Assert.Equal(
            actual: second.Weight,
            expected: 1f
        );
        Assert.Equal(
            actual: second.Blur,
            expected: 0f
        );

        Assert.Equal(
            expected: new Vector3(
                x: (0x0B / 255f),
                y: (0x0D / 255f),
                z: (0x14 / 255f)
            ),
            actual: resolved.Sky.Block.HorizonLow
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0x1B / 255f),
                y: (0x23 / 255f),
                z: (0x50 / 255f)
            ),
            actual: resolved.Sky.Block.HorizonHigh
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
                Shadow: WorldShadowMode.Always, Name: "sun"
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
            actual: resolved.Lights.Count
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.Lights.ShadowSlots[0]
        );

        var key = resolved.Lights[0];
        var fill = resolved.Lights[1];
        var rim = resolved.Lights[2];

        Assert.Equal(
            expected: new Vector3(
                x: 0.3f,
                y: 0.6f,
                z: -0.5f
            ),
            actual: key.Direction
        );
        Assert.Equal(
            actual: key.Weight,
            expected: 0.42f
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
        Assert.True(condition: key.CastsShadow);
        Assert.Equal(
            actual: fill.Kind,
            expected: SdfLightKind.Directional
        );
        Assert.False(condition: fill.CastsShadow);
        Assert.Equal(
            expected: Vector3.One,
            actual: fill.Color
        );
        Assert.Equal(
            actual: fill.Param,
            expected: SdfLights.DefaultPenumbraSlope
        );
        Assert.Equal(
            actual: rim.Kind,
            expected: SdfLightKind.Rim
        );
        Assert.Equal(
            actual: rim.Weight,
            expected: 0.7f
        );
        Assert.Equal(
            actual: rim.Param,
            expected: 5f
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
        var resolved = Resolve(defaults: BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.02f)), Lighting = SunAndSky(), Sky = sky });

        Assert.Equal(
            expected: 3u,
            actual: resolved.Sky.First<SdfSkyGradient>().Count
        );
        Assert.Equal(
            expected: (Color: new Vector3(
                x: (0xE0 / 255f),
                y: (0x8F / 255f),
                z: (0x6B / 255f)
            ), Elevation: 0f),
            actual: resolved.Sky.First<SdfSkyGradient>().Stop(index: 1)
        );
        Assert.Equal(
            expected: 0.02f,
            actual: resolved.Sky.Atmosphere.FogDensity
        );
        Assert.Equal(
            expected: 0,
            actual: resolved.Sky.First<SdfSkyDisc>().Light
        );
        Assert.Equal(
            expected: 0.045f,
            actual: resolved.Sky.First<SdfSkyDisc>().Radius
        );
        Assert.Equal(
            expected: 6f,
            actual: resolved.Sky.First<SdfSkyDisc>().Intensity
        );
        Assert.Equal(
            expected: 64f,
            actual: resolved.Sky.First<SdfSkyStars>().Density
        );
        Assert.Equal(
            expected: 0.85f,
            actual: resolved.Sky.First<SdfSkyStars>().Brightness
        );
        Assert.Equal(
            expected: 1337u,
            actual: resolved.Sky.First<SdfSkyStars>().Seed
        );
    }
    [Fact]
    public void AuthoredAtmosphere_FogAlone_KeepsTheDefaultGradient() {
        var resolved = Resolve(defaults: BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.05f)) });

        Assert.Equal(expected: 2u, actual: resolved.Sky.First<SdfSkyGradient>().Count);
        Assert.Equal(
            expected: 0.05f,
            actual: resolved.Sky.Atmosphere.FogDensity
        );
    }
    [Fact]
    public void AuthoredSky_StarsAlone_DrawOverThePinnedGradient() {
        // A drawn layer without an authored gradient draws over the default look's two-stop gradient, seeded into every
        // environment, not a zeroed stop table.
        var resolved = Resolve(defaults: BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Brightness: 1f)]) });

        Assert.Equal(expected: 2u, actual: resolved.Sky.First<SdfSkyGradient>().Count);
        Assert.Equal(
            expected: 1f,
            actual: resolved.Sky.First<SdfSkyStars>().Brightness
        );
        Assert.Equal(
            expected: 1,
            actual: resolved.Sky.IndexOf(kind: SdfSkyLayerKind.Stars)
        );
        Assert.Equal(
            expected: (Color: SdfSky.DefaultGroundColor, Elevation: -1f),
            actual: resolved.Sky.First<SdfSkyGradient>().Stop(index: 0)
        );
        Assert.Equal(
            expected: (Color: SdfSky.DefaultZenithColor, Elevation: 1f),
            actual: resolved.Sky.First<SdfSkyGradient>().Stop(index: 1)
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
                InkLow: SdfCurvature.DefaultInkHigh
            )),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(
                Ink: 1f,
                InkLow: (SdfCurvature.DefaultInkHigh - 1f)
            )),
                },
            }))
        );
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
            actual: resolved.Lights.Count
        );

        var eighth = resolved.Lights[7];

        Assert.Equal(
            actual: eighth.Weight,
            expected: 0.8f,
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
                Shadow: WorldShadowMode.Always, Name: "sun"
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
            actual: resolved.Lights.Count
        );

        var fifth = resolved.Lights[4];

        Assert.Equal(
            actual: fifth.Kind,
            expected: SdfLightKind.Directional
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
            actual: fifth.Weight,
            expected: 0.9f
        );

        // The packed lights table (what the engine uploads, record for record) must carry the fifth light's direction
        // and weight too.
        var records = new SdfLight[SdfLights.MaxLights];

        resolved.Lights.Pack(records: records);
        Assert.Equal(
            expected: new Vector3(
                x: 0f,
                y: 0f,
                z: 1f
            ),
            actual: records[4].Direction
        );
        Assert.Equal(
            expected: 0.9f,
            actual: records[4].Weight
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
        var track = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
        var missing = Resolve(
            defaults,
            track: track
        ).Lights[0];

        Assert.Equal(
            actual: missing.Weight,
            expected: 0f
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
        ).Lights[0];

        Assert.Equal(
            actual: live.Weight,
            expected: 0.6f
        );
        Assert.Equal(
            actual: live.DynamicSlot,
            expected: SdfProgram.NoDynamicTransformSlot
        );
        Assert.Equal(
            actual: live.Kind,
            expected: SdfLightKind.Occluder
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
            actual: resolved.Lights.Count
        );
        Assert.Equal(
            expected: SdfLightKind.Point,
            actual: resolved.Lights[0].Kind
        );
        Assert.Equal(
            expected: SdfProgram.NoDynamicTransformSlot,
            actual: resolved.Lights[0].DynamicSlot
        );
        Assert.Equal(
            new Vector3(
                x: 7f,
                y: 2f,
                z: 4f
            ),
            resolved.Lights[0].Direction
        );
        Assert.Equal(
            expected: SdfLightKind.Point,
            actual: resolved.Lights[1].Kind
        );
        Assert.Equal(
            expected: SdfProgram.NoDynamicTransformSlot,
            actual: resolved.Lights[1].DynamicSlot
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
            expected: SdfProgram.NoDynamicTransformSlot,
            actual: resolved.Lights[0].DynamicSlot
        );
    }
    [Fact]
    public void AtmosphereFogDensity_Negative_RefusesByName_ControlNonNegativeClean() {
        Laws.RefusalWithControl(
            lawId: "render.atmosphere.fog-density-negative",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: -0.01f)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Density: 0.01f)) },
            }))
        );
    }
    [Fact]
    public void AtmosphereHazeAmount_TakingAllTheLight_RefusesByName_ControlBelowCeilingClean() {
        Laws.RefusalWithControl(
            lawId: "render.atmosphere.haze-amount-ceiling",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Haze: new WorldRenderHaze(Amount: 1f)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Haze: new WorldRenderHaze(Amount: SdfAtmosphere.MaxHazeAmount)) },
            }))
        );
    }
    [Fact]
    public void AtmosphereHeightFalloff_ThinnerThanTheFloor_RefusesByName_ControlAtTheFloorClean() {
        Laws.RefusalWithControl(
            lawId: "render.atmosphere.falloff-floor",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Height: new WorldRenderAirHeight(Falloff: 0f))) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Fog: new WorldRenderFog(Height: new WorldRenderAirHeight(Falloff: SdfAtmosphere.MinFalloff))) },
            }))
        );
    }
    [Fact]
    public void AtmosphereMediumColor_OutsideItsGrammar_RefusesByName_ControlHexClean() {
        Laws.RefusalWithControl(
            lawId: "render.atmosphere.medium-color",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Medium: new WorldRenderMedium(Color: new BindableColor(Raw: "teal"))) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Medium: new WorldRenderMedium(Color: new BindableColor(Raw: "#1F6F78"))) },
            }))
        );
    }
    [Fact]
    public void AnAuthoredAtmosphereIsExactlyTheKindsItStates() {
        // An absent section renders the default look's fog; an authored one carries no kind it leaves out.
        var unauthored = Resolve(defaults: BaseDefaults());
        var hazeOnly = Resolve(defaults: BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Haze: new WorldRenderHaze(Amount: 0.3f)) });
        var water = Resolve(defaults: BaseDefaults() with { Atmosphere = new WorldRenderAtmosphere(Medium: new WorldRenderMedium(Surface: -2f)) });

        Assert.Equal(expected: SdfAtmosphere.Default, actual: unauthored.Sky.Atmosphere);
        Assert.Equal(expected: 0f, actual: hazeOnly.Sky.Atmosphere.FogDensity);
        Assert.Equal(expected: 0.3f, actual: hazeOnly.Sky.Atmosphere.HazeAmount);
        Assert.Equal(expected: SdfAtmosphere.DefaultHazeAnisotropy, actual: hazeOnly.Sky.Atmosphere.HazeAnisotropy);
        Assert.Equal(expected: 0f, actual: water.Sky.Atmosphere.FogDensity);
        Assert.Equal(expected: -2f, actual: water.Sky.Atmosphere.MediumSurface);
        Assert.Equal(expected: SdfAtmosphere.DefaultMediumExtinction, actual: water.Sky.Atmosphere.MediumExtinction);
        Assert.Equal(expected: SdfAtmosphere.DefaultMediumColor, actual: water.Sky.Atmosphere.MediumColor);
    }
    // The stack is open: a kind may appear as often as authored, each layer counting under its own label.
    [Fact]
    public void SkyLayerKind_Repeated_IsAdmittedAndLabelledApart() {
        WorldRenderSkyLayer[] layers = [
            new WorldRenderSkyLayer.Clouds(Coverage: 0.3f),
            new WorldRenderSkyLayer.Clouds(Coverage: 0.6f),
            new WorldRenderSkyLayer.Clouds(Coverage: 0.1f, Name: "high"),
        ];

        Assert.True(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: layers) },
        })));
        Assert.Equal(expected: new[] { "clouds", "clouds#2", "high" }, actual: WorldSkyLayers.LabelsOf(layers: layers));
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
            actual: resolved.Lights.Curvature.Cavity
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.Lights.Curvature.Rim
        );
        Assert.Equal(
            expected: 0f,
            actual: resolved.Lights.Curvature.Ink
        );
        Assert.Equal(
            expected: SdfCurvature.DefaultInkLow,
            actual: resolved.Lights.Curvature.InkLow
        );
        Assert.Equal(
            expected: SdfCurvature.DefaultInkHigh,
            actual: resolved.Lights.Curvature.InkHigh
        );
        Assert.Equal(
            expected: SdfCurvature.DefaultInkColor,
            actual: resolved.Lights.Curvature.InkColor
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
            actual: resolved.Lights.Curvature.Cavity
        );
        Assert.Equal(
            expected: 0.9f,
            actual: resolved.Lights.Curvature.Ink
        );
        Assert.Equal(
            expected: 11f,
            actual: resolved.Lights.Curvature.InkHigh
        );
        Assert.Equal(
            expected: new Vector3(
                x: (0x10 / 255f),
                y: (0x10 / 255f),
                z: (0x18 / 255f)
            ),
            actual: resolved.Lights.Curvature.InkColor
        );
        // Curvature alone leaves the pinned lights in place.
        Assert.Equal(
            expected: 2,
            actual: resolved.Lights.Count
        );
    }
    [Fact]
    public void SunColor_BoundToStateTextCell_ResolvesToTheCell_AndFollowsItsRowWithoutARevisionMove() {
        var track = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard());
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
        ).Lights[0].Color;

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
        ).Lights[0].Color;
        var held = track.Resolve(
            definition: definition,
            mirror: mirror,
            revision: 1
        ).Lights[0].Color;

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
