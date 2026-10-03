using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

// The open layer stack as a document authors it: layers resolve in their authored order, a kind as often as authored, each
// with its kind's blend and visibility unless it states its own, its opacity, mask, transform and lowest tier; the sky
// frame and the sky's quality tier reach the block; and the validator refuses by name a stack its passes cannot draw: a
// field run past the upper runs, a mask that is neither or both of a band and a cone, a clock the timeline does not
// declare, a panorama of an undeclared screen or one the lighting would see, and a layer named for a fixed
// work-counter row (a field run's or the atmosphere's), which its row could not hold apart.
public sealed partial class WorldRenderLightingSkyLawTests {
    [Fact]
    public void AStackResolvesInItsAuthoredOrderWithItsKindsDefaultsAndItsOwnFields() {
        var resolved = Resolve(defaults: BaseDefaults() with {
            Sky = new WorldRenderSky(
                Frame: new WorldRenderSkyFrame(Up: new Vector3(x: 0f, y: 1f, z: 1f)),
                Layers: [
                    new WorldRenderSkyLayer.Pattern(Cells: 12f),
                    new WorldRenderSkyLayer.Stars(Brightness: 1f),
                    new WorldRenderSkyLayer.Clouds(Coverage: 0.4f),
                    new WorldRenderSkyLayer.Clouds(Coverage: 0.2f) {
                        Blend = WorldSkyBlend.Screen,
                        Mask = new WorldRenderSkyMask(Band: [0.1d, 0.9d], Feather: 0.05d),
                        Opacity = 0.5f,
                        Tier = WorldSkyTier.Medium,
                        Transform = new WorldRenderSkyTransform(Turn: new BindableScalar(literal: 1f)),
                        Visibility = WorldSkyVisibility.Both,
                    },
                    new WorldRenderSkyLayer.Aurora(Intensity: 2f),
                ]
            ),
        });
        var sky = resolved.Sky;

        // An authored stack without a gradient draws over the default look's, which stays beneath it.
        Assert.Equal(expected: 6, actual: sky.LayerCount);
        Assert.Equal(
            actual: sky.Layers.ToArray().Select(selector: static layer => (layer.Kind, layer.Blend, layer.Visibility)),
            expected: [
                (SdfSkyLayerKind.Gradient, SdfSkyBlend.Over, SdfSkyVisibility.Both),
                (SdfSkyLayerKind.Pattern, SdfSkyBlend.Over, SdfSkyVisibility.Camera),
                (SdfSkyLayerKind.Stars, SdfSkyBlend.Add, SdfSkyVisibility.Camera),
                (SdfSkyLayerKind.Clouds, SdfSkyBlend.Over, SdfSkyVisibility.Camera),
                (SdfSkyLayerKind.Clouds, SdfSkyBlend.Screen, SdfSkyVisibility.Both),
                (SdfSkyLayerKind.Aurora, SdfSkyBlend.Add, SdfSkyVisibility.Camera),
            ]
        );
        Assert.Equal(expected: new[] { "gradient", "pattern", "stars", "clouds", "clouds#2", "aurora" }, actual: Enumerable.Range(count: 6, start: 0).Select(selector: sky.LabelAt));
        Assert.Equal(expected: 0.2f, actual: sky.Parameters<SdfSkyClouds>(index: 4).Coverage);
        Assert.Equal(expected: SdfSkyTier.Medium, actual: sky.TierAt(index: 4));

        var masked = sky.LayerAt(index: 4);

        Assert.Equal(actual: masked.Opacity, expected: 0.5f);
        Assert.Equal(actual: masked.Mask, expected: SdfSkyMask.Elevation);
        Assert.Equal(expected: ((float)Math.Sin(a: 0.1d)), actual: masked.MaskBand.X);
        Assert.Equal(expected: ((float)Math.Sin(a: 0.9d)), actual: masked.MaskBand.Y);
        Assert.Equal(expected: SdfSkyLayer.RotationOf(tilt: 0d, turn: 1d), actual: masked.Rotation);
        Assert.Equal(expected: SdfSkyTier.High, actual: sky.Quality);

        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(block: out var block, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: resolved.Lights, softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes]);
        Assert.Equal(expected: Vector3.Normalize(value: new Vector3(x: 0f, y: 1f, z: 1f)), actual: block.FrameUp);
        Assert.Equal(actual: block.LayerCount, expected: 6u);
        Assert.Equal(actual: (block.BaseRun, block.UpperRuns), expected: (1u, 1u));
    }
    // A layer above the sky's quality tier writes no entry; the tier comes from world.sky-quality, or the definition's boot
    // tier when the resolver is handed none.
    [Fact]
    public void ALayerAboveTheSkysQualityWritesNoEntry() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = BaseDefaults() with {
                SkyQuality = WorldSkyTier.Low,
                Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Clouds(Coverage: 0.4f) { Tier = WorldSkyTier.High }]),
            },
        };
        var mirror = ClientFixtures.StateMirror(definition: definition);
        var low = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: mirror, revision: 0);
        var high = new WorldEnvironmentResolve(domains: new WorldValueDomainGuard()).Resolve(definition: definition, mirror: mirror, revision: 0, skyQuality: SdfSkyTier.High);
        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        Assert.Equal(expected: SdfSkyTier.Low, actual: low.Sky.Quality);
        low.Sky.Pack(block: out var lowBlock, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: low.Lights, softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes]);
        Assert.Equal(actual: lowBlock.LayerCount, expected: 1u);
        Assert.Equal(expected: SdfSkyLayerKind.Gradient, actual: layers[0].Kind);
        high.Sky.Pack(block: out var highBlock, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: high.Lights, softboxes: new SdfSoftbox[SdfSky.MaxSoftboxes]);
        Assert.Equal(actual: highBlock.LayerCount, expected: 2u);
        Assert.Equal(expected: SdfSkyLayerKind.Clouds, actual: layers[1].Kind);
    }
    [Fact]
    public void SkyFieldRun_PastTheUpperRuns_RefusesByName_ControlTwoClean() {
        static WorldRenderSkyLayer[] Stack(int upperRuns) => [
            new WorldRenderSkyLayer.Clouds(Coverage: 0.3f),
            .. Enumerable.Range(count: upperRuns, start: 0).SelectMany(selector: static _ => new WorldRenderSkyLayer[] { new WorldRenderSkyLayer.Stars(Brightness: 1f), new WorldRenderSkyLayer.Clouds(Coverage: 0.3f) }),
        ];

        Laws.RefusalWithControl(
            lawId: "render.sky.field-runs",
            deniedOutcome: () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: Stack(upperRuns: (SdfSky.MaxUpperFieldRuns + 1))) } })),
            controlOutcome: () => TryValidateLocal(definition: (Fixtures.BuildDocument() with { RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: Stack(upperRuns: SdfSky.MaxUpperFieldRuns)) } }))
        );
    }
    [Fact]
    public void SkyMask_BandAndCone_RefusesByName_ControlBandClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.mask-one",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Noise() { Mask = new WorldRenderSkyMask(Band: [0d, 1d], Cone: new WorldRenderSkyCone(Spread: 0.5d, Toward: Vector3.UnitY)) }]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Noise() { Mask = new WorldRenderSkyMask(Band: [0d, 1d]) }]) },
            }))
        );
    }
    [Fact]
    public void SkyLayerClock_Undeclared_RefusesByName_ControlUnclockedClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.layer-clock",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Aurora(Intensity: 1f) { Clock = "nowhere" }]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Aurora(Intensity: 1f)]) },
            }))
        );
    }
    [Fact]
    public void SkyPanorama_LitOrOfAnUndeclaredScreen_RefusesByName_ControlDeclaredClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.panorama-screen",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Panorama(Screen: 17)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Panorama(Screen: Fixtures.TestPatternScreenIndex)]) },
            }))
        );
        Laws.RefusalWithControl(
            lawId: "render.sky.panorama-camera",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Panorama(Screen: Fixtures.TestPatternScreenIndex) { Visibility = WorldSkyVisibility.Both }]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Panorama(Screen: Fixtures.TestPatternScreenIndex) { Visibility = WorldSkyVisibility.Camera }]) },
            }))
        );
    }
    [Fact]
    public void SkyLayerName_AFixedRowsLabel_RefusesByName_ControlOtherNameClean() {
        Laws.RefusalWithControl(
            lawId: "render.sky.layer-name-not-a-fixed-row",
            deniedOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Brightness: 0.5f, Name: SdfSkyDetails.Atmosphere)]) },
            })),
            controlOutcome: static () => TryValidateLocal(definition: (Fixtures.BuildDocument() with {
                RenderRaw = BaseDefaults() with { Sky = new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Brightness: 0.5f, Name: "air")]) },
            }))
        );
    }
}
