using Puck.Capture;
using Puck.HumbleGamingBrick;

namespace Puck.Demo.Forge;

/// <summary>
/// The <c>--forge-critterswap</c> tool mode: build the CRITTER-SWAP <c>.gbc</c> (a genuine SM83 link-trading toy — no
/// GPU title bake, battery-backed SRAM), SELF-VERIFY it on real Humble machines (including a full two-machine
/// <see cref="SerialLinkSession"/> swap), and write it plus an <c>&lt;out&gt;.emulated.png</c> boot proof. Dispatched
/// STRAIGHT from <see cref="ForgeCliSeams"/> (not via <see cref="RomForge"/>, which is at its class-coupling ceiling),
/// like the ORACLE and town forges — the cart needs no GPU host, so this is a synchronous CPU build → verify → write.
/// </summary>
internal static class CritterSwapForge {
    /// <summary>Builds, self-verifies (single-cart behaviour AND a two-machine link swap), and writes the CRITTER-SWAP
    /// cartridge plus its boot-proof PNG.</summary>
    /// <param name="outputPath">Where to write the <c>.gbc</c>.</param>
    /// <returns>0 on success.</returns>
    public static int Run(string outputPath) {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var rom = CritterSwapRom.Build();

        // Verify BEFORE writing: a failed swap or a non-deterministic run throws here, so a broken cart never ships.
        CritterSwapRom.Verify(rom: rom);
        RomForge.WriteRom(outputPath: outputPath, rom: rom);

        using var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: ConsoleModel.Cgb, cartridgeRom: rom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        machine.Machine.Run(tCycles: (70224UL * 60UL));
        PngEncoder.Write(height: Framebuffer.ScreenHeight, path: Path.ChangeExtension(path: outputPath, extension: ".emulated.png"), rgba: RomForge.FramebufferToRgba(machine: machine), width: Framebuffer.ScreenWidth);

        Console.WriteLine(value: $"critterswap forge | wrote {outputPath} ({rom.Length} bytes) | boot it with: --rom {outputPath} (or link two cabinets in the overworld and trade)");

        return 0;
    }
}
