using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfViewQualityLawTests {
    [InlineData(0f, 0f, 1f)]
    [InlineData(0f, 0.25f, 0.25f)]
    [InlineData(0.5f, 0f, 0.5f)]
    [InlineData(0f, 2f, 1f)]
    [InlineData(2f, 0f, 1f)]
    [InlineData(0.5f, 0.25f, 0.25f)]
    [Theory]
    public void RestrictionTakesTheShorterEffectiveShadowReach(float left, float right, float expected) {
        var quality = new SdfViewQuality { ShadowDistanceScale = left }.Restrict(other: new SdfViewQuality { ShadowDistanceScale = right });

        Assert.Equal(actual: ((quality.ShadowDistanceScale == 0f) ? 1f : quality.ShadowDistanceScale), expected: expected);
    }
    [Fact]
    public void ACameraInheritsItsSelectedHostsQualityAndAddsEveryRestriction() {
        var inherited = new SdfViewQuality {
            DisableFarBound = true,
            ShadowDistanceScale = 0.5f,
            UseCameraTileShadowMask = true,
            UseFastSoftShadowMarch = true,
        };
        var own = new SdfViewQuality {
            DisableAmbientOcclusion = true,
            DisableSoftShadows = true,
            ShadowDistanceScale = 0.25f,
            UseFastAmbientOcclusion = true,
        };
        var expected = new SdfViewQuality {
            DisableAmbientOcclusion = true,
            DisableFarBound = true,
            DisableSoftShadows = true,
            ShadowDistanceScale = 0.25f,
            UseCameraTileShadowMask = true,
            UseFastAmbientOcclusion = true,
            UseFastSoftShadowMarch = true,
        };
        var view = new SdfViewSnapshot(Camera: default, Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f));
        var frame = new SdfFrame(
            Program: new SdfProgramBuilder().Build(),
            ProgramChanged: true,
            Time: 3f,
            Views: [view, view with { Quality = inherited }]
        );
        var source = new SdfCameraFrameSource(host: new FixedFrameSource(frame: frame)) {
            Camera = default(CameraSnapshot),
            HostFrame = frame,
            HostView = 1,
            Quality = own,
        };
        var filmed = source.CaptureFrame(deltaSeconds: 0f, height: 36u, interpolationAlpha: 0f, width: 64u);

        Assert.Equal(actual: Assert.Single(collection: filmed.Views).Quality, expected: expected);
        Assert.Equal(actual: own.Restrict(other: inherited), expected: expected);
        Assert.Same(actual: filmed.Program, expected: frame.Program);
        Assert.Equal(actual: filmed.Time, expected: frame.Time);
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => frame;
    }
}
