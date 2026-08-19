using System.Numerics;

using Puck.SdfVm;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <c>render.lighting</c>/<c>render.sky</c> resolution (<see cref="WorldRenderCycleTrack"/>):
/// absence, at any level, must resolve to <see cref="SdfFrame"/>'s own pinned defaults bit-exactly, every authored
/// field must thread through untouched, and a state-bound color reads its cell live.</summary>
public sealed class WorldRenderLightingSkyLawTests {
    private static WorldRenderDefaults BaseDefaults() => WorldRenderDefaults.Absent;
    private static WorldRenderLightingState Resolve(WorldRenderDefaults defaults, IReadOnlyList<WorldStateRow>? state = null, int revision = 0, WorldRenderCycleTrack? track = null) => (track ?? new WorldRenderCycleTrack()).Resolve(
        definition: (Fixtures.BuildDocument().WithWorldState(rows: (state ?? [])) with { RenderRaw = defaults }),
        revision: revision,
        tick: 0UL
    );
    private static WorldStateRow ColorsRow(string hex) => new(
        Name: WorldCellName.Parse(candidate: "colors"),
        Kind: CellKind.Text,
        Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "sun"), Text: hex)]
    );

    [Fact]
    public void SunColor_BoundToStateTextCell_ResolvesToTheCell_AndFollowsARevisionMove() {
        var track = new WorldRenderCycleTrack();
        var lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "state.colors.sun"));

        var first = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#FFD9A6")], revision: 1, track: track);
        var second = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#4C5C8C")], revision: 2, track: track);
        var stale = Resolve(defaults: BaseDefaults() with { Lighting = lighting }, state: [ColorsRow(hex: "#000000")], revision: 2, track: track);

        Assert.Equal(expected: new Vector3((0xFF / 255f), (0xD9 / 255f), (0xA6 / 255f)), actual: first.SunColor);
        Assert.Equal(expected: new Vector3((0x4C / 255f), (0x5C / 255f), (0x8C / 255f)), actual: second.SunColor);
        // Same revision: the cached resolution stands until the next delivery.
        Assert.Equal(expected: second.SunColor, actual: stale.SunColor);
    }
    [Fact]
    public void SunColor_BoundToUndeclaredCell_RefusesByName_ControlDeclaredTextCellClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.sun-color-binding",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "state.colors.moon")) },
                StateRaw = new WorldStateSection(World: [ColorsRow(hex: "#FFD9A6")]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "state.colors.sun")) },
                StateRaw = new WorldStateSection(World: [ColorsRow(hex: "#FFD9A6")]),
            }))
        );
    }
    [Fact]
    public void SunColor_BoundToNonTextRow_RefusesByName_ControlTextRowClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.sun-color-binding-kind",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "state.colors.sun")) },
                StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "colors"), Kind: CellKind.Int, Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "sun"), Value: 7L)])]),
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "state.colors.sun")) },
                StateRaw = new WorldStateSection(World: [ColorsRow(hex: "#FFD9A6")]),
            }))
        );
    }
    private static bool TryValidateLocal(WorldDefinition definition) => WorldDefinitionValidator.TryValidate(
        definition: definition,
        neighbours: null,
        reason: out _
    );

    [Fact]
    public void LightingSunColor_MalformedHex_RefusesByName_ControlWellFormedClean() {
        Laws.RefusalWithControl(
            lawId: "render.lighting.sun-color",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "orange")) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Color: "#FFD9A6")) },
            }))
        );
    }
    [Fact]
    public void SkyZenith_MalformedHex_RefusesByName_ControlWellFormedClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.zenith-color",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Zenith: "not-a-color") },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Zenith: "#1B2350") },
            }))
        );
    }
    [Fact]
    public void SkyFogDensity_Negative_RefusesByName_ControlNonNegativeClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.fog-density-negative",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(FogDensity: -0.01f) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(FogDensity: 0.01f) },
            }))
        );
    }
    [Fact]
    public void SkySunDiscRadians_OutOfRange_RefusesByName_ControlInRangeClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.sun-disc-radians-range",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Sun: new WorldRenderSkySun(DiscRadians: 0f, Intensity: 1f)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Sun: new WorldRenderSkySun(DiscRadians: 0.05f, Intensity: 1f)) },
            }))
        );
    }
    [Fact]
    public void SkyStarsDensity_NonPositive_RefusesByName_ControlPositiveClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.stars-density-positive",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Stars: new WorldRenderSkyStars(Density: 0f, Brightness: 0.5f, Seed: 1u)) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Stars: new WorldRenderSkyStars(Density: 32f, Brightness: 0.5f, Seed: 1u)) },
            }))
        );
    }

    [Fact]
    public void AbsentLightingAndSky_ResolveToSdfFrameDefaultsBitExact() {
        var settings = Resolve(defaults: BaseDefaults());

        Assert.Equal(expected: SdfFrame.DefaultSunDirection, actual: settings.SunDirection);
        Assert.Equal(expected: SdfFrame.DefaultSunWeight, actual: settings.SunWeight);
        Assert.Equal(expected: Vector3.One, actual: settings.SunColor);
        Assert.Equal(expected: SdfFrame.DefaultAmbientBase, actual: settings.AmbientBase);
        Assert.Equal(expected: SdfFrame.DefaultAmbientHemisphere, actual: settings.AmbientHemisphere);
        Assert.Equal(expected: Vector3.One, actual: settings.AmbientColor);

        Assert.False(condition: settings.SkyEnabled);
        Assert.Equal(expected: SdfFrame.DefaultSkyZenithColor, actual: settings.SkyZenithColor);
        Assert.Equal(expected: SdfFrame.DefaultSkyHorizonColor, actual: settings.SkyHorizonColor);
        Assert.Equal(expected: SdfFrame.DefaultSkyGroundColor, actual: settings.SkyGroundColor);
        Assert.Equal(expected: SdfFrame.DefaultSkyFogDensity, actual: settings.SkyFogDensity);
        Assert.Equal(expected: SdfFrame.DefaultSkySunDiscRadians, actual: settings.SkySunDiscRadians);
        Assert.Equal(expected: 0f, actual: settings.SkySunDiscIntensity);
        Assert.Equal(expected: SdfFrame.DefaultSkyStarDensity, actual: settings.SkyStarDensity);
        Assert.Equal(expected: 0f, actual: settings.SkyStarBrightness);
        Assert.Equal(expected: 0u, actual: settings.SkyStarSeed);
    }

    [Fact]
    public void AuthoredLighting_EveryFieldThreadsThrough() {
        var lighting = new WorldRenderLighting(
            Sun: new WorldRenderSun(
                Direction: new Vector3(0.3f, 0.6f, -0.5f),
                Weight: 0.42f,
                Color: "#FFD9A6"
            ),
            Ambient: new WorldRenderAmbient(
                Base: 0.11f,
                Hemisphere: 0.13f,
                Color: "#4C5C8C"
            )
        );
        var settings = Resolve(defaults: BaseDefaults() with { Lighting = lighting });

        Assert.Equal(expected: new Vector3(0.3f, 0.6f, -0.5f), actual: settings.SunDirection);
        Assert.Equal(expected: 0.42f, actual: settings.SunWeight);
        Assert.Equal(expected: new Vector3((0xFF / 255f), (0xD9 / 255f), (0xA6 / 255f)), actual: settings.SunColor);
        Assert.Equal(expected: 0.11f, actual: settings.AmbientBase);
        Assert.Equal(expected: 0.13f, actual: settings.AmbientHemisphere);
        Assert.Equal(expected: new Vector3((0x4C / 255f), (0x5C / 255f), (0x8C / 255f)), actual: settings.AmbientColor);
    }

    [Fact]
    public void AuthoredLighting_PartialSection_UnsetFieldsKeepSdfFrameDefaults() {
        var lighting = new WorldRenderLighting(Sun: new WorldRenderSun(Weight: 0.5f));
        var settings = Resolve(defaults: BaseDefaults() with { Lighting = lighting });

        Assert.Equal(expected: SdfFrame.DefaultSunDirection, actual: settings.SunDirection);
        Assert.Equal(expected: 0.5f, actual: settings.SunWeight);
        Assert.Equal(expected: Vector3.One, actual: settings.SunColor);
        Assert.Equal(expected: SdfFrame.DefaultAmbientBase, actual: settings.AmbientBase);
        Assert.Equal(expected: SdfFrame.DefaultAmbientHemisphere, actual: settings.AmbientHemisphere);
    }

    [Fact]
    public void AuthoredSky_EveryFieldThreadsThroughAndEnablesTheProceduralSky() {
        var sky = new WorldRenderSky(
            Zenith: "#1B2350",
            Horizon: "#E08F6B",
            Ground: "#0B0D14",
            FogDensity: 0.02f,
            Sun: new WorldRenderSkySun(DiscRadians: 0.045f, Intensity: 6f),
            Stars: new WorldRenderSkyStars(Density: 64f, Brightness: 0.85f, Seed: 1337u)
        );
        var settings = Resolve(defaults: BaseDefaults() with { Sky = sky });

        Assert.True(condition: settings.SkyEnabled);
        Assert.Equal(expected: new Vector3((0x1B / 255f), (0x23 / 255f), (0x50 / 255f)), actual: settings.SkyZenithColor);
        Assert.Equal(expected: new Vector3((0xE0 / 255f), (0x8F / 255f), (0x6B / 255f)), actual: settings.SkyHorizonColor);
        Assert.Equal(expected: new Vector3((0x0B / 255f), (0x0D / 255f), (0x14 / 255f)), actual: settings.SkyGroundColor);
        Assert.Equal(expected: 0.02f, actual: settings.SkyFogDensity);
        Assert.Equal(expected: 0.045f, actual: settings.SkySunDiscRadians);
        Assert.Equal(expected: 6f, actual: settings.SkySunDiscIntensity);
        Assert.Equal(expected: 64f, actual: settings.SkyStarDensity);
        Assert.Equal(expected: 0.85f, actual: settings.SkyStarBrightness);
        Assert.Equal(expected: 1337u, actual: settings.SkyStarSeed);
    }

    [Fact]
    public void AuthoredSky_FogDensityIsIndependentOfEnabled() {
        // render.sky.fogDensity is read every frame regardless of the gradient/disc/star gate — a world can raise
        // fog without opting into the procedural gradient.
        var sky = new WorldRenderSky(FogDensity: 0.05f);
        var settings = Resolve(defaults: BaseDefaults() with { Sky = sky });

        Assert.True(condition: settings.SkyEnabled);
        Assert.Equal(expected: 0.05f, actual: settings.SkyFogDensity);
        Assert.Equal(expected: SdfFrame.DefaultSkyZenithColor, actual: settings.SkyZenithColor);
    }
}
