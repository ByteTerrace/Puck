using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Every old decoded lighting value has the same meaning in the native records. Distinct sentinels expose
/// exchanged fields, and the host's direction and disc bakes retain their previous rounding.</summary>
public sealed class SdfLightingUploadLawTests {
    private static SdfLighting Sample() {
        var environment = new SdfLighting {
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

    // The distinct-sentinel fixture's 53-row reference bytes pin each decoded value independently of the native packer.
    private static float[] PreviousLayout() => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(
        Convert.FromHexString(
            "000000410000A0400000803F5839343C5F1DA63E0F2CF9BEB7A44F3F85EBD13ECDCCCC3DCDCC4C3E9A99993E00000000B81E053E000000000000000000000000" +
            "00004040000000C00000C040E17AB43FCDCC8C3F9A99993F6666A63F0000803FD7A3903F00000000000000000000000000008040000080BF0000E040713D1A40" +
            "66660640CDCC0C403333134000000040EC5108400000000000000000000000000000A0400000000000000041713D5A4066664640CDCC4C403333534000004040" +
            "EC514840000000000000A041000000000000C0400000803F00001041B81E8D4033338340666686409A99894000008040F6288440000000000000A84100000000" +
            "E7DF103F2C92253EB7F64E3FB81EAD403333A3406666A6409A99A94000000000F628A4400000803F0000000000000000000000410000404000003041B81ECD40" +
            "3333C3406666C6409A99C9400000803FF628C440000000000000000000000000000010410000804000004041B81EED403333E3406666E6409A99E94000000040" +
            "F628E4400000000000000000000000007B142E3E1F856B3EA470BD3E0000A040B81E053EE17A943E85EBD13E000098410000804000000000B6197D449A99D93F" +
            "0AD7233C8FC2F53CEC51B83D000080BFAE47813F0AD7833F1F858B3F000000BFD7A3004085EB01408FC2054000000000D7A3404085EB41408FC245400000003F" +
            "00006C426666A63F0000E0400000000052B89E3E1F852B3F713D4A3F000000005C8F423EF628DC3E8FC2353F14AE073F713D8A3E666646400000F84100000000" +
            "00005040000098C00000A0BF0000F040295CAF3F48E13A3F00000000000000000000804000000000000000000000000077D6883E77D6083FB3414D3F7B142E3E" +
            "CDCC4C3D295C8F3DAE47E13D1F856B3E52B89E3EF628DC3E0000000000000000EB26BE3E309D0E3FEB263E3F8FC2953F6666863FC3F5883F7B148E3FA4709D3F" +
            "14AEA73F3E0AB73F00000000000000002439D93EC3D0103FF304353F48E10A4033330340E17A04403D0A074052B80E400AD713401F851B400000000000000000" +
            "2A64E93E9ADE113F1F0B2F3F48E14A4033334340E17A44403D0A474052B84E400AD753401F855B400000000000000000295C8F3DAE47E13D1F856B3E00000000" +
            "D7A3F03E3D0A173FE17A543F00000000")).ToArray();

    private static Vector3 Triple(float[] rows, int index) => new(rows[index], rows[index + 1], rows[index + 2]);

    [Fact]
    public void Native_records_preserve_every_previously_decoded_value_and_zero_their_padding() {
        var environment = Sample();
        var previous = PreviousLayout();
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
