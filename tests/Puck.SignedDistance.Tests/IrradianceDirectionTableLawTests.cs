using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SignedDistance.Tests;

// The continuation search's candidate table: a bin lists every ray within the continuation cone of each direction that
// falls in it, at the single-precision cosine the shaders compute, so a search over the list picks the ray a scan of
// every ray picks.
public sealed class IrradianceDirectionTableLawTests(ITestOutputHelper output) {
    [InlineData(128)]
    [InlineData(256)]
    [Theory]
    public void ABinListsEveryRayInTheConeOfEachDirectionInIt(int rays) {
        var cone = MathF.Cos(x: ((float)IrradianceLattice.ReprojectionCone));
        var bases = Enumerable.Range(count: rays, start: 0).Select(selector: ray => IrradianceLattice.BaseDirection(count: rays, index: ray))
            .Select(selector: static direction => new Double3(X: ((float)direction.X), Y: ((float)direction.Y), Z: ((float)direction.Z))).ToArray();
        var queries = Enumerable.Range(count: 4_000, start: 0).Select(selector: static index => IrradianceLattice.BaseDirection(count: 4_000, index: index))
            .Concat(second: Specials()).ToArray();
        var bins = (IrradianceDirectionTable.Bins * IrradianceDirectionTable.Bins);
        var lists = new bool[bins][];
        var longest = 0;

        for (var bin = 0; (bin < bins); bin++) {
            var candidates = IrradianceDirectionTable.Candidates(bin: bin, rays: rays).ToArray();

            Assert.Equal(expected: candidates.Order(), actual: candidates);
            Assert.All(collection: candidates, action: ray => Assert.InRange(actual: ray, high: (rays - 1), low: 0));
            lists[bin] = new bool[rays];
            foreach (var ray in candidates) { lists[bin][ray] = true; }
            longest = Math.Max(val1: longest, val2: candidates.Length);
        }
        output.WriteLine(message: $"{rays} rays: longest list {longest}");
        Assert.Equal(expected: longest, actual: IrradianceDirectionTable.MaximumCandidates(rays: rays));
        // A search visits a small share of the rays.
        Assert.True(condition: ((longest * 6) <= rays), userMessage: $"the longest list holds {longest} of {rays} rays");

        for (var orientation = 0; (orientation < IrradianceLattice.Orientations); orientation++) {
            var stored = bases.Select(selector: direction => IrradianceLattice.Orient(direction: direction, orientation: orientation)).ToArray();

            foreach (var query in queries) {
                var (x, y, z) = (((float)query.X), ((float)query.Y), ((float)query.Z));
                var bin = IrradianceDirectionTable.BinOf(direction: IrradianceDirectionTable.Unorient(direction: new Double3(X: x, Y: y, Z: z), orientation: orientation));

                Assert.InRange(actual: bin, high: (bins - 1), low: 0);
                for (var ray = 0; (ray < rays); ray++) {
                    var cosine = (((((float)stored[ray].X) * x) + (((float)stored[ray].Y) * y)) + (((float)stored[ray].Z) * z));

                    if ((cosine >= cone) && !lists[bin][ray]) {
                        Assert.Fail(message: $"ray {ray} lies in the cone of ({x}, {y}, {z}) at orientation {orientation}, outside bin {bin}'s list");
                    }
                }
            }
        }
    }
    [Fact]
    public void UnorientInvertsEveryOrientationExactly() {
        var direction = new Double3(X: 0.125, Y: -0.5, Z: 0.75);

        for (var orientation = 0; (orientation < IrradianceLattice.Orientations); orientation++) {
            Assert.Equal(expected: direction, actual: IrradianceDirectionTable.Unorient(direction: IrradianceLattice.Orient(direction: direction, orientation: orientation), orientation: orientation));
        }
        Assert.Equal(expected: -1, actual: IrradianceDirectionTable.BinOf(direction: new Double3(X: 0.0, Y: 0.0, Z: 0.0)));
    }

    // The axes and the octants' diagonals: the octahedral square's corners, edge midpoints and fold lines.
    private static IEnumerable<Double3> Specials() {
        for (var x = -1; (x <= 1); x++) {
            for (var y = -1; (y <= 1); y++) {
                for (var z = -1; (z <= 1); z++) {
                    if ((x != 0) || (y != 0) || (z != 0)) { yield return new Double3(X: x, Y: y, Z: z).Normalize(); }
                }
            }
        }
    }
}
