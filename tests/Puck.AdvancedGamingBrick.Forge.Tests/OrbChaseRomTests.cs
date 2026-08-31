using Puck.AdvancedGamingBrick.Forge.Games;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

// Build() runs the full OrbChaseVerify battery on a real machine before returning, so a rom in hand is already a
// boot/play/determinism proof; these tests pin the header contract and build-to-build byte identity on top.
public sealed class OrbChaseRomTests {
    [Fact]
    public void BuildProducesAVerifiedDirectBootImage() {
        var rom = OrbChaseRom.Build();

        Assert.Equal(expected: AgbForgeCartridge.RomSize, actual: rom.Length);
        Assert.Equal(expected: 0x96, actual: rom[0xB2]);

        var sum = 0;

        for (var offset = 0xA0; (offset <= 0xBC); offset++) {
            sum += rom[offset];
        }

        Assert.Equal(expected: ((byte)(((0 - sum) - 0x19) & 0xFF)), actual: rom[0xBD]);

        // Direct boot never reads the logo field; the default build leaves it zeroed.
        for (var offset = 0x04; (offset < (0x04 + AgbForgeCartridge.LogoLength)); offset++) {
            Assert.Equal(expected: 0, actual: rom[offset]);
        }
    }
    [Fact]
    public void BuildTwiceYieldsByteIdenticalRoms() {
        var first = OrbChaseRom.Build();
        var second = OrbChaseRom.Build();

        Assert.Equal(actual: second, expected: first);
    }
}
