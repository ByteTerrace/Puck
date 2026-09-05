using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// Runs a gambatte <c>_outaudio0</c>/<c>_outaudio1</c> case and checks whether the final rendered frame's audio is
/// constant (silence) or varies (sound), ported from <c>test/testrunner.cpp</c>'s <c>evaluateStrTestResults</c>: it
/// compares every sample of the last frame against the first, the same coarse check the reference tester runs — it
/// never compares sample values against gambatte's own mixer, only whether the stream is flat. The sink is configured
/// at half the CPU clock (2097152 Hz) immediately before the case's last frame: <see cref="AudioOutputComponent"/>
/// emits one stereo frame every two T-cycles at that rate with no resampling drift, the same 35112-samples-per-video-frame
/// ratio gambatte's own raw test buffer uses, and configuring resets the ring so only the last frame's samples remain
/// once it has run.
/// </summary>
internal static class AudioProbe {
    private const int RawSampleRate = 2_097_152;

    /// <summary>Runs a case to a verdict.</summary>
    /// <param name="ledgerCase">The case to run; <see cref="LedgerCase.ExpectedAudio"/> must be set.</param>
    /// <returns>The probe outcome.</returns>
    public static ProbeOutcome Run(LedgerCase ledgerCase) {
        var expectation = ledgerCase.ExpectedAudio;

        if (expectation is null) {
            return new ProbeOutcome(
                Detail: "no expected audio disposition configured for this case",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var rom = File.ReadAllBytes(path: ledgerCase.FullPath);

        using var machine = PostMachine.Build(
            model: ledgerCase.Model,
            rom: rom
        );
        using var liveness = LivenessGate.Attach(cpu: machine.GetRequiredService<Sm83>());

        var sink = machine.GetRequiredService<IAudioSink>();
        var priorFrames = (ledgerCase.FrameCap - 1);

        if (priorFrames > 0) {
            PostMachine.RunFrames(
                frames: priorFrames,
                instance: machine
            );
        }

        // Configuring resets the ring, so only the case's final frame is captured below.
        sink.Configure(sampleRate: RawSampleRate);

        PostMachine.RunFrames(
            frames: 1,
            instance: machine
        );

        if (!liveness.IsAlive) {
            return new ProbeOutcome(
                Detail: liveness.Reason,
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var samples = new short[sink.AvailableSampleCount];

        _ = sink.ReadSamples(destination: samples);

        if (samples.Length < 2) {
            return new ProbeOutcome(
                Detail: $"no audio samples produced for the final frame (frame cap {ledgerCase.FrameCap})",
                Verdict: ProbeVerdict.Inconclusive
            );
        }

        var left0 = samples[0];
        var right0 = samples[1];
        var silent = true;

        for (var frame = 1; (frame < (samples.Length / 2)); ++frame) {
            if (
                (samples[(frame * 2)] != left0) ||
                (samples[((frame * 2) + 1)] != right0)
            ) {
                silent = false;

                break;
            }
        }

        var expectSilence = (expectation == AudioExpectation.Silence);
        var passed = (silent == expectSilence);

        return new ProbeOutcome(
            Detail: $"{(silent ? "silent" : "varying")} over {(samples.Length / 2)} samples after {ledgerCase.FrameCap} frames (expected {(expectSilence ? "silence" : "sound")})",
            Verdict: (passed
                ? ProbeVerdict.Pass
                : ProbeVerdict.Fail)
        );
    }
}
