using Puck.Hosting;
using System.Numerics;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Promoted render fields use the shared domain guard at admission and at the actual environment seam.</summary>
public sealed class WorldEnvironmentDomainLawTests {
    private static WorldStateRow Row(string name, int value) => new(CellName.Parse(name), CellKind.Int,
        Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value))]);
    private static void Present(WorldStateMirror mirror, ulong seconds, params int[] moved) {
        mirror.Refresh(new WorldStateStamp(seconds, seconds * EngineTicks.PerSecond, moved, false));
        mirror.Apply(1f);
    }
    private static WorldDefinition Stars(BindableScalar depth, int initial) => Fixtures.BuildDocument().WithWorldState([Row("depth", initial)]) with {
        RenderRaw = new WorldRenderDefaults(Sky: new([new WorldRenderSkyLayer.Stars(Twinkle: new(Depth: depth)) { Name = "night" }])),
        TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Initial_bound_values_outside_the_field_domain_refuse_even_in_an_inactive_key(bool keyed) {
        BindableScalar bound = new("state.depth");
        if (keyed) { bound = new(new WorldKeys<BindableScalar>("day", [new(0d, 0.5f), new(4d, bound)])); }
        var invalid = Stars(bound, 2);
        Assert.False(WorldDefinitionValidator.TryValidateLocally(invalid, out var reason));
        Assert.Contains("render.sky.layers[0].twinkle.depth", reason);
        Assert.Contains("state.depth", reason);
        Assert.Contains("initially to 2", reason);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(Stars(bound, 1), out reason), reason);
    }

    [Fact]
    public void A_bound_twinkle_depth_clamps_before_packing_and_only_changed_inputs_add_work() {
        var admitted = Stars(new("state.depth"), 1);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(admitted, out var reason), reason);
        var live = admitted;
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldEnvironmentResolve();
        var transitions = new List<WorldValueDomainDiagnostic>();
        resolve.Domains.Transition += transitions.Add;
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        live = admitted.WithWorldState([Row("depth", 2)]);
        Present(mirror, 1, 0);
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(1, resolve.Domains.Clamps);
        var checks = resolve.Domains.Checks;
        Present(mirror, 2);
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(checks, resolve.Domains.Checks);
        live = admitted.WithWorldState([Row("depth", 0)]);
        Present(mirror, 3, 0);
        Assert.Equal(0f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(2, transitions.Count);
        Assert.Equal("render.sky.layers[night].twinkle.depth", transitions[0].Field);
        Assert.Equal("clamped", transitions[0].Action);
        Assert.Equal("recovered", transitions[1].Action);
        Assert.True(resolve.TryRead(WorldEnvironmentResolve.DomainClamps, out var clamps));
        Assert.Equal(1, clamps);
    }

    [Fact]
    public void Crossing_curvature_keys_hold_the_last_valid_pair_and_a_new_view_uses_admitted_tick_zero() {
        var low = new BindableScalar(new WorldKeys<BindableScalar>("day", [new(0d, 1f), new(4d, 3f)]));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]),
            RenderRaw = new WorldRenderDefaults(Lighting: new(Curvature: new(Ink: 1f, InkLow: low, InkHigh: 2f))),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var mirror = ClientFixtures.StateMirror(definition);
        var resolve = new WorldEnvironmentResolve();
        var reports = new List<WorldValueDomainDiagnostic>();
        resolve.Domains.Transition += reports.Add;
        Present(mirror, 1);
        Assert.Equal(1.5f, resolve.Resolve(definition, 0, mirror).CurvatureInkLow);
        Present(mirror, 2);
        var held = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(1.5f, held.CurvatureInkLow);
        Assert.Equal(2f, held.CurvatureInkHigh);
        Present(mirror, 4);
        Assert.Equal(1.5f, resolve.Resolve(definition, 0, mirror).CurvatureInkLow);
        var lateView = new WorldEnvironmentResolve().Resolve(definition, 0, mirror);
        Assert.Equal(1f, lateView.CurvatureInkLow);
        Assert.Equal(2f, lateView.CurvatureInkHigh);
        Present(mirror, 7);
        Assert.Equal(1.5f, resolve.Resolve(definition, 0, mirror).CurvatureInkLow);
        Assert.Equal(2, reports.Count);
        Assert.Equal("held", reports[0].Action);
        Assert.Equal("recovered", reports[1].Action);
    }

    [Fact]
    public void A_new_view_with_an_invalid_bound_cloud_scale_holds_the_authored_initial_value() {
        var admitted = Fixtures.BuildDocument().WithWorldState([Row("scale", 7)]) with {
            RenderRaw = new WorldRenderDefaults(Sky: new([new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar("state.scale")) { Name = "air" }])),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(admitted, out var reason), reason);
        var live = admitted.WithWorldState([Row("scale", 0)]);
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldEnvironmentResolve();
        Assert.Equal(7f, resolve.Resolve(admitted, 0, mirror).CloudScale);
        Assert.Equal(7d, Assert.Single(resolve.Domains.Diagnostics).Used);
        var replacement = admitted.WithWorldState([Row("scale", 11)]);
        Assert.Equal(11f, resolve.Resolve(replacement, 1, mirror).CloudScale);
    }

    [Fact]
    public void State_only_definition_copies_preserve_the_views_latest_valid_value() {
        var admitted = Fixtures.BuildDocument().WithWorldState([Row("scale", 7)]) with {
            RenderRaw = new WorldRenderDefaults(Sky: new([new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar("state.scale")) { Name = "air" }])),
        };
        var live = admitted;
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldEnvironmentResolve();
        Assert.Equal(7f, resolve.Resolve(live, 0, mirror).CloudScale);
        live = admitted.WithWorldState([Row("scale", 11)]);
        Present(mirror, 1, 0);
        Assert.Equal(11f, resolve.Resolve(live, 0, mirror).CloudScale);
        live = admitted.WithWorldState([Row("scale", 0)]);
        Present(mirror, 2, 0);
        Assert.Equal(11f, resolve.Resolve(live, 0, mirror).CloudScale);
        Assert.Equal(11d, Assert.Single(resolve.Domains.Diagnostics).Used);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Directional_lights_and_softboxes_refuse_initial_zero_then_hold_runtime_zero(bool softbox) {
        var direction = new BindableDirection(new BindableScalar("state.axis"), 0f, 0f);
        var render = softbox
            ? new WorldRenderDefaults(Environment: new(Softboxes: [new WorldRenderSoftbox(direction, new(1f, 1f)) { Name = "panel" }]))
            : new WorldRenderDefaults(Lighting: new(Lights: [new WorldRenderLight.Directional(Direction: direction) { Name = "sun" }]));
        var invalid = Fixtures.BuildDocument().WithWorldState([Row("axis", 0)]) with { RenderRaw = render };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(invalid, out var reason));
        Assert.Contains("direction", reason);
        Assert.Contains("nonzero", reason);
        var admitted = invalid.WithWorldState([Row("axis", 1)]);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(admitted, out reason), reason);
        var live = admitted;
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldEnvironmentResolve();
        Vector3 Read() {
            var environment = resolve.Resolve(live, 0, mirror);
            return softbox ? environment.GetSoftbox(0).Direction : environment.GetLight(0).Direction;
        }
        Assert.Equal(Vector3.UnitX, Read());
        live = admitted.WithWorldState([Row("axis", -1)]);
        Present(mirror, 1, 0);
        Assert.Equal(-Vector3.UnitX, Read());
        live = admitted.WithWorldState([Row("axis", 0)]);
        Present(mirror, 2, 0);
        Assert.Equal(-Vector3.UnitX, Read());
        Assert.Equal(1, resolve.Domains.Holds);
    }
}
