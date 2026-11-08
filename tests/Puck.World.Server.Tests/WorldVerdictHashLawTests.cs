using Puck.Maths;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>verdict</c> row trait folds into the declaration hash. Marking a row moves the
/// hash, and two verdicts that differ in their gate or status hash differently, so a verdict is covered like any other
/// declaration.</summary>
public sealed class WorldVerdictHashLawTests {
    private static ulong Hash(WorldStateSection section) {
        var hash = Fnv1aHash.Create();

        WorldStateHashComposition.AppendDeclaration(
            hash: ref hash,
            state: section
        );

        return hash.Value;
    }
    private static WorldStateRow Row() => new(
        Name: CellName.Parse(candidate: "phaseAdvances"),
        Kind: CellKind.Int,
        Cells: [
            new StateCell(
                Key: CellName.Parse(candidate: "ok"),
                Value: CellValue.Int(value: 0L)
            ),
            new StateCell(
                Key: CellName.Parse(candidate: "generation"),
                Value: CellValue.Int(value: 0L)
            ),
        ]
    );
    private static WorldStateSection Section(WorldStateRow row) => new(World: [row]);

    [Fact]
    public void MarkingARowAVerdictMovesTheDeclarationHash() {
        var plain = Hash(section: Section(row: Row()));
        var marked = Hash(section: Section(row: Row() with {
            Verdict = new WorldVerdictTrait(
                Gate: "the generation advanced",
                Status: CellName.Parse(candidate: "ok")
            ),
        }));

        Assert.NotEqual(
            actual: marked,
            expected: plain
        );
    }
    [Fact]
    public void TwoVerdictsDifferingOnlyInTheirGateOrStatusHashDifferently() {
        var left = Hash(section: Section(row: Row() with {
            Verdict = new WorldVerdictTrait(
                Gate: "the generation advanced",
                Status: CellName.Parse(candidate: "ok")
            ),
        }));
        var gate = Hash(section: Section(row: Row() with {
            Verdict = new WorldVerdictTrait(
                Gate: "the generation did not advance",
                Status: CellName.Parse(candidate: "ok")
            ),
        }));
        var status = Hash(section: Section(row: Row() with {
            Verdict = new WorldVerdictTrait(
                Gate: "the generation advanced",
                Status: CellName.Parse(candidate: "generation")
            ),
        }));

        Assert.NotEqual(
            actual: gate,
            expected: left
        );
        Assert.NotEqual(
            actual: status,
            expected: left
        );
    }
}
