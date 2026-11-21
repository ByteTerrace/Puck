using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A keyed private site's boot and redraw batch must retain the site's authority key.</summary>
public sealed class WorldGeneratorPrivateBatchLawTests {
    [Fact]
    public void KeyedPrivateFillMatchesIndividualPrivateDrawsAndContinuesItsRawCursor() {
        var source = new StateGenerator(Source: GeneratorSource.StreamDraw);
        var authority = new PrivateDrawFixture();
        var installed = authority.Bind(new Draw(Generator: source, Timing: DrawTiming.Event, PrivateStream: true), 1);
        var row = new WorldStateRow(Name: CellName.Parse(candidate: "privateTray"), Kind: CellKind.Int, Capacity: 2,
            Draw: installed,
            Cells: [new StateCell(CellName.Parse(candidate: "z"), CellValue.Int(value: 0)), new StateCell(CellName.Parse(candidate: "b"), CellValue.Int(value: 0))]);
        var definition = new WorldDefinition(Generation: new WorldGenerationDefaults(WorldSeed: 42), StateRaw: new WorldStateSection(World: [row]));
        var seed = GeneratorEngine.ComputeDrawSeed(documentSeed: 42, instanceIdentity: "law", site: "state.privateTray");

        for (var pass = 0; (pass < 2); pass++) {
            Assert.True(condition: WorldDrawBootResolver.TryFillSite(authority: authority, definition: definition, filled: out var filled, instanceIdentity: "law", keys: null, reason: out var reason, row: row), userMessage: reason);
            Assert.Equal(((pass + 1) * 2), filled.DrawCursor);
            for (var cell = 0; (cell < 2); cell++) {
                Assert.True(condition: GeneratorEngine.TryFire(source, CellKind.Int, seed, ((pass * 2) + cell), null,
                    out var expected, out reason, installed.Secret!.Open(authority)), userMessage: reason);
                Assert.Equal(expected.Numeric, filled.Cells![1 - cell].Value.Raw);
            }
            row = filled;
        }
    }
}
