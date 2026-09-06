using System.Text;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Exercises the public synchronous host seam and isolation of explicit hardware and trace options.</summary>
internal sealed class EmbeddingStage : IPostStage<PostContext> {
    /// <inheritdoc/>
    public string Name => "embedding";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        var rom = SyntheticRom.Create();
        Encoding.ASCII.GetBytes(s: "SIIRTC_V").CopyTo(array: rom, index: rom.Length - 16);
        var bios = new byte[ReplacementBios.ImageSize];
        var traceCount = 0;
        var options = new AgbMachineOptions {
            DisablePrefetch = true,
            DisableRtc = true,
            BusTrace = _ => ++traceCount,
        };
        using var normal = new AdvancedGamingBrickCore(bios: bios, cartridgeRom: rom);
        using var overridden = new AdvancedGamingBrickCore(configuration: new AgbMachineConfiguration(bios: bios, rom: rom, options: options));
        using var traced = new AdvancedGamingBrickCore(configuration: new AgbMachineConfiguration(bios: bios, rom: rom, options: new AgbMachineOptions { BusTrace = _ => { } }));
        if (!normal.Instance.Machine.Identity.HasRtc || normal.Instance.Machine.Identity.PrefetchDisabled ||
            overridden.Instance.Machine.Identity.HasRtc || !overridden.Instance.Machine.Identity.PrefetchDisabled ||
            traced.Instance.Machine.Identity != normal.Instance.Machine.Identity) {
            return PostStageOutcome.Fail(detail: "hardware options leaked between cores, or trace changed snapshot identity");
        }
        var countBefore = traceCount;
        normal.RunCycles(cycles: 4096);
        traced.RunCycles(cycles: 4096);
        if (traceCount != countBefore || !normal.Instance.Machine.Snapshot().Data.SequenceEqual(other: traced.Instance.Machine.Snapshot().Data)) {
            return PostStageOutcome.Fail(detail: "tracing changed emulated state or used another core's sink");
        }
        overridden.RunCycles(cycles: 4096);
        if (traceCount <= countBefore) {
            return PostStageOutcome.Fail(detail: "explicit bus trace sink received no accesses");
        }
        using var fork = overridden.Instance.Fork();
        if (fork.Machine.Identity != overridden.Instance.Machine.Identity) {
            return PostStageOutcome.Fail(detail: "fork did not retain hardware options");
        }
        var snapshot = overridden.Instance.Machine.Snapshot();
        _ = fork.Machine.RunCycles(cycles: 4096);
        overridden.RunCycles(cycles: 4096);
        if (!fork.Machine.Snapshot().Data.SequenceEqual(other: overridden.Instance.Machine.Snapshot().Data)) {
            return PostStageOutcome.Fail(detail: "configured fork diverged from its source");
        }
        try {
            normal.Instance.Machine.Restore(snapshot: snapshot);
            return PostStageOutcome.Fail(detail: "snapshot with incompatible hardware options was accepted");
        } catch (InvalidOperationException) {
            // Rejection must precede mutation.
        }
        if (normal.CycleCount != traced.CycleCount) {
            return PostStageOutcome.Fail(detail: "rejected snapshot changed the target clock");
        }
        return CoreEmbeddingProbe.Verify(core: normal, cycleBudget: AdvancedGamingBrickMachine.CyclesPerFrame, framebufferLength: 240 * 160);
    }
}
