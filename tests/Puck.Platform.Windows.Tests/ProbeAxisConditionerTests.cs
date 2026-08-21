using Puck.Maths;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeAxisConditionerTests {
    private static readonly ProbeAxisPolicy SymmetricPolicy = new(
        Minimum: FixedQ4816.NegativeOne,
        Maximum: FixedQ4816.One,
        Neutral: FixedQ4816.Zero,
        Deadband: FixedQ4816.FromDouble(value: 0.10),
        Hysteresis: FixedQ4816.Zero,
        Smoothing: FixedQ4816.Zero,
        QuantizeBits: 16,
        MaxAgeTicks: 1_000_000L
    );

    [Fact]
    public void Constructor_rejects_a_quantize_bit_count_outside_1_to_16() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ProbeAxisConditioner(policy: (SymmetricPolicy with { QuantizeBits = 0 })));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ProbeAxisConditioner(policy: (SymmetricPolicy with { QuantizeBits = 17 })));
    }
    [Fact]
    public void Constructor_rejects_a_negative_max_age() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ProbeAxisConditioner(policy: (SymmetricPolicy with { MaxAgeTicks = -1L })));
    }
    [Fact]
    public void Constructor_rejects_a_deadband_gate_that_could_never_release_or_never_activate() {
        // Release requires a magnitude below deadband - hysteresis: negative when hysteresis exceeds the deadband.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ProbeAxisConditioner(policy: (SymmetricPolicy with { Deadband = FixedQ4816.FromDouble(value: 0.05), Hysteresis = FixedQ4816.FromDouble(value: 0.10) })));
        // Activation requires a magnitude above deadband + hysteresis: outside [0, 1] when the sum reaches one.
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new ProbeAxisConditioner(policy: (SymmetricPolicy with { Deadband = FixedQ4816.FromDouble(value: 0.80), Hysteresis = FixedQ4816.FromDouble(value: 0.30) })));
        // The equal case is a reachable gate (release below zero never fires, which a zero deadband also accepts).
        _ = new ProbeAxisConditioner(policy: (SymmetricPolicy with { Deadband = FixedQ4816.FromDouble(value: 0.10), Hysteresis = FixedQ4816.FromDouble(value: 0.10) }));
    }
    [Fact]
    public void Step_rejects_a_channel_ordinal_outside_the_measurement() {
        var conditioner = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var reading = MakeMeasurement(value: 0.0, captureTimestamp: 0L);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => conditioner.Step(reading: reading, channel: -1, nowTimestamp: 0L));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => conditioner.Step(reading: reading, channel: 1, nowTimestamp: 0L));
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(-0.05)]
    [InlineData(0.10)]
    [InlineData(-0.10)]
    public void A_value_at_or_inside_the_deadband_yields_neutral(double raw) {
        var conditioner = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var sample = conditioner.Step(reading: MakeMeasurement(value: raw, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L);

        Assert.Equal(actual: sample.Value, expected: FixedQ4816.Zero);
        Assert.False(condition: sample.Expired);
    }
    [Fact]
    public void Leaving_the_deadband_requires_the_magnitude_to_exceed_deadband_plus_hysteresis_on_both_sides() {
        var positiveAtBoundary = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var positiveBeyond = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var negativeAtBoundary = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var negativeBeyond = new ProbeAxisConditioner(policy: SymmetricPolicy);

        // Exactly at the threshold (0.10) does not exceed it, so the gate stays inactive.
        Assert.Equal(
            actual: positiveAtBoundary.Step(reading: MakeMeasurement(value: 0.10, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value,
            expected: FixedQ4816.Zero
        );
        Assert.Equal(
            actual: negativeAtBoundary.Step(reading: MakeMeasurement(value: -0.10, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value,
            expected: FixedQ4816.Zero
        );
        // Past it, the gate activates and the value passes through.
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: positiveBeyond.Step(reading: MakeMeasurement(value: 0.11, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: negativeBeyond.Step(reading: MakeMeasurement(value: -0.11, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
    }
    [Fact]
    public void Re_entering_the_deadband_requires_the_magnitude_to_fall_below_deadband_minus_hysteresis() {
        var conditioner = new ProbeAxisConditioner(policy: SymmetricPolicy);

        // Activate the deadband gate first.
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.20, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
        // With hysteresis zero, re-entry uses the same boundary: exactly at the deadband still counts as active
        // (the exit test is a strict "<", not "<="), so the gate only releases once the magnitude drops below it.
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.10, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
        Assert.Equal(
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.05, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value,
            expected: FixedQ4816.Zero
        );
    }
    [Fact]
    public void Hysteresis_widens_the_band_beyond_the_plain_deadband() {
        var policy = (SymmetricPolicy with { Hysteresis = FixedQ4816.FromDouble(value: 0.05) });
        var conditioner = new ProbeAxisConditioner(policy: policy);

        // Enter threshold is deadband + hysteresis = 0.15: 0.12 alone must not activate.
        Assert.Equal(
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.12, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value,
            expected: FixedQ4816.Zero
        );
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.16, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
        // Exit threshold is deadband - hysteresis = 0.05: 0.07 alone must not release an already-active gate.
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.07, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value
        );
        Assert.Equal(
            actual: conditioner.Step(reading: MakeMeasurement(value: 0.03, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value,
            expected: FixedQ4816.Zero
        );
    }

    [Fact]
    public void Zero_smoothing_tracks_the_gated_value_with_no_lag() {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.Zero });
        var conditioner = new ProbeAxisConditioner(policy: policy);

        var first = conditioner.Step(reading: MakeMeasurement(value: 0.20, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L);
        var second = conditioner.Step(reading: MakeMeasurement(value: 0.60, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L);

        Assert.Equal(actual: first.Value, expected: Quantize(raw: 0.20, bits: policy.QuantizeBits));
        // Zero smoothing is the identity: the second step lands exactly on the new input, not partway from the first.
        Assert.Equal(actual: second.Value, expected: Quantize(raw: 0.60, bits: policy.QuantizeBits));
    }
    [Fact]
    public void Nonzero_smoothing_lags_toward_the_gated_value_instead_of_jumping_to_it() {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.FromDouble(value: 0.5) });
        var conditioner = new ProbeAxisConditioner(policy: policy);

        _ = conditioner.Step(reading: MakeMeasurement(value: 0.20, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L);
        var second = conditioner.Step(reading: MakeMeasurement(value: 0.60, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L);

        Assert.True(condition: (second.Value > Quantize(raw: 0.20, bits: policy.QuantizeBits)));
        Assert.True(condition: (second.Value < Quantize(raw: 0.60, bits: policy.QuantizeBits)));
    }

    [Fact]
    public void Quantization_is_symmetric_about_the_neutral() {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.Zero, QuantizeBits = 4 });
        var positive = new ProbeAxisConditioner(policy: policy);
        var negative = new ProbeAxisConditioner(policy: policy);

        var positiveValue = positive.Step(reading: MakeMeasurement(value: 0.37, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value;
        var negativeValue = negative.Step(reading: MakeMeasurement(value: -0.37, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value;

        Assert.Equal(actual: negativeValue, expected: -positiveValue);
    }
    [Fact]
    public void Quantization_is_idempotent_on_an_already_quantized_value() {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.Zero, QuantizeBits = 4 });
        var first = new ProbeAxisConditioner(policy: policy);
        var second = new ProbeAxisConditioner(policy: policy);

        var quantized = first.Step(reading: MakeMeasurement(value: 0.37, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value;
        // Feed the already-quantized output straight back in as the next raw channel value (the symmetric policy's
        // span is exactly [-1, 1], so normalization is the identity on a value already in range).
        var requantized = second.Step(reading: MakeMeasurement(value: (double)quantized, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value;

        Assert.Equal(actual: requantized, expected: quantized);
    }

    [Fact]
    public void One_bit_quantization_exposes_exactly_negative_one_zero_and_one() {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.Zero, QuantizeBits = 1 });
        var observed = new HashSet<FixedQ4816>();

        for (var raw = -1.0; (raw <= 1.0); raw += 0.05) {
            var conditioner = new ProbeAxisConditioner(policy: policy);

            _ = observed.Add(item: conditioner.Step(reading: MakeMeasurement(value: raw, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value);
        }

        Assert.Equal(expected: [FixedQ4816.NegativeOne, FixedQ4816.Zero, FixedQ4816.One], actual: observed.OrderBy(keySelector: static value => value).ToArray());
    }
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Quantization_exposes_two_to_the_bits_plus_one_values_with_exact_endpoints(int bits) {
        var policy = (SymmetricPolicy with { Deadband = FixedQ4816.Zero, Smoothing = FixedQ4816.Zero, QuantizeBits = bits });
        var observed = new HashSet<FixedQ4816>();
        var steps = (1 << bits);

        // Sweep finer than the quantization step so every representable value is reached at least once.
        for (var index = 0; (index <= (steps * 8)); index++) {
            var raw = (-1.0 + ((2.0 * index) / (steps * 8)));
            var conditioner = new ProbeAxisConditioner(policy: policy);

            _ = observed.Add(item: conditioner.Step(reading: MakeMeasurement(value: raw, captureTimestamp: 0L), channel: 0, nowTimestamp: 0L).Value);
        }

        Assert.Equal(expected: (steps + 1), actual: observed.Count);
        Assert.Contains(expected: FixedQ4816.NegativeOne, collection: observed);
        Assert.Contains(expected: FixedQ4816.Zero, collection: observed);
        Assert.Contains(expected: FixedQ4816.One, collection: observed);
    }

    [Fact]
    public void A_stale_measurement_expires_to_neutral_exactly_once_then_stops_changing() {
        var conditioner = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var live = MakeMeasurement(value: 0.50, captureTimestamp: 0L);
        var fresh = conditioner.Step(reading: live, channel: 0, nowTimestamp: 0L);

        Assert.False(condition: fresh.Expired);
        Assert.True(condition: fresh.Changed);

        var stale = MakeMeasurement(value: 0.50, captureTimestamp: 0L);
        var firstExpiry = conditioner.Step(reading: stale, channel: 0, nowTimestamp: (SymmetricPolicy.MaxAgeTicks + 1L));

        Assert.True(condition: firstExpiry.Expired);
        Assert.True(condition: firstExpiry.Changed);
        Assert.Equal(actual: firstExpiry.Value, expected: FixedQ4816.Zero);
        Assert.Equal(actual: firstExpiry.Confidence, expected: FixedQ4816.Zero);

        var repeatedExpiry = conditioner.Step(reading: stale, channel: 0, nowTimestamp: (SymmetricPolicy.MaxAgeTicks + 2L));

        Assert.True(condition: repeatedExpiry.Expired);
        Assert.False(condition: repeatedExpiry.Changed);

        var recovered = MakeMeasurement(value: 0.50, captureTimestamp: (SymmetricPolicy.MaxAgeTicks + 2L));
        var reactivated = conditioner.Step(reading: recovered, channel: 0, nowTimestamp: (SymmetricPolicy.MaxAgeTicks + 2L));

        Assert.False(condition: reactivated.Expired);
        Assert.True(condition: reactivated.Changed);
    }
    [Fact]
    public void The_first_ever_step_can_already_be_stale_and_still_reports_a_single_edge() {
        var conditioner = new ProbeAxisConditioner(policy: SymmetricPolicy);
        var stale = MakeMeasurement(value: 0.50, captureTimestamp: 0L);

        var first = conditioner.Step(reading: stale, channel: 0, nowTimestamp: (SymmetricPolicy.MaxAgeTicks + 1L));

        Assert.True(condition: first.Expired);
        Assert.True(condition: first.Changed);

        var second = conditioner.Step(reading: stale, channel: 0, nowTimestamp: (SymmetricPolicy.MaxAgeTicks + 2L));

        Assert.True(condition: second.Expired);
        Assert.False(condition: second.Changed);
    }

    private static FixedQ4816 Quantize(double raw, int bits) {
        var quantumRaw = (1L << (17 - bits));
        var rawUnits = FixedQ4816.FromDouble(value: raw).Value;
        var half = (quantumRaw / 2);
        var offset = (rawUnits >= 0L) ? half : -half;
        var quantized = (((rawUnits + offset) / quantumRaw) * quantumRaw);

        return FixedQ4816.FromRawBits(value: quantized);
    }
    private static ProbeReading MakeMeasurement(double value, long captureTimestamp) {
        var channels = default(ProbeChannelValues);

        channels[0] = FixedQ4816.FromDouble(value: value);

        return new ProbeReading(
            sequence: 0L,
            captureTimestamp: captureTimestamp,
            completionTimestamp: captureTimestamp,
            confidence: FixedQ4816.One,
            channelCount: 1,
            channels: channels
        );
    }
}
