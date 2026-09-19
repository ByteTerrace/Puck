namespace Puck.World.Transpiler.Vocabulary;

public static partial class WorldConstructs {
    private static IReadOnlyList<WorldConstruct> Tests() => [
        new(
            DocumentMember: "(nothing)",
            Grammar: "test \"name\" { given { row = literal … } when { ticks n | seatN: command … } expect { gate … } }",
            Keyword: "test",
            Members: [
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Text,
                    Lowering: "the generated test world's file name, its verdict row names and its rule names",
                    Name: "name",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The test's name, which is what a failing verdict is addressed by."
                ),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Cells,
                    Lowering: "the generated test world's own `state.world[].cells[].value`, kind-checked against the row each names",
                    Name: "given",
                    Position: WorldMemberPosition.Body,
                    Summary: "The cells the generated test world boots at, one `row = literal` or `row[key] = literal` per line."
                ),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Statements,
                    Lowering: "the generated test world's `schedule` section: one row per seat step at the tick the preceding `ticks` reached, and the export tick the grid ends on",
                    Name: "when",
                    Position: WorldMemberPosition.Body,
                    Summary: "The tick grid and the commands along it: `ticks n` carries the cursor, `seatN: <command line>` submits one admitted step verb at it."
                ),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Statements,
                    Lowering: "one generated `state.world[]` verdict row and one generated `rules[]` entry per line: the rule fires on the last reached tick alone, and its effect writes pass or fail from the expectation beside the values it read",
                    Name: "expect",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "What the test claims, one rule-gate expression per line."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "test \"${1:name}\" {\n    when {\n        ticks ${2:1}\n    }\n    expect {\n        $0\n    }\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "A world's behaviour stated in the world's own language; the document carries no trace of it and each test lowers to a generated test world of its own."
        ),
    ];
}
