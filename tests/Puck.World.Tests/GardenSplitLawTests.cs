using System.Text.Json;
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
/// <remarks>Body-index reassignment was the sharp edge the original split exposed: <see cref="WorldPopulation.ReconcileInhabitants"/>
/// grows each inhabited placement at the highest free slot, walking placements in document order, so moving a
/// game's placements to a different point in the imports fold moves every one of its bodies to a different index
/// range. Chess now closes that edge at the root: <c>pieceCell</c>/<c>pieceCode</c> are keyed by PLACEMENT ID
/// (<c>piece0</c>..<c>piece31</c>), never a body index, and the 32 original <c>tabletop-write-board-NN</c> rules are
/// one <c>forEach</c> rule addressing bodies through <c>placement:$each</c> — so nothing in the fragment names a
/// body index at all, and no composed order can desynchronize it. <see cref="ChessPieceBodies_MatchDeclaredPiecePlacementIds"/>
/// checks the declared key set against the real <see cref="WorldPopulation"/>'s inhabited placements rather than
/// trusting the literals; <see cref="ChessPiecesAndBoardSquares_ComposeOverTabletop"/> checks the placement-parent
/// primitive every piece/board-square rides instead of an absolute world-space position.</remarks>
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
    // An expression reads the same whether authored as an infix string or a postfix token object; both sides are
    // compared in the printed spelling, so a document that moved between the two spellings still matches.
    private static JsonNode? Canonicalize(JsonNode? node) {
        switch (node) {
            case JsonObject { Count: 1 } tokensObject when tokensObject["tokens"] is JsonArray:
                return JsonValue.Create(Spell(tokensObject));
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
            // A NUMBER value is renormalized to a rounded double regardless of how it was authored ("10" vs "10.0",
            // or a placement-parent composition's own double arithmetic reproducing "-0.35000000000000003" where the
            // fixture's own hand-authored literal reads "-0.35") — both sides' geometry is composed through DIFFERENT
            // arithmetic paths (one hand-authored absolute, one resolved through NormalizePlacementParents' rotate-
            // then-translate), so comparing the underlying JSON REPRESENTATION rather than the represented VALUE
            // would fail on floating-point noise neither side's authoring actually disagrees on.
            case JsonValue asValue when asValue.TryGetValue<double>(out var number):
                return JsonValue.Create(Math.Round(number, 9));
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

    private static void RemoveById(JsonObject document, string sectionPath, HashSet<string> ids) {
        var segments = sectionPath.Split('.');
        JsonNode? cursor = document;

        foreach (var segment in segments) {
            cursor = (cursor as JsonObject)?[segment];
        }

        if (cursor is not JsonArray array) {
            return;
        }

        for (var index = (array.Count - 1); (index >= 0); index--) {
            if ((array[index] is JsonObject row) && (row["id"] is JsonValue idValue) && ids.Contains(idValue.GetValue<string>())) {
                array.RemoveAt(index);
            }
        }
    }

    // The placement-parent primitive needs an ANCHOR for every game whose pieces share a physical surface (see
    // chess.world.json's/dominoes.world.json's own remarks and docs/campaign.md's module-convention entry).
    // Chess/billiards/bowling anchor to a placement the pre-split fixture ALREADY authored (tabletop/billiardsTray/
    // bowlingLane); dominoes has no such object, so 'dominoRun' is a genuinely NEW invisible marker placement (plus
    // its own marker prototype) the pre-split fixture never carried — subtracted here on the same terms
    // TicTacToe*Names subtracts tic-tac-toe's own new content.
    private static readonly HashSet<string> NewAnchorPlacementIds = ["dominoRun"];
    private static readonly HashSet<string> NewAnchorPrototypeIds = ["dominoRun"];

    // 32 tightly-packed pieces settling from spawn can cross the population's raw $physics:quiescent edge before
    // every piece finishes its own physical settle; every tabletop rule now gates on a 'settleHold' row (a counter
    // that only reaches its margin once quiescence has held continuously) instead, and the first such settle is
    // further routed through a one-time 'gameStarted' snapshot that seeds 'previousBoard' from the just-derived
    // 'board' before any classifier reads it — see chess.world.json's own remarks. Neither is content the split is
    // meant to preserve unchanged, so this subtracts the four rules/two state rows it added and reverts every
    // rule's debounced gate back to the raw quiescent-edge spelling the pre-split fixture carries.
    private static readonly HashSet<string> SettleHoldRuleNames = [
        "tabletop-settle-hold-advance", "tabletop-settle-hold-reset", "tabletop-game-start-snapshot", "tabletop-game-start-flag",
    ];
    private static readonly HashSet<string> SettleHoldStateRowNames = ["settleHold", "gameStarted"];

    // Two genuine legality-content fixes layered onto the split, neither of which the pre-split fixture ever got
    // right either: 'turn' declared its initial value as 0 while 'moverColor' (tabletop-mover-color) reads 1 for
    // white, so white's own first move could never satisfy tabletop-verdict's (moverColor == turn) term; and
    // 'tabletop-shape-pawn-pick' compared moverColor against the wrong constant, selecting pawnLegalBlack for
    // white's own move. Reverted here to the pre-split fixture's own (equally wrong) spelling so the comparison
    // that remains is everything else — see chess.world.json's own remarks for why 1 and 1 are the CORRECT values.
    private static void NormalizeSettleHoldDebounce(JsonObject tree) {
        if (tree["rules"] is JsonArray rules) {
            foreach (var rule in rules) {
                if ((rule is not JsonObject ruleObject) || (ruleObject["name"] is not JsonValue nameValue)) {
                    continue;
                }

                if ((nameValue.GetValue<string>() == "tabletop-shape-pawn-pick") && (ruleObject["effects"] is JsonArray pickEffects) && (pickEffects[0] is JsonObject pickEffect)) {
                    pickEffect["expression"] = RespellConstant(pickEffect["expression"]!, index: 1, value: 0m);
                }

                RevertSettleHoldGate(ruleObject["gate"] as JsonObject);
            }
        }

        if ((tree["state"] as JsonObject)?["world"] is JsonArray worldRows) {
            foreach (var row in worldRows) {
                if ((row is JsonObject rowObject) && (rowObject["name"] is JsonValue name) && (name.GetValue<string>() == "turn")) {
                    rowObject["value"] = 0;
                }
            }
        }

        RemoveNamed(tree, "rules", SettleHoldRuleNames);
        RemoveNamed(tree, "state.world", SettleHoldStateRowNames);
    }

    private static void RevertSettleHoldGate(JsonObject? gate) {
        if (gate is null) {
            return;
        }

        if ((gate["state"] is JsonValue state) && (state.GetValue<string>() == "settleHold") && (gate["value"]?.GetValue<int>() == 60)) {
            gate["state"] = "$physics:quiescent";
            gate["value"] = 1;

            return;
        }

        if (gate["predicates"] is JsonArray predicates) {
            foreach (var predicate in predicates) {
                RevertSettleHoldGate(predicate as JsonObject);
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
        NormalizeSettleHoldDebounce(tree);
        // Resolved/normalized BEFORE the new anchor is subtracted — NormalizePlacementParents/NormalizeBoardAnchorOrigins
        // must still find 'dominoRun' in the tree to resolve what composes over it.
        NormalizeBoardAnchorOrigins(tree);
        NormalizePlacementParents(tree);
        RemoveById(tree, "placements.rows", NewAnchorPlacementIds);
        RemoveById(tree, "prototypes", NewAnchorPrototypeIds);
        NormalizeBodyIndexedRowKeys(tree);
        NormalizeWriteBoardRuleNumbers(tree);
        NormalizePlacementEachBodyRef(tree);

        return tree;
    }

    // 'tabletop-derive-cell-upright'/'tabletop-derive-cell-tilted' read/gate on the CURRENT forEach piece's body
    // through 'placement:$each' now that pieceCode is keyed by placement id rather than body index (the plain 'each'
    // binding only resolves a NUMERIC key) — see chess.world.json's own remarks. The pre-split fixture predates the
    // placement-addressed re-authoring and spells the same two body-ref reads as plain 'each'; this rewrites the
    // post-split spelling back to match so the comparison that remains is the rule's actual behavior, never which
    // body-reference grammar names the same forEach piece.
    private static void NormalizePlacementEachBodyRef(JsonObject tree) {
        if (tree["rules"] is not JsonArray rules) {
            return;
        }

        foreach (var rule in rules) {
            if (rule is not JsonObject ruleObject) {
                continue;
            }

            foreach (var effect in (ruleObject["effects"] as JsonArray ?? [])) {
                if ((effect is JsonObject effectObject) && (effectObject["fromState"] is JsonValue fromState) && (fromState.GetValue<string>() == "$board:cellOf:board:placement:$each")) {
                    effectObject["fromState"] = "$board:cellOf:board:each";
                }
            }

            if ((ruleObject["gate"] as JsonObject)?["predicates"] is not JsonArray predicates) {
                continue;
            }

            foreach (var predicate in predicates) {
                var target = ((predicate as JsonObject)?["predicate"] as JsonObject) ?? (predicate as JsonObject);

                if ((target?["state"] is JsonValue state) && (state.GetValue<string>() == "$upright:placement:$each")) {
                    target["state"] = "$upright:each";
                }
            }
        }
    }

    // The placement-parent primitive (WorldPlacement.Parent) — every chess piece/board-square now composes over
    // 'tabletop' rather than authoring an absolute world-space position (see chess.world.json's own remarks and
    // docs/campaign.md's module-convention entry). The pre-split fixture predates the primitive and authors every
    // placement's ABSOLUTE position directly; this resolves each parented row to that same absolute position
    // (parent-relative offset rotated by the parent's own resolved yaw, then translated by the parent's own resolved
    // position — WorldPlacementFrameCompilation's exact composition) and drops 'parent', so the comparison that
    // remains is "the same piece sits at the same world position", never which frame it is authored relative to.
    private static void NormalizePlacementParents(JsonObject tree) {
        if ((tree["placements"] as JsonObject)?["rows"] is not JsonArray rows) {
            return;
        }

        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var row in rows) {
            if ((row is JsonObject rowObject) && (rowObject["id"] is JsonValue idValue)) {
                byId[idValue.GetValue<string>()] = rowObject;
            }
        }

        var resolved = new Dictionary<string, (double X, double Y, double Z, double YawDegrees)>(StringComparer.Ordinal);

        (double X, double Y, double Z, double YawDegrees) Resolve(JsonObject row) {
            var id = row["id"]!.GetValue<string>();

            if (resolved.TryGetValue(id, out var cached)) {
                return cached;
            }

            var position = (JsonArray)row["position"]!;
            var localX = position[0]!.GetValue<double>();
            var localY = position[1]!.GetValue<double>();
            var localZ = position[2]!.GetValue<double>();
            var localYaw = row["yawDegrees"]?.GetValue<double>() ?? 0.0;

            if (row["parent"] is not JsonValue parentValue) {
                var identity = (localX, localY, localZ, localYaw);

                resolved[id] = identity;

                return identity;
            }

            var parent = Resolve(byId[parentValue.GetValue<string>()]);
            var radians = (parent.YawDegrees * Math.PI / 180.0);
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            // WorldPlacementFrameCompilation's exact rotation: (X, Z) rotated by the parent's yaw, Y untouched.
            var rotatedX = ((localX * cos) + (localZ * sin));
            var rotatedZ = ((-localX * sin) + (localZ * cos));
            // Rounded: a parented row's composed position is a fresh double computation (rotate then translate),
            // never bit-identical to the pre-split fixture's own literal even when it represents the SAME value —
            // this compares AUTHORED geometry, not floating-point provenance.
            var composed = (
                X: Math.Round((parent.X + rotatedX), 9),
                Y: Math.Round((parent.Y + localY), 9),
                Z: Math.Round((parent.Z + rotatedZ), 9),
                YawDegrees: ((parent.YawDegrees + localYaw) % 360.0)
            );

            resolved[id] = composed;

            return composed;
        }

        foreach (var row in rows) {
            if ((row is not JsonObject rowObject) || (rowObject["parent"] is null)) {
                continue;
            }

            var frame = Resolve(rowObject);

            rowObject["position"] = new JsonArray { frame.X, frame.Y, frame.Z };
            rowObject["yawDegrees"] = frame.YawDegrees;
            rowObject.Remove("parent");
        }
    }

    // A Grid topology a placement's 'board' facet anchors takes its world origin from that placement's composed
    // frame plus its authored (now LOCAL) origin (WorldTopologyCompilation.Find(WorldDefinition, string) — see the
    // schema reference's tabletop-primitive section). Resolved against the RAW tree (before NormalizePlacementParents
    // strips 'parent') so the anchor's own absolute position — itself possibly parent-composed — is available; the
    // pre-split fixture authors every topology's origin absolutely, so this is the same "compare absolute geometry,
    // never which frame it is authored relative to" normalization NormalizePlacementParents applies to placements.
    private static void NormalizeBoardAnchorOrigins(JsonObject tree) {
        if (((tree["state"] as JsonObject)?["lattices"] is not JsonArray lattices) || ((tree["placements"] as JsonObject)?["rows"] is not JsonArray rows)) {
            return;
        }

        var byId = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var row in rows) {
            if ((row is JsonObject rowObject) && (rowObject["id"] is JsonValue idValue)) {
                byId[idValue.GetValue<string>()] = rowObject;
            }
        }

        foreach (var topology in lattices) {
            if ((topology is not JsonObject topologyObject) || (topologyObject["name"] is not JsonValue nameValue)) {
                continue;
            }

            var topologyName = nameValue.GetValue<string>();
            JsonObject? anchor = null;

            foreach (var row in rows) {
                if ((row is JsonObject rowObject) && (rowObject["board"] as JsonObject)?["topology"] is JsonValue boardTopology && (boardTopology.GetValue<string>() == topologyName)) {
                    anchor = rowObject;

                    break;
                }
            }

            if (anchor is null) {
                continue;
            }

            // Only translation composes into a board's origin (WorldTopologyCompilation.Compile) — the anchor's own
            // yaw is not applied to the grid's own axes.
            var anchorPosition = new List<double>();
            var cursor = anchor;
            var totalX = 0.0;
            var totalY = 0.0;
            var totalZ = 0.0;

            while (cursor is not null) {
                var position = (JsonArray)cursor["position"]!;

                totalX += position[0]!.GetValue<double>();
                totalY += position[1]!.GetValue<double>();
                totalZ += position[2]!.GetValue<double>();
                cursor = (cursor["parent"] is JsonValue parentValue) ? byId[parentValue.GetValue<string>()] : null;
            }

            var origin = (JsonArray)topologyObject["origin"]!;

            topologyObject["origin"] = new JsonArray {
                (origin[0]!.GetValue<double>() + totalX),
                (origin[1]!.GetValue<double>() + totalY),
                (origin[2]!.GetValue<double>() + totalZ),
            };
        }
    }

    // pieceCell/pieceCode key their 32 cells by body index in the pre-split fixture ("74".."105") and by PLACEMENT ID
    // in the shipped module ("piece0".."piece31", the placement-addressed re-authoring — see chess.world.json's own
    // remarks and docs/campaign.md's module-convention entry): the one row shape both re-keys, since neither a body
    // index nor a placement-id spelling is "content" the split/re-authoring is meant to preserve literally. Comparing
    // those keys directly against the pre-split fixture would fail on a difference each re-authoring is DELIBERATELY
    // making (ChessPieceBodies_MatchAuthoredPieceCellAndPieceCodeKeys is the law that actually checks the current
    // keys are right); this rewrites each cell's key to its own 0-based position in the row instead, so the
    // comparison that remains is "the same piece, in the same authored order, carries the same value" — content, not
    // the body index or placement id a piece happens to be addressed by today.
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

    // The pre-split fixture's 32 tabletop-write-board-NN rules carry the same body index in their own name and every
    // internal reference; the shipped module instead carries ONE tabletop-write-board rule (forEach: pieceCode,
    // 'placement:$each'/'$cell:pieceCell:$each' — the placement-addressed re-authoring). Either shape normalizes to
    // the SAME canonical 32-rule form (renumbered 0..31 by the piece's own 0-based position, matching
    // NormalizeBodyIndexedRowKeys above) so the comparison that remains is the rule family's actual shape, never a
    // body index/placement id or whether it happens to be spelled as one rule or 32 equivalent ones.
    private static void NormalizeWriteBoardRuleNumbers(JsonObject tree) {
        if (tree["rules"] is not JsonArray rules) {
            return;
        }

        var collapsedIndex = -1;

        for (var index = 0; (index < rules.Count); index++) {
            if ((rules[index] is JsonObject ruleObject) && (ruleObject["name"] is JsonValue nameValue) && (nameValue.GetValue<string>() == "tabletop-write-board")) {
                collapsedIndex = index;

                break;
            }
        }

        if (collapsedIndex >= 0) {
            var collapsed = (JsonObject)rules[collapsedIndex]!;

            Assert.Equal("pieceCode", collapsed["forEach"]?.GetValue<string>());
            rules.RemoveAt(collapsedIndex);

            for (var piece = 0; (piece < 32); piece++) {
                var n = piece.ToString();

                rules.Add(new JsonObject {
                    ["name"] = $"tabletop-write-board-{n}",
                    ["mode"] = "Edge",
                    ["gate"] = new JsonObject {
                        ["$type"] = "all",
                        ["predicates"] = new JsonArray {
                            new JsonObject { ["$type"] = "compareState", ["comparison"] = "Equal", ["state"] = "$physics:quiescent", ["value"] = 1 },
                            new JsonObject { ["$type"] = "compareState", ["comparison"] = "NotEqual", ["state"] = "pieceCell", ["key"] = n, ["value"] = -1 },
                        },
                    },
                    ["effects"] = new JsonArray {
                        new JsonObject { ["$type"] = "setState", ["state"] = "board", ["key"] = $"$cell:pieceCell:{n}", ["fromState"] = "pieceCode", ["fromKey"] = n },
                    },
                });
            }

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

    private static HashSet<string> KeysAsPlacementIds(WorldDefinition definition, string rowName) {
        var row = WorldDefinitionRows.FindStateRow(definition.State, rowName)!;

        return [.. (row.Cells ?? []).Select(cell => cell.Key.Value)];
    }

    // pieceCell/pieceCode are keyed by PLACEMENT ID (piece0..piece31) — the placement-addressed re-authoring: a
    // game's own content never keys itself by body index, which is an artefact of wherever WorldPopulation happens
    // to seat inhabited placements today (see chess.world.json's own remarks and docs/campaign.md's module-
    // convention entry). This checks the declared key set matches the 32 declared piece placements exactly — never
    // where those placements land in the entity table, which the placement:$each/placement-ordinal machinery
    // resolves at runtime rather than at authoring time.
    [Fact]
    public void ChessPieceBodies_MatchDeclaredPiecePlacementIds() {
        var definition = LoadGarden();
        using var fixture = Fixtures.FreshServer(definition: definition);

        var fromPieceCell = KeysAsPlacementIds(definition, "pieceCell");
        var fromPieceCode = KeysAsPlacementIds(definition, "pieceCode");
        var declaredPieceIds = definition.Placements.Where(p => (p.Id.StartsWith("piece", StringComparison.Ordinal) && (p.Inhabit is not null))).Select(p => p.Id).ToHashSet();
        var fromPopulation = new HashSet<string>();

        for (var index = 0; (index < fixture.Server.Population.Capacity); index++) {
            if (fixture.Server.Population.InhabitantPlacementId(index) is { } placementId && placementId.StartsWith("piece", StringComparison.Ordinal)) {
                fromPopulation.Add(placementId);
            }
        }

        Assert.Equal(32, declaredPieceIds.Count);
        Assert.Equal(fromPieceCell, fromPieceCode);
        Assert.Equal(declaredPieceIds, fromPieceCell);
        Assert.Equal(declaredPieceIds, fromPopulation);

        // Control: a body-index-shaped key set is NOT what pieceCell/pieceCode declare — proving this law actually
        // discriminates a stale body-index literal rather than passing on any old set.
        var bodyIndexShaped = Enumerable.Range(74, 32).Select(n => n.ToString()).ToHashSet();

        Assert.NotEqual(bodyIndexShaped, fromPieceCell);
    }

    // Every piece placement composes over 'tabletop', its position/yaw the LOCAL offset the tabletop's composed
    // frame resolves — the placement-parent primitive chess.world.json rides so any host can restate the anchor
    // at a different position and every piece/board-square follows, without touching a single body index.
    [Fact]
    public void ChessPiecesAndBoardSquares_ComposeOverTabletop() {
        var definition = LoadGarden();

        foreach (var placement in definition.Placements) {
            if (placement.Id.StartsWith("piece", StringComparison.Ordinal) && (placement.Inhabit is not null)) {
                Assert.Equal("tabletop", placement.Parent);
            } else if (placement.Id.StartsWith("boardSquare-", StringComparison.Ordinal)) {
                Assert.Equal("tabletop", placement.Parent);
            }
        }
    }

    private static string Spell(JsonObject tokensObject) =>
        ExpressionSpelling.Print(JsonSerializer.Deserialize(tokensObject.ToJsonString(), WorldJsonContext.Default.ValueExpressionTokens)!.Tokens);
    // Rewrites one constant token of an expression in either spelling, returning the infix spelling.
    private static JsonNode RespellConstant(JsonNode expression, int index, decimal value) {
        var text = (expression is JsonObject tokensObject) ? Spell(tokensObject) : expression.GetValue<string>();
        Assert.True(ExpressionSpelling.TryParse(text, out var tokens, out var error), error);
        var edited = tokens.ToArray();
        Assert.IsType<ValueToken.Constant>(edited[index]);
        edited[index] = new ValueToken.Constant(value);
        return JsonValue.Create(ExpressionSpelling.Print(edited));
    }
}
