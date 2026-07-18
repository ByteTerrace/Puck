using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-A stage: proves the MBC5 rumble variant's motor latch directly against a synthetic cartridge (header type
/// <c>0x1E</c> — RAM+RUMBLE+BATTERY). RAM-bank select bit 3 (<c>0x4000</c>-<c>0x5FFF</c>) drives the motor instead of
/// selecting a bank on this variant: writing it sets/clears <see cref="ICartridge.MotorLevel"/> while masking the
/// actual RAM bank to bits 0-2 only, and a NON-rumble MBC5 (header <c>0x1B</c>) must show no motor regardless of the
/// same bit pattern — the control the latch is real, not a false-positive on every MBC5.
/// </summary>
internal sealed class RumbleDeviceStage : IPostStage {
    /// <inheritdoc/>
    public string Name =>
        "rumble-device";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rumbleRom = SyntheticRom.Create(cartridgeType: 0x1E, ramSize: 0x02); // MBC5+RAM+RUMBLE+BATTERY

        using var rumbleMachine = PostMachine.Build(model: ConsoleModel.Cgb, rom: rumbleRom);

        var rumbleCartridge = rumbleMachine.GetRequiredService<ICartridge>();

        if (rumbleCartridge.MotorLevel != 0f) {
            return PostStageOutcome.Fail(detail: $"a freshly booted rumble cartridge reports MotorLevel={rumbleCartridge.MotorLevel} (expected 0 at reset)");
        }

        // Motor ON: RAM-bank register bit 3 set (bits 0-2 select bank 3).
        rumbleCartridge.WriteControl(address: 0x4000, value: 0x0B);

        if (rumbleCartridge.MotorLevel != 1f) {
            return PostStageOutcome.Fail(detail: $"writing 0x0B (bit 3 set) to the RAM-bank register left MotorLevel={rumbleCartridge.MotorLevel} (expected 1)");
        }

        // Motor OFF, same low bits (bank 3 stays selected — the latch is independent of the bank number).
        rumbleCartridge.WriteControl(address: 0x4000, value: 0x03);

        if (rumbleCartridge.MotorLevel != 0f) {
            return PostStageOutcome.Fail(detail: $"clearing bit 3 (0x03) left MotorLevel={rumbleCartridge.MotorLevel} (expected 0)");
        }

        // A snapshot round-trip preserves the latched motor state — it is ordinary snapshot state.
        rumbleCartridge.WriteControl(address: 0x4000, value: 0x0F); // bit 3 set + bank bits 0-2 = 7 (masked to 7 max)

        if (rumbleCartridge.MotorLevel != 1f) {
            return PostStageOutcome.Fail(detail: $"writing 0x0F left MotorLevel={rumbleCartridge.MotorLevel} (expected 1)");
        }

        var snapshot = rumbleMachine.Machine.Snapshot();

        using var restored = PostMachine.Build(model: ConsoleModel.Cgb, rom: rumbleRom);

        restored.Machine.Restore(snapshot: snapshot);

        var restoredCartridge = restored.GetRequiredService<ICartridge>();

        if (restoredCartridge.MotorLevel != 1f) {
            return PostStageOutcome.Fail(detail: $"a snapshot restore lost the latched motor state (MotorLevel={restoredCartridge.MotorLevel}, expected 1)");
        }

        // Control: a NON-rumble MBC5 (header 0x1B) never reports a motor, even under the identical bit pattern —
        // proves the latch is gated on CartridgeHeader.HasRumble, not "any MBC5 RAM-bank write".
        var plainRom = SyntheticRom.Create(cartridgeType: 0x1B, ramSize: 0x02);

        using var plainMachine = PostMachine.Build(model: ConsoleModel.Cgb, rom: plainRom);

        var plainCartridge = plainMachine.GetRequiredService<ICartridge>();

        plainCartridge.WriteControl(address: 0x4000, value: 0x0F);

        if (plainCartridge.MotorLevel != 0f) {
            return PostStageOutcome.Fail(detail: $"a NON-rumble MBC5 (header 0x1B) reported MotorLevel={plainCartridge.MotorLevel} after the same RAM-bank write — the latch is not gated on CartridgeHeader.HasRumble");
        }

        return PostStageOutcome.Pass(detail: "the MBC5 rumble variant's motor latch (RAM-bank register bit 3) sets/clears MotorLevel, survives a snapshot round-trip, and stays silent on a non-rumble MBC5 under the identical write");
    }
}
