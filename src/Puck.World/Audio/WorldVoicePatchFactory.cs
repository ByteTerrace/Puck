using Puck.Audio.Mixing;
using Puck.Assets.Documents;

namespace Puck.World.Audio;

/// <summary>Converts a normalized <c>puck.synth.v1</c> document into <see cref="Puck.Audio.Mixing.VoicePatch"/> —
/// the one place a <c>puck.synth.v1</c> document crosses into <c>Puck.Audio</c>, which itself parses no document.</summary>
public static class WorldVoicePatchFactory {
    // Puck.Assets.Documents.SynthOscillator and Puck.Audio.Mixing.SynthOscillator declare the SAME oscillator
    // kinds in the SAME ordinal order (KEEP IN SYNC) — the two cannot share one type across the layering
    // boundary (Puck.Audio parses no document), so this is the one place the two enumerations meet.
    private static Puck.Audio.Mixing.SynthOscillator ToRuntimeOscillator(Puck.Assets.Documents.SynthOscillator oscillator) => (oscillator switch {
        Puck.Assets.Documents.SynthOscillator.Pulse => Puck.Audio.Mixing.SynthOscillator.Pulse,
        Puck.Assets.Documents.SynthOscillator.Saw => Puck.Audio.Mixing.SynthOscillator.Saw,
        Puck.Assets.Documents.SynthOscillator.Triangle => Puck.Audio.Mixing.SynthOscillator.Triangle,
        Puck.Assets.Documents.SynthOscillator.Sine => Puck.Audio.Mixing.SynthOscillator.Sine,
        Puck.Assets.Documents.SynthOscillator.Noise => Puck.Audio.Mixing.SynthOscillator.Noise,
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(oscillator),
        actualValue: oscillator,
        message: "Unrecognized oscillator kind."
    ),
    });

    /// <summary>Converts a normalized document (see <see cref="SynthPatchCanonicalizer.Normalize"/> — every
    /// optional member defaulted, cross-oscillator fields cleared) into the runtime block.</summary>
    /// <param name="document">The normalized document.</param>
    /// <returns>The runtime parameter block.</returns>
    public static VoicePatch FromDocument(SynthPatchDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var dutyThousandths = (document.DutyThousandths ?? 500);
        var sustainThousandths = (document.SustainThousandths ?? 1000);

        return new(
            Oscillator: ToRuntimeOscillator(oscillator: (document.Oscillator ?? Puck.Assets.Documents.SynthOscillator.Pulse)),
            DutyThresholdQ32: ((uint)((((ulong)dutyThousandths) << 32) / 1000UL)),
            DutyDcOffsetQ16: ((int)((((2L * dutyThousandths) - 1000L) * 65536L) / 1000L)),
            Polynomial: (document.Polynomial ?? 0),
            AttackFrames: (document.AttackFrames ?? 0),
            DecayFrames: (document.DecayFrames ?? 0),
            SustainQ16: ((int)((sustainThousandths * 65536L) / 1000L)),
            ReleaseFrames: (document.ReleaseFrames ?? 0),
            PitchMillihertz: document.PitchMillihertz,
            SweepMillihertzPerFrame: (document.SweepMillihertzPerFrame ?? 0),
            VibratoDepthMillihertz: (document.VibratoDepthMillihertz ?? 0),
            VibratoRateMillihertz: (document.VibratoRateMillihertz ?? 0),
            DurationFrames: (document.DurationFrames ?? 0),
            FilterMode: VoiceFilterMode.Bypass,
            FilterCoefficientQ16: 0,
            FilterDampingQ16: 65536
        );
    }
}
