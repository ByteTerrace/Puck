using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Builds the boot ROM image a revision executes from reset. The image is a real boot program: it verifies the
/// cartridge logo and header checksum, shows a mark while the divider runs, plays the start-up chime, selects the
/// compatibility mode a cartridge without the color flag needs, hands the cartridge the revision's register file, and
/// unmaps itself through <c>0xFF50</c> at <c>0x00FE</c> so execution falls into <c>0x0100</c>.
/// <para>
/// The handoff is timed rather than incidental. The divider counter a cartridge reads at <c>0x0100</c> is the boot
/// program's running time, and <see cref="BootDivPrediction"/> holds the per-revision, per-header counter the hardware
/// produces — so the image carries that prediction's tables, computes its own target from the cartridge header, resets
/// the divider, and then consumes exactly the predicted count before unmapping. The cost of everything before the reset
/// is therefore free, and the cost of everything after it is a straight-line constant the builder measures by booting
/// the image it just emitted (<see cref="Calibrate"/>).
/// </para>
/// </summary>
/// <remarks>
/// A machine booted through one of these images has a different <see cref="MachineIdentity"/> than a machine started at
/// the seeded post-boot handoff, because the identity fingerprints the boot ROM image. The two are not
/// interchangeable for snapshot restore.
/// </remarks>
public static class BootRomBuilder {
    /// <summary>The length of the monochrome boot image, mapped over <c>0x0000</c>-<c>0x00FF</c>.</summary>
    public const int MonochromeLength = 0x0100;
    /// <summary>The length of the Color boot image, mapped over <c>0x0000</c>-<c>0x00FF</c> and
    /// <c>0x0200</c>-<c>0x08FF</c>; the cartridge header shows through the gap.</summary>
    public const int ColorLength = 0x0900;

    // The unmap instruction occupies the last two bytes of the low window so the program counter falls into 0x0100.
    private const int UnmapOffset = 0x00FE;
    // The Color image's code and data live above the header window.
    private const ushort ColorCodeBase = 0x0200;

    /// <summary>Builds the boot ROM image for a revision.</summary>
    /// <param name="model">The revision whose boot program to emit.</param>
    /// <returns>A 256-byte monochrome image, or a 2304-byte Color image.</returns>
    public static byte[] Build(ConsoleModel model) {
        var layout = BootRomLayout.For(model: model);
        var image = Emit(
            layout: layout,
            calibration: BootRomCalibration.Zero
        );

        return Emit(
            calibration: Calibrate(
                image: image,
                layout: layout
            ),
            layout: layout
        );
    }

    // Emits the whole image for a layout at a fixed calibration.
    private static byte[] Emit(BootRomLayout layout, BootRomCalibration calibration) {
        var image = new byte[layout.SupportsColor
            ? ColorLength
            : MonochromeLength];
        var code = BootRomProgram.Emit(
            calibration: calibration,
            layout: layout
        );
        var codeBase = (layout.SupportsColor
            ? ((int)ColorCodeBase)
            : 0);

        if (layout.SupportsColor) {
            // The low window carries the entry jump and the compatibility-palette selection tables; the program itself
            // sits above the header window.
            image[0] = 0xC3;
            image[1] = ((byte)(ColorCodeBase & 0xFF));
            image[2] = ((byte)(ColorCodeBase >> 8));

            BootRomLowWindow.Build().CopyTo(
                array: image,
                index: BootRomLowWindow.Base
            );
        }

        var limit = (layout.SupportsColor
            ? ColorLength
            : UnmapOffset);

        if ((codeBase + code.Length) > limit) {
            throw new InvalidOperationException(message: $"The {layout.Model} boot program is 0x{code.Length:X} bytes and does not fit the 0x{(limit - codeBase):X} bytes available.");
        }

        code.CopyTo(
            array: image,
            index: codeBase
        );

        image[UnmapOffset] = 0xE0;
        image[(UnmapOffset + 1)] = 0x50;

        return image;
    }
    // Solves the two straight-line constants the emitted program subtracts: the divider tail (so the handoff counter is
    // exactly the prediction) and, for a layout that hands off mid vertical blank, the distance from the LCD enable (so
    // the handoff line is exactly the seeded one). The two are independent — the divider is reset after the enable — so
    // each converges on its own. Solving them by BOOTING the emitted image keeps the constants a measurement of this
    // machine rather than a hand count of instruction timings.
    private static BootRomCalibration Calibrate(BootRomLayout layout, byte[] image) {
        var calibration = BootRomCalibration.Zero;

        for (var attempt = 0; (attempt < 16); ++attempt) {
            var settled = true;

            foreach (var probe in layout.Probes) {
                var rom = BootRomProbeCartridge.Create(probe: probe);
                var expected = BootDivPrediction.Compute(
                    header: CartridgeHeader.Parse(rom: rom),
                    model: layout.Model
                );

                Observe(
                    image: image,
                    divider: out var divider,
                    layout: layout,
                    lcdY: out var lcdY,
                    rom: rom
                );

                // One machine cycle of tail is four divider steps and four dots; a whole line is 456 dots.
                var dividerError = (((int)divider) - expected);
                var lineError = (((int)lcdY) - probe.HandoffLine);

                if (dividerError != 0) {
                    calibration = calibration.WithDividerAdjustment(machineCycles: (dividerError / 4));
                    settled = false;
                }

                if ((probe.HandoffLine != 0) && (lineError != 0)) {
                    calibration = calibration.WithEnableAdjustment(
                        machineCycles: (Normalize(lineError: lineError) * (PpuDotsPerLine / 4)),
                        colorCartridge: probe.SupportsColor
                    );
                    settled = false;
                }

                if (!settled) {
                    break;
                }
            }

            if (settled) {
                return calibration;
            }

            image = Emit(
                calibration: calibration,
                layout: layout
            );
        }

        throw new InvalidOperationException(message: $"The {layout.Model} boot program's handoff timing did not converge.");
    }
    // Boots the image against one probe cartridge and reads back the handoff instant.
    private static void Observe(BootRomLayout layout, byte[] image, byte[] rom, out ushort divider, out byte lcdY) {
        using var instance = MachineFactory.Create(
            configuration: new MachineConfiguration(
                bootRom: image,
                cartridgeRom: rom,
                model: layout.Model
            ),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

        var bus = instance.GetRequiredService<ISystemBus>();
        var machine = instance.Machine;

        for (var guard = 0; (guard < BootInstructionCeiling); ++guard) {
            if ((bus.ReadByte(address: MemoryMap.BootRomDisable) & 0x01) != 0) {
                divider = instance.GetRequiredService<Puck.HumbleGamingBrick.Interfaces.ITimer>().DivCounter;
                lcdY = bus.ReadByte(address: MemoryMap.LcdY);

                return;
            }

            machine.StepInstruction();
        }

        throw new InvalidOperationException(message: $"The {layout.Model} boot program never unmapped itself.");
    }

    // A line error is read modulo the frame's line count, so an enable that overshot into the next frame walks back
    // the short way instead of chasing the wrap.
    private static int Normalize(int lineError) {
        var wrapped = (((lineError % ScanlinesPerFrame) + ScanlinesPerFrame) % ScanlinesPerFrame);

        return ((wrapped > (ScanlinesPerFrame / 2))
            ? (wrapped - ScanlinesPerFrame)
            : wrapped);
    }

    private const int BootInstructionCeiling = 8_000_000;
    private const int PpuDotsPerLine = 456;
    private const int ScanlinesPerFrame = 154;
}
