namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-A stage: proves the solar sensor's GPIO protocol directly against <see cref="AgbCartridge"/> — no
/// ARM execution needed, since <see cref="AgbCartridge.WriteGpio"/>/<see cref="AgbCartridge.ReadGpio"/>/
/// <see cref="AgbCartridge.SetLightLevel"/> are the exact seam a game's ROM code drives: RESET (pin 1) high zeroes the
/// counter and samples the CURRENT recorded light level; each CLK (pin 0) rising edge while not held in reset advances
/// the counter by one; the output bit (pin 3) trips once the counter reaches the level's threshold. Self-contained (a
/// hand-stamped solar-sensor game code, no ROM asset, no BIOS), so it always runs — the golden-replay counterpart
/// (<see cref="SolarReplayStage"/>) needs a real cartridge and skips without one.
/// </summary>
internal sealed class SolarDeviceStage : IPostStage {
    // A solar-sensor cart's game code — keys HasSolar (and HasRtc, which real solar-sensor carts also carry; the RTC
    // and light sensor share the GPIO pins but never interfere: RTC only reacts while pin 2 (CS) is high, the light
    // sensor only while pin 2 is low, exactly as this test drives it).
    private static readonly byte[] s_gameCode = "U3IJ"u8.ToArray();

    /// <inheritdoc/>
    public string Name =>
        "solar-device";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var rom = new byte[0x200];

        s_gameCode.CopyTo(destination: rom.AsSpan(start: 0xAC, length: 4));

        var cartridge = new AgbCartridge(rom: rom);

        if (!cartridge.HasGpio) {
            return PostStageOutcome.Infra(detail: "the solar-sensor game code did not key the GPIO overlay on — AgbGameOverrides regressed");
        }

        // Direction: pins 0-2 (CLK/RESET/CS) are the game's outputs; pin 3 (the sensor's serial output) is an input.
        cartridge.WriteGpio(register: 0xC6u, value: 0x07);
        cartridge.WriteGpio(register: 0xC8u, value: 0x01); // enable readback

        foreach (var level in (ReadOnlySpan<byte>)[0, 1, 64, 128, 200, 254, 255]) {
            cartridge.SetLightLevel(level: level);

            // RESET high (CS low: the light sensor is selected, not the RTC) samples the just-recorded level.
            cartridge.WriteGpio(register: 0xC4u, value: 0x02);
            cartridge.WriteGpio(register: 0xC4u, value: 0x00); // release RESET; ready for the first CLK rising edge

            var threshold = (255 - level);

            if (threshold == 0) {
                // A brightest-possible reading trips the counter (0) before any CLK edge at all.
                if ((cartridge.ReadGpio(register: 0xC4u) & 0x08) == 0) {
                    return PostStageOutcome.Fail(detail: $"level {level} (threshold 0): expected the sensor already tripped after RESET, before any CLK pulse");
                }

                continue;
            }

            if ((cartridge.ReadGpio(register: 0xC4u) & 0x08) != 0) {
                return PostStageOutcome.Fail(detail: $"level {level} (threshold {threshold}): tripped immediately after RESET — expected {threshold} CLK pulses first");
            }

            for (var pulse = 1; (pulse <= threshold); ++pulse) {
                cartridge.WriteGpio(register: 0xC4u, value: 0x01); // CLK rising edge — advances the counter

                var tripped = ((cartridge.ReadGpio(register: 0xC4u) & 0x08) != 0);

                if (tripped != (pulse == threshold)) {
                    return PostStageOutcome.Fail(detail: $"level {level} (threshold {threshold}): counter reached {pulse}, sensor reports tripped={tripped} (expected {(pulse == threshold)})");
                }

                cartridge.WriteGpio(register: 0xC4u, value: 0x00); // CLK falling edge — rearms for the next pulse
            }
        }

        return PostStageOutcome.Pass(detail: "the solar counter tripped at exactly the CLK-pulse count implied by each recorded light level (0, 1, 64, 128, 200, 254, 255), matching the modeled RESET/CLK/threshold protocol");
    }
}
