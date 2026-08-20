using System.Numerics;
using Puck.Assets;
using Puck.ShaderVm.Programs;
using Xunit;

namespace Puck.ShaderVm.Tests;

// A host render of the sky program, written where a human can look at it. The GPU interpreter must agree with this.
public sealed class SkyPreview {
    private const int Height = 512;
    private const int Width = 1024;

    [Theory]
    [InlineData("sky-base")]
    [InlineData("sky-night")]
    [InlineData("sky-day")]
    public void RendersTheAuthoredSky(string name) {
        var directory = Environment.GetEnvironmentVariable(variable: "PUCK_SKY_PREVIEW_DIR");

        Assert.SkipWhen(condition: string.IsNullOrEmpty(value: directory), reason: "Opt-in harness: set PUCK_SKY_PREVIEW_DIR to the directory the preview is written to.");

        var settings = Settings(name: name);
        var program = SkyProgram.Compile(cloudSeed: settings.CloudSeed, starSeed: settings.StarSeed);
        var parameters = new Vector4[SkyParameters.Count];

        SkyParameters.Pack(rows: parameters, settings: in settings);

        var pixels = new byte[Width * Height * 4];
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        _ = Parallel.For(body: y => {
            for (var x = 0; (x < Width); x++) {
                var context = new ShaderContext(Coordinate: RayDirection(x: x, y: y));
                var color = ShaderInterpreter.Evaluate(
                    context: in context,
                    parameters: parameters,
                    program: program
                );
                var offset = (((y * Width) + x) * 4);

                pixels[(offset + 0)] = Encode(value: color.X);
                pixels[(offset + 1)] = Encode(value: color.Y);
                pixels[(offset + 2)] = Encode(value: color.Z);
                pixels[(offset + 3)] = 255;
            }
        }, fromInclusive: 0, toExclusive: Height);

        stopwatch.Stop();
        PngEncoder.Write(
            height: Height,
            path: Path.Combine(path1: directory!, path2: (name + ".png")),
            rgba: pixels,
            width: Width
        );
        File.WriteAllText(
            contents: $"{name}: instructions={program.InstructionCount} constants={program.ConstantCount} words={program.Words.Length} milliseconds={stopwatch.ElapsedMilliseconds}",
            path: Path.Combine(path1: directory!, path2: (name + ".txt"))
        );
    }
    // The three keys null.world.json authors, with the wind integrated to a fixed offset so the frame is stable.
    private static SkyFrameSettings Settings(string name) {
        var wind = new SkyFrameSettings {
            CloudDrift = new Vector2(x: 3.1f, y: -1.4f),
            CloudShear = new Vector2(x: 0.7f, y: 2.2f),
            CloudSpin = 0.35f,
            StarPhase = 0.21f,
        };

        return (name switch {
            "sky-night" => wind with {
                CloudColor = Srgb(hex: 0x1A1E33),
                CloudCoverage = 0.3f,
                Ground = Srgb(hex: 0x020308),
                Horizon = Srgb(hex: 0x0E1430),
                StarBrightness = 1f,
                SunColor = Srgb(hex: 0x8090C0),
                SunDirection = Vector3.Normalize(value: new Vector3(x: 0f, y: -1f, z: 0f)),
                SunDiscIntensity = 0f,
                Zenith = Srgb(hex: 0x04050C),
            },
            "sky-day" => wind with {
                CloudColor = Srgb(hex: 0xFFFFFF),
                CloudCoverage = 0.45f,
                Ground = Srgb(hex: 0x6E7A88),
                Horizon = Srgb(hex: 0xBFD8F5),
                StarBrightness = 0f,
                SunColor = Srgb(hex: 0xFFF6E8),
                SunDirection = Vector3.Normalize(value: new Vector3(x: 0.2f, y: 1f, z: -0.3f)),
                SunDiscIntensity = 8f,
                SunDiscRadians = 0.03f,
                Zenith = Srgb(hex: 0x3F7FDF),
            },
            _ => wind,
        });
    }
    private static Vector3 Srgb(uint hex) => new(
        x: MathF.Pow(x: (((hex >> 16) & 0xFFu) / 255f), y: 2.2f),
        y: MathF.Pow(x: (((hex >> 8) & 0xFFu) / 255f), y: 2.2f),
        z: MathF.Pow(x: ((hex & 0xFFu) / 255f), y: 2.2f)
    );
    // Equirectangular: longitude across, latitude down, so one image carries the whole sphere.
    private static Vector4 RayDirection(int x, int y) {
        var latitude = ((MathF.PI * 0.5f) - ((((((float)y) + 0.5f) / ((float)Height))) * MathF.PI));
        var longitude = (((((((float)x) + 0.5f) / ((float)Width))) * 2f) - 1f) * MathF.PI;
        var radius = MathF.Cos(x: latitude);

        return new Vector4(
            w: 0f,
            x: (radius * MathF.Sin(x: longitude)),
            y: MathF.Sin(x: latitude),
            z: (radius * -MathF.Cos(x: longitude))
        );
    }
    private static byte Encode(float value) => ((byte)Math.Clamp(
        max: 255,
        min: 0,
        value: ((int)((MathF.Pow(x: Math.Clamp(value: value, max: 1f, min: 0f), y: (1f / 2.2f)) * 255f) + 0.5f))
    ));
}
