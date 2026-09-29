using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class PackLightingLawTests {
    [Fact]
    public void FifthLightHasItsOwnNativeRecord() {
        var lighting = new SdfLighting { LightCount = 5 };
        for (var i = 0; i < 5; i++) {
            lighting.SetLight(i, new(SdfLightKind.Directional, Vector3.UnitZ, Vector3.One,
                i == 4 ? 0.9f : 0.1f * i, 0.1f, false));
        }
        var upload = new SdfLightingUpload();
        upload.Pack(lighting);
        Assert.Equal(5u, upload.LightFrame.Count);
        Assert.Equal(Vector3.UnitZ, upload.Lights[4].Direction);
        Assert.Equal(0.9f, upload.Lights[4].Weight);
    }

    [Fact]
    public void OccluderPositionAndAnchorPackWithoutDirectionNormalization() {
        var lighting = new SdfLighting { LightCount = 1 };
        lighting.SetLight(0, new(SdfLightKind.Occluder, new(12f, 3f, -4f), Vector3.Zero, 0.6f, 2.5f, false, 7));
        var upload = new SdfLightingUpload();
        upload.Pack(lighting);
        var light = upload.Lights[0];
        Assert.Equal(new Vector3(12f, 3f, -4f), light.Direction);
        Assert.Equal(0.6f, light.Weight);
        Assert.Equal(4u, light.Kind);
        Assert.Equal(2.5f, light.Parameter);
        Assert.Equal(7, light.DynamicSlot);
    }
}
