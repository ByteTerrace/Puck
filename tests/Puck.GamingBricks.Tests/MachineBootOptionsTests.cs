namespace Puck.GamingBricks.Tests;

/// <summary>Pins the common firmware-selection grammar independently of either emulated machine.</summary>
public sealed class MachineBootOptionsTests {
    [Fact]
    public void DefaultsSelectColdPuckFirmware() {
        var options = MachineBootOptions.Parse(options: null, machineTokens: out var tokens);
        Assert.Equal(expected: MachineBootMode.Cold, actual: options.Mode);
        Assert.Null(@object: options.ImagePath);
        Assert.Empty(collection: tokens);
    }

    [Theory]
    [InlineData("cgb fast dmgspeed bios=C:\\Firmware Files\\boot.bin", "C:\\Firmware Files\\boot.bin")]
    [InlineData("cgb FAST dmgspeed bios=\"C:\\Firmware Files\\boot.bin\"", "C:\\Firmware Files\\boot.bin")]
    public void ExternalPathsConsumeTheFinalArgument(string input, string path) {
        var options = MachineBootOptions.Parse(options: input, machineTokens: out var tokens);
        Assert.Equal(expected: MachineBootMode.Fast, actual: options.Mode);
        Assert.Equal(expected: path, actual: options.ImagePath);
        Assert.Equal(expected: new[] { "cgb", "dmgspeed" }, actual: tokens);
        var roundTrip = MachineBootOptions.Parse(options: options.Format(), machineTokens: out var remaining);
        Assert.Equal(expected: options, actual: roundTrip);
        Assert.Empty(collection: remaining);
    }

    [Fact]
    public void SkippingStartupDoesNotRequireAnExternalImage() {
        var options = MachineBootOptions.Parse(options: "fast bios=puck", machineTokens: out _);
        Assert.Equal(expected: new MachineBootOptions(Mode: MachineBootMode.Fast, ImagePath: null), actual: options);
    }

    [Fact]
    public void HardwareOnlyReconfigurationRetainsExplicitFirmwareAndStartup() {
        var original = new MachineBootOptions(Mode: MachineBootMode.Fast, ImagePath: "external.bin");
        var unchanged = MachineBootOptions.Parse(options: "cgb", machineTokens: out _, defaults: original);
        Assert.Equal(expected: original, actual: unchanged);
        var replaced = MachineBootOptions.Parse(options: "bios=puck", machineTokens: out _, defaults: original);
        Assert.Equal(expected: MachineBootMode.Fast, actual: replaced.Mode);
        Assert.Null(@object: replaced.ImagePath);
    }

    [Theory]
    [InlineData("cold fast")]
    [InlineData("fast cold bios=puck")]
    [InlineData("bios=")]
    [InlineData("bios=\"\"")]
    public void AmbiguousOrEmptySelectionsAreRefused(string input) =>
        Assert.Throws<ArgumentException>(testCode: () => MachineBootOptions.Parse(options: input, machineTokens: out _));
}
