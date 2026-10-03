using System.Numerics;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The K word preserves four independent eight-bit shadow visibilities without changing record size.</summary>
public sealed class SdfShadowVisibilityLawTests {
    [Fact]
    public void EveryEightBitCodePacksAndUnpacksInEachIndependentSlot() {
        Assert.Equal(actual: SdfVisibility.ShadowBits, expected: 8);
        Assert.Equal(actual: SdfVisibility.ShadowMask, expected: 255u);

        for (var slot = 0; (slot < 4); slot++) {
            for (var code = 0; (code < 256); code++) {
                byte[] codes = [17, 61, 139, 233];

                codes[slot] = ((byte)code);
                var word = ((uint)codes[0]) | (((uint)codes[1]) << 8) | (((uint)codes[2]) << 16) | (((uint)codes[3]) << 24);
                var visibility = new Vector4(x: (codes[0] / 255f), y: (codes[1] / 255f), z: (codes[2] / 255f), w: (codes[3] / 255f));

                Assert.Equal(expected: word, actual: SdfVisibility.PackShadows(visibility: visibility));
                for (var lane = 0; (lane < 4); lane++) {
                    Assert.Equal(expected: (codes[lane] / 255f), actual: SdfVisibility.ShadowAt(slot: lane, word: word));
                }
            }
        }
    }
    [Fact]
    public void PackingSaturatesAndRoundsMidpointsUpWithoutMixingSlots() {
        Assert.Equal(expected: 0xFFFF0000u,
            actual: SdfVisibility.PackShadows(visibility: new Vector4(w: 2f, x: -2f, y: 0f, z: 1f)));
        Assert.Equal(expected: 0xFF804001u,
            actual: SdfVisibility.PackShadows(visibility: new Vector4(w: (254.5f / 255f), x: (0.5f / 255f), y: (63.5f / 255f), z: (127.5f / 255f))));

        for (var code = 0; (code < 255); code++) {
            var visibility = new Vector4(w: 1f, x: ((code + 0.25f) / 255f),
                y: ((code + 0.5f) / 255f), z: ((code + 0.75f) / 255f));
            var expected = ((uint)code) | (((uint)(code + 1)) << 8) | (((uint)(code + 1)) << 16) | 0xFF000000u;

            Assert.Equal(expected: expected, actual: SdfVisibility.PackShadows(visibility: visibility));
        }
    }
}
