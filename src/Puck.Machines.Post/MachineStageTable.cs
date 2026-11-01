using Puck.Abstractions.Machines;

namespace Puck.Machines.Post;

/// <summary>The factories and observation used by the shared queued-host stages. Each factory returns a fresh host
/// owned and disposed by its probe.</summary>
/// <typeparam name="THost">The machine family's queued host.</typeparam>
/// <param name="WithContent">Builds the fixture host with audio detached.</param>
/// <param name="Empty">Builds a host with no cartridge and audio detached.</param>
/// <param name="WithAudio">Builds the fixture host with the requested audio rate.</param>
/// <param name="Observe">Reads a deterministic fingerprint of authoritative state.</param>
/// <param name="RequestedRate">The audio rate requested by <paramref name="WithAudio"/>.</param>
/// <param name="ScratchAddress">A writable address the fixture never changes.</param>
/// <param name="RegionStart">The first byte of the memory comparison window.</param>
/// <param name="RegionLength">The memory comparison window's length in bytes.</param>
public sealed record QueuedHostStageFactory<THost>(Func<THost> WithContent, Func<THost> Empty,
    Func<THost> WithAudio, Func<THost, long> Observe, int RequestedRate = 32_000,
    int ScratchAddress = 0, int RegionStart = 0, int RegionLength = 0)
    where THost : QueuedMachineHost, IMachineMemoryPeek;
/// <summary>The machine-neutral stage table. A battery binds its core probes and queued-host factory once, then
/// selects table entries in its own report order. Core-specific embedding checks remain in the battery.</summary>
public static class MachineStageTable {
    /// <summary>Creates the shared Tier-A stages with a battery's machine factories and probe budgets.</summary>
    /// <typeparam name="TContext">The battery's run context.</typeparam>
    /// <typeparam name="THost">The machine family's queued host.</typeparam>
    /// <param name="determinism">Compares two independently built machines.</param>
    /// <param name="forkDeterminism">Checks fork replay and ownership.</param>
    /// <param name="throughput">Measures the family's throughput workload.</param>
    /// <param name="allocation">Checks the family's allocation workload.</param>
    /// <param name="hosts">Creates the factories for one queued-host stage run.</param>
    /// <param name="isConcurrent">Whether functional stages may run beside other stages. Measurements run alone.</param>
    /// <param name="forkName">The fork stage's display and filter name.</param>
    /// <param name="framePublicationName">The frame-publication stage's display and filter name.</param>
    /// <param name="memoryAccessName">The memory-access stage's display and filter name.</param>
    /// <returns>The stages keyed by their display and filter names.</returns>
    public static IReadOnlyDictionary<string, IPostStage<TContext>> Create<TContext, THost>(
        Func<TContext, PostStageOutcome> determinism, Func<TContext, PostStageOutcome> forkDeterminism,
        Func<TContext, PostStageOutcome> throughput, Func<TContext, PostStageOutcome> allocation,
        Func<TContext, QueuedHostStageFactory<THost>> hosts, bool isConcurrent = true,
        string forkName = "fork-determinism", string framePublicationName = "queued-host-frame-publication",
        string memoryAccessName = "queued-host-memory-access")
        where THost : QueuedMachineHost, IMachineMemoryPeek {
        Stage<TContext>[] stages = [
            new(IsConcurrent: isConcurrent, Name: "determinism", Probe: determinism),
            new(IsConcurrent: isConcurrent, Name: forkName, Probe: forkDeterminism),
            new(IsConcurrent: false, Name: "throughput", Probe: throughput),
            new(IsConcurrent: false, Name: "zero-alloc", Probe: allocation),
            new("queued-host-checkpoint", isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyCheckpoint(withContent: factory.WithContent));
            }),
            new("queued-host-backpressure", isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyBackpressure(withContent: factory.WithContent));
            }),
            new(framePublicationName, isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyFramePublication(
                    withContent: factory.WithContent, empty: factory.Empty));
            }),
            new("queued-host-audio", isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyAudio(attached: factory.WithAudio,
                    detached: factory.WithContent, requestedRate: factory.RequestedRate));
            }),
            new("queued-host-time-travel", isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyTimeTravel(withContent: factory.WithContent,
                    withAudio: factory.WithAudio, observe: factory.Observe));
            }),
            new(memoryAccessName, isConcurrent, context => {
                var factory = hosts(context);

                return Outcome(result: QueuedHostContractProbe.VerifyConcurrentMemoryAccess(withContent: factory.WithContent,
                    scratchAddress: factory.ScratchAddress, regionStart: factory.RegionStart, regionLength: factory.RegionLength));
            }),
        ];

        return stages.ToDictionary(keySelector: static stage => stage.Name,
            elementSelector: static stage => ((IPostStage<TContext>)stage), comparer: StringComparer.Ordinal);
    }
    /// <summary>Folds a host's side-effect-free memory window into an authoritative-state fingerprint.</summary>
    /// <param name="host">The host whose memory is read.</param>
    /// <param name="address">The window's first byte.</param>
    /// <param name="length">The window's length in bytes.</param>
    /// <returns>The FNV-1a fingerprint, reinterpreted as a signed integer.</returns>
    public static long ObserveMemory(IMachineMemoryPeek host, int address, int length) {
        Span<byte> window = stackalloc byte[length];

        host.PeekBytes(address: address, destination: window);
        return unchecked((long)Puck.Maths.Fnv1aHash.Compute(values: window));
    }

    private static PostStageOutcome Outcome(QueuedHostProbeResult result) =>
        (result.Passed ? PostStageOutcome.Pass(detail: result.Detail) : PostStageOutcome.Fail(detail: result.Detail));

    private sealed record Stage<TContext>(string Name, bool IsConcurrent, Func<TContext, PostStageOutcome> Probe) : IPostStage<TContext> {
        public PostTier Tier => PostTier.A;

        public PostStageOutcome Run(TContext context) => Probe(context);
    }
}
