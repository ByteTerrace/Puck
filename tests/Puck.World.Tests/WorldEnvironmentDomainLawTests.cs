using Puck.Hosting;
using System.Numerics;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Promoted render fields use the shared domain guard at admission and at the actual environment seam.</summary>
public sealed class WorldEnvironmentDomainLawTests {
    private static WorldStateRow Row(string name, int value) => new(CellName.Parse(candidate: name), CellKind.Int,
        Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(value: value))]);
    private static void Present(WorldStateMirror mirror, ulong seconds, params int[] moved) {
        mirror.Refresh(stamp: new WorldStateStamp(EngineTick: (seconds * EngineTicks.PerSecond), Everything: false, MovedRows: moved, Tick: seconds));
        mirror.Apply(fraction: 1f);
    }
    private static WorldDefinition Stars(BindableScalar depth, int initial) => Fixtures.BuildDocument().WithWorldState([Row(name: "depth", value: initial)]) with {
        RenderRaw = new WorldRenderDefaults(Sky: new(Layers: [new WorldRenderSkyLayer.Stars(Twinkle: new(Depth: depth)) { Name = "night" }])),
        TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]),
    };

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void Initial_bound_values_outside_the_field_domain_refuse_even_in_an_inactive_key(bool keyed) {
        BindableScalar bound = new(binding: "state.depth");

        if (keyed) { bound = new(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 0.5f), new(4d, bound)])); }
        var invalid = Stars(depth: bound, initial: 2);

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: invalid, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "render.sky.layers[0].twinkle.depth");
        Assert.Contains(actualString: reason, expectedSubstring: "state.depth");
        Assert.Contains(actualString: reason, expectedSubstring: "initially to 2");
        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: Stars(depth: bound, initial: 1), reason: out reason), userMessage: reason);
    }
    [Fact]
    public void A_bound_twinkle_depth_clamps_before_packing_and_only_changed_inputs_add_work() {
        var admitted = Stars(depth: new(binding: "state.depth"), initial: 1);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var reason), userMessage: reason);
        var live = admitted;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => live));

        mirror.Install(engineTick: 0, tick: 0);
        var resolve = new WorldEnvironmentResolve();
        var transitions = new List<WorldValueDomainDiagnostic>();

        resolve.Domains.Transition += transitions.Add;
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        live = admitted.WithWorldState([Row(name: "depth", value: 2)]);
        Present(mirror, 1, 0);
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(1, resolve.Domains.Clamps);
        var checks = resolve.Domains.Checks;

        Present(mirror, 2);
        Assert.Equal(1f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(checks, resolve.Domains.Checks);
        live = admitted.WithWorldState([Row(name: "depth", value: 0)]);
        Present(mirror, 3, 0);
        Assert.Equal(0f, resolve.Resolve(admitted, 0, mirror).TwinkleDepth);
        Assert.Equal(2, transitions.Count);
        Assert.Equal("render.sky.layers[night].twinkle.depth", transitions[0].Field);
        Assert.Equal("clamped", transitions[0].Action);
        Assert.Equal("recovered", transitions[1].Action);
        Assert.True(condition: resolve.TryRead(kind: WorldEnvironmentResolve.DomainClamps, value: out var clamps));
        Assert.Equal(actual: clamps, expected: 1);
    }
    [Fact]
    public void Crossing_curvature_keys_hold_the_last_valid_pair_and_a_new_view_uses_admitted_tick_zero() {
        var low = new BindableScalar(keys: new WorldKeys<BindableScalar>(Clock: "day", Keys: [new(0d, 1f), new(4d, 3f)]));
        var definition = Fixtures.BuildDocument() with {
            TimelineRaw = new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]),
            RenderRaw = new WorldRenderDefaults(Lighting: new(Curvature: new(Ink: 1f, InkLow: low, InkHigh: 2f))),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason), userMessage: reason);
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
        var admitted = Fixtures.BuildDocument().WithWorldState([Row(name: "scale", value: 7)]) with {
            RenderRaw = new WorldRenderDefaults(Sky: new(Layers: [new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar(binding: "state.scale")) { Name = "air" }])),
        };

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out var reason), userMessage: reason);
        var live = admitted.WithWorldState([Row(name: "scale", value: 0)]);
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => live));

        mirror.Install(engineTick: 0, tick: 0);
        var resolve = new WorldEnvironmentResolve();

        Assert.Equal(7f, resolve.Resolve(admitted, 0, mirror).CloudScale);
        Assert.Equal(7d, Assert.Single(collection: resolve.Domains.Diagnostics).Used);
        var replacement = admitted.WithWorldState([Row(name: "scale", value: 11)]);

        Assert.Equal(11f, resolve.Resolve(replacement, 1, mirror).CloudScale);
    }
    [Fact]
    public void State_only_definition_copies_preserve_the_views_latest_valid_value() {
        var admitted = Fixtures.BuildDocument().WithWorldState([Row(name: "scale", value: 7)]) with {
            RenderRaw = new WorldRenderDefaults(Sky: new(Layers: [new WorldRenderSkyLayer.Clouds(Scale: new BindableScalar(binding: "state.scale")) { Name = "air" }])),
        };
        var live = admitted;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => live));

        mirror.Install(engineTick: 0, tick: 0);
        var resolve = new WorldEnvironmentResolve();

        Assert.Equal(7f, resolve.Resolve(live, 0, mirror).CloudScale);
        live = admitted.WithWorldState([Row(name: "scale", value: 11)]);
        Present(mirror, 1, 0);
        Assert.Equal(11f, resolve.Resolve(live, 0, mirror).CloudScale);
        live = admitted.WithWorldState([Row(name: "scale", value: 0)]);
        Present(mirror, 2, 0);
        Assert.Equal(11f, resolve.Resolve(live, 0, mirror).CloudScale);
        Assert.Equal(11d, Assert.Single(collection: resolve.Domains.Diagnostics).Used);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void Directional_lights_and_softboxes_refuse_initial_zero_then_hold_runtime_zero(bool softbox) {
        var direction = new BindableDirection(x: new BindableScalar(binding: "state.axis"), y: 0f, z: 0f);
        var render = (softbox
            ? new WorldRenderDefaults(Environment: new(Softboxes: [new WorldRenderSoftbox(direction, new(x: 1f, y: 1f)) { Name = "panel" }]))
            : new WorldRenderDefaults(Lighting: new(Lights: [new WorldRenderLight.Directional(Direction: direction) { Name = "sun" }])));
        var invalid = Fixtures.BuildDocument().WithWorldState([Row(name: "axis", value: 0)]) with { RenderRaw = render };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: invalid, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "direction");
        Assert.Contains(actualString: reason, expectedSubstring: "nonzero");
        var admitted = invalid.WithWorldState([Row(name: "axis", value: 1)]);

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(definition: admitted, reason: out reason), userMessage: reason);
        var live = admitted;
        var mirror = new WorldStateMirror(view: new WorldDocumentStateView(definition: () => live));

        mirror.Install(engineTick: 0, tick: 0);
        var resolve = new WorldEnvironmentResolve();

        Vector3 Read() {
            var environment = resolve.Resolve(live, 0, mirror);

            return (softbox ? environment.GetSoftbox(index: 0).Direction : environment.GetLight(index: 0).Direction);
        }
        Assert.Equal(Vector3.UnitX, Read());
        live = admitted.WithWorldState([Row(name: "axis", value: -1)]);
        Present(mirror, 1, 0);
        Assert.Equal(-Vector3.UnitX, Read());
        live = admitted.WithWorldState([Row(name: "axis", value: 0)]);
        Present(mirror, 2, 0);
        Assert.Equal(-Vector3.UnitX, Read());
        Assert.Equal(1, resolve.Domains.Holds);
    }
}
