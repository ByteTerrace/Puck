using Puck.HumbleGamingBrick.Forge.Games;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>
/// The forge's standing gate: the worked-example cart assembles, its bytes are identical across builds (the forge
/// holds no wall clock or RNG), and the built ROM survives the "verify by running" pass on a real Humble machine.
/// </summary>
public sealed class ArcadeQuestForgeTests {
    [Fact]
    public void Build_IsByteIdenticalAcrossRuns() {
        var first = ArcadeQuestRom.Build();
        var second = ArcadeQuestRom.Build();

        Assert.Equal(actual: second, expected: first);
    }
    [Fact]
    public void Build_ThenVerify_PassesOnARealMachine() {
        var rom = ArcadeQuestRom.Build();

        Assert.Equal(expected: (32 * 1024), actual: rom.Length);
        ArcadeQuestRom.Verify(rom: rom);
    }
}
