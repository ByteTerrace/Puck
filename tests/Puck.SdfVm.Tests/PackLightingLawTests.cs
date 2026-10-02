using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class PackLightingLawTests {
    [Fact]
    public void FifthLightHasItsOwnNativeRecord() {
        var lighting = new SdfLighting { LightCount = 5 };

        for (var i = 0; (i < 5); i++) {
            lighting.SetLight(index: i, light: new(SdfLightKind.Directional, Vector3.UnitZ, Vector3.One,
                ((i == 4) ? 0.9f : (0.1f * i)), 0.1f, false));
        }
        var upload = new SdfLightingUpload();

        upload.Pack(environment: lighting);
        Assert.Equal(5u, upload.LightFrame.Count);
        Assert.Equal(Vector3.UnitZ, upload.Lights[4].Direction);
        Assert.Equal(0.9f, upload.Lights[4].Weight);
    }
    [Fact]
    public void OccluderPositionAndAnchorPackWithoutDirectionNormalization() {
        var lighting = new SdfLighting { LightCount = 1 };

        lighting.SetLight(index: 0, light: new(SdfLightKind.Occluder, new(x: 12f, y: 3f, z: -4f), Vector3.Zero, 0.6f, 2.5f, false, 7));
        var upload = new SdfLightingUpload();

        upload.Pack(environment: lighting);
        var light = upload.Lights[0];

        Assert.Equal(new Vector3(x: 12f, y: 3f, z: -4f), light.Direction);
        Assert.Equal(actual: light.Weight, expected: 0.6f);
        Assert.Equal(actual: light.Kind, expected: 4u);
        Assert.Equal(actual: light.Parameter, expected: 2.5f);
        Assert.Equal(actual: light.DynamicSlot, expected: 7);
    }
}
