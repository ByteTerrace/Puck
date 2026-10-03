using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>Axial rotations retain the general interpreter's exact intervals and frame while avoiding its expansion.</summary>
public sealed class SdfBoundsRotationWorkLawTests(ITestOutputHelper output) {
    [Fact]
    public void AxialRotationsKeepBoundsBitsWithoutExpandingTheGeneralFormula() {
        Quaternion[] turns = [
            Quaternion.Identity, new(w: -1, x: 0, y: 0, z: 0),
            new(w: 0, x: 1, y: 0, z: 0), new(w: 0, x: -1, y: 0, z: 0),
            new(w: 0, x: 0, y: 1, z: 0), new(w: 0, x: 0, y: -1, z: 0),
            new(w: 0, x: 0, y: 0, z: 1), new(w: 0, x: 0, y: 0, z: -1),
            Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.7f)
        ];
        var hash = Fnv1aHash.Create();
        var expanded = 0L;
        var generalQueries = 0L;

        for (var turn = 0; (turn < turns.Length); turn++) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.Translate(offset: new Vector3(x: 0.3f, y: -0.7f, z: 0.5f)).Rotate(rotation: turns[turn]);
            _ = builder.Box(halfExtents: new Vector3(x: 1f, y: 0.6f, z: 0.4f), material: material, round: 0.1f);
            var evaluator = new SdfFieldEvaluator(program: builder.Build());

            hash.Add(value: evaluator.Frame.Value);

            // Include points, boxes narrower than the linear rotation slack, ordinary swept boxes, and both
            // sides of the certified frame. The digest was read from the full interval formula before the shortcut.
            long[] centers = [-262144, -65537, -1, 0, 1, 65537, 262144, -evaluator.Frame.Value, evaluator.Frame.Value];
            long[] widths = [0, 1, 2, 3, 4, 5, 131, 65536];

            foreach (var center in centers) {
                foreach (var width in widths) {
                    var lower = FixedPosition.FromLocal(local: new FixedVector3(
                        X: FixedQ4816.FromRawBits(value: center),
                        Y: FixedQ4816.FromRawBits(value: -center),
                        Z: FixedQ4816.FromRawBits(value: (center / 2))));
                    var upper = (lower + new FixedVector3(
                        X: FixedQ4816.FromRawBits(value: width),
                        Y: FixedQ4816.FromRawBits(value: (width * 2)),
                        Z: FixedQ4816.FromRawBits(value: (width * 3))));
                    var answered = evaluator.TryDistanceBounds(distance: out var bounds, instructionsWalked: out var walked, lower: lower,
                        rotationsExpanded: out var rotations, upper: upper);

                    hash.Add(value: (answered ? 1L : 0L));
                    hash.Add(value: bounds.Lower.Value);
                    hash.Add(value: bounds.Upper.Value);
                    hash.Add(value: (bounds.IsUnbounded ? 1L : 0L));
                    hash.Add(value: walked);
                    expanded += rotations;
                    if (answered && (turn == (turns.Length - 1))) {
                        generalQueries++;
                    }
                }
            }
        }

        output.WriteLine(message: $"bounds digest {hash.Value:X16}; full expansions {expanded}; general queries {generalQueries}");
        Assert.Equal(expected: 0x7D5E18850F025A7AUL, actual: hash.Value);
        // Ten axial boxes at the frame boundary retain the full overflow-sensitive formula.
        Assert.Equal(actual: expanded, expected: (generalQueries + 10L));
    }
}
