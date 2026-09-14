using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>The name registry covers the document model, the checked-in table matches it, and an aliased import
/// rewrites every spelling a name can take.</summary>
public sealed class WorldNameRegistryLawTests {
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
                    { "$type": "setState", "state": "turn", "expression": { "tokens": [ { "$type": "state", "name": "turn", "key": "$cell:board:$each" }, { "$type": "boardShift", "topology": "cube", "direction": "north" } ] } },
                    { "$type": "addState", "state": "board", "key": "$each", "expression": "turn + 1" }
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
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["tokens"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "$cell:twin_board:$each",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["tokens"]![0]!["key"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_cube",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["tokens"]![1]!["topology"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "north",
            actual: module["rules"]![0]!["effects"]![0]!["expression"]!["tokens"]![1]!["direction"]!.GetValue<string>()
        );
        Assert.Equal(
            expected: "twin_turn + 1",
            actual: module["rules"]![0]!["effects"]![1]!["expression"]!.GetValue<string>()
        );
        Assert.DoesNotContain(
            actualString: text,
            expectedSubstring: "\"board\""
        );
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
    [InlineData("3 - board", "3 - a_board")]
    [InlineData("board[from] + game[to]", "a_board[from] + a_game[to]")]
    [InlineData("boardShift(board, cube, L0) & 0xFF", "boardShift(a_board, a_cube, L0) & 0xFF")]
    [InlineData("$board:mask:board:1:1 | min(armor, 2)", "$board:mask:a_board:1:1 | min(a_armor, 2)")]
    [InlineData("`pile-one`[$each] ? 1 : 0", "`a_pile-one`[$each] ? 1 : 0")]
    [InlineData("buffs[board[$each]]", "buffs[a_board[$each]]")]
    [InlineData("$table:armor:power[$bind:move]", "$table:a_armor:power[$bind:move]")]
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
    [InlineData("$bind:board", "$bind:board")]
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
            expected: "rules[].effects[][setState].expression{tokens}.tokens[][state].name"
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
