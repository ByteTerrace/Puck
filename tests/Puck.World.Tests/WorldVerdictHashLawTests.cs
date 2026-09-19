using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>CONTRACT UNDER TEST: the <c>verdict</c> row trait folds into the declaration hash only when it is
/// authored. Marking a row moves the hash — so a verdict is covered like any other declaration — while a section
/// whose rows carry no verdict folds exactly the bytes it folded before the trait existed, which is what keeps
/// every recorded shipped-world state baseline where it is.</summary>
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
    public void AVerdictFreeSectionsDeclarationHashIsPinned() {
        // The pin is the whole law: an unconditional presence byte for the trait would move this value, and with it
        // every recorded shipped-world state baseline, for documents that declare no verdict anywhere. Re-record it
        // only alongside a deliberate change to what the declaration folds.
        Assert.Equal(
            actual: Hash(section: Section(row: Row())),
            expected: 0xBB68650F5C622514UL
        );
    }
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
