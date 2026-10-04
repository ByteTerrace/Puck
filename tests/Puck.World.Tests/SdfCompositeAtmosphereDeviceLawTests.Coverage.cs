using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfCompositeAtmosphereDeviceLawTests {
    private sealed record CoverageProbe(bool GeometryOnly, SdfSky Sky, Half[] Lit, Half[]? ViewImage = null);
    private sealed record CompositeResult((long Atmosphere, long Layers) Counts, Half[] Color);

    [Fact]
    public void VulkanPreservesFarCoverageThroughItsOutputAndFilteredLayer() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfCompositeAtmosphereDeviceLawTests));

        VerifyCoverage(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXPreservesFarCoverageThroughItsOutputAndFilteredLayer() {
        using var device = DirectXTestDevices.Hardware();

        VerifyCoverage(services: device.Services, extension: ".dxil");
    }

    // Actual composite outputs feed the next composite's view layer. Binary fractions make the oracle independent of
    // atmosphere, traversal and transcendental precision: empty, half-covered and opaque columns over a constant sky.
    private static void VerifyCoverage(GpuDeviceServices services, string extension) {
        var surface = new Vector3(x: 0.75f, y: 0.25f, z: 0.5f);
        var backdrop = new Vector3(x: 0.125f, y: 0.5f, z: 0.25f);
        var lit = new Half[(Width * Height * 4)];
        var lights = new SdfLights();

        static float Coverage(uint x) => ((x < 4) ? 0f : ((x < 8) ? 0.5f : 1f));
        for (var y = 0u; y < Height; y++) {
            for (var x = 0u; x < Width; x++) {
                var offset = ((y * Width + x) * 4);
                var coverage = Coverage(x: x);
                var value = (surface * coverage);

                lit[offset] = ((Half)value.X); lit[offset + 1] = ((Half)value.Y); lit[offset + 2] = ((Half)value.Z); lit[offset + 3] = ((Half)coverage);
            }
        }
        var sky = new SdfSky();

        sky.Atmosphere = SdfAtmosphere.None;
        sky.ClearLayers();
        _ = sky.Add(label: "backdrop", parameters: new SdfSkyGradient {
            Count = 2, Color0 = backdrop, Color1 = backdrop, Elevation0 = -1f, Elevation1 = 1f,
        });
        var far = Run(services: services, extension: extension, atmosphere: SdfAtmosphere.None, lights: lights,
            coverageProbe: new CoverageProbe(GeometryOnly: true, Sky: sky, Lit: lit));
        var ordinary = Run(services: services, extension: extension, atmosphere: SdfAtmosphere.None, lights: lights,
            coverageProbe: new CoverageProbe(GeometryOnly: false, Sky: sky, Lit: lit));

        Assert.Equal(expected: (Atmosphere: 0L, Layers: 0L), actual: far.Counts);
        for (var y = 0u; y < Height; y++) {
            for (var x = 0u; x < Width; x++) {
                var coverage = Coverage(x: x);

                CheckCoverage(pixels: far.Color, x: x, y: y, expected: (surface * coverage), alpha: coverage);
                CheckCoverage(pixels: ordinary.Color, x: x, y: y, expected: ((surface * coverage) + (backdrop * (1f - coverage))), alpha: 1f);
            }
        }

        // Shift the fitted rectangle half a source texel left. Pixel 3 filters empty and half coverage to 1/4; pixel 7
        // filters half and full coverage to 3/4. A second multiplication by alpha would visibly darken both samples.
        _ = sky.Add(label: "far", parameters: new SdfSkyView {
            Right = Vector3.UnitX, Up = Vector3.UnitY, Forward = -Vector3.UnitZ,
            Intensity = 1f, ImageSlot = 0, Coverage = 1,
            Rect = new Vector4(x: (-1f - (1f / Width)), y: -0.5f, z: (1f - (1f / Width)), w: 0.5f),
        });
        var composed = Run(services: services, extension: extension, atmosphere: SdfAtmosphere.None, lights: lights,
            coverageProbe: new CoverageProbe(GeometryOnly: false, Sky: sky, Lit: new Half[lit.Length], ViewImage: far.Color));

        foreach (var (x, coverage) in new[] { (1u, 0f), (3u, 0.25f), (5u, 0.5f), (7u, 0.75f), (12u, 1f) }) {
            CheckCoverage(pixels: composed.Color, x: x, y: 3, expected: ((surface * coverage) + (backdrop * (1f - coverage))), alpha: 1f);
        }
    }
    private static void CheckCoverage(Half[] pixels, uint x, uint y, Vector3 expected, float alpha) {
        var offset = ((y * Width + x) * 4);
        var values = new[] { expected.X, expected.Y, expected.Z, alpha };

        for (var channel = 0u; channel < 4; channel++) {
            Assert.True(condition: MathF.Abs(x: (((float)pixels[offset + channel]) - values[channel])) <= 0.0005f,
                userMessage: $"({x},{y}) channel {channel}: expected {values[channel]}, actual {pixels[offset + channel]}");
        }
    }
}
