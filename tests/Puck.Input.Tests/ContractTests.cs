using System.Numerics;
using Puck.Input.Lighting;
using Puck.Input.Output;

namespace Puck.Input.Tests;

public sealed class ContractTests {
    [Fact]
    public void Normalization_rejects_non_finite_and_invalid_ranges() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GamepadNormalization.ApplyRadialDeadzone(stick: Vector2.One, deadzone: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GamepadNormalization.ApplyRadialDeadzone(stick: Vector2.One, deadzone: 1f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GamepadNormalization.NormalizeTrigger(raw: float.PositiveInfinity, threshold: 0f, range: 1f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => GamepadNormalization.NormalizeTrigger(raw: 1f, threshold: 1f, range: 1f));
        _ = Assert.Throws<ArgumentException>(() => GamepadNormalization.ReadVector3Int16(source: [0, 1, 2, 3, 4], offset: 0, scale: 1f));
    }

    [Fact]
    public void NormalizeTrigger_clamps_out_of_range_raw_values() {
        Assert.Equal(expected: 0f, actual: GamepadNormalization.NormalizeTrigger(raw: -10f, threshold: 10f, range: 100f));
        Assert.Equal(expected: 1f, actual: GamepadNormalization.NormalizeTrigger(raw: 200f, threshold: 10f, range: 100f));
    }

    [Fact]
    public void Source_factories_reject_identifiers_outside_the_declared_vocabulary() {
        Assert.Equal(expected: "keyboard.a", actual: InputSources.Keyboard.Letter(letter: 'A'));
        Assert.Equal(expected: "keyboard.f12", actual: InputSources.Keyboard.Function(number: 12));
        Assert.Equal(expected: "keyboard.1", actual: InputSources.Keyboard.Digit(number: 1));
        Assert.Equal(expected: "keyboard.numpad1", actual: InputSources.Keyboard.NumpadDigit(number: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InputSources.Keyboard.Letter(letter: 'É'));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InputSources.Keyboard.Function(number: 13));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InputSources.Keyboard.Digit(number: 10));
    }

    [Fact]
    public void Lane_policy_rejects_undefined_modes_and_negative_seats() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new InputLanePolicy(mode: (InputLaneMode)99));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => InputLanePolicy.ForPlayer(playerIndex: -1));
    }

    [Fact]
    public void Trigger_curve_rejects_negative_and_past_end_zone_queries() {
        var effect = TriggerEffectSpec.ContinuousCurve(zoneStrengths: [1, 2, 3]);

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => effect.ZoneStrength(zone: -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => effect.ZoneStrength(zone: TriggerEffectSpec.ZoneCount));
    }

    [Fact]
    public void Empty_lamp_array_never_synthesizes_a_phantom_lamp() {
        var device = new TestLampArrayDevice(lampCount: 0);
        var celebration = new LightCelebration(device: device);

        celebration.Begin(score: 10_000);

        Assert.False(condition: celebration.IsPlaying);
        Assert.False(condition: celebration.Tick(elapsedSeconds: 0.1));
        Assert.Equal(expected: 0, actual: device.LampInfoRequests);
        Assert.Equal(expected: 0, actual: device.BatchUpdates);
    }

    [Fact]
    public void Celebration_rejects_invalid_elapsed_time() {
        var celebration = new LightCelebration(device: new TestLampArrayDevice(lampCount: 1));

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => celebration.Tick(elapsedSeconds: -0.01));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => celebration.Tick(elapsedSeconds: double.NaN));
    }
}
