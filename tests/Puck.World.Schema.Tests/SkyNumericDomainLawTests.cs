using System.Numerics;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Sky shaping is checked in the shader's consumed format, with ordinary shared domain holds.</summary>
public sealed class SkyNumericDomainLawTests {
    [Theory]
    [InlineData("noise", "scale")]
    [InlineData("clouds", "normalTap")]
    [InlineData("aurora", "width")]
    [InlineData("stars", "radiusFraction")]
    public void Every_authored_key_leaf_owes_its_consumed_format_constraint(string kind, string field) {
        var value = new BindableScalar(keys: new WorldKeys<BindableScalar>("day", [new(0d, 1f), new(4d, 1e-39f)]));
        WorldRenderSkyLayer layer = kind switch {
            "noise" => new WorldRenderSkyLayer.Noise(Scale: value),
            "clouds" => new WorldRenderSkyLayer.Clouds { NormalTap = value },
            "aurora" => new WorldRenderSkyLayer.Aurora(Width: value),
            _ => new WorldRenderSkyLayer.Stars { RadiusFraction = value },
        };
        var definition = new WorldDefinition(RenderRaw: new(Sky: new([layer with { Name = "paint" }])),
            TimelineRaw: new(Clocks: [new("day", PeriodSeconds: 8d)]));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
        Assert.Contains(field + ".keys[1].value", reason);
        Assert.Contains("normal binary32", reason);
    }

    [Fact]
    public void Star_radius_is_formed_before_narrowing_and_refuses_a_flushed_or_infinite_native_radius() {
        Assert.True(WorldSkyNumeric.StarsFit([1e-38f, 2f]));
        Assert.Equal((float)((double)1e-38f / 2d * Math.PI), WorldSkyNumeric.AngularRadius(1e-38f, 2f));
        Assert.True(float.IsNormal(WorldSkyNumeric.AngularRadius(1e-38f, 2f)));
        Assert.False(WorldSkyNumeric.StarsFit([1e-38f, 1e10f]));
        Assert.False(WorldSkyNumeric.StarsFit([float.MaxValue, 1f]));
        Assert.False(WorldSkyNumeric.StarsFit([0f, 48f]));
    }

    [Fact]
    public void A_vector_offset_holds_only_its_invalid_component_without_normalizing_the_valid_magnitude() {
        var guard = new WorldValueDomainGuard();
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("x"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(1))]),
        ]));
        var vector = new BindableVector3(new BindableScalar("state.x"), 2f, 3f);
        var source = new Source { Value = 5d };
        var values = new WorldValueResolver(definition, default, source);
        var group = new WorldValueDomainGroup(guard, definition, "sky.paint");
        Assert.Equal(new Vector3(5f, 2f, 3f), group.Vector("offset", vector, values, Vector3.Zero, WorldValueDomain.Finite));
        source.Value = double.NaN;
        Assert.Equal(new Vector3(5f, 2f, 3f), group.Vector("offset", vector, values, Vector3.Zero, WorldValueDomain.Finite));
        Assert.Equal("sky.paint.offset[0]", Assert.Single(guard.Diagnostics).Field);
        var checks = guard.Checks;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { _ = group.Vector("offset", vector, values, Vector3.Zero, WorldValueDomain.Finite); }
        Assert.Equal(bytes, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(checks, guard.Checks);
    }

    [Fact]
    public void A_native_reciprocal_accepts_useful_subnormal_denominators_but_refuses_nonrepresentable_factors() {
        Assert.True(WorldSkyNumeric.NoiseFits([1e-38f]));
        Assert.True(float.IsNormal(WorldSkyNumeric.Reciprocal(1e-38f)));
        Assert.False(WorldSkyNumeric.NoiseFits([1e-39f]));
        Assert.False(WorldSkyNumeric.NoiseFits([float.MaxValue]));
        Assert.False(WorldSkyNumeric.NoiseFits([0f]));
        Assert.False(WorldSkyNumeric.NoiseFits([float.NaN]));
        Assert.True(WorldSkyNumeric.NoiseFits([.00001f]));
        Assert.Equal(100000f, WorldSkyNumeric.Reciprocal(.00001f));
    }

    [Fact]
    public void Coupled_cloud_and_curtain_bounds_check_actual_products_without_an_arbitrary_dome_cap() {
        Assert.True(WorldSkyNumeric.CloudsFit([2f, float.MaxValue, .6f, .18f, .7f, .25f, .05f]));
        Assert.False(WorldSkyNumeric.CloudsFit([1e-20f, float.MaxValue, .6f, .18f, .7f, .25f, .05f]));
        Assert.False(WorldSkyNumeric.CloudsFit([2f, 6f, .6f, .18f, float.MaxValue, .25f, .05f]));
        Assert.False(WorldSkyNumeric.CloudsFit([2f, 6f, float.MaxValue, float.MaxValue / 3f, 0f, .25f, .05f]));
        Assert.True(WorldSkyNumeric.AuroraFits([.15f, .15f, .18f]));
        Assert.False(WorldSkyNumeric.AuroraFits([.15f, float.MaxValue, .18f]));
        Assert.False(WorldSkyNumeric.AuroraFits([.15f, .15f, 1e-39f]));
    }

    [Fact]
    public void A_live_derived_constraint_holds_the_whole_latest_tuple_and_reports_two_transitions() {
        var guard = new WorldValueDomainGuard();
        var reports = new List<WorldValueDomainDiagnostic>();
        var source = new Source { Value = .25d };
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("scale"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(2))]),
        ]));
        var layer = new WorldRenderSkyLayer.Noise(Scale: new BindableScalar("state.scale"));
        var tuple = WorldSkyNumeric.Noise(layer);
        var group = new WorldValueDomainGroup(guard, definition, "render.sky.layers[paint]");
        var values = new WorldValueResolver(definition, default, source);

        guard.Transition += reports.Add;
        Assert.Equal(.25f, group.Tuple(tuple, values)[0]);
        source.Value = 1e-39d;
        Assert.Equal(.25f, group.Tuple(tuple, values)[0]);
        var checks = guard.Checks;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { _ = group.Tuple(tuple, values); }
        Assert.Equal(bytes, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(checks, guard.Checks);
        Assert.Equal(1, guard.Holds);
        var diagnostic = Assert.Single(guard.Diagnostics);
        Assert.Contains("scale=state.scale", diagnostic.Source);
        Assert.Contains("positive normal binary32 reciprocal", diagnostic.Domain);
        Assert.Equal("[0.25]", diagnostic.UsedValues);
        source.Value = .5d;
        Assert.Equal(.5f, group.Tuple(tuple, values)[0]);
        Assert.Equal(2, reports.Count);
        Assert.Equal("held", reports[0].Action);
        Assert.Equal("recovered", reports[1].Action);
    }

    [Fact]
    public void A_coupled_hold_does_not_keep_only_the_field_that_caused_the_invalid_product() {
        var guard = new WorldValueDomainGuard();
        float[] initial = [.15f, .15f, .18f];
        float[] valid = [.5f, 2f, .25f];
        float[] invalid = [.25f, float.MaxValue, .5f];

        Assert.Equal(valid, guard.Tuple("aurora.coordinates", "scale/height/width", valid, initial,
            WorldSkyNumeric.AuroraFits, "finite curtain coordinates").ToArray());
        Assert.Equal(valid, guard.Tuple("aurora.coordinates", "scale/height/width", invalid, initial,
            WorldSkyNumeric.AuroraFits, "finite curtain coordinates").ToArray());
        Assert.Equal("[0.5, 2, 0.25]", Assert.Single(guard.Diagnostics).UsedValues);
    }

    private sealed class Source : IWorldValueSource {
        public double Value { get; set; }
        public bool TryScalar(in StateBinding binding, out double value) { value = Value; return true; }
        public bool TryColor(in StateBinding binding, out Vector4 value) { value = default; return false; }
    }

    [Fact]
    public void A_scalar_hold_after_a_rejected_tuple_uses_the_last_presented_component() {
        var guard = new WorldValueDomainGuard();
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [
            new WorldStateRow(CellName.Parse("scale"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(1))]),
            new WorldStateRow(CellName.Parse("height"), CellKind.Int, Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(1))]),
        ]));
        var layer = new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar("state.scale")) { Height = new BindableScalar("state.height") };
        var tuple = WorldSkyNumeric.Clouds(layer);
        var source = new TupleSource { Scale = .5d, Height = 2d };
        var group = new WorldValueDomainGroup(guard, definition, "render.sky.layers[clouds]");
        var values = new WorldValueResolver(definition, default, source);

        Assert.Equal(.5f, group.Tuple(tuple, values)[0]);
        source.Scale = .25d;
        source.Height = float.MaxValue;
        Assert.Equal(.5f, group.Tuple(tuple, values)[0]);
        source.Scale = double.NaN;
        source.Height = 2d;
        Assert.Equal(.5f, group.Tuple(tuple, values)[0]);
        Assert.Equal(2f, group.Tuple(tuple, values)[4]);
        var checks = guard.Checks;
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) { _ = group.Tuple(tuple, values); }
        Assert.Equal(bytes, GC.GetAllocatedBytesForCurrentThread());
        Assert.Equal(checks, guard.Checks);
        source.Scale = .5d;
        source.Height = -1d;
        Assert.Equal(0f, group.Tuple(tuple, values)[4]);
        source.Height = double.NaN;
        Assert.Equal(0f, group.Tuple(tuple, values)[4]);
        Assert.Equal(1, guard.Clamps);
    }

    private sealed class TupleSource : IWorldValueSource {
        public double Scale { get; set; }
        public double Height { get; set; }
        public bool TryScalar(in StateBinding binding, out double value) {
            value = binding.Row.ToString() == "scale" ? Scale : Height;
            return true;
        }
        public bool TryColor(in StateBinding binding, out Vector4 value) { value = default; return false; }
    }
}
