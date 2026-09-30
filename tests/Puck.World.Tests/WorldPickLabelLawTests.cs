using Puck.Abstractions.Counting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a hover label names what a pick answered only at the pixel it answered for. A pointer that moves
/// on while a copy is in flight is shown the older answer with that answer's own coordinates, never the placement under
/// coordinates nobody asked about, and a steady answer's label allocates nothing.
/// </summary>
public sealed class WorldPickLabelLawTests {
    [Fact]
    public void TheLabelCarriesTheCoordinatesItsAnswerWasSampledAt() {
        var maps = new WorldPickMapBuilder();

        maps.Instances(first: 0, end: 1, target: new WorldPickTarget(BodyIndex: null, Placement: "crate"));
        maps.Instances(first: 1, end: 2, target: new WorldPickTarget(BodyIndex: 3, Placement: null));
        var map = maps.Snapshot(pool: []);
        var program = new SdfProgramBuilder().Build();
        var label = new WorldPickLabel();

        SdfPickResult Answer(uint x, uint y, uint source) => new(Request: 1, X: x, Y: y, Width: 32, Height: 32,
            Identity: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Sdf, source: source), Distance: 1, Material: 0,
            Program: program, MeshRevision: 0, Map: map);

        Assert.Null(@object: label.Of(pick: null));
        foreach (var (x, y, source) in new (uint, uint, uint)[] { (3, 4, 1), (10, 4, 1), (10, 5, 1), (10, 5, 2), (11, 5, 1) }) {
            var answer = Answer(source: source, x: x, y: y);

            Assert.Equal(
                actual: label.Of(pick: answer),
                expected: ((source == 1) ? $"placement 'crate' at {x},{y}" : $"body 3 at {x},{y}")
            );
        }
        // A miss names nothing, whatever it named before.
        Assert.Null(@object: label.Of(pick: Answer(source: 1, x: 11, y: 5) with { Identity = 0 }));

        var steady = Answer(source: 1, x: 7, y: 9);
        var text = label.Of(pick: steady);

        Assert.Same(expected: text, actual: label.Of(pick: steady));
        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: () => label.Of(pick: steady)));
    }
}
