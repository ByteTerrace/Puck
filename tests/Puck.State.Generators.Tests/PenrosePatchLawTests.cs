using Puck.Assets.Documents;
using Puck.Testing;
using Xunit;

namespace Puck.State.Generators.Tests;

/// <summary>CONTRACT UNDER TEST: a Penrose patch names every tile's rhomb kind and the ribbons running through it,
/// and hands both to a document as derived rows over the patch's own graph. The kinds are the two P3 rhombs in the
/// golden ratio; a thin rhomb's side-sharing chain is one tile or two, which is why a ribbon is the straight run
/// through parallel sides instead; and a seeded draw cuts one board out of one seed.</summary>
public sealed class PenrosePatchLawTests {
    private const int Tiles = 60;

    private static LatticeTopology.Tiling Tiling(TilingFamily family = TilingFamily.Penrose, int radius = 8) => new(
        Name: "patch",
        Origin: new DocumentVector3(
            x: 0f,
            y: 0f,
            z: 0f
        ),
        CellSize: 1f,
        Family: family,
        Radius: radius
    );
    private static PenrosePatch Describe() {
        Assert.True(
            condition: PenrosePatch.TryDescribe(
                patch: out var patch,
                reason: out var reason,
                tiling: Tiling()
            ),
            userMessage: reason
        );

        return patch!;
    }
    private static PenrosePatch Draw(ulong seed) {
        Assert.True(
            condition: PenrosePatch.TryDraw(
                documentSeed: seed,
                instanceIdentity: "law",
                patch: out var patch,
                reason: out var reason,
                site: "board",
                tileCount: Tiles,
                tiling: Tiling()
            ),
            userMessage: reason
        );

        return patch!;
    }

    [Fact]
    public void EveryTileIsOneOfTheTwoRhombsAndTheyStandInTheGoldenRatio() {
        var patch = Describe();
        var thin = 0;

        for (var cell = 0; (cell < patch.TileCount); cell++) {
            Assert.Contains(
                collection: new[] { PenroseRhomb.Fat, PenroseRhomb.Thin },
                expected: patch.KindOf(cell: cell)
            );

            if (patch.KindOf(cell: cell) == PenroseRhomb.Thin) {
                thin++;
            }
        }

        var fat = (patch.TileCount - thin);

        Assert.True(condition: (thin > 0));
        Assert.True(condition: (fat > thin));
        // Thin to fat approaches 1/phi; a patch this size is already inside a tenth of it.
        Assert.True(
            condition: (Math.Abs(value: ((((double)thin) / fat) - 0.6180339887498949)) < 0.1),
            userMessage: $"thin {thin} against fat {fat}"
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => patch.KindOf(cell: patch.TileCount));
    }
    [Fact]
    public void AThinChainIsOneRhombOrTwoAndAFatRhombBelongsToNone() {
        var patch = Describe();
        var sizes = new int[patch.ThinChainCount];

        for (var cell = 0; (cell < patch.TileCount); cell++) {
            var chain = patch.ThinChainOf(cell: cell);

            if (patch.KindOf(cell: cell) == PenroseRhomb.Fat) {
                Assert.Equal(
                    actual: chain,
                    expected: PenrosePatch.NoChain
                );

                continue;
            }

            Assert.InRange(
                actual: chain,
                high: (patch.ThinChainCount - 1),
                low: 0
            );
            sizes[chain]++;
        }

        Assert.NotEmpty(collection: sizes);
        foreach (var size in sizes) {
            Assert.InRange(
                actual: size,
                high: 2,
                low: 1
            );
        }
    }
    [Fact]
    public void EveryTileCarriesOneRibbonPerAxisAndARibbonRunsStraightThroughManyTiles() {
        var patch = Describe();
        var lengths = new int[patch.RibbonCount];

        for (var cell = 0; (cell < patch.TileCount); cell++) {
            var first = patch.RibbonOf(
                axis: 0,
                cell: cell
            );
            var second = patch.RibbonOf(
                axis: 1,
                cell: cell
            );

            Assert.NotEqual(
                actual: second,
                expected: first
            );
            lengths[first]++;
            lengths[second]++;
        }

        Assert.Equal(
            actual: lengths.Sum(),
            expected: (patch.TileCount * PenrosePatch.RibbonAxes)
        );
        Assert.True(
            condition: (lengths.Max() >= 10),
            userMessage: $"the longest ribbon runs through {lengths.Max()} tiles"
        );
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => patch.RibbonOf(
            axis: PenrosePatch.RibbonAxes,
            cell: 0
        ));
    }
    [Fact]
    public void TheDerivedRowsCarryOneGeneratedCellPerTile() {
        var patch = Describe();
        var kinds = patch.KindRow(name: TopologyArenaFixture.Name(value: "kind"));
        var ribbons = patch.RibbonRow(
            axis: 1,
            name: TopologyArenaFixture.Name(value: "ribbon")
        );
        var chains = patch.ThinChainRow(name: TopologyArenaFixture.Name(value: "chain"));

        foreach (var row in new[] { chains, kinds, ribbons }) {
            Assert.True(condition: row.Generated);
            Assert.Equal(
                actual: row.Cells!.Count,
                expected: patch.TileCount
            );
            Assert.Equal(
                actual: ((StateDomain.CellsOf)row.EffectiveDomain).Topology,
                expected: patch.Graph.Name
            );
        }
        for (var cell = 0; (cell < patch.TileCount); cell++) {
            Assert.Equal(
                actual: kinds.Cells![cell].Key.Value,
                expected: patch.Graph.Cells[cell].Id
            );
            Assert.Equal(
                actual: kinds.Cells[cell].Value.AsInt,
                expected: ((long)patch.KindOf(cell: cell))
            );
            Assert.Equal(
                actual: chains.Cells![cell].Value.AsInt,
                expected: patch.ThinChainOf(cell: cell)
            );
            Assert.Equal(
                actual: ribbons.Cells![cell].Value.AsInt,
                expected: patch.RibbonOf(
                    axis: 1,
                    cell: cell
                )
            );
        }
    }
    [Fact]
    public void OneSeedYieldsOneBoardAndAnotherSeedMovesIt() {
        var first = Draw(seed: 7UL);
        var again = Draw(seed: 7UL);

        Assert.Equal(
            actual: again.TileCount,
            expected: Tiles
        );
        for (var cell = 0; (cell < first.TileCount); cell++) {
            Assert.Equal(
                actual: again.TileOf(cell: cell),
                expected: first.TileOf(cell: cell)
            );
            Assert.Equal(
                actual: again.Graph.Cells[cell].Id,
                expected: first.Graph.Cells[cell].Id
            );
            Assert.Equal(
                actual: again.KindOf(cell: cell),
                expected: first.KindOf(cell: cell)
            );
            Assert.Equal(
                actual: again.ThinChainOf(cell: cell),
                expected: first.ThinChainOf(cell: cell)
            );
            Assert.Equal(
                actual: again.RibbonOf(
                    axis: 0,
                    cell: cell
                ),
                expected: first.RibbonOf(
                    axis: 0,
                    cell: cell
                )
            );
        }

        var moved = Enumerable
            .Range(
                count: 8,
                start: 0
            )
            .Select(selector: static seed => string.Join(
                separator: ',',
                values: Draw(seed: ((ulong)seed)).Graph.Cells.Select(selector: static cell => cell.Id)
            ))
            .Distinct(comparer: StringComparer.Ordinal)
            .Count();

        Assert.True(
            condition: (moved > 1),
            userMessage: "every seed cut the same patch"
        );
    }
    [Fact]
    public void ThePatchGraphHoldsOnlyTheSelectedTilesAndTheirSharedSides() {
        var patch = Draw(seed: 3UL);
        var ids = patch.Graph.Cells.Select(selector: static cell => cell.Id).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: ids.Count,
            expected: Tiles
        );
        foreach (var edge in patch.Graph.Edges) {
            Assert.Contains(
                collection: ids,
                expected: edge.From
            );
            Assert.Contains(
                collection: ids,
                expected: edge.To
            );
        }
        Assert.NotEmpty(collection: patch.Graph.Edges);
    }
    [Fact]
    public void ATilingThatLaysDownNoRhombsRefusesByName() {
        Assert.False(condition: PenrosePatch.TryDescribe(
            patch: out _,
            reason: out var reason,
            tiling: Tiling(family: TilingFamily.Kagome)
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "Kagome"
        );
        Assert.False(condition: PenrosePatch.TryDraw(
            documentSeed: 1UL,
            instanceIdentity: "law",
            patch: out _,
            reason: out var tooMany,
            site: "board",
            tileCount: 100000,
            tiling: Tiling()
        ));
        Assert.Contains(
            actualString: tooMany,
            expectedSubstring: "patch"
        );
    }
}
