using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The corpus inventory is generated from the corpus, and every construct a shipped game claims to prove is
/// counted in that game's own source.</summary>
/// <remarks><see cref="TheCommittedInventoryIsTheCorpusCount"/> writes the fresh inventory beside the test assembly
/// (<see cref="Puck.Testing.TestRecords"/>) before comparing, and <c>puck baselines corpus-inventory</c> promotes that
/// copy over <see cref="CorpusInventory.CommittedPath"/>.</remarks>
public class CorpusInventoryTests {
    // What each game package claims to prove, as (section, construct) rows of the inventory its own source must be
    // counted in. A claim here is the package's check in the state and language programme.
    private static readonly Dictionary<string, (string Section, string Construct)[]> Claims = new(comparer: StringComparer.Ordinal) {
        ["games/tetromino"] = [
            ("Rule groups", "Staged"),
            ("Effects", "scheduleState"),
            ("Traits", "cycle"),
            ("Document sections", "patterns"),
            ("Operand channels", "match"),
            ("Draw sources", "WeightedNumeric"),
            ("Draw modes", "RestartOnExhaustion"),
            ("Overflow", "Saturate"),
        ],
    };
    public static TheoryData<string> Claimants() => new(values: Claims.Keys);
    [MemberData(nameof(Claimants))]
    [Theory]
    public void EveryConstructAGameClaimsIsCountedInItsSource(string game) {
        foreach (var (section, construct) in Claims[game]) {
            var row = Inventory.Value.SingleOrDefault(predicate: candidate => ((candidate.Section == section) && (candidate.Construct == construct)));

            Assert.True(condition: (row is not null), userMessage: $"the inventory has no {section} row '{construct}'");
            Assert.True(condition: row.Users.Contains(value: game), userMessage: $"{game} claims {section} '{construct}', which the inventory counts only in {string.Join(separator: ", ", values: row.Users)}");
        }
    }
}
