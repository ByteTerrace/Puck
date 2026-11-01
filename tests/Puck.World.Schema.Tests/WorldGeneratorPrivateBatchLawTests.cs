using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A keyed private site's boot and redraw batch must retain the site's authority key.</summary>
public sealed class WorldGeneratorPrivateBatchLawTests {
    [Fact]
    public void KeyedPrivateFillMatchesIndividualPrivateDrawsAndContinuesItsRawCursor() {
        var source = new StateGenerator(Source: GeneratorSource.StreamDraw);
        var key = new ClosedBitset256(Word0: 1, Word1: 2, Word2: 3, Word3: 4);
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "privateTray"), Kind: CellKind.Int, Capacity: 2,
            Draw: new Draw(Generator: source, Timing: DrawTiming.Event, Secret: key),
            Cells: [new StateCell(CellName.Parse(candidate: "a"), CellValue.Int(value: 0)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 0))]);
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: [row]));
        var seed = GeneratorEngine.ComputeDrawSeed(documentSeed: 42, instanceIdentity: "law", site: "state.privateTray");

        for (var pass = 0; (pass < 2); pass++) {
            Assert.True(condition: WorldDrawBootResolver.TryFillKeyedSite(definition: definition, filled: out var filled, instanceIdentity: "law", keys: null, reason: out var reason, row: row, worldSeed: 42), userMessage: reason);
            Assert.Equal(((pass + 1) * 2), filled.DrawCursor);
            for (var cell = 0; (cell < 2); cell++) {
                Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, seed.State, seed.Stream, ((pass * 2) + cell), null,
                    out var expected, out reason, key), userMessage: reason);
                Assert.Equal(expected.Numeric, filled.Cells![cell].Value.Raw);
            }
            row = filled;
        }
    }
}
