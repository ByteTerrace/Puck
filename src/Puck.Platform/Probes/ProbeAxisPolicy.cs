using Puck.Maths;

namespace Puck.Platform.Probes;

/// <summary>
/// The policy a <see cref="ProbeAxisConditioner"/> compiles an axis binding row into: how one
/// <see cref="ProbeReading"/> channel becomes a per-tick axis sample in the fixed <c>[-1, 1]</c> domain. This is
/// the whole conditioning contract every axis binding row reduces to — nothing else about the probes pipeline
/// reaches the conditioner.
/// </summary>
/// <param name="Minimum">The reading channel's declared minimum value (a <c>ProbeChannelSpec</c>'s
/// <c>Min</c>).</param>
/// <param name="Maximum">The reading channel's declared maximum value (a <c>ProbeChannelSpec</c>'s
/// <c>Max</c>).</param>
/// <param name="Neutral">The reading channel's declared neutral value, in <c>[Minimum, Maximum]</c>. Maps to an
/// axis value of zero.</param>
/// <param name="Deadband">The unipolar deadband radius around zero, in the mapped <c>[-1, 1]</c> axis domain;
/// non-negative, and <c>Deadband + Hysteresis</c> below one.</param>
/// <param name="Hysteresis">The margin applied on both sides of <see cref="Deadband"/>: leaving the deadband
/// requires the magnitude to exceed <c>Deadband + Hysteresis</c>, and re-entering it requires the magnitude to fall
/// below <c>Deadband - Hysteresis</c>. Non-negative and at most <see cref="Deadband"/>.</param>
/// <param name="Smoothing">The exponential moving-average factor in <c>[0, 1]</c> applied to the post-deadband
/// value; zero bypasses smoothing (the identity).</param>
/// <param name="QuantizeBits">The output quantization: a step of <c>2^(1 - QuantizeBits)</c> across the
/// <c>[-1, 1]</c> axis domain, so <c>2^QuantizeBits + 1</c> values are representable with zero and both endpoints
/// exact. Valid range <c>1..16</c> so the step is always an exact <see cref="FixedQ4816"/> multiple.</param>
/// <param name="MaxAgeTicks">The maximum age, in <see cref="System.Diagnostics.Stopwatch"/> ticks, between a
/// reading's <see cref="ProbeReading.CaptureTimestamp"/> and the sampling instant before the axis is
/// treated as stale and returns to neutral with zero confidence.</param>
public readonly record struct ProbeAxisPolicy(
    FixedQ4816 Minimum,
    FixedQ4816 Maximum,
    FixedQ4816 Neutral,
    FixedQ4816 Deadband,
    FixedQ4816 Hysteresis,
    FixedQ4816 Smoothing,
    int QuantizeBits,
    long MaxAgeTicks
);
