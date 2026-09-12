using Puck.HumbleGamingBrick.Interfaces;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>
/// Compatible firmware accepts independently authored logos; the two explicit strict policies still verify their
/// own table. A header logo is neither a signature nor a restricted-distribution security boundary.
/// </summary>
public sealed class BootRomMarkTests {
    private const int InstructionCeiling = 4_000_000;

    [Theory]
    [InlineData(ConsoleModel.DmgB)]
    [InlineData(ConsoleModel.CgbD)]
    public void CompatibleRunsEraHouseAndArbitraryLogos(ConsoleModel model) {
        var rom = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: model).Probes[0]);
        Assert.True(condition: HandsOff(mark: BootRomMark.Compatible, rom: rom, model: model));
        CartridgeHeader.HouseLogo.CopyTo(destination: rom.AsSpan(start: CartridgeHeader.LogoOffset));
        Assert.True(condition: HandsOff(mark: BootRomMark.Compatible, rom: rom, model: model));
        rom.AsSpan(start: CartridgeHeader.LogoOffset, length: CartridgeHeader.Logo.Length).Fill(value: 0xA5);
        Assert.True(condition: HandsOff(mark: BootRomMark.Compatible, rom: rom, model: model));
    }

    [Theory]
    [InlineData(ConsoleModel.DmgB)]
    [InlineData(ConsoleModel.CgbD)]
    public void CompatibleStillRefusesCorruptedHeaderChecksum(ConsoleModel model) {
        var rom = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: model).Probes[0]);
        rom[0x014D] ^= 0x01;
        Assert.False(condition: HandsOff(mark: BootRomMark.Compatible, rom: rom, model: model));
    }

    [Fact]
    public void CompatibleIsTheDefaultAndUnknownPolicyIsRefused() {
        Assert.Equal(expected: BootRomBuilder.Build(model: ConsoleModel.Mgb, mark: BootRomMark.Compatible),
            actual: BootRomBuilder.Build(model: ConsoleModel.Mgb));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => BootRomBuilder.Build(model: ConsoleModel.Mgb, mark: (BootRomMark)255));
    }

    [Fact]
    public void AForgedCartridgeIsRunByTheHouseImageAndRefusedByTheEraOne() {
        var forged = new HgbCartridgeCompiler().Compile(document: GamingBricks.Forge.CartridgeDocuments.Create(target: "cgb", title: "MARK")).Rom;

        Assert.True(condition: HandsOff(mark: BootRomMark.House, rom: forged), userMessage: "the house image wedged on a forged cartridge");
        Assert.False(condition: HandsOff(mark: BootRomMark.Era, rom: forged), userMessage: "the era image handed off to a forged cartridge");
    }

    [Fact]
    public void AnEraCartridgeIsRunByTheEraImageAndRefusedByTheHouseOne() {
        var era = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: ConsoleModel.CgbE).Probes[0]);

        Assert.True(condition: HandsOff(mark: BootRomMark.Era, rom: era), userMessage: "the era image wedged on an era cartridge");
        Assert.False(condition: HandsOff(mark: BootRomMark.House, rom: era), userMessage: "the house image handed off to an era cartridge");
    }

    [Fact]
    public void TheHouseMarkIsAWholeHeaderBitmapDistinctFromTheEraOne() {
        Assert.Equal(expected: CartridgeHeader.Logo.Length, actual: CartridgeHeader.HouseLogo.Length);
        Assert.False(condition: CartridgeHeader.HouseLogo.SequenceEqual(other: CartridgeHeader.Logo));
    }

    // Boots the image against the cartridge and reports whether the boot program ever unmapped itself, which is the
    // instant it hands the machine over.
    private static bool HandsOff(BootRomMark mark, byte[] rom, ConsoleModel model = ConsoleModel.CgbE) {
        using var instance = MachineFactory.Create(
            configuration: new MachineConfiguration(
                bootRom: BootRomBuilder.Build(model: model, mark: mark),
                cartridgeRom: rom,
                model: model
            ),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var bus = instance.GetRequiredService<ISystemBus>();
        var machine = instance.Machine;

        for (var guard = 0; (guard < InstructionCeiling); ++guard) {
            if ((bus.ReadByte(address: MemoryMap.BootRomDisable) & 0x01) != 0) {
                return true;
            }

            machine.StepInstruction();
        }

        return false;
    }
}
