using Puck.Capture;
using Puck.HumbleGamingBrick;

namespace Puck.Demo.Forge;

/// <summary>
/// The <c>--forge-oracle</c> tool mode: build the ORACLE <c>.gbc</c> (a genuine SM83 fortune-telling cart — no GPU,
/// no battery, no sound; the whole trick is that on a deterministic machine the fortune is always right), self-verify
/// it on a real Humble machine, and write it plus an <c>&lt;out&gt;.emulated.png</c> boot proof. Dispatched STRAIGHT
/// from <see cref="ForgeCliSeams"/> (not via <see cref="RomForge"/>, which is at its class-coupling ceiling), the same
/// way the town forge is — the cart needs no GPU host, so this is a synchronous CPU build+verify.
/// </summary>
internal static class OracleForge {
    /// <summary>Builds, self-verifies, and writes the ORACLE cartridge (plus its boot-proof PNG).</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <returns>0 on success.</returns>
    public static int Run(string outputPath) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var rom = OracleRom.Build();

        RomForge.WriteRom(outputPath: outputPath, rom: rom);
        OracleRom.Verify(rom: rom);

        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        machine.Machine.Run(tCycles: (70224UL * 60UL));
        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".emulated.png"), rgba: RomForge.FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);

        Console.WriteLine(value: $"oracle forge | wrote {outputPath} ({rom.Length} bytes) | boot it with: --rom {outputPath}");

        return 0;
    }
}
