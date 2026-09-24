using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <c>advance</c> is evaluated on the engine-tick clock
/// (<see cref="Puck.Maths.FixedTickConversion.TicksPerSecond"/> per second), never a simulation tick converted at the
/// current rate — at a constant simulation rate this must agree, at every simulation-tick boundary, with the
/// document's own per-tick rational rate. At 5 per second and simulation rate <c>rateHz</c>, that per-tick rate is
/// <c>5/rateHz</c>, and both spellings reach <c>floor(5n/rateHz)</c> after <c>n</c> simulation ticks. Expected values
/// are derived independently here from the simulation rate alone, never from <see cref="StateAdvance"/>'s own
/// engine-tick formula.</summary>
public sealed class EngineTimeAdvanceRateLawTests {
    private static StateRow Row(long? min = null, long? max = null) => new(
        Name: CellName.Parse(candidate: "gauge"),
        Kind: CellKind.Int,
        Min: min,
        Max: max,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: CellValue.Int(value: 0L)
            )]
    );

    // Every engine tick that completes one whole simulation tick at rateHz corresponds to exactly
    // 50400/rateHz engine ticks — an exact division because the simulation-rate contract requires rateHz to divide
    // 50400. This is the independent per-simulation-tick oracle: floor(numerator * n / rateHz) for a rate of
    // numerator per second, denominator 1.
    [InlineData(30L)]
    [InlineData(60L)]
    [InlineData(240L)]
    [Theory]
    public void APerSecondRateAgreesWithItsPerTickRationalRateAtEverySimulationTickBoundary(long rateHz) {
        var engineTicksPerSimulationTick = (Puck.Maths.FixedTickConversion.TicksPerSecond / ((ulong)rateHz));
        var advance = new StateAdvance(
            PerSecondDenominator: 1,
            PerSecondNumerator: 5
        );
        var row = Row(max: long.MaxValue);

        for (var n = 0L; (n <= (3 * rateHz)); n += (rateHz / 6)) {
            var expected = ((5L * n) / rateHz);
            var actual = advance.ComputeCurrentValue(
                baseValue: 0L,
                currentEngineTick: (((ulong)n) * engineTicksPerSimulationTick),
                row: row
            );

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
    // A negative (draining) rate's magnitude is the same per-tick floor as its positive counterpart, with the sign
    // applied afterward — decay truncates toward zero, not toward negative infinity, at every rate this contract
    // names.
    [InlineData(30L)]
    [InlineData(60L)]
    [InlineData(240L)]
    [Theory]
    public void ANegativeRateDrainsByTheSamePerTickMagnitudeAtEveryNamedRate(long rateHz) {
        var engineTicksPerSimulationTick = (Puck.Maths.FixedTickConversion.TicksPerSecond / ((ulong)rateHz));
        var advance = new StateAdvance(
            PerSecondDenominator: 1,
            PerSecondNumerator: -7
        );
        var row = Row(min: long.MinValue);

        for (var n = 0L; (n <= (2 * rateHz)); n += (rateHz / 4)) {
            var expected = -((7L * n) / rateHz);
            var actual = advance.ComputeCurrentValue(
                baseValue: 0L,
                currentEngineTick: (((ulong)n) * engineTicksPerSimulationTick),
                row: row
            );

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
    // Fractional per-second rates that do not divide the simulation rate still land on the same floor a per-tick
    // rational rate would, at 30, 60, and 240 Hz alike — the rate is authored once and means the same thing at
    // every rate the simulation might run at.
    [InlineData(30L, 7L, 3L)]
    [InlineData(60L, 7L, 3L)]
    [InlineData(240L, 7L, 3L)]
    [Theory]
    public void AFractionalRateNotDividingTheSimulationRateStillMatchesThePerTickFloor(long rateHz, long numerator, long denominator) {
        var engineTicksPerSimulationTick = (Puck.Maths.FixedTickConversion.TicksPerSecond / ((ulong)rateHz));
        var advance = new StateAdvance(
            PerSecondDenominator: denominator,
            PerSecondNumerator: numerator
        );
        var row = Row(max: long.MaxValue);

        for (var n = 0L; (n <= (5 * rateHz)); n += (rateHz / 5)) {
            var expected = ((numerator * n) / (denominator * rateHz));
            var actual = advance.ComputeCurrentValue(
                baseValue: 0L,
                currentEngineTick: (((ulong)n) * engineTicksPerSimulationTick),
                row: row
            );

            Assert.Equal(
                actual: actual,
                expected: expected
            );
        }
    }
}
