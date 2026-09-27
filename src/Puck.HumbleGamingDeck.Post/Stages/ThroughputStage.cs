namespace Puck.HumbleGamingDeck.Post;

/// <summary>Reports master-tick throughput without gating execution speed.</summary>
internal sealed class ThroughputStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "throughput";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        return MachineStageProbes.MeasureThroughput(build: PostMachine.Build,
            advance: static instance => instance.Machine.RunCycles(masterTicks: PostMachine.BatchTicks),
            readCycles: static instance => instance.Machine.MasterTicks,
            rate: HgdConsoleModel.NtscRp2A03G.MasterClockRate(), warmBatches: 20, measureBatches: 200, cycleUnit: "master ticks");
    }
}
