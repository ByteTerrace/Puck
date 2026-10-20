using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>Compares snapshots from independent machines executing the same master-tick budgets, on every board's
/// fixture.</summary>
internal sealed class DeterminismStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "determinism";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var details = new List<string>();

        foreach (var fixture in PostMachine.Fixtures) {
            var outcome = MachineStageProbes.VerifyDeterminism<MachineInstance<HgdMachine, HgdMachineConfiguration>, HgdMachineSnapshot, HgdMachineIdentity, ulong>(
                build: () => PostMachine.Build(image: fixture.Image), runFrames: PostMachine.RunBatches,
                snapshot: static instance => instance.Machine.Snapshot(), frames: 100, describeDivergence: PostMachine.Describe);

            if (outcome.Verdict != PostVerdict.Pass) {
                return (outcome with { Detail = $"{fixture.Name}: {outcome.Detail}" });
            }
            details.Add(item: $"{fixture.Name}: {outcome.Detail}");
        }

        return PostStageOutcome.Pass(detail: string.Join(separator: "; ", values: details));
    }
}
