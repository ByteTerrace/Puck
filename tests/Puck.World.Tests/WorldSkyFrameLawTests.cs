using System.Numerics;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The shared authored frame surrounds each local layer rotation and uses the ordinary direction guard.</summary>
public sealed class WorldSkyFrameLawTests {
    private static Quaternion Inverse(SdfLighting lighting) {
        var sky = Assert.IsType<SdfSkySnapshot>(lighting.Sky);
        var common = Assert.IsType<SdfSkyParameterTable<SdfSkyLayerData>>(sky.Common);
        var value = common.Rows[0].InverseRotation;
        return new(value.X, value.Y, value.Z, value.W);
    }

    private static void Near(Vector3 expected, Vector3 actual) => Assert.InRange(Vector3.Distance(expected, actual), 0f, 2e-6f);

    [Fact]
    public void The_shared_frame_surrounds_layer_rotation_and_its_integrated_motion() {
        var definition = Fixtures.BuildDocument() with {
            RenderRaw = new(Sky: new([new WorldRenderSkyLayer.Pattern {
                Name = "paint", Transform = new(Rate: MathF.PI / 4f),
            }]) { Frame = new(Up: Vector3.UnitX) }),
        };
        Assert.True(WorldDefinitionValidator.TryValidateLocally(definition, out var reason), reason);
        var resolve = new WorldEnvironmentResolve();
        var mirror = ClientFixtures.StateMirror(definition);
        var initial = Inverse(resolve.Resolve(definition, 0, mirror));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, initial));
        Near(Vector3.UnitX, Vector3.Transform(-Vector3.UnitY, initial));
        mirror.Refresh(new WorldStateStamp(2, 2 * EngineTicks.PerSecond, ReadOnlyMemory<int>.Empty, false));
        mirror.Apply(1f);
        var moved = Inverse(resolve.Resolve(definition, 0, mirror));
        // A local quarter-turn sends +Z to +X, then the shared frame sends +X to world -Y.
        Near(Vector3.UnitZ, Vector3.Transform(-Vector3.UnitY, moved));
        Near(Vector3.UnitY, Vector3.Transform(Vector3.UnitX, moved));
        Assert.Equal(2, resolve.RateEvaluations);
        _ = resolve.Resolve(definition, 0, mirror);
        Assert.Equal(2, resolve.RateEvaluations);
    }

    [Fact]
    public void A_frame_refuses_initial_zero_and_holds_the_last_presented_axis_until_recovery() {
        static WorldStateRow Row(int axis) => new(CellName.Parse("axis"), CellKind.Int,
            Cells: [new StateCell(WorldStateRow.SlotKey, CellValue.Int(axis))]);
        var sky = new WorldRenderSky([new WorldRenderSkyLayer.Pattern { Name = "paint" }]) {
            Frame = new(new BindableDirection(0f, new BindableScalar(binding: "state.axis"), 0f)),
        };
        var invalid = Fixtures.BuildDocument().WithWorldState([Row(0)]) with { RenderRaw = new(Sky: sky) };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(invalid, out var reason));
        Assert.Contains("render.sky.frame.up", reason);
        var admitted = invalid.WithWorldState([Row(1)]);
        Assert.True(WorldDefinitionValidator.TryValidateLocally(admitted, out reason), reason);
        var live = admitted;
        var mirror = new WorldStateMirror(new WorldDocumentStateView(() => live));
        mirror.Install(0, 0);
        var resolve = new WorldEnvironmentResolve();
        var transitions = new List<WorldValueDomainDiagnostic>();
        resolve.Domains.Transition += transitions.Add;
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, Inverse(resolve.Resolve(live, 0, mirror))));
        Quaternion At(int axis, ulong tick) {
            live = admitted.WithWorldState([Row(axis)]);
            mirror.Refresh(new WorldStateStamp(tick, tick * EngineTicks.PerSecond, new int[] { 0 }, false));
            mirror.Apply(1f);
            return Inverse(resolve.Resolve(live, 0, mirror));
        }
        var opposite = At(-1, 1);
        Near(Vector3.UnitY, Vector3.Transform(-Vector3.UnitY, opposite));
        Near(-Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, opposite));
        Assert.Equal(opposite, At(0, 2));
        var checks = resolve.Domains.Checks;
        Assert.Equal(opposite, Inverse(resolve.Resolve(live, 0, mirror)));
        Assert.Equal(checks, resolve.Domains.Checks);
        Near(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, At(1, 3)));
        Assert.Equal(2, transitions.Count);
        Assert.Equal("render.sky.frame.up", transitions[0].Field);
        Assert.Equal("held", transitions[0].Action);
        Assert.Equal("recovered", transitions[1].Action);
    }
}
