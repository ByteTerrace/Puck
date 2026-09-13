namespace Puck.GamingBricks.Tests;

/// <summary>Pins the common firmware-selection grammar independently of either emulated machine.</summary>
public sealed class MachineBootOptionsTests {
    [InlineData("cold fast")]
    [InlineData("fast cold bios=puck")]
    [InlineData("bios=")]
    [InlineData("bios=\"\"")]
    [Theory]
    public void AmbiguousOrEmptySelectionsAreRefused(string input) =>
        Assert.Throws<ArgumentException>(testCode: () => MachineBootOptions.Parse(
            options: input,
            machineTokens: out _
        ));
    [Fact]
    public void DefaultsSelectColdPuckFirmware() {
        var options = MachineBootOptions.Parse(
            options: null,
            machineTokens: out var tokens
        );

        Assert.Equal(
            expected: MachineBootMode.Cold,
            actual: options.Mode
        );
        Assert.Null(@object: options.ImagePath);
        Assert.Empty(collection: tokens);
    }
    [InlineData("cgb fast dmgspeed bios=C:\\Firmware Files\\boot.bin", "C:\\Firmware Files\\boot.bin")]
    [InlineData("cgb FAST dmgspeed bios=\"C:\\Firmware Files\\boot.bin\"", "C:\\Firmware Files\\boot.bin")]
    [Theory]
    public void ExternalPathsConsumeTheFinalArgument(string input, string path) {
        var options = MachineBootOptions.Parse(
            options: input,
            machineTokens: out var tokens
        );

        Assert.Equal(
            expected: MachineBootMode.Fast,
            actual: options.Mode
        );
        Assert.Equal(
            expected: path,
            actual: options.ImagePath
        );
        Assert.Equal(
            actual: tokens,
            expected: new[] { "cgb", "dmgspeed" }
        );
        var roundTrip = MachineBootOptions.Parse(
            options: options.Format(),
            machineTokens: out var remaining
        );

        Assert.Equal(
            actual: roundTrip,
            expected: options
        );
        Assert.Empty(collection: remaining);
    }
    [Fact]
    public void HardwareOnlyReconfigurationRetainsExplicitFirmwareAndStartup() {
        var original = new MachineBootOptions(
            ImagePath: "external.bin",
            Mode: MachineBootMode.Fast
        );
        var unchanged = MachineBootOptions.Parse(
            defaults: original,
            machineTokens: out _,
            options: "cgb"
        );

        Assert.Equal(
            actual: unchanged,
            expected: original
        );
        var replaced = MachineBootOptions.Parse(
            defaults: original,
            machineTokens: out _,
            options: "bios=puck"
        );

        Assert.Equal(
            expected: MachineBootMode.Fast,
            actual: replaced.Mode
        );
        Assert.Null(@object: replaced.ImagePath);
    }
    [Fact]
    public void SkippingStartupDoesNotRequireAnExternalImage() {
        var options = MachineBootOptions.Parse(
            options: "fast bios=puck",
            machineTokens: out _
        );

        Assert.Equal(
            expected: new MachineBootOptions(
                ImagePath: null,
                Mode: MachineBootMode.Fast
            ),
            actual: options
        );
    }
}
