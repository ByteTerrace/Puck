using Puck.HumbleGamingBrick.Interfaces;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>
/// The boot bitmap decides which cartridges an image runs: a boot program hands off only to a cartridge carrying the
/// mark it was built with, and wedges on every other one. The pair is exclusive because the Color image has no room
/// for a second 48-byte table, so the mark is a substitution rather than an addition.
/// </summary>
public sealed class BootRomMarkTests {
    private const int InstructionCeiling = 4_000_000;

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
    private static bool HandsOff(BootRomMark mark, byte[] rom) {
        using var instance = MachineFactory.Create(
            configuration: new MachineConfiguration(
                bootRom: BootRomBuilder.Build(model: ConsoleModel.CgbE, mark: mark),
                cartridgeRom: rom,
                model: ConsoleModel.CgbE
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
