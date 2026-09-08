using Puck.Abstractions.Machines;

namespace Puck.GamingBricks.Post;

/// <summary>Checks synchronous input, output, replay and lookahead through the public host-neutral core contract.</summary>
public static class CoreEmbeddingProbe {
    /// <summary>Drives a caller-owned core without a queued worker, renderer, audio device or filesystem save.</summary>
    /// <param name="core">The core to advance; ownership remains with the caller.</param>
    /// <param name="cycleBudget">A positive master-clock budget spanning at least one video frame.</param>
    /// <param name="framebufferLength">Expected native pixel count.</param>
    /// <returns>The self-checking outcome.</returns>
    public static PostStageOutcome Verify(IQueuedMachineCore core, long cycleBudget, int framebufferLength) {
        var initialCycles = core.CycleCount;
        core.RunCycles(cycles: -1);
        core.RunCycles(cycles: 0);
        if (core.CycleCount != initialCycles) {
            return PostStageOutcome.Fail(detail: "nonpositive host budget advanced the machine");
        }
        core.ApplyInput(input: new MachinePadState());
        core.ConfigureAudio(sampleRate: 48_000);
        core.RunCycles(cycles: cycleBudget);
        if (core.CycleCount <= initialCycles || core.Framebuffer.Length != framebufferLength) {
            return PostStageOutcome.Fail(detail: "direct host did not advance or expose the native framebuffer");
        }
        var samples = new short[8192];
        var count = core.DrainAudioSamples(destination: samples);
        if (count <= 0 || (count & 1) != 0) {
            return PostStageOutcome.Fail(detail: "direct host did not receive interleaved stereo audio");
        }
        core.ConfigureAudio(sampleRate: 0);
        byte[] state = [], replay = [], expected = [];
        var length = core.CaptureState(buffer: ref state);
        using var lookahead = core.CreateLookahead();
        lookahead.RunFrame();
        core.RunCycles(cycles: cycleBudget);
        var expectedLength = core.CaptureState(buffer: ref expected);
        core.RestoreState(buffer: state, length: length);
        core.RunCycles(cycles: cycleBudget);
        var replayLength = core.CaptureState(buffer: ref replay);
        if (replayLength != expectedLength || !expected.AsSpan(start: 0, length: expectedLength).SequenceEqual(other: replay.AsSpan(start: 0, length: replayLength))) {
            return PostStageOutcome.Fail(detail: "direct host capture/restore did not replay byte-identically");
        }
        if (core.DrainAudioSamples(destination: samples) != 0) {
            return PostStageOutcome.Fail(detail: "disabled audio retained presentation samples");
        }
        return PostStageOutcome.Pass(detail: "synchronous core: nonpositive budgets, input, native framebuffer, stereo audio, disable, snapshot replay and independent lookahead; no host infrastructure");
    }
}
