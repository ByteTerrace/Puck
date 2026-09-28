using System.Numerics;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Admission and live presentation share numeric domains; live invalidity cannot silently substitute a
/// default or repeatedly spend work on an unchanged input.</summary>
public sealed class WorldValueDomainLawTests {
    [Fact]
    public void Closed_bounds_clamp_each_changed_input_and_report_only_invalidity_transitions() {
        var guard = new WorldValueDomainGuard();
        var reports = new List<WorldValueDomainDiagnostic>();
        guard.Transition += reports.Add;
        Assert.Equal(0.4d, guard.Scalar("stars.depth", "state.depth", 0.4d, 0.25d, WorldValueDomain.Unit));
        Assert.Equal(1d, guard.Scalar("stars.depth", "state.depth", 2d, 0.25d, WorldValueDomain.Unit));
        Assert.Equal(0d, guard.Scalar("stars.depth", "state.depth", -1d, 0.25d, WorldValueDomain.Unit));
        Assert.Equal(0.6d, guard.Scalar("stars.depth", "state.depth", 0.6d, 0.25d, WorldValueDomain.Unit));
        Assert.Equal(4, guard.Checks);
        Assert.Equal(2, guard.Clamps);
        Assert.Equal(0, guard.Holds);
        Assert.Equal(2, reports.Count);
        Assert.Equal(new WorldValueDomainDiagnostic("stars.depth", "state.depth", 2d, 1d, WorldValueDomain.Unit.ToString(), "clamped"), reports[0]);
        Assert.Equal("recovered", reports[1].Action);
        Assert.Equal(0.6d, reports[1].Used);
        Assert.Empty(guard.Diagnostics);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-2d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Open_bounds_and_nonfinite_inputs_hold_the_actual_last_valid_value(double invalid) {
        var guard = new WorldValueDomainGuard();
        Assert.Equal(3d, guard.Scalar("cloud.scale", "state.scale", 3d, 2d, WorldValueDomain.Positive));
        Assert.Equal(3d, guard.Scalar("cloud.scale", "state.scale", invalid, 2d, WorldValueDomain.Positive));
        Assert.Equal(1, guard.Holds);
        Assert.Equal("held", Assert.Single(guard.Diagnostics).Action);
        var checks = guard.Checks;
        Assert.Equal(3d, guard.Scalar("cloud.scale", "state.scale", invalid, 2d, WorldValueDomain.Positive));
        Assert.Equal(checks, guard.Checks);
        Assert.Equal(1, guard.Holds);
        Assert.Equal(4d, guard.Scalar("cloud.scale", "state.scale", 4d, 2d, WorldValueDomain.Positive));
        Assert.Empty(guard.Diagnostics);
    }

    [Fact]
    public void Curvature_holds_both_last_valid_thresholds_across_equal_and_reversed_inputs() {
        var guard = new WorldValueDomainGuard();
        var reports = new List<WorldValueDomainDiagnostic>();
        guard.Transition += reports.Add;
        Assert.Equal((2d, 9d), guard.OrderedPair("ink", "clock day/state.high", 2d, 9d, 1d, 8d));
        Assert.Equal((2d, 9d), guard.OrderedPair("ink", "clock day/state.high", 4d, 4d, 1d, 8d));
        Assert.Equal((2d, 9d), guard.OrderedPair("ink", "clock day/state.high", 5d, 3d, 1d, 8d));
        Assert.Equal((1d, 6d), guard.OrderedPair("ink", "clock day/state.high", 1d, 6d, 1d, 8d));
        Assert.Equal(2, reports.Count);
        Assert.Equal(9d, reports[0].SecondUsed);
        Assert.Equal("recovered", reports[1].Action);
        Assert.Equal(2, guard.Holds);
    }

    [Fact]
    public void A_hold_after_a_closed_bound_clamp_keeps_the_last_valid_presented_value() {
        var guard = new WorldValueDomainGuard();
        var domain = new WorldValueDomain(0d, 1d, MinimumOpen: true);
        Assert.Equal(0.4d, guard.Scalar("softness", "state.softness", 0.4d, 0.5d, domain));
        Assert.Equal(1d, guard.Scalar("softness", "state.softness", 2d, 0.5d, domain));
        Assert.Equal(1d, guard.Scalar("softness", "state.softness", 0d, 0.5d, domain));
        var diagnostic = Assert.Single(guard.Diagnostics);
        Assert.Equal("held", diagnostic.Action);
        Assert.Equal(0d, diagnostic.Value);
        Assert.Equal(1d, diagnostic.Used);
    }

    [Fact]
    public void A_new_view_during_invalid_input_holds_its_admitted_initial_binding_and_revision_reseeds_it() {
        static WorldDefinition Document(int initial) => new(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("scale"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(initial))]),
        ]));
        var definition = Document(7);
        var source = new Source { Value = double.NaN };
        var values = new WorldValueResolver(definition, default, source);
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(guard, definition, "cloud");
        var binding = new BindableScalar("state.scale");
        Assert.Equal(7f, group.Scalar("scale", binding, values, 1f, WorldValueDomain.Positive));
        Assert.Equal(7d, Assert.Single(guard.Diagnostics).Used);
        source.Value = 9d;
        Assert.Equal(9f, group.Scalar("scale", binding, values, 1f, WorldValueDomain.Positive));
        source.Value = -1d;
        Assert.Equal(9f, group.Scalar("scale", binding, values, 1f, WorldValueDomain.Positive));
        guard.Reset();
        group = new(guard, Document(11), "cloud");
        Assert.Equal(11f, group.Scalar("scale", binding, values, 1f, WorldValueDomain.Positive));
    }

    [Fact]
    public void Repeated_valid_pair_inputs_allocate_nothing_and_add_no_domain_work() {
        var definition = new WorldDefinition();
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(guard, definition, "softbox");
        var values = new WorldValueResolver(definition, default);
        var size = new BindableVector2(2f, 3f);
        for (var index = 0; index < 10; index++) { _ = group.Pair("size", size, values, Vector2.One, WorldValueDomain.Positive); }
        var checks = guard.Checks;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { _ = group.Pair("size", size, values, Vector2.One, WorldValueDomain.Positive); }
        Assert.Equal(allocated, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(checks, guard.Checks);
    }

    private sealed class Source : IWorldValueSource {
        public double Value { get; set; }
        public bool TryScalar(in StateBinding binding, out double value) { value = Value; return true; }
        public bool TryColor(in StateBinding binding, out Vector4 value) { value = default; return false; }
    }

    [Theory]
    [InlineData(false, 1e300)]
    [InlineData(false, 1e-300)]
    [InlineData(true, 1e300)]
    [InlineData(true, 1e-300)]
    public void Scalar_and_angle_consumers_hold_values_that_overflow_or_underflow_their_presented_format(bool angle, double invalid) {
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("value"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(2))]),
        ]));
        var source = new Source { Value = 3d };
        var values = new WorldValueResolver(definition, default, source);
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(guard, definition, "field");
        var binding = new BindableScalar("state.value");
        float Resolve() => angle ? group.Angle("angle", new BindableAngle(binding), values, 1f, WorldValueDomain.Positive)
            : group.Scalar("scalar", binding, values, 1f, WorldValueDomain.Positive);
        Assert.Equal(3f, Resolve());
        source.Value = invalid;
        Assert.Equal(3f, Resolve());
        Assert.Equal(1, guard.Holds);
        Assert.Equal((float)invalid, Assert.Single(guard.Diagnostics).Value);
    }

    [Fact]
    public void Curvature_checks_the_final_binary32_edges_when_distinct_doubles_would_round_equal() {
        var before = float.BitDecrement(2f);
        var low = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, before), new(4d, 2f)]));
        var definition = new WorldDefinition(TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));
        var guard = new WorldValueDomainGuard();
        var group = new WorldValueDomainGroup(guard, definition, "curvature");
        var values = new WorldValueResolver(definition, new PresentedTick(2UL * EngineTicks.PerSecond, 0d));
        Assert.True(values.Scalar(low, 0d) < 2d);
        Assert.Equal((before, 2f), group.Curvature(low, 2f, values, 0f, 10f));
        Assert.Equal(1, guard.Holds);
    }

    [Theory]
    [InlineData(false, 0d)]
    [InlineData(false, double.NaN)]
    [InlineData(false, double.PositiveInfinity)]
    [InlineData(true, 0d)]
    [InlineData(true, double.NaN)]
    [InlineData(true, double.PositiveInfinity)]
    public void Directions_hold_the_latest_valid_triple_and_report_invalidity_and_recovery(bool keyed, double invalid) {
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("axis"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(1))]),
        ]), TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));
        var direction = new BindableDirection(new BindableScalar("state.axis"), 0f, 0f);
        if (keyed) { direction = new(new WorldKeys<BindableDirection>("day", [new(0d, direction), new(4d, Vector3.UnitY)])); }
        var source = new Source { Value = -1d };
        var values = new WorldValueResolver(definition, default, source);
        var guard = new WorldValueDomainGuard();
        var reports = new List<WorldValueDomainDiagnostic>();
        guard.Transition += reports.Add;
        var group = new WorldValueDomainGroup(guard, definition, "sun");
        Assert.Equal(-Vector3.UnitX, group.Direction("direction", direction, values, Vector3.UnitY));
        source.Value = invalid;
        Assert.Equal(-Vector3.UnitX, group.Direction("direction", direction, values, Vector3.UnitY));
        var checks = guard.Checks;
        Assert.Equal(-Vector3.UnitX, group.Direction("direction", direction, values, Vector3.UnitY));
        Assert.Equal(checks, guard.Checks);
        Assert.Equal(1, guard.Holds);
        Assert.Equal(0d, Assert.Single(guard.Diagnostics).ThirdUsed);
        source.Value = 1d;
        Assert.Equal(Vector3.UnitX, group.Direction("direction", direction, values, Vector3.UnitY));
        Assert.Equal(2, reports.Count);
        Assert.Equal("held", reports[0].Action);
        Assert.Equal("recovered", reports[1].Action);
    }
}
