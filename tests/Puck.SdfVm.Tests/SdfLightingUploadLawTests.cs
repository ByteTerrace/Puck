using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Every old decoded lighting value has the same meaning in the native records. Distinct sentinels expose
/// exchanged fields, and the host's direction and disc bakes retain their previous rounding.</summary>
public sealed class SdfLightingUploadLawTests {
    private static SdfEnvironment Sample() {
        var environment = new SdfEnvironment {
            LightCount = 8, SkyEnabled = true, SkyStopCount = 4, SoftboxCount = 4,
            FogDensity = 0.011f, CurvatureCavity = 0.17f, CurvatureRim = 0.23f,
            CurvatureInk = 0.37f, CurvatureInkLow = 5f, CurvatureInkHigh = 19f,
            CurvatureInkColor = new(0.13f, 0.29f, 0.41f),
            SunDiscLightIndex = 0, SunDiscRadians = 0.037f, SunDiscIntensity = 1.7f,
            StarDensity = 59f, StarBrightness = 1.3f, StarSeed = 7u,
            TwinkleShare = 0.31f, TwinkleDepth = 0.67f, TwinklePhase = 0.79f,
            CloudColor = new(0.19f, 0.43f, 0.71f), CloudCoverage = 0.53f,
            CloudSoftness = 0.27f, CloudScale = 3.1f, CloudSeed = 31u,
            CloudOffset = new(3.25f, -4.75f), CloudShearOffset = new(-1.25f, 7.5f),
            CloudSpinAngle = 1.37f, CloudCurl = 0.73f,
            HorizonLow = new(0.07f, 0.11f, 0.23f), HorizonHigh = new(0.47f, 0.59f, 0.83f),
        };
        for (var index = 0; index < environment.LightCount; index++) {
            environment.SetLight(index, new((SdfLightKind)(index % 5), new(index + 2f, index - 3f, index + 5f),
                new(0.1f + index, 0.2f + index, 0.3f + index), 0.41f + index, 0.13f + index,
                index == 5, DynamicSlot: index + 17));
        }
        for (var index = 0; index < 4; index++) {
            environment.SetSkyStop(index, new(0.01f + index, 0.03f + index, 0.09f + index), -1f + (index * 0.5f));
            environment.SetSoftbox(index, new(new(1f + index, 2f + index, 3f + index),
                new(0.05f + index, 0.07f + index, 0.11f + index), 0.17f + index,
                new(0.23f + index, 0.31f + index), 0.43f + index));
        }
        return environment;
    }

    private static float[] PreviousLayout(SdfEnvironment environment) {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Albedo: Vector3.One));
        builder.Sphere(1f, material);
        var rows = new float[53 * 4];
        SdfFrameBlock.BakeEnvironment(new(builder.Build(), true, [], 0f) { Environment = environment }, rows);
        return rows;
    }

    private static Vector3 Triple(float[] rows, int index) => new(rows[index], rows[index + 1], rows[index + 2]);

    [Fact]
    public void Native_records_preserve_every_previously_decoded_value_and_zero_their_padding() {
        var environment = Sample();
        var previous = PreviousLayout(environment);
        var upload = new SdfLightingUpload();
        upload.Pack(environment);
        Assert.Equal(new SdfLightFrameData { Count = (uint)previous[0], ShadowIndex = (int)previous[1] }, upload.LightFrame);
        for (var index = 0; index < 8; index++) {
            var offset = 4 + (index * 12);
            Assert.Equal(new SdfLightData {
                Direction = Triple(previous, offset), Weight = previous[offset + 3],
                Color = Triple(previous, offset + 4), Kind = (uint)previous[offset + 7],
                Parameter = previous[offset + 8], Shadows = previous[offset + 9] > 0.5f ? 1u : 0u,
                DynamicSlot = (int)previous[offset + 10],
            }, upload.Lights[index]);
        }
        for (var index = 0; index < 4; index++) {
            var stop = 112 + (index * 4);
            Assert.Equal(new SdfSkyStopData { Color = Triple(previous, stop), Elevation = previous[stop + 3] }, upload.Stops[index]);
            var box = 156 + (index * 12);
            Assert.Equal(new SdfSoftboxData {
                Direction = Triple(previous, box), Weight = previous[box + 3], Color = Triple(previous, box + 4),
                Size = new(previous[box + 7], previous[box + 8]), Blur = previous[box + 9],
            }, upload.Softboxes[index]);
        }
        var shadow = 4 + ((int)previous[1] * 12);
        var disc = 4 + ((int)previous[109] * 12);
        Assert.Equal(new SdfSkyFrameData {
            SkyEnabled = previous[2] > 0.5f ? 1u : 0u, FogDensity = previous[3],
            StopCount = (uint)previous[108], SoftboxCount = (uint)previous[152],
            CurvatureCavity = previous[100], CurvatureRim = previous[101], CurvatureInk = previous[102],
            CurvatureInkLow = previous[103], CurvatureInkColor = Triple(previous, 104), CurvatureInkHigh = previous[107],
            SunDiscDirection = Triple(previous, disc), SunDiscIntensity = previous[111],
            SunDiscExponent = previous[110], SunDiscEnabled = 1u,
            CloudLightDirection = Triple(previous, shadow), CloudLightColor = Triple(previous, shadow + 4),
            StarDensity = previous[128], StarBrightness = previous[129], StarSeed = (uint)previous[130],
            TwinkleShare = previous[132], TwinkleDepth = previous[133], TwinklePhase = previous[134],
            CloudColor = Triple(previous, 136), CloudCoverage = previous[139],
            CloudSoftness = previous[140], CloudScale = previous[141], CloudSeed = (uint)previous[142],
            CloudOffset = new(previous[144], previous[145]), CloudShearOffset = new(previous[146], previous[147]),
            CloudSpinAngle = previous[148], CloudCurl = previous[149],
            HorizonLow = Triple(previous, 204), HorizonHigh = Triple(previous, 208),
        }, upload.SkyFrame);
    }
}
