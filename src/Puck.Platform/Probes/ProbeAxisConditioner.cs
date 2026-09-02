using Puck.Maths;

namespace Puck.Platform.Probes;

/// <summary>One <see cref="ProbeAxisConditioner.Step"/> result.</summary>
/// <param name="Value">The conditioned axis value in <c>[-1, 1]</c>, zero at neutral.</param>
/// <param name="Confidence">The reading's confidence, or zero while <see cref="Expired"/>.</param>
/// <param name="Expired">Whether the sampled reading's age exceeded the policy's <c>MaxAgeTicks</c>.</param>
/// <param name="Changed">Whether this result differs from the previous <see cref="ProbeAxisConditioner.Step"/>
/// call's result — the caller's signal to capture it as a fresh input, never captured on an unchanged repeat.</param>
public readonly record struct ProbeAxisSample(FixedQ4816 Value, FixedQ4816 Confidence, bool Expired, bool Changed);
/// <summary>
/// Compiles a <see cref="ProbeAxisPolicy"/> into a per-tick conditioning step from one <see cref="ProbeReading"/>
/// channel to a <see cref="ProbeAxisSample"/> — the whole policy an axis binding row reduces to: map the channel's
/// declared range onto <c>[-1, 1]</c> about its neutral, gate it through a deadband with hysteresis, optionally
/// smooth it with a fixed-point exponential moving average, quantize it to a step of <c>2^(1 - QuantizeBits)</c>
/// (<c>2^QuantizeBits + 1</c> representable values, neutral and both endpoints exact), and expire it to neutral
/// once the reading is older than the policy's <c>MaxAgeTicks</c>. Mutable, zero-alloc, and deterministic by
/// construction: every step is exact integer arithmetic over <see cref="FixedQ4816"/>, so the same reading
/// sequence at the same sampling instants always compiles to the same axis samples.
/// </summary>
public struct ProbeAxisConditioner {
    private readonly ProbeAxisPolicy m_policy;
    private readonly long m_quantumRaw;

    private bool m_active;
    private bool m_hasSmoothed;
    private FixedQ4816 m_smoothed;
    private bool m_hasEmitted;
    private bool m_lastExpired;
    private FixedQ4816 m_lastValue;
    private FixedQ4816 m_lastConfidence;

    /// <summary>Compiles <paramref name="policy"/> into a fresh conditioner, at rest (inactive deadband, no
    /// smoothing history, nothing yet emitted).</summary>
    /// <param name="policy">The policy to condition against.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/>'s <c>QuantizeBits</c> is outside
    /// <c>1..16</c>, its <c>MaxAgeTicks</c> is negative, its <c>Deadband</c> or <c>Hysteresis</c> is negative, its
    /// <c>Hysteresis</c> exceeds its <c>Deadband</c> (the release threshold would be negative, so the gate could
    /// never release), or <c>Deadband + Hysteresis</c> reaches one (the activation threshold would sit outside the
    /// axis domain, so the gate could never activate).</exception>
    public ProbeAxisConditioner(ProbeAxisPolicy policy) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: policy.QuantizeBits, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: policy.QuantizeBits, other: 16);
        ArgumentOutOfRangeException.ThrowIfNegative(value: policy.MaxAgeTicks);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: policy.Deadband, other: FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: policy.Hysteresis, other: FixedQ4816.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: policy.Hysteresis, other: policy.Deadband);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: (policy.Deadband + policy.Hysteresis), other: FixedQ4816.One);

        m_policy = policy;
        // The axis domain [-1, 1] spans 2 in real value; a step of 2 / 2^QuantizeBits = 2^(1 - QuantizeBits) in
        // real value is 2^(17 - QuantizeBits) raw Q16 units. Bits in 1..16 keeps the exponent in 1..16, always an
        // exact integer step with no rounding at construction.
        m_quantumRaw = (1L << (17 - policy.QuantizeBits));
    }

    /// <summary>Gets the compiled policy.</summary>
    public readonly ProbeAxisPolicy Policy => m_policy;

    /// <summary>Conditions one channel of <paramref name="reading"/> at <paramref name="nowTimestamp"/>.</summary>
    /// <param name="reading">The reading to sample.</param>
    /// <param name="channel">The channel ordinal, <c>0..</c><paramref name="reading"/><c>.ChannelCount - 1</c>.</param>
    /// <param name="nowTimestamp">The sampling instant, in the <see cref="System.Diagnostics.Stopwatch"/> domain
    /// <paramref name="reading"/><c>.CaptureTimestamp</c> shares.</param>
    /// <returns>The conditioned sample.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is negative or is not less than
    /// <paramref name="reading"/><c>.ChannelCount</c>.</exception>
    public ProbeAxisSample Step(in ProbeReading reading, int channel, long nowTimestamp) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: channel, other: reading.ChannelCount);

        var age = (nowTimestamp - reading.CaptureTimestamp);
        var expired = (age > m_policy.MaxAgeTicks);
        FixedQ4816 value;
        FixedQ4816 confidence;

        if (expired) {
            // Reset only on the entering edge, so a fresh reading that arrives later resumes from neutral
            // rather than carrying stale hysteresis/smoothing history forward.
            if (!m_lastExpired || !m_hasEmitted) {
                m_active = false;
                m_hasSmoothed = false;
                m_smoothed = FixedQ4816.Zero;
            }

            value = FixedQ4816.Zero;
            confidence = FixedQ4816.Zero;
        } else {
            var normalized = Normalize(raw: reading[channel], policy: m_policy);

            Gate(normalized: normalized);

            var gated = (m_active ? normalized : FixedQ4816.Zero);

            m_smoothed = Smooth(gated: gated);
            value = Quantize(quantumRaw: m_quantumRaw, value: m_smoothed);
            confidence = reading.Confidence;
        }

        var changed = (
            !m_hasEmitted ||
            (expired != m_lastExpired) ||
            (value != m_lastValue) ||
            (confidence != m_lastConfidence)
        );

        m_hasEmitted = true;
        m_lastExpired = expired;
        m_lastValue = value;
        m_lastConfidence = confidence;

        return new ProbeAxisSample(Changed: changed, Confidence: confidence, Expired: expired, Value: value);
    }

    /// <summary>Maps a raw channel value onto <c>[-1, 1]</c>: <paramref name="policy"/>'s neutral maps to zero, its
    /// maximum to one, and its minimum to negative one, each side scaled independently since the neutral need not
    /// sit at the channel's midpoint. A degenerate side (equal to neutral) maps its whole side to zero.</summary>
    private static FixedQ4816 Normalize(FixedQ4816 raw, in ProbeAxisPolicy policy) {
        if (raw >= policy.Neutral) {
            var span = (policy.Maximum - policy.Neutral);

            return ((span > FixedQ4816.Zero)
                ? FixedQ4816.Clamp(value: ((raw - policy.Neutral) / span), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One)
                : FixedQ4816.Zero);
        }

        var lowerSpan = (policy.Neutral - policy.Minimum);

        return ((lowerSpan > FixedQ4816.Zero)
            ? -FixedQ4816.Clamp(value: ((policy.Neutral - raw) / lowerSpan), minimum: FixedQ4816.Zero, maximum: FixedQ4816.One)
            : FixedQ4816.Zero);
    }
    /// <summary>Updates the deadband's hysteresis state from a newly normalized value.</summary>
    private void Gate(FixedQ4816 normalized) {
        var magnitude = FixedQ4816.Abs(value: normalized);

        if (m_active) {
            if (magnitude < (m_policy.Deadband - m_policy.Hysteresis)) {
                m_active = false;
            }
        } else if (magnitude > (m_policy.Deadband + m_policy.Hysteresis)) {
            m_active = true;
        }
    }
    /// <summary>Applies the exponential moving average, seeding on the first sample after construction or an
    /// expiry reset so the filter never ramps in from an artificial zero.</summary>
    private FixedQ4816 Smooth(FixedQ4816 gated) {
        if (!m_hasSmoothed || (m_policy.Smoothing == FixedQ4816.Zero)) {
            m_hasSmoothed = true;

            return gated;
        }

        return (m_smoothed + ((gated - m_smoothed) * m_policy.Smoothing));
    }
    /// <summary>Rounds to the nearest multiple of <paramref name="quantumRaw"/> raw units, ties away from zero —
    /// symmetric about zero and idempotent on an already-quantized value.</summary>
    private static FixedQ4816 Quantize(FixedQ4816 value, long quantumRaw) {
        var raw = value.Value;
        var half = (quantumRaw / 2);
        var offset = ((raw >= 0L) ? half : -half);
        var quantized = (((raw + offset) / quantumRaw) * quantumRaw);

        quantized = Math.Clamp(value: quantized, min: FixedQ4816.NegativeOne.Value, max: FixedQ4816.One.Value);

        return FixedQ4816.FromRawBits(value: quantized);
    }
}
