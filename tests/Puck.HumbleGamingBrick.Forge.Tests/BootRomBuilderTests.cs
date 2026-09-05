using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>
/// The authored boot ROMs' standing gate: every revision's image assembles to the same bytes on every build (the
/// builder solves its timing by booting the image, and that solve is deterministic), the image is the shape the
/// revision's overlay expects, and a machine that boots through it lands on the same observable handoff as one started
/// at the seeded post-boot state.
/// </summary>
public sealed class BootRomBuilderTests {
    public static TheoryData<ConsoleModel> Models {
        get {
            var models = new TheoryData<ConsoleModel>();

            foreach (var model in Enum.GetValues<ConsoleModel>()) {
                models.Add(model);
            }

            return models;
        }
    }

    [Theory]
    [MemberData(memberName: nameof(Models))]
    public void Build_IsByteIdenticalAcrossRuns(ConsoleModel model) {
        var first = BootRomBuilder.Build(model: model);
        var second = BootRomBuilder.Build(model: model);

        Assert.Equal(
            actual: second,
            expected: first
        );
    }
    [Theory]
    [MemberData(memberName: nameof(Models))]
    public void Build_FillsTheOverlayShape(ConsoleModel model) {
        var image = BootRomBuilder.Build(model: model);

        Assert.Equal(
            actual: image.Length,
            expected: (model.SupportsColor()
                ? BootRomBuilder.ColorLength
                : BootRomBuilder.MonochromeLength)
        );

        // The last two bytes of the low window are the unmap, so the program counter falls into the cartridge's entry
        // point rather than off the end of the overlay.
        Assert.Equal(
            actual: image[0x00FE],
            expected: (byte)0xE0
        );
        Assert.Equal(
            actual: image[0x00FF],
            expected: (byte)0x50
        );
    }
    [Theory]
    [MemberData(memberName: nameof(Models))]
    public void ColdBoot_ReachesTheSeededHandoff(ConsoleModel model) {
        var image = BootRomBuilder.Build(model: model);

        foreach (var (name, rom) in BootRomHandoffCases.Create()) {
            var difference = BootRomHandoff.Compare(
                bootRom: image,
                model: model,
                rom: rom
            );

            Assert.True(
                condition: (difference is null),
                userMessage: $"{model} booting \"{name}\": {difference}"
            );
        }
    }
}
