using System.Numerics;
using Puck.Assets;
using Puck.ShaderVm.Programs;
using Xunit;

namespace Puck.ShaderVm.Tests;

// null.world.json rendered end to end by the Shader VM: one program for the distance field, one for the sky the
// misses shade with. Sphere-traced on the host, so the picture is the reference the GPU interpreter owes.
public sealed class NullWorldPreview {
    private const float Epsilon = 0.0015f;
    private const int Height = 540;
    private const int MaxSteps = 160;
    private const int Width = 960;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RendersTheDocument(bool overview) {
        var directory = Environment.GetEnvironmentVariable(variable: "PUCK_SKY_PREVIEW_DIR");

        Assert.SkipWhen(condition: string.IsNullOrEmpty(value: directory), reason: "Opt-in harness: set PUCK_SKY_PREVIEW_DIR to the directory the preview is written to.");

        var field = ShaderExpressionCompiler.Compile(root: NullWorldScene.Build(point: ShaderExpression.Input(input: ShaderInput.Coordinate)));
        var sky = SkyProgram.Compile();
        var statistics = ShaderProgramStatistics.Measure(program: field);
        var parameters = new Vector4[SkyParameters.Count];
        var settings = new SkyFrameSettings {
            CloudDrift = new Vector2(x: 3.1f, y: -1.4f),
            CloudSpin = 0.35f,
        };

        SkyParameters.Pack(rows: parameters, settings: in settings);

        var palette = NullWorldScene.Palette;
        var pixels = new byte[Width * Height * 4];
        var sun = Vector3.Normalize(value: settings.SunDirection);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var evaluations = 0L;

        _ = Parallel.For(body: y => {
            var local = 0L;

            for (var x = 0; (x < Width); x++) {
                var direction = RayDirection(overview: overview, x: x, y: y);
                var color = Trace(
                    direction: direction,
                    origin: Origin(overview: overview),
                    evaluations: ref local,
                    field: field,
                    palette: palette,
                    parameters: parameters,
                    sky: sky,
                    sun: sun
                );
                var offset = (((y * Width) + x) * 4);

                pixels[(offset + 0)] = Encode(value: color.X);
                pixels[(offset + 1)] = Encode(value: color.Y);
                pixels[(offset + 2)] = Encode(value: color.Z);
                pixels[(offset + 3)] = 255;
            }

            _ = Interlocked.Add(location1: ref evaluations, value: local);
        }, fromInclusive: 0, toExclusive: Height);

        stopwatch.Stop();
        PngEncoder.Write(
            height: Height,
            path: Path.Combine(path1: directory!, path2: (overview ? "null-world-overview.png" : "null-world.png")),
            rgba: pixels,
            width: Width
        );
        File.WriteAllText(
            contents: $"field: instructions={statistics.InstructionCount} constants={field.ConstantCount} stack={statistics.StackDepth} locals={statistics.LocalCount}\n"
                + $"sky:   instructions={sky.InstructionCount}\n"
                + $"trace: fieldEvaluations={evaluations} perPixel={(((double)evaluations) / (Width * Height)):F1} milliseconds={stopwatch.ElapsedMilliseconds}\n"
                + $"cost:  fieldInstructionsPerPixel={((((double)evaluations) / (Width * Height)) * statistics.InstructionCount):F0}\n",
            path: Path.Combine(path1: directory!, path2: (overview ? "null-world-overview.txt" : "null-world.txt"))
        );
    }
    private static Vector3 Trace(ShaderProgram field, ShaderProgram sky, Vector3 direction, Vector3 origin, Vector3 sun, Vector4[] parameters, Vector3[] palette, ref long evaluations) {
        var travelled = 0f;

        for (var step = 0; (step < MaxSteps); step++) {
            var sample = Field(evaluations: ref evaluations, field: field, point: (origin + (direction * travelled)));

            if (sample.X < Epsilon) {
                var point = (origin + (direction * travelled));
                var normal = Normal(evaluations: ref evaluations, field: field, point: point);
                var albedo = palette[Math.Clamp(max: (palette.Length - 1), min: 0, value: ((int)(sample.Y + 0.5f)))];
                var lit = Math.Clamp(value: Vector3.Dot(vector1: normal, vector2: sun), max: 1f, min: 0f);

                return (albedo * (0.28f + (0.9f * lit)));
            }

            travelled += Math.Max(val1: sample.X, val2: Epsilon);

            if (travelled > 80f) {
                break;
            }
        }

        return Sky(direction: direction, parameters: parameters, sky: sky);
    }
    private static Vector4 Field(ShaderProgram field, Vector3 point, ref long evaluations) {
        evaluations++;

        var context = new ShaderContext(Coordinate: new Vector4(x: point.X, y: point.Y, z: point.Z, w: 0f));

        return ShaderInterpreter.Evaluate(context: in context, parameters: [], program: field);
    }
    private static Vector3 Normal(ShaderProgram field, Vector3 point, ref long evaluations) {
        var offset = 0.0006f;
        var x = (Field(evaluations: ref evaluations, field: field, point: (point + new Vector3(x: offset, y: 0f, z: 0f))).X - Field(evaluations: ref evaluations, field: field, point: (point - new Vector3(x: offset, y: 0f, z: 0f))).X);
        var y = (Field(evaluations: ref evaluations, field: field, point: (point + new Vector3(x: 0f, y: offset, z: 0f))).X - Field(evaluations: ref evaluations, field: field, point: (point - new Vector3(x: 0f, y: offset, z: 0f))).X);
        var z = (Field(evaluations: ref evaluations, field: field, point: (point + new Vector3(x: 0f, y: 0f, z: offset))).X - Field(evaluations: ref evaluations, field: field, point: (point - new Vector3(x: 0f, y: 0f, z: offset))).X);
        var gradient = new Vector3(x: x, y: y, z: z);

        return ((gradient.LengthSquared() > 0f)
            ? Vector3.Normalize(value: gradient)
            : Vector3.UnitY
        );
    }
    private static Vector3 Sky(ShaderProgram sky, Vector3 direction, Vector4[] parameters) {
        var context = new ShaderContext(Coordinate: new Vector4(x: direction.X, y: direction.Y, z: direction.Z, w: 0f));
        var color = ShaderInterpreter.Evaluate(context: in context, parameters: parameters, program: sky);

        return new Vector3(x: color.X, y: color.Y, z: color.Z);
    }
    // The document's seatRig: an orbit at distance 5.4626 and pitch 0.4145 about an anchor one unit above the body.
    private static Vector3 Origin(bool overview) => (overview
        ? new Vector3(x: 17f, y: 16f, z: 30f)
        : (new Vector3(x: 0f, y: 1f, z: 0f) + new Vector3(
            x: 0f,
            y: (MathF.Sin(x: 0.4145069f) * 5.4626001f),
            z: (MathF.Cos(x: 0.4145069f) * 5.4626001f)
        ))
    );
    private static Vector3 Target(bool overview) => (overview
        ? new Vector3(x: 0f, y: 11f, z: 0f)
        : new Vector3(x: 0f, y: 1f, z: 0f)
    );
    private static Vector4 RayDirection(int x, int y, bool overview) {
        var aspect = (((float)Width) / ((float)Height));
        var tangent = MathF.Tan(x: ((overview ? 1.25f : 0.9599311f) * 0.5f));
        var forward = Vector3.Normalize(value: (Target(overview: overview) - Origin(overview: overview)));
        var right = Vector3.Normalize(value: Vector3.Cross(vector1: forward, vector2: Vector3.UnitY));
        var up = Vector3.Cross(vector1: right, vector2: forward);
        var ndcX = ((((((float)x) + 0.5f) / ((float)Width)) * 2f) - 1f);
        var ndcY = (1f - ((((((float)y) + 0.5f) / ((float)Height))) * 2f));
        var direction = Vector3.Normalize(value: (forward + (right * ((ndcX * aspect) * tangent)) + (up * (ndcY * tangent))));

        return new Vector4(x: direction.X, y: direction.Y, z: direction.Z, w: 0f);
    }
    private static byte Encode(float value) => ((byte)Math.Clamp(
        max: 255,
        min: 0,
        value: ((int)((MathF.Pow(x: Math.Clamp(value: value, max: 1f, min: 0f), y: (1f / 2.2f)) * 255f) + 0.5f))
    ));
    private static Vector3 Trace(ShaderProgram field, ShaderProgram sky, Vector4 direction, Vector3 origin, Vector3 sun, Vector4[] parameters, Vector3[] palette, ref long evaluations) => Trace(
        direction: new Vector3(x: direction.X, y: direction.Y, z: direction.Z),
        origin: origin,
        evaluations: ref evaluations,
        field: field,
        palette: palette,
        parameters: parameters,
        sky: sky,
        sun: sun
    );
}
