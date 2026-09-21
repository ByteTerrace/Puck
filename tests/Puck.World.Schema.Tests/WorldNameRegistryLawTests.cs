using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The name registry covers the document model, the checked-in table matches it, and an aliased import
/// rewrites every spelling a name can take.</summary>
public sealed class WorldNameRegistryLawTests {
    [Fact]
    public void VisitReadsFreshValuesAndPolymorphicArmsAfterWarmup() {
        var tree = JsonNode.Parse(json: """
            { "state": { "world": [{ "name": "beforeRow", "kind": "Int" }] },
              "rules": [{ "name": "rule", "effects": [{ "$type": "setState", "state": "beforeTarget" }] }] }
            """)!;

        static List<string> Names(JsonNode document) {
            var names = new List<string>();

            WorldModuleNamespace.Visit(node: document, type: typeof(WorldDefinition), visitor: (_, _, value, _, _) => {
                if ((value is JsonValue leaf) && leaf.TryGetValue<string>(value: out var name)) {
                    names.Add(item: name);
                }
            });
            return names;
        }
        Assert.Contains(expected: "beforeRow", collection: Names(document: tree));
        Assert.Contains(expected: "beforeTarget", collection: Names(document: tree));
        tree["state"]!["world"]![0]!["name"] = "afterRow";
        tree["rules"]![0]!["effects"]![0] = JsonNode.Parse(json: """{ "$type": "forEachPool", "pool": "afterPool", "effects": [] }""");
        var changed = Names(document: tree);

        Assert.Contains(collection: changed, expected: "afterRow");
        Assert.Contains(collection: changed, expected: "afterPool");
        Assert.DoesNotContain(collection: changed, expected: "beforeRow");
        Assert.DoesNotContain(collection: changed, expected: "beforeTarget");
        Assert.Equal(expected: changed, actual: Names(document: tree.DeepClone()));
    }

    private static readonly Dictionary<string, string> Declared = new(comparer: StringComparer.Ordinal) {
        ["board"] = "a_board",
        ["cube"] = "a_cube",
        ["run"] = "a_run",
        ["armor"] = "a_armor",
        ["game"] = "a_game",
        ["pile-one"] = "a_pile-one",
    };

    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (
            (directory is not null) &&
            !File.Exists(path: Path.Combine(
            path1: directory.FullName,
            path2: "Puck.slnx"
        ))
        ) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static string Rewrite(string text, WorldNameRole role) =>
        WorldModuleNamespace.Rewrite(
            declared: Declared,
            role: role,
            text: text
        );

    [InlineData("a")]
    [InlineData("_x1")]
    [InlineData("Twin2")]
    [Theory]
    public void AliasAdmitsBareIdentifiers(string alias) {
        Assert.True(
            condition: WorldImport.TryValidateAlias(
                alias: alias,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    [InlineData("")]
    [InlineData("1a")]
    [InlineData("a-b")]
    [InlineData("a.b")]
    [InlineData("$a")]
    [InlineData("a b")]
    [Theory]
    public void AliasRefusesEverythingElse(string alias) {
        Assert.False(condition: WorldImport.TryValidateAlias(
            alias: alias,
            reason: out var reason
        ));
        Assert.NotEmpty(collection: reason);
    }
    [Fact]
    public void ApplyPrefixesEveryDeclarationAndRewritesTheTokenSpelling() {
        var module = JsonNode.Parse(json: /*lang=json*/ """
            {
              "state": {
                "lattices": [ { "$type": "grid", "name": "cube", "origin": [0, 0, 0], "cellSize": 1, "width": 2, "depth": 2 } ],
                "world": [
                  { "name": "board", "kind": "Int", "domain": { "$type": "cellsOf", "topology": "cube" } },
                  { "name": "turn", "kind": "Int", "value": 0 }
                ]
              },
              "rules": [
                {
                  "name": "flip",
                  "forEach": "board",
                  "gate": { "$type": "compareState", "state": "turn", "comparison": "Equal", "comparandState": "$reduce:count:board" },
                  "effects": [
                    { "$type": "setState", "state": "turn", "expression": { "instructions": [ { "op": "Operand", "name": "turn", "key": "$cell:board:$each" }, { "op": "BoardShift", "topology": "cube", "index": "north" } ] } },
                    { "$type": "addState", "state": "board", "key": "$each", "expression": { "instructions": [ { "op": "Operand", "name": "turn" }, { "op": "Constant", "value": 1 }, { "op": "Add" } ] } }
                  ]
                }
              ]
            }
            """)!.AsObject();

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "twin",
                module: module,
                reason: out var reason
            ),
            userMessage: reason
        );

        var text = module.ToJsonString();

        Assert.Equal(
            expected: "twin_cube",
            actual: module["state"]!["lattices"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_cube",
            actual: module["state"]!["world"]![0]!["domain"]!["topology"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_flip",
            actual: module["rules"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_board",
            actual: module["rules"]![0]!["forEach"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "$reduce:count:twin_board",
            actual: module["rules"]![0]!["gate"]!["comparandState"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_turn",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "$cell:twin_board:$each",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]![0]!["key"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_cube",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]![1]!["topology"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "north",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["instructions"]![1]!["index"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_turn",
            actual: module["rules"]![0]!["effects"]![1]!["expression"]!["instructions"]![0]!["name"]!.GetValue<string>()
        );
        Assert.DoesNotContain(
            actualString: text,
            expectedSubstring: "\"board\""
        );
    }
    [Fact]
    public void RestoreReferencesTouchesRegisteredReferencesButNotDeclarationsOrPlainText() {
        const string Placeholder = "__puck_arg_0";
        var module = JsonNode.Parse(json: /*lang=json*/ """
            {
              "state": { "world": [ { "name": "__puck_arg_0", "kind": "Int", "value": 0 } ] },
              "rules": [ {
                "name": "plain",
                "forEach": "__puck_arg_0",
                "gate": { "$type": "compareValue", "left": "__puck_arg_0 + 1", "comparison": "Equal", "right": 1 },
                "effects": [ { "$type": "setState", "state": "__puck_arg_0", "key": "$cell:__puck_arg_0:$each", "value": 1 } ]
              } ],
              "hud": { "panels": [ {
                "id": "plain", "layer": "Under", "style": "Strip",
                "rect": { "x": 0, "y": 0, "width": 1, "height": 1 },
                "elements": [ {
                  "id": "plain", "kind": "Text", "style": "Primary",
                  "rect": { "x": 0, "y": 0, "width": 1, "height": 1 },
                  "binding": "state.__puck_arg_0", "template": "value {state.__puck_arg_0} __puck_arg_0"
                } ]
              } ] }
            }
            """)!.AsObject();

        Assert.True(
            WorldModuleNamespace.TryRestoreReferences(
                module,
                new Dictionary<string, string>(comparer: StringComparer.Ordinal) { [Placeholder] = "host" },
                out var reason
            ),
            reason
        );

        Assert.Equal(Placeholder, module["state"]!["world"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("plain", module["rules"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("host", module["rules"]![0]!["forEach"]!.GetValue<string>());
        Assert.Equal("host + 1", module["rules"]![0]!["gate"]!["left"]!.GetValue<string>());
        Assert.Equal("host", module["rules"]![0]!["effects"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("$cell:host:$each", module["rules"]![0]!["effects"]![0]!["key"]!.GetValue<string>());
        Assert.Equal("state.host", module["hud"]!["panels"]![0]!["elements"]![0]!["binding"]!.GetValue<string>());
        Assert.Equal("value {state.host} __puck_arg_0", module["hud"]!["panels"]![0]!["elements"]![0]!["template"]!.GetValue<string>());
    }
    [InlineData("state.board", "state.a_board")]
    [InlineData("state.board.from", "state.a_board.from")]
    [InlineData("state.board.$target", "state.a_board.$target")]
    [InlineData("world.tick", "world.tick")]
    [Theory]
    public void BindingRoleRewritesTheRowSegment(string authored, string expected) {
        Assert.Equal(
            expected: expected,
            actual: Rewrite(
                role: WorldNameRole.Binding,
                text: authored
            )
        );
        Assert.Equal(
            expected: $"score {{{expected}}} {{{{literal}}}}",
            actual: Rewrite(
                role: WorldNameRole.Template,
                text: $"score {{{authored}}} {{{{literal}}}}"
            )
        );
    }
    [Fact]
    public void CheckedInTableMatchesTheModel() {
        var path = Path.Combine(
            path1: RepositoryRoot(),
            path2: "docs",
            path3: "world-name-registry.md"
        );

        Assert.True(
            condition: File.Exists(path: path),
            userMessage: $"{path} is missing; run `puck registry`."
        );
        Assert.Equal(
            expected: WorldNameRegistry.Render(),
            actual: File.ReadAllText(path: path).ReplaceLineEndings(replacementText: "\n")
        );
    }
    [Fact]
    public void DroppingARegistrationLeavesItsMemberUncovered() {
        var without = WorldNameRegistry.RegisteredFields
            .Where(predicate: static field => !((field.Owner == typeof(ActionEffect.SetState)) && (field.Member == nameof(ActionEffect.SetState.State))))
            .ToArray();

        Assert.Equal(
            expected: (WorldNameRegistry.RegisteredFields.Count - 1),
            actual: without.Length
        );
        Assert.Contains(
            collection: WorldNameRegistry.UncoveredUnder(fields: without),
            filter: static line => line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "rules[].effects[][setState].state ("
            )
        );
    }
    [Fact]
    public void AFoldsFamilyAndItsBodysReadsAreBothVisited() {
        var program = ExpressionProgramJsonConverter.ToNode(program: new ExpressionProgram(Instructions: [Instruction.Fold(
            binder: "c",
            family: "board",
            operation: ExpressionOp.Count,
            subprogram: 0
        )]) {
            Subprograms = [new Subprogram(
                Arity: 0,
                Instructions: [Instruction.Operand(name: "armor")],
                Name: "c"
            )],
        });
        var visited = new List<string>();

        WorldModuleNamespace.Visit(
            node: program,
            type: typeof(ExpressionProgram),
            visitor: (holder, jsonName, value, field, _) => visited.Add(item: $"{jsonName}={value.GetValue<string>()}")
        );
        Assert.Equal(
            actual: visited,
            expected: ["family=board", "name=armor"]
        );
    }
    [InlineData("3 - board", "3 - a_board")]
    [InlineData("board[from] + game[to]", "a_board[from] + a_game[to]")]
    [InlineData("boardShift(board, cube, L0) & 0xFF", "boardShift(a_board, a_cube, L0) & 0xFF")]
    [InlineData("$board:mask:board:1:1 | min(armor, 2)", "$board:mask:a_board:1:1 | min(a_armor, 2)")]
    [InlineData("`pile-one`[$each] ? 1 : 0", "`a_pile-one`[$each] ? 1 : 0")]
    [InlineData("buffs[board[$each]]", "buffs[a_board[$each]]")]
    [InlineData("$table:armor:power[$local:move]", "$table:a_armor:power[$local:move]")]
    [InlineData("$match:run:$zones[game[from]]:prefix", "$match:a_run:$zones[a_game[from]]:prefix")]
    [Theory]
    public void ExpressionRoleRewritesReadsAndTopologyArguments(string authored, string expected) {
        Assert.Equal(
            expected: expected,
            actual: Rewrite(
                role: WorldNameRole.Expression,
                text: authored
            )
        );
    }
    [InlineData("board", "board")]
    [InlineData("12", "12")]
    [InlineData("$each", "$each")]
    [InlineData("$local:board", "$local:board")]
    [InlineData("$cell:board:$value", "$cell:a_board:$value")]
    [InlineData("$zone:$zones[game[from]]:last", "$zone:$zones[a_game[from]]:last")]
    [InlineData("cell:board:target", "cell:a_board:target")]
    [InlineData("placement:$each", "placement:$each")]
    [InlineData("$expr:board[from] + 1", "$expr:a_board[from] + 1")]
    [Theory]
    public void KeyRoleLeavesLiteralsAndRewritesSpellings(string authored, string expected) {
        Assert.Equal(
            expected: expected,
            actual: Rewrite(
                role: WorldNameRole.Key,
                text: authored
            )
        );
    }
    [InlineData("board", "a_board")]
    [InlineData("other", "other")]
    [InlineData("$zones", "$zones")]
    [InlineData("$zones[game[from]]", "$zones[a_game[from]]")]
    [InlineData("$board:mask:board:1:1", "$board:mask:a_board:1:1")]
    [InlineData("$reduce:count:board", "$reduce:count:a_board")]
    [InlineData("$match:run:board:any", "$match:a_run:a_board:any")]
    [InlineData("$table:armor:$each", "$table:a_armor:$each")]
    [Theory]
    public void NamesRoleRewritesBareNamesAndChannelSegments(string authored, string expected) {
        Assert.Equal(
            expected: expected,
            actual: Rewrite(
                role: WorldNameRole.Names,
                text: authored
            )
        );
    }
    [Fact]
    public void RegistryCoversEveryNameShapedMember() {
        Assert.True(
            condition: (WorldNameRegistry.Uncovered.Count == 0),
            userMessage: string.Join(
                separator: "\n",
                values: WorldNameRegistry.Uncovered
            )
        );
    }
    [Fact]
    public void SitesReachEverySpellingOfARuleEffect() {
        var paths = WorldNameRegistry.Sites.Select(selector: static site => site.Path).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Contains(
            collection: paths,
            expected: "state.world[].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "state.lattices[].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].effects[][setState].state"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].effects[][setState].expression{instructions}[state].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].effects[][setState].expression{instructions}[fold].family"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].effects[][setState].expression{subprograms}{instructions}[state].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].gate[compareState].comparandState"
        );
        Assert.Contains(
            collection: paths,
            expected: "rules[].effects[][transformState].transform[transfer].from"
        );
        Assert.Contains(
            collection: paths,
            expected: "patterns[].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "tables[].name"
        );
        Assert.Contains(
            collection: paths,
            expected: "search.jobs[].zones"
        );
        Assert.Contains(
            collection: paths,
            expected: "placements.rows[].board.topology"
        );
        Assert.Contains(
            collection: paths,
            expected: "hud.panels[].elements[].binding"
        );
    }
}
