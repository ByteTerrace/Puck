using System.Numerics;
using System.Reflection;

using Puck.SignedDistance;

using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Exercises <c>SdfWorldEngine.PackEnvironment</c> (private, reflection-invoked — it needs no GPU device)
/// directly, closing the gap between <see cref="SdfEnvironment"/>'s own lane table (proved elsewhere) and the exact
/// bytes the engine uploads for the shader to read.</summary>
public sealed class PackEnvironmentLawTests {
    private static SdfProgram TinyProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));

        builder.Sphere(radius: 1f, material: material);

        return builder.Build();
    }
    private static float[] PackedFloats(SdfEnvironment environment) {
        var packEnvironment = typeof(SdfWorldEngine).GetMethod(
            name: "PackEnvironment",
            bindingAttr: (BindingFlags.NonPublic | BindingFlags.Static)
        ) ?? throw new InvalidOperationException(message: "SdfWorldEngine.PackEnvironment not found.");
        var screenLightByteLengthField = typeof(SdfWorldEngine).GetField(
            name: "ScreenLightByteLength",
            bindingAttr: (BindingFlags.NonPublic | BindingFlags.Static)
        ) ?? throw new InvalidOperationException(message: "SdfWorldEngine.ScreenLightByteLength not found.");
        var byteLength = (int)screenLightByteLengthField.GetValue(obj: null)!;
        var floats = new float[(byteLength / sizeof(float))];
        var frame = new SdfFrame(
            Program: TinyProgram(),
            ProgramChanged: true,
            Views: [],
            Time: 0f,
            WarpAmount: 0f
        ) {
            Environment = environment,
        };

        // PackEnvironment(SdfFrame frame, Span<float> floats) — MethodInfo.Invoke cannot box a Span, so call through
        // a delegate built from the open method instead.
        var del = (PackEnvironmentDelegate)packEnvironment.CreateDelegate(delegateType: typeof(PackEnvironmentDelegate));

        del(frame, floats.AsSpan());

        return floats;
    }
    private delegate void PackEnvironmentDelegate(SdfFrame frame, Span<float> floats);

    [Fact]
    public void FifthLight_PacksIntoTheUploadedFloatBuffer_AtItsOwnRow() {
        var environment = new SdfEnvironment();

        environment.LightCount = 5;
        environment.SetLight(index: 0, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: new Vector3(x: 0f, y: 1f, z: 0f), Color: Vector3.One, Weight: 0.1f, Param: 0.1f, Shadows: true));
        environment.SetLight(index: 1, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: new Vector3(x: 1f, y: 0f, z: 0f), Color: Vector3.One, Weight: 0.2f, Param: 0.1f, Shadows: false));
        environment.SetLight(index: 2, light: new SdfLight(Kind: SdfLightKind.Hemisphere, Direction: Vector3.Zero, Color: Vector3.One, Weight: 0.3f, Param: 0.25f, Shadows: false));
        environment.SetLight(index: 3, light: new SdfLight(Kind: SdfLightKind.Rim, Direction: Vector3.Zero, Color: Vector3.One, Weight: 0.4f, Param: 2f, Shadows: false));
        environment.SetLight(index: 4, light: new SdfLight(Kind: SdfLightKind.Directional, Direction: new Vector3(x: 0f, y: 0f, z: 1f), Color: Vector3.One, Weight: 0.9f, Param: 0.1f, Shadows: false));

        var floats = PackedFloats(environment: environment);
        var envBase = ((SdfProgramBuilder.MaxScreenSurfaces + 8) * 4);
        var row = (envBase + ((SdfEnvironment.LightsRow + (4 * SdfEnvironment.RowsPerLight)) * 4));

        Assert.Equal(expected: 5f, actual: floats[(envBase + 0)]);
        Assert.Equal(expected: 0f, actual: floats[(row + 0)]);
        Assert.Equal(expected: 0f, actual: floats[(row + 1)]);
        Assert.Equal(expected: 1f, actual: floats[(row + 2)]);
        Assert.Equal(expected: 0.9f, actual: floats[(row + 3)]);
    }
}
