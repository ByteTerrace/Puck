using System.Numerics;

using Puck.SignedDistance;

using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Exercises <see cref="SdfFrameBlock.BakeEnvironment"/>, which needs no GPU device, closing the gap between
/// <see cref="SdfEnvironment"/>'s own lane table (proved elsewhere) and the exact rows every pass block carries for the
/// shader to read.</summary>
public sealed class PackEnvironmentLawTests {
    private static float[] PackedFloats(SdfEnvironment environment) {
        var floats = new float[SdfEnvironment.LaneCount];
        var frame = new SdfFrame(
            Program: TinyProgram(),
            ProgramChanged: true,
            Views: [],
            Time: 0f
        ) {
            Environment = environment,
        };

        SdfFrameBlock.BakeEnvironment(
            frame: frame,
            rows: floats
        );

        return floats;
    }
    private static SdfProgram TinyProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        builder.Sphere(
            radius: 1f,
            material: material
        );

        return builder.Build();
    }

    [Fact]
    public void FifthLight_PacksIntoTheUploadedFloatBuffer_AtItsOwnRow() {
        var environment = new SdfEnvironment();

        environment.LightCount = 5;
        environment.SetLight(
            index: 0,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: new Vector3(
                    x: 0f,
                    y: 1f,
                    z: 0f
                ),
                Color: Vector3.One,
                Weight: 0.1f,
                Param: 0.1f,
                Shadows: true
            )
        );
        environment.SetLight(
            index: 1,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: new Vector3(
                    x: 1f,
                    y: 0f,
                    z: 0f
                ),
                Color: Vector3.One,
                Weight: 0.2f,
                Param: 0.1f,
                Shadows: false
            )
        );
        environment.SetLight(
            index: 2,
            light: new SdfLight(
                Kind: SdfLightKind.Hemisphere,
                Direction: Vector3.Zero,
                Color: Vector3.One,
                Weight: 0.3f,
                Param: 0.25f,
                Shadows: false
            )
        );
        environment.SetLight(
            index: 3,
            light: new SdfLight(
                Kind: SdfLightKind.Rim,
                Direction: Vector3.Zero,
                Color: Vector3.One,
                Weight: 0.4f,
                Param: 2f,
                Shadows: false
            )
        );
        environment.SetLight(
            index: 4,
            light: new SdfLight(
                Kind: SdfLightKind.Directional,
                Direction: new Vector3(
                    x: 0f,
                    y: 0f,
                    z: 1f
                ),
                Color: Vector3.One,
                Weight: 0.9f,
                Param: 0.1f,
                Shadows: false
            )
        );

        var floats = PackedFloats(environment: environment);
        var row = ((SdfEnvironment.LightsRow + (4 * SdfEnvironment.RowsPerLight)) * 4);

        Assert.Equal(
            expected: 5f,
            actual: floats[(SdfEnvironment.ControlRow * 4)]
        );
        Assert.Equal(
            expected: 0f,
            actual: floats[(row + 0)]
        );
        Assert.Equal(
            expected: 0f,
            actual: floats[(row + 1)]
        );
        Assert.Equal(
            expected: 1f,
            actual: floats[(row + 2)]
        );
        Assert.Equal(
            expected: 0.9f,
            actual: floats[(row + 3)]
        );
    }
    [Fact]
    public void OccluderPositionAndAnchorPackWithoutDirectionNormalization() {
        var environment = new SdfEnvironment { LightCount = 1 };

        environment.SetLight(
            index: 0,
            light: new(
                SdfLightKind.Occluder,
                new(
                    x: 12f,
                    y: 3f,
                    z: -4f
                ),
                Vector3.Zero,
                0.6f,
                2.5f,
                false,
                7
            )
        );
        var floats = PackedFloats(environment: environment);
        var row = (SdfEnvironment.LightsRow * 4);

        Assert.Equal(
            12f,
            floats[row]
        );
        Assert.Equal(
            3f,
            floats[(row + 1)]
        );
        Assert.Equal(
            -4f,
            floats[(row + 2)]
        );
        Assert.Equal(
            0.6f,
            floats[(row + 3)]
        );
        Assert.Equal(
            4f,
            floats[(row + 7)]
        );
        Assert.Equal(
            2.5f,
            floats[(row + 8)]
        );
        Assert.Equal(
            7f,
            floats[(row + 10)]
        );
    }
}
