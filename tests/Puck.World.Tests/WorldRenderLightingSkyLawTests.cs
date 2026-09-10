using System.Numerics;

using Puck.SignedDistance;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>render.lighting</c>/<c>render.sky</c> resolution (<see cref="WorldRenderCycleTrack"/>):
/// absence resolves to the pinned environment bit-exactly, an authored list is exactly the lights it names, every
/// authored field threads through untouched, a state-bound colour reads its cell live, and the validator refuses the
/// shapes the lane table cannot carry.</summary>
public sealed class WorldRenderLightingSkyLawTests {
    private static WorldRenderDefaults BaseDefaults() => WorldRenderDefaults.Absent;
    private static SdfEnvironment Resolve(WorldRenderDefaults defaults, IReadOnlyList<WorldStateRow>? state = null, int revision = 0, WorldRenderCycleTrack? track = null) => (track ?? new WorldRenderCycleTrack()).Resolve(
        definition: (Fixtures.BuildDocument().WithWorldState(rows: (state ?? [])) with { RenderRaw = defaults }),
        revision: revision,
        tick: 0UL
    );
    private static WorldStateRow ColorsRow(string hex) => new(
        Name: CellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Cells: [new StateCell(Key: CellName.Parse(candidate: "sun"), Text: hex)]
    );
    private static WorldRenderLighting SunAndSky(BindableColor? sunColor = null) => new(Lights: [
        new WorldRenderLight.Directional(Color: sunColor, Shadows: true),
        new WorldRenderLight.Hemisphere(),
    ]);
    private static WorldRenderSky TwoStopSky(string top = "#1B2350", string bottom = "#0B0D14") => new(Layers: [
        new WorldRenderSkyLayer.Gradient(Stops: [
            new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: bottom)),
            new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: top)),
        ]),
    ]);
    private static bool TryValidateLocal(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );

    [Fact]
    public void AbsentLightingAndSky_ResolveToThePinnedEnvironmentBitExact() {
        var resolved = Resolve(defaults: BaseDefaults());
        var pinned = SdfEnvironment.Default();

        Assert.True(condition: pinned.Lanes.SequenceEqual(other: resolved.Lanes));
        Assert.Equal(expected: 2, actual: resolved.LightCount);
        Assert.Equal(expected: 0, actual: resolved.ShadowLightIndex);
        Assert.Equal(expected: SdfEnvironment.DefaultSunDirection, actual: resolved.GetLight(index: 0).Direction);
        Assert.Equal(expected: SdfEnvironment.DefaultPenumbraSlope, actual: resolved.GetLight(index: 0).Param);
        Assert.Equal(expected: SdfEnvironment.DefaultAmbientBase, actual: resolved.GetLight(index: 1).Weight);
        Assert.False(condition: resolved.SkyEnabled);
        Assert.Equal(expected: SdfEnvironment.DefaultFogDensity, actual: resolved.FogDensity);
    }
    [Fact]
    public void AuthoredLights_AreExactlyTheListAuthored_UnsetFieldsTakeTheirKindsDefaults() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Direction: new Vector3(x: 0.3f, y: 0.6f, z: -0.5f), Weight: 0.42f, Color: new BindableColor(Raw: "#FFD9A6"), AngularRadius: 0.2f, Shadows: true),
                new WorldRenderLight.Directional(Direction: new Vector3(x: -1f, y: 0f, z: 0f), Weight: 0.3f),
                new WorldRenderLight.Rim(Weight: 0.7f, Power: 5f, Color: new BindableColor(Raw: "#88AAFF")),
            ]),
        });

        Assert.Equal(expected: 3, actual: resolved.LightCount);
        Assert.Equal(expected: 0, actual: resolved.ShadowLightIndex);

        var key = resolved.GetLight(index: 0);
        var fill = resolved.GetLight(index: 1);
        var rim = resolved.GetLight(index: 2);

        Assert.Equal(expected: new Vector3(x: 0.3f, y: 0.6f, z: -0.5f), actual: key.Direction);
        Assert.Equal(expected: 0.42f, actual: key.Weight);
        Assert.Equal(expected: new Vector3(x: (0xFF / 255f), y: (0xD9 / 255f), z: (0xA6 / 255f)), actual: key.Color);
        Assert.Equal(expected: MathF.Tan(x: 0.2f), actual: key.Param);
        Assert.True(condition: key.Shadows);
        Assert.Equal(expected: SdfLightKind.Directional, actual: fill.Kind);
        Assert.False(condition: fill.Shadows);
        Assert.Equal(expected: Vector3.One, actual: fill.Color);
        Assert.Equal(expected: SdfEnvironment.DefaultPenumbraSlope, actual: fill.Param);
        Assert.Equal(expected: SdfLightKind.Rim, actual: rim.Kind);
        Assert.Equal(expected: 0.7f, actual: rim.Weight);
        Assert.Equal(expected: 5f, actual: rim.Param);
    }
    [Fact]
    public void FiveAuthoredLights_AllFiveReachThePackedLanes_IncludingPastTheFourthSlot() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [
                new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 1f, z: 0f), Weight: 0.1f, Shadows: true),
                new WorldRenderLight.Directional(Direction: new Vector3(x: 1f, y: 0f, z: 0f), Weight: 0.2f),
                new WorldRenderLight.Hemisphere(Base: 0.3f),
                new WorldRenderLight.Rim(Weight: 0.4f, Power: 2f),
                new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 0f, z: 1f), Weight: 0.9f),
            ]),
        });

        Assert.Equal(expected: 5, actual: resolved.LightCount);

        var fifth = resolved.GetLight(index: 4);

        Assert.Equal(expected: SdfLightKind.Directional, actual: fifth.Kind);
        Assert.Equal(expected: new Vector3(x: 0f, y: 0f, z: 1f), actual: fifth.Direction);
        Assert.Equal(expected: 0.9f, actual: fifth.Weight);

        // The packed lane table (what the engine uploads, row for row) must carry the fifth light's direction and
        // weight too — not only what GetLight's own row math reads back.
        var lanes = resolved.Lanes;
        var row = (SdfEnvironment.LightsRow + (4 * SdfEnvironment.RowsPerLight));

        Assert.Equal(expected: 0f, actual: lanes[(row * 4) + 0]);
        Assert.Equal(expected: 0f, actual: lanes[(row * 4) + 1]);
        Assert.Equal(expected: 1f, actual: lanes[(row * 4) + 2]);
        Assert.Equal(expected: 0.9f, actual: lanes[(row * 4) + 3]);
    }
    [Fact]
    public void EightAuthoredLights_TheEighthReachesThePackedLanes() {
        var lights = new WorldRenderLight[8];

        for (var index = 0; (index < 8); index++) {
            lights[index] = new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 0f, z: 1f), Weight: (0.1f * (index + 1)));
        }

        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: lights),
        });

        Assert.Equal(expected: 8, actual: resolved.LightCount);

        var eighth = resolved.GetLight(index: 7);

        Assert.Equal(expected: 0.8f, actual: eighth.Weight, precision: 5);
    }
    [Fact]
    public void SunColor_BoundToStateTextCell_ResolvesToTheCell_AndFollowsARevisionMove() {
        var track = new WorldRenderCycleTrack();
        var lighting = SunAndSky(sunColor: new BindableColor(Raw: "state.colors.sun"));

        var first = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#FFD9A6")], revision: 1, track: track).GetLight(index: 0).Color;
        var second = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#4C5C8C")], revision: 2, track: track).GetLight(index: 0).Color;
        var stale = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#000000")], revision: 2, track: track).GetLight(index: 0).Color;

        Assert.Equal(expected: new Vector3(x: (0xFF / 255f), y: (0xD9 / 255f), z: (0xA6 / 255f)), actual: first);
        Assert.Equal(expected: new Vector3(x: (0x4C / 255f), y: (0x5C / 255f), z: (0x8C / 255f)), actual: second);
        // Same revision: the cached resolution stands until the next delivery.
        Assert.Equal(expected: second, actual: stale);
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
                        new WorldRenderLight.Directional(Direction: new Vector3(x: -1f, y: 1f, z: 0f), Shadows: true),
                    ]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = new WorldRenderLighting(Lights: [
                        new WorldRenderLight.Directional(Shadows: true),
                        new WorldRenderLight.Directional(Direction: new Vector3(x: -1f, y: 1f, z: 0f)),
                    ]),
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
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 1f, z: 0f))]) },
            }))
        );
    }
    [Fact]
    public void AngularRadius_PastThePenumbraCeiling_RefusesByName_ControlInRangeClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.angular-radius",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(AngularRadius: 0.5f, Shadows: true)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(AngularRadius: 0.1f, Shadows: true)]) },
            }))
        );
    }
    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
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
    [Fact]
    public void Stylization_Absent_ResolvesToThePinnedDefaults() {
        var resolved = Resolve(defaults: BaseDefaults());

        Assert.Equal(expected: 0f, actual: resolved.CurvatureCavity);
        Assert.Equal(expected: 0f, actual: resolved.CurvatureRim);
        Assert.Equal(expected: 0f, actual: resolved.CurvatureInk);
        Assert.Equal(expected: SdfEnvironment.DefaultCurvatureInkLow, actual: resolved.CurvatureInkLow);
        Assert.Equal(expected: SdfEnvironment.DefaultCurvatureInkHigh, actual: resolved.CurvatureInkHigh);
        Assert.Equal(expected: SdfEnvironment.DefaultCurvatureInkColor, actual: resolved.CurvatureInkColor);
    }
    [Fact]
    public void Stylization_Authored_ThreadsThroughUntouched() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Lighting = new WorldRenderLighting(
                Curvature: new WorldRenderCurvature(Cavity: 0.6f, Rim: 0.2f, Ink: 0.9f, InkLow: 3f, InkHigh: 11f, InkColor: new BindableColor(Raw: "#101018"))
            ),
        });

        Assert.Equal(expected: 0.6f, actual: resolved.CurvatureCavity);
        Assert.Equal(expected: 0.9f, actual: resolved.CurvatureInk);
        Assert.Equal(expected: 11f, actual: resolved.CurvatureInkHigh);
        Assert.Equal(expected: new Vector3(x: (0x10 / 255f), y: (0x10 / 255f), z: (0x18 / 255f)), actual: resolved.CurvatureInkColor);
        // Curvature alone leaves the pinned lights in place.
        Assert.Equal(expected: 2, actual: resolved.LightCount);
    }
    [Fact]
    public void CurvatureInkBand_Inverted_RefusesByName_ControlOrderedClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.curvature-ink-band",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: 12f, InkHigh: 4f)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: 4f, InkHigh: 12f)) },
            }))
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
                        new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#1B2350")),
                        new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#0B0D14")),
                    ])]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = TwoStopSky() },
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
    public void SkySunDiscRadius_OutOfRange_RefusesByName_ControlInRangeClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.sun-disc-radius-range",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Radius: 0f, Intensity: 1f)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Radius: 0.05f, Intensity: 1f)]) },
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
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Light: 1, Radius: 0.05f, Intensity: 1f)]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with {
                    Lighting = SunAndSky(),
                    Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Light: 0, Radius: 0.05f, Intensity: 1f)]),
                },
            }))
        );
    }
    [Fact]
    public void SkyStarsDensity_NonPositive_RefusesByName_ControlPositiveClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.stars-density-positive",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Density: 0f, Brightness: 0.5f, Seed: 1u)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Density: 32f, Brightness: 0.5f, Seed: 1u)]) },
            }))
        );
    }
    [Fact]
    public void AuthoredSky_EveryLayerThreadsThroughAndEnablesTheAuthoredGradient() {
        var sky = new WorldRenderSky(Layers: [
            new WorldRenderSkyLayer.Gradient(Stops: [
                new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#0B0D14")),
                new WorldRenderSkyStop(Elevation: 0f, Color: new BindableColor(Raw: "#E08F6B")),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#1B2350")),
            ]),
            new WorldRenderSkyLayer.Fog(Density: 0.02f),
            new WorldRenderSkyLayer.SunDisc(Radius: 0.045f, Intensity: 6f),
            new WorldRenderSkyLayer.Stars(Density: 64f, Brightness: 0.85f, Seed: 1337u),
        ]);
        var resolved = Resolve(defaults: BaseDefaults() with { Lighting = SunAndSky(), Sky = sky });

        Assert.True(condition: resolved.SkyEnabled);
        Assert.Equal(expected: 3, actual: resolved.SkyStopCount);
        Assert.Equal(expected: (new Vector3(x: (0xE0 / 255f), y: (0x8F / 255f), z: (0x6B / 255f)), 0f), actual: resolved.GetSkyStop(index: 1));
        Assert.Equal(expected: 0.02f, actual: resolved.FogDensity);
        Assert.Equal(expected: 0, actual: resolved.SunDiscLightIndex);
        Assert.Equal(expected: 0.045f, actual: resolved.SunDiscRadians);
        Assert.Equal(expected: 6f, actual: resolved.SunDiscIntensity);
        Assert.Equal(expected: 64f, actual: resolved.StarDensity);
        Assert.Equal(expected: 0.85f, actual: resolved.StarBrightness);
        Assert.Equal(expected: 1337u, actual: resolved.StarSeed);
    }
    [Fact]
    public void AuthoredSky_FogAlone_LeavesThePinnedGradient() {
        var resolved = Resolve(defaults: BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Fog(Density: 0.05f)]) });

        Assert.False(condition: resolved.SkyEnabled);
        Assert.Equal(expected: 0.05f, actual: resolved.FogDensity);
    }
    [Fact]
    public void Cycle_KeyMovesALightBySlot_AndBlendsTheDirectionAlongTheArc() {
        var stateRow = new WorldStateRow(Name: CellName.Parse(candidate: "clock"), Kind: CellKind.Fixed, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: Puck.Maths.FixedQ4816.FromDouble(value: 0.25d).Value)]);
        var defaults = BaseDefaults() with {
            Lighting = SunAndSky(),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Direction: new Vector3(x: 1f, y: 0f, z: 0f), Weight: 0f), new WorldRenderLight.Hemisphere()])),
                new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 1f, z: 0f), Weight: 1f), new WorldRenderLight.Hemisphere()])),
            ]),
        };
        var resolved = Resolve(defaults: defaults, state: [stateRow]);
        var key = resolved.GetLight(index: 0);

        // Halfway between east and straight up: the arc's midpoint, unit length, and the weight's linear midpoint.
        Assert.Equal(expected: 0.5f, actual: key.Weight, precision: 5);
        Assert.Equal(expected: 1f, actual: key.Direction.Length(), precision: 4);
        Assert.Equal(expected: MathF.Sqrt(x: 0.5f), actual: key.Direction.X, precision: 4);
        Assert.Equal(expected: MathF.Sqrt(x: 0.5f), actual: key.Direction.Y, precision: 4);
        Assert.True(condition: key.Shadows);
    }
    [Fact]
    public void Cycle_KeyThatChangesTheLightListShape_RefusesByName_ControlSameShapeClean() {
        Laws.RefusalWithControl(
            lawId: "render.cycle.light-shape",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "clock"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)])]) with {
                RenderRaw = BaseDefaults() with {
                    Lighting = SunAndSky(),
                    Cycle = new WorldRenderCycle(State: "clock", Keys: [
                        new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.2f)])),
                        new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Rim(), new WorldRenderLight.Hemisphere()])),
                    ]),
                },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "clock"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)])]) with {
                RenderRaw = BaseDefaults() with {
                    Lighting = SunAndSky(),
                    Cycle = new WorldRenderCycle(State: "clock", Keys: [
                        new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.2f), new WorldRenderLight.Hemisphere()])),
                        new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.9f), new WorldRenderLight.Hemisphere()])),
                    ]),
                },
            }))
        );
    }

    [Fact]
    public void AuthoredSky_StarsAlone_DrawOverThePinnedGradient() {
        // A drawn layer without an authored gradient enables the sky, and the gradient it draws over is the pinned
        // two-stop one seeded into every environment — not a zeroed stop table.
        var resolved = Resolve(defaults: BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Brightness: 1f)]) });

        Assert.True(condition: resolved.SkyEnabled);
        Assert.Equal(expected: 1f, actual: resolved.StarBrightness);
        Assert.Equal(expected: 2, actual: resolved.SkyStopCount);
        Assert.Equal(expected: (SdfEnvironment.DefaultSkyGroundColor, -1f), actual: resolved.GetSkyStop(index: 0));
        Assert.Equal(expected: (SdfEnvironment.DefaultSkyZenithColor, 1f), actual: resolved.GetSkyStop(index: 1));
    }
    private static WorldDefinition ClockCycle(WorldRenderDefaults defaults) => (Fixtures.BuildDocument().WithWorldState(rows: [new WorldStateRow(Name: CellName.Parse(candidate: "clock"), Kind: CellKind.Int, Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 0L)])]) with {
        RenderRaw = defaults,
    });
    [Fact]
    public void Cycle_OverUnauthoredLighting_IsJudgedAgainstThePinnedTopology() {
        // No static render.lighting: the keys move the pinned sun and hemisphere, so a key naming a third light is
        // adding one, which the lane blend cannot carry (the track would silently drop it).
        static WorldDefinition Cycle(WorldRenderLighting second) => ClockCycle(defaults: (BaseDefaults() with {
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.2f), new WorldRenderLight.Hemisphere()])),
                new WorldRenderCycleKey(At: 0.5f, Lighting: second),
            ]),
        }));

        Laws.RefusalWithControl(
            lawId: "render.cycle.pinned-light-shape",
            deniedOutcome: static () => TryValidateLocal(definition: Cycle(second: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.9f), new WorldRenderLight.Hemisphere(), new WorldRenderLight.Rim()]))),
            controlOutcome: static () => TryValidateLocal(definition: Cycle(second: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.9f), new WorldRenderLight.Hemisphere()])))
        );
    }
    [Fact]
    public void Cycle_SparseStopsThatResolveOutOfOrder_RefuseByName_ControlOrderedClean() {
        // Each key alone is ascending; only the inherited resolution [0.9, 0.8, 1] is not.
        static WorldDefinition Cycle(float firstStopAtSecondKey) => ClockCycle(defaults: (BaseDefaults() with {
            Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
                new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#0B0D14")),
                new WorldRenderSkyStop(Elevation: 0f, Color: new BindableColor(Raw: "#1B2350")),
                new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#2B3360")),
            ])]),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(), new WorldRenderSkyStop(Elevation: 0.8f), new WorldRenderSkyStop()])])),
                new WorldRenderCycleKey(At: 0.5f, Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(Elevation: firstStopAtSecondKey), new WorldRenderSkyStop(), new WorldRenderSkyStop()])])),
            ]),
        }));

        Laws.RefusalWithControl(
            lawId: "render.cycle.resolved-stops-ascending",
            deniedOutcome: static () => TryValidateLocal(definition: Cycle(firstStopAtSecondKey: 0.9f)),
            controlOutcome: static () => TryValidateLocal(definition: Cycle(firstStopAtSecondKey: -0.9f))
        );
    }
    [Fact]
    public void CurvatureInkBand_OneEndPastTheDefaultOther_RefusesByName_ControlBelowClean() {
        // inkHigh absent resolves to the engine default: a low equal to it gives smoothstep a zero-width band.
        Laws.RefusalWithControl(
            lawId: "render.lighting.curvature-ink-band-default",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: SdfEnvironment.DefaultCurvatureInkHigh)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: (SdfEnvironment.DefaultCurvatureInkHigh - 1f))) },
            }))
        );
    }
    [Fact]
    public void Cycle_InkBandThatResolvesInverted_RefusesByName_ControlOrderedClean() {
        // Key one lowers the high end; key two raises the low end past it while leaving the high end to inheritance.
        static WorldDefinition Cycle(float secondLow) => ClockCycle(defaults: (BaseDefaults() with {
            Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f, InkLow: 6f, InkHigh: 16f)),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Curvature: new WorldRenderCurvature(InkHigh: 8f))),
                new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Curvature: new WorldRenderCurvature(InkLow: secondLow))),
            ]),
        }));

        Laws.RefusalWithControl(
            lawId: "render.cycle.resolved-ink-band",
            deniedOutcome: static () => TryValidateLocal(definition: Cycle(secondLow: 10f)),
            controlOutcome: static () => TryValidateLocal(definition: Cycle(secondLow: 7f))
        );
    }

    private static WorldRenderSky ThreeStopSky() => new(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [
        new WorldRenderSkyStop(Elevation: -1f, Color: new BindableColor(Raw: "#0B0D14")),
        new WorldRenderSkyStop(Elevation: 0f, Color: new BindableColor(Raw: "#1B2350")),
        new WorldRenderSkyStop(Elevation: 1f, Color: new BindableColor(Raw: "#2B3360")),
    ])]);
    [Fact]
    public void Cycle_StopsValidOnlyThroughTheWrap_Validate() {
        // Key A moves stop 0 to 0.9 and key B stop 1 to 0.95: on the first pass A holds [0.9, 0, 1], which is not what
        // A renders — A inherits B's 0.95 through the wrap and renders [0.9, 0.95, 1]. The retained pass is judged.
        Assert.True(condition: TryValidateLocal(definition: ClockCycle(defaults: (BaseDefaults() with {
            Sky = ThreeStopSky(),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(Elevation: 0.9f), new WorldRenderSkyStop(), new WorldRenderSkyStop()])])),
                new WorldRenderCycleKey(At: 0.5f, Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Gradient(Stops: [new WorldRenderSkyStop(), new WorldRenderSkyStop(Elevation: 0.95f), new WorldRenderSkyStop()])])),
            ]),
        }))));
    }
    [Fact]
    public void Cycle_InheritedShadowFlagsThatResolveToTwoShadowingLights_RefuseByName_ControlOneClean() {
        // The statics shadow light 0; a key that sets light 1's flag without clearing light 0's resolves to two.
        static WorldDefinition Cycle(bool? firstFlag) => ClockCycle(defaults: (BaseDefaults() with {
            Lighting = new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Shadows: true), new WorldRenderLight.Directional(Direction: new Vector3(x: 0f, y: 1f, z: 0f))]),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.5f), new WorldRenderLight.Directional()])),
                new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Shadows: firstFlag), new WorldRenderLight.Directional(Shadows: true)])),
            ]),
        }));

        Laws.RefusalWithControl(
            lawId: "render.cycle.resolved-shadow-lights",
            deniedOutcome: static () => TryValidateLocal(definition: Cycle(firstFlag: null)),
            controlOutcome: static () => TryValidateLocal(definition: Cycle(firstFlag: false))
        );
    }
    [Fact]
    public void Cycle_OverCurvatureOnlyLighting_MovesThePinnedTwoLights_AndASunDiscMayNameSlotZero() {
        // A curvature-only section keeps the pinned sun and hemisphere, so a key moving those two slots is valid, and
        // a sun disc bound to slot 0 names that pinned directional even with no lighting section at all.
        Assert.True(condition: TryValidateLocal(definition: ClockCycle(defaults: (BaseDefaults() with {
            Lighting = new WorldRenderLighting(Curvature: new WorldRenderCurvature(Ink: 1f)),
            Cycle = new WorldRenderCycle(State: "clock", Keys: [
                new WorldRenderCycleKey(At: 0f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.2f), new WorldRenderLight.Hemisphere()])),
                new WorldRenderCycleKey(At: 0.5f, Lighting: new WorldRenderLighting(Lights: [new WorldRenderLight.Directional(Weight: 0.9f), new WorldRenderLight.Hemisphere()])),
            ]),
        }))));
        Assert.True(condition: TryValidateLocal(definition: (Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.SunDisc(Light: 0, Radius: 0.05f, Intensity: 1f)]) },
        })));
    }

    private static WorldRenderSoftbox Softbox(float x = 1f, float y = 1f, float z = 1f, float width = 0.3f, float height = 0.4f) => new(
        Direction: new Vector3(x: x, y: y, z: z),
        Size: new Vector2(x: width, y: height),
        Color: new BindableColor(Raw: "#FFFFFF"),
        Weight: 0.5f
    );
    [Fact]
    public void AbsentEnvironment_ResolvesToNoSoftboxesAndABlackHorizon() {
        var resolved = Resolve(defaults: BaseDefaults());

        Assert.Equal(expected: 0, actual: resolved.SoftboxCount);
        Assert.Equal(expected: Vector3.Zero, actual: resolved.HorizonLow);
        Assert.Equal(expected: Vector3.Zero, actual: resolved.HorizonHigh);
        Assert.Equal(expected: SdfTonemapMode.None, actual: resolved.Tonemap);
    }
    [Fact]
    public void AuthoredEnvironment_SoftboxesAndHorizonThreadThroughAndUnsetFieldsTakeTheirDefaults() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Environment = new WorldRenderEnvironment(
                Softboxes: [
                    new WorldRenderSoftbox(Direction: new Vector3(x: 0f, y: 1f, z: 0f), Size: new Vector2(x: 0.32f, y: 0.48f), Color: new BindableColor(Raw: "#FFD9A6"), Weight: 0.8f, Blur: 0.1f),
                    new WorldRenderSoftbox(Direction: new Vector3(x: 1f, y: 0f, z: 0f), Size: new Vector2(x: 0.19f, y: 0.52f)),
                ],
                Horizon: new WorldRenderHorizon(Low: new BindableColor(Raw: "#0B0D14"), High: new BindableColor(Raw: "#1B2350"))
            ),
        });

        Assert.Equal(expected: 2, actual: resolved.SoftboxCount);

        var first = resolved.GetSoftbox(index: 0);
        var second = resolved.GetSoftbox(index: 1);

        Assert.Equal(expected: new Vector3(x: 0f, y: 1f, z: 0f), actual: first.Direction);
        Assert.Equal(expected: new Vector3(x: (0xFF / 255f), y: (0xD9 / 255f), z: (0xA6 / 255f)), actual: first.Color);
        Assert.Equal(expected: 0.8f, actual: first.Weight);
        Assert.Equal(expected: new Vector2(x: 0.32f, y: 0.48f), actual: first.Size);
        Assert.Equal(expected: 0.1f, actual: first.Blur);

        // Unset weight/blur/color take their engine defaults (white, weight 1, blur 0), not the previous softbox's.
        Assert.Equal(expected: Vector3.One, actual: second.Color);
        Assert.Equal(expected: 1f, actual: second.Weight);
        Assert.Equal(expected: 0f, actual: second.Blur);

        Assert.Equal(expected: new Vector3(x: (0x0B / 255f), y: (0x0D / 255f), z: (0x14 / 255f)), actual: resolved.HorizonLow);
        Assert.Equal(expected: new Vector3(x: (0x1B / 255f), y: (0x23 / 255f), z: (0x50 / 255f)), actual: resolved.HorizonHigh);
    }
    [Theory]
    [InlineData(WorldTonemap.None, SdfTonemapMode.None)]
    [InlineData(WorldTonemap.Filmic, SdfTonemapMode.Filmic)]
    public void AuthoredTonemap_ResolvesToItsMatchingMode(WorldTonemap authored, SdfTonemapMode expected) {
        var resolved = Resolve(defaults: BaseDefaults() with { Tonemap = authored });

        Assert.Equal(expected: expected, actual: resolved.Tonemap);
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
    public void Environment_ZeroSoftboxDirection_RefusesByName_ControlNonzeroClean() {
        Laws.RefusalWithControl(
            lawId: "render.environment.softbox-direction",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox(x: 0f, y: 0f, z: 0f)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Environment = new WorldRenderEnvironment(Softboxes: [Softbox()]) },
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
}
