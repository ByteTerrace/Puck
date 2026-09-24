namespace Puck.World.Transpiler.Vocabulary;

public static partial class WorldConstructs {
    private static IReadOnlyList<WorldConstruct> Tests() => [
        new(
            DocumentMember: "(nothing)",
            Grammar: "test \"name\" [with module(arguments)] { given { row = literal | world { row = literal … } … } when { ticks n | seatN [refused [\"text\"]]: command | world { seatN: command … } … } expect { gate | world { gate … } … } }",
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
                    Kind: WorldMemberKind.Value,
                    Lowering: "the whole generated test world: the named module expanded with these arguments, standing alone rather than inside the enclosing document",
                    Name: "subject",
                    Position: WorldMemberPosition.Header,
                    Summary: "The module the test is about, written `with module(arguments)`; omitted for a test whose subject is the world it stands in."
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
                    Lowering: "the generated test world's `schedule` section: one row per seat step at the tick the preceding `ticks` reached, a `refused` step carrying `expect: Refused` and its `refusal` text, and the export tick the grid ends on",
                    Name: "when",
                    Position: WorldMemberPosition.Body,
                    Summary: "The tick grid and the commands along it: `ticks n` carries the cursor, `seatN: <command line>` submits one admitted step or read verb at it, and `seatN refused \"text\": <command line>` claims the world refuses it, the recorded refusal containing the text when one is written."
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
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Statements,
                    Lowering: "one generated document per world of the composition, its entry world carrying the `schedule` section whose `instances` arm the rest and whose rows address them by name; a far world's own verdict rule fires one tick before the booted world's export tick, which is where that world stands at the shared export step",
                    Name: "world block",
                    Position: WorldMemberPosition.Body,
                    Summary: "Inside `given`, `when` or `expect` at the root of a source that emits several worlds: `<world> { … }` addresses one of them by its own name. There is no default world, so every line of such a test stands inside one; `ticks` stays outside, since one grid carries the whole composed run."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "test \"${1:name}\" {\n    when {\n        ticks ${2:1}\n    }\n    expect {\n        $0\n    }\n}",
            Sugar: WorldConstructSugar.ReadBack(condition: "a generated test world is decompiled whose verdict rows, witnesses and verdict rules stand last and whose schedule is seat steps alone: the block prints over the rest of the document, whose boot values already carry what `given` wrote; a composed test's documents arm one another by file and are refused"),
            Summary: "Behaviour stated in the world's own language — a world's own, a module's under the arguments the test gives it, or a whole composition's; the document carries no trace of it and each test lowers to a generated test world of its own."
        ),
    ];
}
