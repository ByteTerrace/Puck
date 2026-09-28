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
}
