namespace Puck.HumbleGamingDeck.Post;

/// <summary>Verifies fork replay, isolation, and disposal through the shared machine lifecycle, on every board's
/// fixture.</summary>
internal sealed class ForkDeterminismStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "fork-lifecycle";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var details = new List<string>();

        foreach (var fixture in PostMachine.Fixtures) {
            using var parent = PostMachine.Build(image: fixture.Image);
            var outcome = MachineStageProbes.VerifyForkLifecycle<HgdMachine, HgdMachineConfiguration, HgdMachineSnapshot, HgdMachineIdentity, ulong>(
                parent: parent, runFrames: static (machine, count) => machine.RunCycles(masterTicks: (((ulong)count) * PostMachine.BatchTicks)),
                snapshot: static machine => machine.Snapshot(), warmFrames: 20, tailFrames: 30, describeDivergence: PostMachine.Describe);

            if (outcome.Verdict != PostVerdict.Pass) {
                return (outcome with { Detail = $"{fixture.Name}: {outcome.Detail}" });
            }
            details.Add(item: $"{fixture.Name}: {outcome.Detail}");
        }

        return PostStageOutcome.Pass(detail: string.Join(separator: "; ", values: details));
    }
}
