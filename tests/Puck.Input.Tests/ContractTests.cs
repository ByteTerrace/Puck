using System.Numerics;
using Puck.Input.Output;

namespace Puck.Input.Tests;

public sealed class ContractTests {
    [Fact]
    public void Normalization_rejects_non_finite_and_invalid_ranges() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GamepadNormalization.ApplyRadialDeadzone(stick: Vector2.One, deadzone: float.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GamepadNormalization.ApplyRadialDeadzone(stick: Vector2.One, deadzone: 1f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GamepadNormalization.NormalizeTrigger(range: 1f, raw: float.PositiveInfinity, threshold: 0f));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GamepadNormalization.NormalizeTrigger(range: 1f, raw: 1f, threshold: 1f));
        _ = Assert.Throws<ArgumentException>(testCode: () => GamepadNormalization.ReadVector3Int16(offset: 0, scale: 1f, source: [0, 1, 2, 3, 4]));
    }
    [Fact]
    public void NormalizeTrigger_clamps_out_of_range_raw_values() {
        Assert.Equal(expected: 0f, actual: GamepadNormalization.NormalizeTrigger(range: 100f, raw: -10f, threshold: 10f));
        Assert.Equal(expected: 1f, actual: GamepadNormalization.NormalizeTrigger(range: 100f, raw: 200f, threshold: 10f));
    }
    [Fact]
    public void Source_factories_reject_identifiers_outside_the_declared_vocabulary() {
        Assert.Equal(expected: "keyboard.a", actual: InputSources.Keyboard.Letter(letter: 'A'));
        Assert.Equal(expected: "keyboard.f12", actual: InputSources.Keyboard.Function(number: 12));
        Assert.Equal(expected: "keyboard.1", actual: InputSources.Keyboard.Digit(number: 1));
        Assert.Equal(expected: "keyboard.numpad1", actual: InputSources.Keyboard.NumpadDigit(number: 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => InputSources.Keyboard.Letter(letter: 'É'));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => InputSources.Keyboard.Function(number: 13));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => InputSources.Keyboard.Digit(number: 10));
    }
    [Fact]
    public void Lane_policy_rejects_undefined_modes_and_negative_seats() {
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new InputLanePolicy(mode: ((InputLaneMode)99)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => InputLanePolicy.ForPlayer(playerIndex: -1));
    }
    [Fact]
    public void Trigger_curve_rejects_negative_and_past_end_zone_queries() {
        var effect = TriggerEffectSpec.ContinuousCurve(zoneStrengths: [1, 2, 3]);

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => effect.ZoneStrength(zone: -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => effect.ZoneStrength(zone: TriggerEffectSpec.ZoneCount));
    }

}
