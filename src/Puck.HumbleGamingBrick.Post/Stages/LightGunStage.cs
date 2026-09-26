using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Tier-C stage: the light gun on the infrared receive line. A Color machine boots <see cref="LightGunProbeCartridge"/>, which
/// draws a white left half and a black right half and publishes RP's received-light bit to work RAM every loop.
/// <para>
/// Four proofs. Sensing: the gun aimed off the screen reads dark, aimed at a white pixel reads light, and aimed at a
/// black pixel reads dark, each through the ROM's own read of RP, so the aim changes what the running program observes.
/// Snapshot: an aim held at a snapshot is restored with it, so a fresh machine restored from that snapshot reads light
/// without being aimed again. Determinism: two runs of one aim script reproduce the published bits and the final
/// snapshot exactly. Control: the same aim on a machine whose program never arms the receiver leaves RP's light bit
/// unread, so the bit the ROM publishes is the gun's and not a constant.
/// </para>
/// </summary>
internal sealed class LightGunStage : IPostStage<PostContext> {
    private const int SettleFrames = 4;

    // The aim script: off, white, black, white, off, one frame each after settling.
    private static readonly MachinePointer[] Script = [
        MachinePointer.Off,
        At(column: 40),
        At(column: (LightGunProbeCartridge.DarkColumn + 40)),
        At(column: 8),
        MachinePointer.Off,
    ];
    private static readonly byte[] ScriptExpected = [0, 1, 0, 1, 0];

    /// <inheritdoc/>
    public bool IsConcurrent =>
        true;
    /// <inheritdoc/>
    public string Name =>
        "light-gun";
    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    // An aim at a column of the screen's middle row, as the fraction of the screen a host records.
    private static MachinePointer At(int column) => new(
        x: ((ushort)((column * MachinePointer.FractionUnits) / Framebuffer.ScreenWidth)),
        y: ((ushort)(((Framebuffer.ScreenHeight / 2) * MachinePointer.FractionUnits) / Framebuffer.ScreenHeight))
    );
    private static byte Sensed(MachineInstance instance) =>
        instance.GetRequiredService<ISystemBus>().ReadByte(address: LightGunProbeCartridge.SensedAddress);
    // Runs the aim script on a fresh machine, returning the bit the ROM published after each step and the final state.
    private static (byte[] Published, MachineSnapshot State) RunScript(byte[] rom) {
        using var instance = PostMachine.Build(
            model: ConsoleModel.CgbE,
            rom: rom
        );
        var gun = instance.GetRequiredService<ILightGun>();
        var published = new byte[Script.Length];

        PostMachine.RunFrames(
            frames: SettleFrames,
            instance: instance
        );

        for (var step = 0; (step < Script.Length); step++) {
            gun.Aim(pointer: Script[step]);
            PostMachine.RunFrames(
                frames: 1,
                instance: instance
            );
            published[step] = Sensed(instance: instance);
        }

        return (published, instance.Machine.Snapshot());
    }

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = LightGunProbeCartridge.Create();
        var first = RunScript(rom: rom);

        if (!first.Published.AsSpan().SequenceEqual(other: ScriptExpected)) {
            return PostStageOutcome.Fail(detail: $"the aim script off/white/black/white/off published [{string.Join(
                separator: ",",
                values: first.Published
            )}], expected [{string.Join(
                separator: ",",
                values: ScriptExpected
            )}]");
        }

        var second = RunScript(rom: rom);

        if (!second.Published.AsSpan().SequenceEqual(other: first.Published)) {
            return PostStageOutcome.Fail(detail: "a second run of the aim script published different bits");
        }

        if (!first.State.ContentEquals(other: second.State)) {
            return PostStageOutcome.Fail(detail: $"a second run of the aim script ended in a different state — {HashDivergenceProbe.DescribeDivergence(
                a: first.State,
                b: second.State
            )}");
        }

        // An aim held at a snapshot is machine state: a fresh machine restored from it reads light unaimed.
        MachineSnapshot aimed;

        using (var instance = PostMachine.Build(
            model: ConsoleModel.CgbE,
            rom: rom
        )) {
            PostMachine.RunFrames(
                frames: SettleFrames,
                instance: instance
            );
            instance.GetRequiredService<ILightGun>().Aim(pointer: At(column: 40));
            aimed = instance.Machine.Snapshot();
        }

        using (var restored = PostMachine.Build(
            model: ConsoleModel.CgbE,
            rom: rom
        )) {
            restored.Machine.Restore(snapshot: aimed);
            PostMachine.RunFrames(
                frames: 1,
                instance: restored
            );

            if (Sensed(instance: restored) != 1) {
                return PostStageOutcome.Fail(detail: "a machine restored from a snapshot taken while the gun was aimed at a white pixel read dark — the aim is not snapshot state");
            }
        }

        // Control: the synthetic WRAM-fill ROM never arms RP, so aiming the gun at whatever it shows changes nothing in
        // the RP read-back, which reports light only while the receiver is armed.
        using (var control = PostMachine.Build(
            model: ConsoleModel.CgbE,
            rom: SyntheticRom.Create(supportsColor: true)
        )) {
            PostMachine.RunFrames(
                frames: SettleFrames,
                instance: control
            );
            control.GetRequiredService<ILightGun>().Aim(pointer: At(column: 40));

            var register = control.GetRequiredService<IInfrared>().ReadRegister();

            if ((register & 0x02) == 0) {
                return PostStageOutcome.Fail(detail: $"RP read 0x{register:X2} with its receiver unarmed — the gun's light reached bit 1 without the data-read-enable bits");
            }
        }

        return PostStageOutcome.Pass(detail: "the light gun reads dark off the screen and on a black pixel and light on a white one through the ROM's own RP read, its aim survives a snapshot, a second run reproduces the published bits and final state, and an unarmed receiver ignores it");
    }
}
