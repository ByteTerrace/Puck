namespace Puck.HumbleGamingDeck.Post;

/// <summary>Verifies replay and fork equivalence at each master-clock phase within instructions.</summary>
internal sealed class SnapshotStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "mid-instruction-replay";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        for (var alignment = 0; (alignment < 4); ++alignment) {
            var configuration = new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: PostMachine.CreateImage()),
                powerOn: new HgdPowerOnProfile(alignmentPhase: alignment, workRamFill: 0xA5));
            using var instance = HgdMachineFactory.Create(configuration: configuration);
            using var partitioned = HgdMachineFactory.Create(configuration: configuration);
            var machine = instance.Machine;

            machine.RunCycles(masterTicks: 400);
            for (var tick = 0; (tick < 400); ++tick) {
                partitioned.Machine.RunCycles(masterTicks: 1);
            }
            if (!machine.Snapshot().ContentEquals(other: partitioned.Machine.Snapshot())) {
                return PostStageOutcome.Fail(detail: $"chunk partition differs at alignment {alignment}");
            }
            for (var tick = 0; (tick < 120); ++tick) {
                machine.RunCycles(masterTicks: 1);
                var middle = machine.Snapshot();
                using var fork = instance.Fork();

                machine.RunCycles(masterTicks: 997);
                fork.Machine.RunCycles(masterTicks: 997);
                var uninterrupted = machine.Snapshot();

                if (!uninterrupted.ContentEquals(other: fork.Machine.Snapshot())) {
                    return PostStageOutcome.Fail(detail: $"mid-cycle fork differs at alignment {alignment}, offset {tick}");
                }
                machine.Restore(snapshot: middle);
                machine.RunCycles(masterTicks: 997);
                if (!uninterrupted.ContentEquals(other: machine.Snapshot())) {
                    return PostStageOutcome.Fail(detail: $"mid-cycle restore differs at alignment {alignment}, offset {tick}");
                }
                machine.Restore(snapshot: middle);
            }
            var initial = machine.Snapshot();

            machine.StepMasterTick();
            machine.StepMasterTick();
            var overshot = machine.Snapshot();

            machine.RunCycles(masterTicks: 5);
            var expected = machine.Snapshot();

            machine.Restore(snapshot: overshot);
            machine.RunCycles(masterTicks: 2);
            machine.RunCycles(masterTicks: 3);
            if (!expected.ContentEquals(other: machine.Snapshot()) || (expected.TakenAt != (initial.TakenAt + 5))) {
                return PostStageOutcome.Fail(detail: "manual-step overshoot is not carried across restore and partitioned budgets");
            }
        }

        return PostStageOutcome.Pass(detail: "480 mid-instruction/master-phase restore and fork continuations; four alignments; partition and overshoot equivalence");
    }
}
