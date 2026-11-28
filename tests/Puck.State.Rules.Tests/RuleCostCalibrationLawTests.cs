using Xunit;

namespace Puck.State.Rules.Tests;

/// <summary>CONTRACT UNDER TEST: a calibration turns the work sheet into time line by line, charging every unit kind
/// at its own figure, a door at the door figure rather than its sheet weight, and the fixed costs of judging a binding
/// and of firing once, and tallies the same exclusion tree the units are bounded by; an unbounded line stays unbounded
/// in time, and the arena's growth units are priced at the growth figure.</summary>
public sealed class RuleCostCalibrationLawTests {
    private static readonly Dictionary<(int RowOrdinal, CellKey Key), RuleCellWriters> NoWriters = [];

    // A sweep of 100 bindings, 40 judged and 4 firing: setup 40 units, check 3 units a binding, and a firing of one
    // door and five element units.
    private static RuleWorkContributor Line(string name, RuleWork check, long elements, long doors = 1L) {
        var effects = ((doors * RuleWorkBudget.DoorUnits) + elements);

        return new(
            CheckUnits: (40L + (40L * check)),
            Cost: new RuleCost(Check: check, Effects: effects, Setup: 100L) { Doors = doors },
            Discriminators: [],
            Firings: 4L,
            FiringUnits: (4L * effects),
            IsInteraction: false,
            Judged: 40L,
            Multiplier: 100L,
            Name: name,
            SetupUnits: 40L,
            WorkUnits: ((40L + (40L * check)) + (4L * effects)),
            WorstFirings: 100L,
            WorstUnits: ((100L + (100L * check)) + (100L * effects))
        );
    }

    [Fact]
    public void TheUncalibratedFiguresPriceEveryUnitAtTheOldAssumption() {
        var line = Line(check: 3L, elements: 5L, name: "sweep");
        var (tick, worst) = RuleCostCalibration.Uncalibrated.Price(hostUnits: 7L, lines: [line], writers: NoWriters);

        Assert.Equal(expected: ((line.WorkUnits.Units + 7L) * RuleCostCalibration.UncalibratedPicosecondsPerUnit), actual: tick.Picoseconds);
        Assert.Equal(expected: ((line.WorstUnits.Units + 7L) * RuleCostCalibration.UncalibratedPicosecondsPerUnit), actual: worst.Picoseconds);
    }
    [Fact]
    public void EachQuantityIsChargedAtItsOwnFigureWithTheFixedCostsOfJudgingAndFiring() {
        var calibration = new RuleCostCalibration(Binding: 1_000L, Check: 10L, Door: 50_000L, Effect: 100L, Firing: 10_000L, Growth: 3L, Host: 2L, Setup: 1L);
        var line = Line(check: 3L, elements: 5L, name: "sweep");
        var timed = calibration.Time(line: line);

        // setup 40 x 1 + judged 40 x 1000 + check 120 x 10; four firings, each 10000 + one door 50000 + 5 elements x 100.
        Assert.Equal(expected: (40L + 40_000L + 1_200L), actual: timed.CheckUnits.Units);
        Assert.Equal(expected: (4L * (10_000L + 50_000L + 500L)), actual: timed.FiringUnits.Units);
        Assert.Equal(expected: (timed.CheckUnits.Units + timed.FiringUnits.Units), actual: timed.WorkUnits.Units);
        // The worst case judges and fires every binding: setup 100, 100 judged, 300 check units, 100 firings.
        Assert.Equal(expected: ((100L + 100_000L + 3_000L) + (100L * (10_000L + 50_000L + 500L))), actual: timed.WorstUnits.Units);
        Assert.Equal(expected: (timed.WorkUnits.Units + 14L), actual: calibration.Price(hostUnits: 7L, lines: [line], writers: NoWriters).Tick.Picoseconds);
        Assert.Equal(expected: 300L, actual: calibration.GrowthTime(units: 100L).Picoseconds);
    }
    [Fact]
    public void ADoorIsPricedAtItsFigureWhateverItsSheetWeight() {
        var calibration = new RuleCostCalibration(Binding: 0L, Check: 0L, Door: 40L, Effect: 1_000L, Firing: 0L, Growth: 0L, Host: 0L, Setup: 0L);
        var doors = calibration.Time(line: Line(check: 0L, doors: 2L, elements: 0L, name: "writes"));
        var elements = calibration.Time(line: Line(check: 0L, doors: 0L, elements: (2L * RuleWorkBudget.DoorUnits), name: "elements"));

        // Equal units on the sheet; two doors cost 80 ps a firing, where the same units of element work cost 1024000.
        Assert.Equal(expected: (4L * 80L), actual: doors.FiringUnits.Units);
        Assert.Equal(expected: (4L * 2L * RuleWorkBudget.DoorUnits * 1_000L), actual: elements.FiringUnits.Units);
    }
    [Fact]
    public void ExclusiveLinesShareOneAllowanceInTimeAsInUnits() {
        var calibration = new RuleCostCalibration(Binding: 0L, Check: 1L, Door: 1L, Effect: 1L, Firing: 50L, Growth: 1L, Host: 1L, Setup: 1L);
        var pinned = new RulePinnedCell(High: 0L, Key: default, Low: 0L, RowOrdinal: 0);
        var other = pinned with { High = 1L, Low = 1L };
        var left = Line(check: 1L, elements: 2L, name: "left") with { Discriminators = [pinned] };
        var right = Line(check: 1L, elements: 9L, name: "right") with { Discriminators = [other] };
        var tick = calibration.Price(hostUnits: 0L, lines: [left, right], writers: NoWriters).Changes.Picoseconds;
        var (leftTimed, rightTimed) = (calibration.Time(line: left), calibration.Time(line: right));

        // Both checks are charged; of two firings that pin one cell to different values, only the dearer can run.
        Assert.Equal(expected: ((leftTimed.CheckUnits.Units + rightTimed.CheckUnits.Units) + rightTimed.FiringUnits.Units), actual: tick);
    }
    [Fact]
    public void AnUnboundedLineHasNoTimeAndANegativeFigureIsRefused() {
        var line = Line(check: RuleWork.Unmodeled(reason: "operation 255"), elements: 5L, name: "sweep");
        var (tick, _) = RuleCostCalibration.Uncalibrated.Price(hostUnits: 0L, lines: [line], writers: NoWriters);

        Assert.False(condition: tick.IsKnown);
        Assert.False(condition: tick.Fits(budgetPicoseconds: long.MaxValue));
        Assert.NotNull(@object: (RuleCostCalibration.Uncalibrated with { Door = -1L }).Refusal());
        Assert.Null(@object: RuleCostCalibration.Uncalibrated.Refusal());
    }
}
