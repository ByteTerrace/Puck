using Puck.Machines;

namespace Puck.HumbleGamingDeck.Post;

/// <summary>The Deck battery's Tier A and Tier B stage registry.</summary>
internal static class PostStages {
    /// <summary>Creates the stages in their execution order.</summary>
    /// <returns>The independent stage instances for one battery run.</returns>
    public static IPostStage<PostContext>[] Create() {
        var shared = MachineStageTable.Create<PostContext, DeckMachineHost>(
            allocation: RunAllocation, determinism: RunDeterminism,
            forkDeterminism: RunForkDeterminism, forkName: "fork-lifecycle", framePublicationName: "queued-host-frames", hosts: HostFactory, isConcurrent: false,
            memoryAccessName: "queued-host-memory", throughput: RunThroughput);

        return [shared["determinism"], shared["zero-alloc"], shared["fork-lifecycle"],
            new SnapshotStage(), new LoaderStage(), new CpuTimingStage(), shared["throughput"],
            new EmbeddingStage(), shared["queued-host-checkpoint"], shared["queued-host-backpressure"],
            shared["queued-host-frames"], shared["queued-host-audio"], shared["queued-host-time-travel"],
            shared["queued-host-memory"], new PictureStage(), new ShippedCartridgesStage(),
            new ControllerStage(), new OamDmaStage(), new ApuStage(),
            new Nes6502SstStage(), new NestestStage(), new NestestBootStage(), new BlarggSuiteStage(), new AccuracyCoinStage()];
    }

    private static PostStageOutcome RunDeterminism(PostContext context) {
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
    private static PostStageOutcome RunForkDeterminism(PostContext context) {
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
    private static PostStageOutcome RunThroughput(PostContext context) {
        return MachineStageProbes.MeasureThroughput(build: PostMachine.Build,
            advance: static instance => instance.Machine.RunCycles(masterTicks: PostMachine.BatchTicks),
            readCycles: static instance => instance.Machine.MasterTicks,
            rate: HgdConsoleModel.NtscRp2A03G.MasterClockRate(), warmBatches: 20, measureBatches: 200, cycleUnit: "master ticks");
    }
    private static PostStageOutcome RunAllocation(PostContext context) {
        return MachineStageProbes.VerifyZeroAllocation(build: PostMachine.Build, measureFrames: 100,
            runFrames: PostMachine.RunBatches, warmFrames: 30);
    }
    private static QueuedHostStageFactory<DeckMachineHost> HostFactory(PostContext context) => new(
        WithContent: static () => new DeckMachineHost(cartridgeImage: PostMachine.CreateImage()),
        Empty: static () => new DeckMachineHost(),
        WithAudio: static () => new DeckMachineHost(audioSampleRate: 32_000, cartridgeImage: PostMachine.CreateImage()),
        Observe: static host => MachineStageTable.ObserveMemory(address: 0x0000, host: host, length: 0x800),
        // The fixture writes only $0000 and $6000, leaving $0300 available to the poke probe.
        ScratchAddress: 0x0300, RegionStart: 0x0000, RegionLength: 0x800);
}
