using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The actual environment resolver preserves every authored sky row in native tables, uses one shared
/// rate integration path, and keeps inactive work separate from structural storage and per-view admission.</summary>
public sealed class WorldSkyNativeResolveLawTests {
    private static SdfSkyParameterTable<T> Table<T>(SdfSkySnapshot sky, string kind) where T : unmanaged {
        for (var index = 0; index < sky.TableCount; index++) {
            if (sky.Table(index).Kind == kind) { return Assert.IsType<SdfSkyParameterTable<T>>(sky.Table(index)); }
        }
        throw new InvalidOperationException("The expected native sky table is absent: " + kind);
    }
    private static void Present(WorldStateMirror mirror, ulong seconds) {
        mirror.Refresh(new WorldStateStamp(seconds, seconds * EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
    }
    private static WorldRenderSkyLayer.Gradient Gradient(string name, string low, string high) => new([
        new(-1f, new BindableColor(low)), new(1f, new BindableColor(high)),
    ]) { Name = name };

    [Fact]
    public void Repeated_kinds_retain_order_native_seed_bits_and_independent_gradient_ranges() {
        var definition = Fixtures.BuildDocument() with { RenderRaw = new(Sky: new([
            Gradient("base", "#000000", "#FFFFFF"),
            new WorldRenderSkyLayer.Stars(Brightness: 1f, Seed: 0x80000001u) { Name = "near" },
            Gradient("veil", "#FF0000", "#0000FF"),
            new WorldRenderSkyLayer.Stars(Brightness: 2f, Seed: 0x01000001u) { Name = "far", Tier = QualityTier.High },
        ]) { Quality = QualityTier.Low }) };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var sky = Assert.IsType<SdfSkySnapshot>(new WorldEnvironmentResolve().Resolve(definition, 0,
            ClientFixtures.StateMirror(definition)).Sky);
        Assert.Equal(4, sky.LayerCount);
        Assert.Equal(4, sky.RunCount);
        Assert.Equal(new[] { "base", "near", "veil", "far" }, Enumerable.Range(0, 4).Select(index => sky.Layer(index).Name));
        var stars = Table<SdfSkyStarsData>(sky, "stars");
        Assert.Equal(2, stars.Count);
        Assert.Equal(0x80000001u, stars.Rows[0].Seed);
        Assert.Equal(0x01000001u, stars.Rows[1].Seed);
        Assert.Equal(1f, stars.Rows[0].Brightness);
        Assert.Equal(2f, stars.Rows[1].Brightness);
        Assert.True(sky.Layer(3).Enabled);
        Assert.Equal(QualityTier.Low, sky.Quality);
        var gradients = Table<SdfSkyGradientData>(sky, "gradient");
        Assert.Equal(0u, gradients.Rows[0].FirstStop);
        Assert.Equal(2u, gradients.Rows[1].FirstStop);
        var stops = Assert.IsType<SdfSkyParameterTable<SdfSkyStopData>>(sky.Stops);
        Assert.Equal(4, stops.Count);
        Assert.Equal(Vector3.Zero, stops.Rows[0].Color);
        Assert.Equal(Vector3.UnitX, stops.Rows[2].Color);
        Assert.Equal(Vector3.UnitZ, stops.Rows[3].Color);
        var common = Assert.IsType<SdfSkyParameterTable<SdfSkyLayerData>>(sky.Common);
        Assert.Equal(0u, common.Rows[1].ParameterIndex);
        Assert.Equal(1u, common.Rows[3].ParameterIndex);
        Assert.Equal(common.Rows[1].Kind, common.Rows[3].Kind);
        Assert.Equal((uint)QualityTier.High, common.Rows[3].MinimumTier);
    }

    [Fact]
    public void Each_layer_uses_its_own_integral_and_common_rotation_does_not_rescale_by_clock_span() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new(Sky: new([
                new WorldRenderSkyLayer.Clouds(Coverage: 1f, Drift: new BindableVector2(1f, 0f)) { Name = "a" },
                new WorldRenderSkyLayer.Clouds(Coverage: 1f, Drift: new BindableVector2(0f, 2f)) { Name = "b" },
                new WorldRenderSkyLayer.Pattern { Name = "paint", Clock = "day", Transform = new(Rate: .25f) },
            ])),
            TimelineRaw = new(Clocks: [new("day", PeriodSeconds: 8d, SpanSeconds: 24d)]),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        Present(mirror, 2);
        var sky = Assert.IsType<SdfSkySnapshot>(resolve.Resolve(definition, 0, mirror).Sky);
        var clouds = Table<SdfSkyCloudsData>(sky, "clouds");
        Assert.Equal(new Vector2(2f, 0f), clouds.Rows[0].Offset);
        Assert.Equal(new Vector2(0f, 4f), clouds.Rows[1].Offset);
        var common = Assert.IsType<SdfSkyParameterTable<SdfSkyLayerData>>(sky.Common);
        var expected = Quaternion.Conjugate(Quaternion.CreateFromAxisAngle(Vector3.UnitY, .5f));
        Assert.Equal(new Vector4(expected.X, expected.Y, expected.Z, expected.W), common.Rows[2].InverseRotation);
        Assert.Equal(.25d, sky.Layer(2).Phase);
        Assert.Equal(3, resolve.RateEvaluations);
        _ = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(3, resolve.RateEvaluations);
        Present(mirror, 7);
        _ = resolve.Resolve(definition, 0, mirror);
        Present(mirror, 2);
        sky = Assert.IsType<SdfSkySnapshot>(resolve.Resolve(definition, 0, mirror).Sky);
        Assert.Equal(new Vector2(2f, 0f), Table<SdfSkyCloudsData>(sky, "clouds").Rows[0].Offset);
        Assert.Equal(new Vector4(expected.X, expected.Y, expected.Z, expected.W),
            Assert.IsType<SdfSkyParameterTable<SdfSkyLayerData>>(sky.Common).Rows[2].InverseRotation);
    }

    [Fact]
    public void Zero_opacity_skips_kind_dependencies_domains_and_motion_without_changing_run_storage() {
        var opacity = new BindableScalar(keys: new WorldKeys<BindableScalar>("day", [new(0d, 0f), new(4d, 1f)]));
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new(Sky: new([
                new WorldRenderSkyLayer.Noise(Scale: .5f) { Name = "base" },
                new WorldRenderSkyLayer.Stars(Brightness: 1f, Twinkle: new(Share: 1f, Depth: 1f, Rate: 2f)) {
                    Name = "stars", Opacity = opacity,
                },
                new WorldRenderSkyLayer.Clouds(Coverage: 1f, Drift: new BindableVector2(1f, 2f)) { Name = "off", Opacity = 0f },
            ])),
            TimelineRaw = new(Clocks: [new("day", PeriodSeconds: 8d)]),
        };
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        var first = Assert.IsType<SdfSkySnapshot>(resolve.Resolve(definition, 0, mirror).Sky);
        var retained = SdfSkySnapshot.Copy(first, null)!;
        Assert.False(first.Layer(1).Enabled);
        Assert.False(first.Layer(2).Enabled);
        Assert.Equal(0, resolve.RateEvaluations);
        Assert.Equal(default, Table<SdfSkyCloudsData>(first, "clouds").Rows[0]);
        var starTable = Table<SdfSkyStarsData>(retained, "stars");
        var commonTable = retained.Common;
        Present(mirror, 2);
        var next = Assert.IsType<SdfSkySnapshot>(resolve.Resolve(definition, 0, mirror).Sky);
        Assert.True(next.Layer(1).Enabled);
        Assert.False(next.Layer(2).Enabled);
        Assert.Equal(1, resolve.RateEvaluations);
        Assert.Equal(3, next.RunCount);
        Assert.Equal(0f, starTable.Rows[0].Brightness);
        Assert.Same(retained, SdfSkySnapshot.Copy(next, retained));
        Assert.Same(starTable, Table<SdfSkyStarsData>(retained, "stars"));
        Assert.Same(commonTable, retained.Common);
        Assert.Equal(1f, starTable.Rows[0].Brightness);
    }
}
