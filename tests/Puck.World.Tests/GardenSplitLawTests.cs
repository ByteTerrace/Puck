using System.Text.Json.Nodes;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: splitting the garden's monolithic <c>puck.world.json</c> into <c>imports</c>-fan-in game fragments
/// (<c>games/{chess,poker,dominoes,billiards,bowling,tictactoe}.world.json</c>) is a pure re-authoring of CONTENT —
/// no row, rule, or lattice was lost, duplicated, or silently changed — even though the composed ORDER of every
/// keyed list the split touches provably cannot match the pre-split file's own order: <see cref="WorldDocumentBasis.TryMergeImports"/>
/// folds each import as one contiguous block (in <c>imports</c> list order), and the importing file's own new rows
/// always land after that whole block (a keyed-list merge keeps basis order, then appends the overlay's new keys)
/// — so content the pre-split file interleaved (ambient wildlife placements sitting textually between the domino
/// and billiards placements, say) can never reproduce that interleaving once dominoes and billiards become two
/// separate imported fragments with the wildlife staying in the substrate. A raw <c>JsonNode.DeepEquals</c> between
/// the pre-split and post-split composed trees is therefore false by construction; the honest proof canonicalizes
/// every keyed list (sort by its own identity key, exactly <see cref="WorldDocumentBasis"/>'s own precedence:
/// id/name/key/index) on both sides first, and the control below proves that canonicalization is not vacuously
/// true — it still catches a genuine content change.</summary>
/// <remarks>Body-index reassignment is the sharp edge this reordering has: <see cref="WorldPopulation.ReconcileInhabitants"/>
/// grows each inhabited placement at the highest free slot, walking placements in document order — so a placement
/// earlier in the composed list claims a HIGHER body index, and moving a game's placements to a different point in
/// the imports fold moves every one of its bodies to a different index range. A fragment authoring per-body row
/// keys or rule literals (chess's <c>pieceCell</c>/<c>pieceCode</c> keys, the 32 <c>tabletop-write-board-NN</c>
/// rules) must keep those literals matched to wherever the composed order actually seats its bodies —
/// <see cref="ChessPieceBodies_MatchAuthoredPieceCellAndPieceCodeKeys"/> cross-checks the authored keys against
/// where the REAL <see cref="WorldPopulation"/> seats those placements, rather than trusting the literals.</remarks>
public sealed class GardenSplitLawTests {
    // The settled row-identity precedence WorldDocumentBasis composes by (see its own remarks) — reimplemented here
    // (rather than reused) because it is a private implementation detail of that static class; this canonicalizer
    // needs only "does every element of this array carry the same one of these keys", not the ambiguity/tombstone
    // refusal machinery a live merge needs.
    private static readonly string[] RowKeyPrecedence = ["id", "name", "key", "index"];

    // Every row name the tic-tac-toe fragment contributes that the pre-split fixture (authored before tic-tac-toe
    // existed) never carried — subtracted from the post-split tree before comparison so the two sides describe the
    // same six-games-minus-one content. See games/tictactoe.world.json's own rules/state.world/state.lattices.
    private static readonly HashSet<string> TicTacToeRuleNames = ["ttt-place-mark", "ttt-reject-out-of-range", "ttt-reject-illegal", "ttt-check-win"];
    private static readonly HashSet<string> TicTacToeStateRowNames = [
        "tttBoard", "tttActive", "tttMoveCell", "tttMoveRequest", "tttMoveApplied", "tttBoardVersion", "tttWinCheckVersion",
        "tttWinner", "tttMoveCount",
        "tttChunkX0", "tttChunkX1", "tttChunkX2", "tttChunkX3", "tttChunkX4", "tttChunkX5", "tttChunkX6", "tttChunkX7",
        "tttChunkO0", "tttChunkO1", "tttChunkO2", "tttChunkO3", "tttChunkO4", "tttChunkO5", "tttChunkO6", "tttChunkO7",
        "tttXWin", "tttOWin",
    ];
    private static readonly HashSet<string> TicTacToeLatticeNames = ["tttCube"];

    // The two rows whose cell KEYS are body indices rather than authored content (see NormalizeBodyIndexedRowKeys).
    private static readonly HashSet<string> BodyIndexedRowNames = ["pieceCell", "pieceCode"];

    private static string RepoRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        return directory!.FullName;
    }

    // The pre-split composed tree, frozen at the migration point (basis already resolved, so it compares apples to
    // apples against PostSplitComposedTree below, which is also basis-resolved) — src/Puck.World/Assets/worlds
    // never carried a second, un-split copy of the garden; this fixture is the only place that history is kept, and
    // only for this one law.
    private static JsonObject PreSplitComposedTree() {
        var path = Path.Combine(RepoRoot(), "tests", "Puck.World.Tests", "Fixtures", "pre-split-garden.world.json");
        var tree = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

        NormalizeBodyIndexedRowKeys(tree);
        NormalizeWriteBoardRuleNumbers(tree);

        return tree;
    }

    private static JsonObject PostSplitComposedTree() {
        var path = Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(Puck.World.WorldDefinitionFileSource.TryComposeDocumentTree(path, out var tree, out var reason), reason);

        return tree!;
    }

    private static bool CarriesKey(JsonArray array, string key) {
        if (array.Count == 0) {
            return false;
        }

        foreach (var element in array) {
            if ((element is not JsonObject row) || (row[key] is not JsonValue)) {
                return false;
            }
        }

        return true;
    }

    // Orders by numeric value when both keys parse as one (every body-index/cell-index key in this document is a
    // decimal integer string, and "103" must sort after "74" rather than before it, as a plain ordinal string
    // compare would) and falls back to ordinal text otherwise (poker's card keys, "id"/"name" keys).
    private sealed class KeyComparer : IComparer<JsonNode?> {
        public static readonly KeyComparer Instance = new();

        public int Compare(JsonNode? x, JsonNode? y) {
            var xText = x?.ToJsonString() ?? string.Empty;
            var yText = y?.ToJsonString() ?? string.Empty;

            if (TryNumber(x, out var xNumber) && TryNumber(y, out var yNumber)) {
                return xNumber.CompareTo(yNumber);
            }

            return string.CompareOrdinal(strA: xText, strB: yText);
        }

        // A row's identity key (id/name/key/index) is always JSON-authored as a quoted string ("key": "12"), never
        // a bare JSON number, so the numeric probe parses the STRING content rather than asking the JsonValue for a
        // long directly (which fails: its underlying element kind is String, not Number).
        private static bool TryNumber(JsonNode? node, out long value) {
            value = 0;

            return (node is JsonValue asValue) && asValue.TryGetValue<string>(out var text) && long.TryParse(text, out value);
        }
    }

    private static bool TryFindRowKey(JsonArray array, out string key) {
        foreach (var candidate in RowKeyPrecedence) {
            if (CarriesKey(array, candidate)) {
                key = candidate;

                return true;
            }
        }

        key = string.Empty;

        return false;
    }

    // Deep-clones while sorting every keyed list (id/name/key/index, WorldDocumentBasis's own precedence) by that
    // key's own JSON text — order is exactly what a split cannot preserve (see the type remarks), so a faithful
    // comparison must ignore it; every other shape (object member order, scalar/mixed arrays) is left exactly as
    // JsonNode.DeepEquals already treats it (objects compare member-wise regardless of order; DeepEquals is what
    // decides scalars).
    private static JsonNode? Canonicalize(JsonNode? node) {
        switch (node) {
            case JsonObject asObject: {
                var result = new JsonObject();

                foreach (var (name, value) in asObject) {
                    result[name] = Canonicalize(value);
                }

                return result;
            }
            case JsonArray asArray: {
                var items = new List<JsonNode?>();

                foreach (var element in asArray) {
                    items.Add(Canonicalize(element));
                }

                if (TryFindRowKey(asArray, out var key)) {
                    items = [.. items.OrderBy(item => ((JsonObject)item!)[key], KeyComparer.Instance)];
                }

                var result = new JsonArray();

                foreach (var item in items) {
                    result.Add(item);
                }

                return result;
            }
            default:
                return node?.DeepClone();
        }
    }

    private static void RemoveNamed(JsonObject document, string sectionPath, HashSet<string> names) {
        var segments = sectionPath.Split('.');
        JsonNode? cursor = document;

        foreach (var segment in segments) {
            cursor = (cursor as JsonObject)?[segment];
        }

        if (cursor is not JsonArray array) {
            return;
        }

        for (var index = (array.Count - 1); (index >= 0); index--) {
            if ((array[index] is JsonObject row) && (row["name"] is JsonValue nameValue) && names.Contains(nameValue.GetValue<string>())) {
                array.RemoveAt(index);
            }
        }
    }

    // Subtracts tic-tac-toe's own new rows/rules/lattice — the one genuinely new game the split added beside moving
    // the five pre-existing ones — so what remains describes exactly the pre-split fixture's own content.
    private static JsonObject PostSplitMinusTicTacToe() {
        var tree = (JsonObject)PostSplitComposedTree().DeepClone();

        RemoveNamed(tree, "rules", TicTacToeRuleNames);
        RemoveNamed(tree, "state.world", TicTacToeStateRowNames);
        RemoveNamed(tree, "state.lattices", TicTacToeLatticeNames);
        NormalizeBodyIndexedRowKeys(tree);
        NormalizeWriteBoardRuleNumbers(tree);

        return tree;
    }

    // pieceCell/pieceCode key their 32 cells by body index (see the type remarks) — the one row shape the split
    // legitimately re-keys, since chess's piece bodies land at a different index range once the imports fold moves
    // the piece placements. Comparing those keys against the pre-split fixture would fail on a difference the split
    // is meant to make (ChessPieceBodies_MatchAuthoredPieceCellAndPieceCodeKeys is the law that actually checks
    // the new keys are right); this rewrites each cell's key to its own 0-based position in the row instead, so the
    // comparison that remains is "the same piece, in the same authored order, carries the same value" — content,
    // not the body index a placement happens to land at today.
    private static void NormalizeBodyIndexedRowKeys(JsonObject tree) {
        if ((tree["state"] as JsonObject)?["world"] is not JsonArray rows) {
            return;
        }

        foreach (var row in rows) {
            if ((row is not JsonObject rowObject) || (rowObject["name"] is not JsonValue name) || !BodyIndexedRowNames.Contains(name.GetValue<string>())) {
                continue;
            }

            if (rowObject["cells"] is not JsonArray cells) {
                continue;
            }

            for (var index = 0; (index < cells.Count); index++) {
                ((JsonObject)cells[index]!)["key"] = index.ToString();
            }
        }
    }

    // The 32 tabletop-write-board-NN rules carry the same body index in their own name and every internal
    // reference (see chess.world.json's remarks) — the rule-level counterpart to NormalizeBodyIndexedRowKeys above,
    // renumbering NN to the rule's own 0-based position among that family (in array order) so the two sides match
    // by name and the comparison that remains is the rule's actual shape, not the body index it currently targets.
    private static void NormalizeWriteBoardRuleNumbers(JsonObject tree) {
        if (tree["rules"] is not JsonArray rules) {
            return;
        }

        var ordinal = 0;

        foreach (var rule in rules) {
            if ((rule is not JsonObject ruleObject) || (ruleObject["name"] is not JsonValue nameValue)) {
                continue;
            }

            var name = nameValue.GetValue<string>();

            if (!name.StartsWith("tabletop-write-board-", StringComparison.Ordinal)) {
                continue;
            }

            var newNumber = ordinal.ToString();

            ordinal++;
            ruleObject["name"] = $"tabletop-write-board-{newNumber}";

            var predicate = (JsonObject)((JsonArray)((JsonObject)ruleObject["gate"]!)["predicates"]!)[1]!;

            predicate["key"] = newNumber;

            var effect = (JsonObject)((JsonArray)ruleObject["effects"]!)[0]!;

            effect["fromKey"] = newNumber;
            effect["key"] = $"$cell:pieceCell:{newNumber}";
        }
    }

    [Fact]
    public void RawComposedTrees_AreNotDeepEqual_TheReorderingIsReal() {
        // A positive control on the problem this law solves: if this ever started passing, either the split
        // stopped reordering anything (wonderful — simplify the law below to a raw DeepEquals and delete this) or
        // the fixture/tree are being compared some other way that no longer exercises real content.
        Assert.False(JsonNode.DeepEquals(PreSplitComposedTree(), PostSplitMinusTicTacToe()));
    }

    [Fact]
    public void ComposedTree_MatchesPreSplitContent_ModuloProvenReordering() {
        var pre = Canonicalize(PreSplitComposedTree());
        var post = Canonicalize(PostSplitMinusTicTacToe());

        Assert.True(JsonNode.DeepEquals(pre, post));
    }

    [Fact]
    public void ComposedTree_Control_ACorruptedRowIsCaughtEvenAfterCanonicalizing() {
        // Proves ComposedTree_MatchesPreSplitContent... is not vacuously true — canonicalizing (sorting keyed
        // lists) does not also hide an actual value change. Corrupt one shipped chess row's value and the same
        // comparison must fail.
        var post = (JsonObject)PostSplitMinusTicTacToe().DeepClone();
        var rules = (JsonArray)post["rules"]!;
        var corrupted = false;

        foreach (var rule in rules) {
            if ((rule is JsonObject row) && (row["name"] is JsonValue name) && (name.GetValue<string>() == "tabletop-king-cell")) {
                row["mode"] = "Level";
                corrupted = true;

                break;
            }
        }

        Assert.True(corrupted, "expected the shipped 'tabletop-king-cell' rule to survive the split under that name");
        Assert.False(JsonNode.DeepEquals(Canonicalize(PreSplitComposedTree()), Canonicalize(post)));
    }

    // WorldDefinitionLoader.TryLoadFile (not the lower-level WorldDefinitionFileSource.TryLoad) — it also resolves
    // every "boot"-timed WorldDraw site, exactly like the real Puck.World.exe boot path, so a creation driver naming
    // a draw-filled cadence row validates the same way it does for a real server.
    private static WorldDefinition LoadGarden() {
        var path = Path.Combine(RepoRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(WorldDefinitionLoader.TryLoadFile(path, out var definition, out var reason), reason);

        return definition!;
    }

    private static HashSet<int> KeysAsBodyIndices(WorldDefinition definition, string rowName) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, rowName)!;

        return [.. (row.Cells ?? []).Select(cell => int.Parse(cell.Key.Value))];
    }

    [Fact]
    public void ChessPieceBodies_MatchAuthoredPieceCellAndPieceCodeKeys() {
        var definition = LoadGarden();
        using var fixture = Fixtures.FreshServer(definition: definition);

        var fromPieceCell = KeysAsBodyIndices(definition, "pieceCell");
        var fromPieceCode = KeysAsBodyIndices(definition, "pieceCode");
        var fromPopulation = new HashSet<int>();

        for (var index = 0; (index < fixture.Server.Population.Capacity); index++) {
            if (fixture.Server.Population.InhabitantPlacementId(index) is { } placementId && placementId.StartsWith("piece", StringComparison.Ordinal)) {
                fromPopulation.Add(index);
            }
        }

        Assert.Equal(fromPieceCell, fromPieceCode);
        Assert.Equal(fromPieceCell, fromPopulation);

        // Control: the pre-split range 12..43 (see the type remarks) is NOT the live range under the current
        // imports order — proving this law actually discriminates a stale-literal mismatch rather than passing on
        // any old set. If chess.world.json's literals ever drift from where WorldPopulation really seats the
        // placements, fromPieceCell/fromPieceCode will disagree with fromPopulation and the asserts above fail.
        var preSplitRange = Enumerable.Range(12, 32).ToHashSet();

        Assert.NotEqual(preSplitRange, fromPopulation);
    }
}
