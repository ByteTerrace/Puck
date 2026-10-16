using System.Collections.Immutable;

namespace Puck.Input.Output;

/// <summary>One step of a <see cref="HomeLedPattern"/>.</summary>
/// <param name="Intensity">The step's brightness, 0 (off) through 15 (full).</param>
/// <param name="FadeSteps">How long the LED fades into this step, in multiples of the pattern's step duration,
/// 0 through 15 (0 cuts at once).</param>
/// <param name="HoldSteps">How long the LED holds this step, in multiples of the pattern's step duration, 1 through
/// 15.</param>
public readonly record struct HomeLedStep(byte Intensity, byte FadeSteps, byte HoldSteps);
/// <summary>
/// A brightness pattern for a single-channel Home LED (the Switch Pro's), played by the controller itself:
/// <see cref="Steps"/> run in order and the whole sequence repeats <see cref="Repeat"/> times. Encoded as
/// subcommand <c>0x38</c> (dekuNukem: Set HOME Light).
/// </summary>
/// <param name="StepDuration">The base duration every fade and hold multiplies: about 8 ms through 175 ms in 15
/// steps; the controller rounds to the nearest it supports.</param>
/// <param name="StartIntensity">The brightness before the first step, and the steady brightness of a pattern with
/// no steps, 0 through 15.</param>
/// <param name="Repeat">How many times the sequence plays, 1 through 15, or 0 to repeat until replaced; ignored
/// without steps.</param>
/// <param name="Steps">The steps, at most 15; empty holds <paramref name="StartIntensity"/> until replaced.</param>
public readonly record struct HomeLedPattern(TimeSpan StepDuration, byte StartIntensity, byte Repeat, ImmutableArray<HomeLedStep> Steps) {
    /// <summary>The most steps a pattern can carry.</summary>
    public const int MaximumSteps = 15;

    /// <summary>Creates a pattern that holds one brightness until replaced.</summary>
    /// <param name="intensity">The brightness, 0 (off) through 15 (full).</param>
    /// <returns>The steady pattern.</returns>
    public static HomeLedPattern Steady(byte intensity) => new(
        Repeat: 0,
        StartIntensity: intensity,
        StepDuration: TimeSpan.FromMilliseconds(value: 8),
        Steps: [new HomeLedStep(FadeSteps: 0, HoldSteps: 1, Intensity: intensity),]
    );
    /// <summary>Creates a pattern that breathes between off and a brightness until replaced.</summary>
    /// <param name="intensity">The peak brightness, 0 through 15.</param>
    /// <param name="period">The time for one breath; each half is one fade, quantized to the controller's steps.</param>
    /// <returns>The breathing pattern.</returns>
    public static HomeLedPattern Breathing(byte intensity, TimeSpan period) {
        var half = TimeSpan.FromTicks(value: Math.Max(
            val1: 1L,
            val2: (period.Ticks / 2L)
        ));
        var step = TimeSpan.FromTicks(value: Math.Clamp(
            max: TimeSpan.FromMilliseconds(value: 175).Ticks,
            min: TimeSpan.FromMilliseconds(value: 8).Ticks,
            value: (half.Ticks / 15L)
        ));
        var fade = ((byte)Math.Clamp(
            max: 15L,
            min: 1L,
            value: (half.Ticks / step.Ticks)
        ));

        return new HomeLedPattern(
            Repeat: 0,
            StartIntensity: 0,
            StepDuration: step,
            Steps: [
                new HomeLedStep(FadeSteps: fade, HoldSteps: 1, Intensity: intensity),
                new HomeLedStep(FadeSteps: fade, HoldSteps: 1, Intensity: 0),
            ]
        );
    }
}
