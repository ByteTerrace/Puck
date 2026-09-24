namespace Puck.World.Transpiler.Vocabulary;

// The `state { }` section's constructs: the five `state.world` row declarations, the embedding spaces, and the
// `sql { }` dialect. The refusals each of these raises are named in src/Puck.World.Transpiler/README.md.
public static partial class WorldConstructs {
    // Computed rather than stored: static field initializers of a partial class run in an unspecified file order,
    // and `Table`'s own initializer reads these.
    private static IReadOnlyList<string> CellKinds => ["Int", "Fixed", "Bool", "Text", "Vector"];
    // The kinds a range and a per-second accumulation are defined over.
    private static IReadOnlyList<string> NumericKinds => ["Int", "Fixed"];
    private static IReadOnlyList<string> Wraps => ["None", "X", "Y", "Both"];

    // The clause a kind-conditional member's summary carries, written from the facet so the two cannot disagree.
    private static string OnlyWhereKindIs(IReadOnlyList<string> kinds) => $"only where the row's kind is {string.Join(
        separator: " or ",
        values: kinds
    )}";
    private static WorldConstructMember Advance(string? node = null) => new(
        AdmittedKinds: NumericKinds,
        Default: null,
        DocumentKeys: ["advance"],
        DocumentNode: node,
        Kind: WorldMemberKind.Rate,
        Name: "advance",
        Position: WorldMemberPosition.Modifier,
        Summary: $"Per-second continuous accumulation, its sign carrying the direction; {OnlyWhereKindIs(kinds: NumericKinds)}."
    );
    private static WorldConstructMember Bounds(IReadOnlyList<string>? kinds = null) => new(
        AdmittedKinds: (kinds ?? NumericKinds),
        DocumentKeys: ["min", "max", "overflow"],
        Kind: WorldMemberKind.Value,
        Name: "bounds",
        Position: WorldMemberPosition.Modifier,
        Summary: $"The row's range and what a write past it does; {OnlyWhereKindIs(kinds: (kinds ?? NumericKinds))}."
    );
    private static WorldConstructMember Capacity(string summary) => new(
        DocumentKeys: ["capacity"],
        Kind: WorldMemberKind.Number,
        Name: "capacity",
        Position: WorldMemberPosition.Modifier,
        Summary: summary
    );
    private static WorldConstructMember RowKind(IReadOnlyList<string> choices) => new(
        Choices: choices,
        DocumentKeys: ["kind"],
        Kind: WorldMemberKind.CellKind,
        Name: "kind",
        Position: WorldMemberPosition.Header,
        Required: true,
        Summary: "The domain every cell of the row holds a value in."
    );
    private static WorldConstructMember RowEnum() => new(
        DocumentKeys: ["enum"],
        Kind: WorldMemberKind.Reference,
        Name: "enum",
        Position: WorldMemberPosition.Header,
        Summary: "The declared enum the row's cells are drawn from, written `: Enum` after the name; the row is then Int."
    );
    private static WorldConstructMember RowName(string summary) => new(
        DocumentKeys: ["name"],
        Kind: WorldMemberKind.Name,
        Name: "name",
        Position: WorldMemberPosition.Header,
        Required: true,
        Summary: summary
    );
    private static IReadOnlyList<WorldConstruct> State() => [
        new(
            DocumentMember: "state",
            Grammar: "state { world { … } [spaces { … }] [lattices [ … ]] [body [ … ]] [identity [ … ]] }",
            Keyword: "state",
            Members: [
                new(
                    DocumentKeys: ["world"],
                    Kind: WorldMemberKind.Statements,
                    Name: "world",
                    Position: WorldMemberPosition.Body,
                    Summary: "The world's own rows, as declarations or as the explicit array."
                ),
            ],
            RootArm: WorldRootArm.State,
            Shape: WorldConstructShape.Section,
            Snippet: "state {\n    world {\n        $0\n    }\n}",
            Sugar: new(Fallback: "the generic value path, which carries every member unchanged", Open: true),
            Summary: "The simulation state: the world's rows, its lattices, its embedding spaces, and the per-participant lanes."
        ),
        new(
            DocumentMember: "state.world",
            Enclosing: "state",
            Grammar: "world { table | slot | pile | grid | row … }",
            Keyword: "world",
            Shape: WorldConstructShape.Section,
            Snippet: "world {\n    $0\n}",
            Sugar: new(Fallback: "the explicit `world [ ]` array", Condition: "every row in it sugars or falls back to `row { }` on its own", Open: true),
            Summary: "The authoritative rows, written as declarations; the second authoring of the section in either form is refused."
        ),
        new(
            DocumentMember: "state.spaces",
            Enclosing: "state",
            Grammar: "spaces { space name { … } }",
            Keyword: "spaces",
            Shape: WorldConstructShape.Section,
            Snippet: "spaces {\n    space ${1:name} {\n        model: \"${2:model}\"\n        revision: \"${3:1}\"\n        dimensions: ${4:256}\n    }\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The document's named vector embedding spaces, declared before any row names one."
        ),
        new(
            DocumentMember: "state.spaces[]",
            Enclosing: "spaces",
            Grammar: "space name { model: … revision: … dimensions: … }",
            Keyword: "space",
            Members: [
                RowName(summary: "The space's name, which a Vector row's `space(…)` modifier names."),
                new(
                    DocumentKeys: ["model"],
                    Kind: WorldMemberKind.Text,
                    Name: "model",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "The embedding model the lock file beside the source was produced with."
                ),
                new(
                    DocumentKeys: ["revision"],
                    Kind: WorldMemberKind.Text,
                    Name: "revision",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "The model revision, so a lock produced against another one is refused rather than read."
                ),
                new(
                    DocumentKeys: ["dimensions"],
                    Kind: WorldMemberKind.Number,
                    Name: "dimensions",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "The vector width, inside 8 to 1024."
                ),
            ],
            Shape: WorldConstructShape.Row,
            Snippet: "space ${1:name} {\n    model: \"${2:model}\"\n    revision: \"${3:1}\"\n    dimensions: ${4:256}\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One vector embedding space."
        ),
        new(
            DocumentMember: "state.world[]",
            Enclosing: "world",
            Grammar: "table name[family] [: Enum] [capacity(n)] [bounds([minimum]..[maximum], overflow:)] [advance(perSecond:)] [evicts] [space(name)] [embeds(row)] { key = value [advance(perSecond:)] [behavior(none)] }",
            Keyword: "table",
            Members: [
                RowName(summary: "The row's name, which every rule and binding reads it by."),
                RowEnum(),
                Capacity(summary: "The row's cell-count ceiling; smaller than its own authored cells is refused."),
                Bounds(),
                Advance(),
                new(
                    DocumentKeys: ["evicts"],
                    Kind: WorldMemberKind.Flag,
                    Name: "evicts",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "Drops the oldest cell instead of refusing a write at capacity."
                ),
                new(
                    AdmittedKinds: ["Vector"],
                    DocumentKeys: ["space"],
                    Kind: WorldMemberKind.Reference,
                    Name: "space",
                    Position: WorldMemberPosition.Modifier,
                    Summary: $"The declared embedding space the row's cells live in; {OnlyWhereKindIs(kinds: ["Vector"])}."
                ),
                new(
                    AdmittedKinds: ["Text"],
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Reference,
                    Lowering: "mints a companion `state.world[]` Vector row",
                    Name: "embeds",
                    Position: WorldMemberPosition.Modifier,
                    Summary: $"Mints a companion Vector row over this row's own keys; {OnlyWhereKindIs(kinds: ["Text"])}."
                ),
                new(
                    DocumentKeys: ["cells"],
                    Kind: WorldMemberKind.Cells,
                    Name: "cells",
                    Position: WorldMemberPosition.Body,
                    Summary: "The row's initial cells, omitted from the document when the table has none."
                ),
                new(
                    DocumentKeys: ["key"],
                    Kind: WorldMemberKind.Name,
                    Name: "key",
                    Position: WorldMemberPosition.Cell,
                    Required: true,
                    Summary: "One cell's key; the reserved `$` prefix is refused."
                ),
                new(
                    DocumentKeys: ["value"],
                    Kind: WorldMemberKind.Value,
                    Name: "value",
                    Position: WorldMemberPosition.Cell,
                    Required: true,
                    Summary: "The cell's initial value, converted per the row's kind."
                ),
                new(
                    AdmittedKinds: NumericKinds,
                    DocumentKeys: ["advance"],
                    Kind: WorldMemberKind.Rate,
                    Name: "advance",
                    Position: WorldMemberPosition.Cell,
                    Summary: $"This cell's own accumulation rate, in place of the row's; {OnlyWhereKindIs(kinds: NumericKinds)}."
                ),
                new(
                    Choices: ["none"],
                    DocumentKeys: ["behavior"],
                    Kind: WorldMemberKind.Enumeration,
                    Name: "behavior",
                    Position: WorldMemberPosition.Cell,
                    Summary: "Opts the cell out of the row's accumulation; refused beside the cell's own `advance`."
                ),
            ],
            Shape: WorldConstructShape.Declaration,
            Snippet: "table ${1:name} {\n    ${2:key} = $0\n}",
            Sugar: new(
                AdmittedKeys: ["domain", "kind"],
                Condition: "its name is a bare identifier, its kind is a cell kind, its `domain` is the bare `keys` shape the lowering would itself have written, every bound and cell value is a literal of the row's kind, and every `advance` rate reduces to one literal",
                Fallback: "`row { }`, the explicit form"
            ),
            Summary: "A keyed row, written as its cells rather than as the explicit `cells` array."
        ),
        new(
            DocumentMember: "state.world[]",
            Enclosing: "world",
            Grammar: "slot name[family] [: Enum] [= value] [bounds([minimum]..[maximum], overflow:)] [advance(perSecond:)] [space(name)]",
            Keyword: "slot",
            Members: [
                RowName(summary: "The row's name, which every rule and binding reads it by."),
                RowEnum(),
                new(
                    DocumentKeys: ["value"],
                    Kind: WorldMemberKind.Value,
                    Name: "value",
                    Position: WorldMemberPosition.Header,
                    Summary: "The slot's initial value; omitted, the row gains its cell from the first write."
                ),
                Bounds(),
                Advance(),
                new(
                    AdmittedKinds: ["Vector"],
                    DocumentKeys: ["space"],
                    Kind: WorldMemberKind.Reference,
                    Name: "space",
                    Position: WorldMemberPosition.Modifier,
                    Summary: $"The declared embedding space the row's value lives in; {OnlyWhereKindIs(kinds: ["Vector"])}."
                ),
            ],
            Shape: WorldConstructShape.Declaration,
            Snippet: "slot ${1:name} = $0",
            Sugar: new(
                AdmittedKeys: ["kind"],
                Condition: "its name is a bare identifier, its kind is a cell kind, it carries no `domain`, and every bound and its value is a literal of that kind",
                Fallback: "`row { }`, the explicit form"
            ),
            Summary: "A one-cell row, written as its value rather than as the explicit `value` field."
        ),
        new(
            DocumentMember: "state.world[]",
            Enclosing: "world",
            Grammar: "pile name[family] of tokenRow [capacity(n)] { token … }",
            Keyword: "pile",
            Members: [
                RowName(summary: "The row's name, which every rule and binding reads it by."),
                new(
                    DocumentKeys: ["domain"],
                    Kind: WorldMemberKind.Reference,
                    Name: "of",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The plain token-domain row supplying the keys this pile holds membership over."
                ),
                Capacity(summary: "The pile's member ceiling, never past its token domain's own."),
                new(
                    DocumentKeys: ["cells"],
                    Kind: WorldMemberKind.Tokens,
                    Name: "tokens",
                    Position: WorldMemberPosition.Body,
                    Summary: "The tokens the pile starts holding, in written order; a repeat is refused."
                ),
            ],
            Shape: WorldConstructShape.Declaration,
            Snippet: "pile ${1:name} of ${2:tokenRow} {\n    $0\n}",
            Sugar: new(
                AdmittedKeys: ["kind"],
                Condition: "its kind is Bool, its domain is the ordered `keysOf` shape with nothing else on it, and every cell is a bare key holding `true`",
                Fallback: "`row { }`, the explicit form"
            ),
            Summary: "An ordered-membership row over another row's keys; a pile is always Bool, so it carries no kind."
        ),
        new(
            DocumentMember: "state.world[]",
            Enclosing: "world",
            Grammar: "grid name[family] [: Enum] dimensions(width:, depth:) [wrap(…)] [cellSize(n)] [origin(x, y, z)] [band(n)] [empty(v)] [positions(row)] [inverse(tokens:, codes:)] [bounds(…)] [{ \"ordinal\" = value … }]",
            Keyword: "grid",
            Members: [
                RowName(summary: "The row's name, which is also the name of the topology the declaration mints."),
                RowEnum(),
                new(
                    DocumentKeys: ["width", "depth"],
                    DocumentNode: "state.lattices[]",
                    Kind: WorldMemberKind.Value,
                    Name: "dimensions",
                    Position: WorldMemberPosition.Modifier,
                    Required: true,
                    Summary: "The topology's cell counts along +X and +Z."
                ),
                new(
                    Choices: Wraps,
                    Default: "\"None\"",
                    DocumentKeys: ["wrap"],
                    DocumentNode: "state.lattices[]",
                    Kind: WorldMemberKind.Enumeration,
                    Name: "wrap",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The topology's wrapped axes."
                ),
                new(
                    Default: "1",
                    DocumentKeys: ["cellSize"],
                    DocumentNode: "state.lattices[]",
                    Kind: WorldMemberKind.Length,
                    Name: "cellSize",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The topology's cubic cell edge, in world units."
                ),
                new(
                    Default: "[0, 0, 0]",
                    DocumentKeys: ["origin"],
                    DocumentNode: "state.lattices[]",
                    Kind: WorldMemberKind.Point,
                    Name: "origin",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The topology's minimum corner, in world units."
                ),
                new(
                    Default: "0",
                    DocumentKeys: ["band"],
                    DocumentNode: "state.lattices[]",
                    Kind: WorldMemberKind.Length,
                    Name: "band",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The vertical half-extent a position must lie within to resolve to a cell; 0 admits any height."
                ),
                new(
                    Default: "0",
                    DocumentKeys: ["domain"],
                    Kind: WorldMemberKind.Value,
                    Name: "empty",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The value an unwritten cell reads, a raw integer on the wire whatever the row's kind."
                ),
                new(
                    DocumentKeys: ["valuesFrom"],
                    DocumentNode: "state.world[]",
                    Kind: WorldMemberKind.Reference,
                    Name: "positions",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "Marks another row's integer values as this topology's cell ordinals, by setting that row's `valuesFrom`."
                ),
                new(
                    DocumentKeys: ["inverse"],
                    Kind: WorldMemberKind.Value,
                    Name: "inverse",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "Derives the board from a tokens row and a codes row instead of from an authored body."
                ),
                Bounds(kinds: ["Int"]),
                new(
                    DocumentKeys: ["cells"],
                    Kind: WorldMemberKind.Cells,
                    Name: "cells",
                    Position: WorldMemberPosition.Body,
                    Summary: "Initial cells keyed by literal topology ordinal; refused beside `inverse`."
                ),
            ],
            Shape: WorldConstructShape.Declaration,
            Snippet: "grid ${1:name} dimensions(width: ${2:8}, depth: ${3:8})",
            Sugar: new(
                AdmittedKeys: ["kind"],
                Condition: "it is the only row over its topology, and that topology carries nothing outside what the modifiers can spell",
                Fallback: "`row { }` beside an explicit `state.lattices` entry"
            ),
            Summary: "A board and the Grid topology under it, from one statement."
        ),
        new(
            DocumentMember: "state.world[]",
            Enclosing: "world",
            Grammar: "row { <every explicit StateRow field> }",
            Keyword: "row",
            Shape: WorldConstructShape.Section,
            Snippet: "row {\n    $0\n}",
            Sugar: new(Fallback: "nothing — a `row { }` is what the four declarations fall back to", Open: true),
            Summary: "The explicit row form, carrying every field the four declarations cannot spell."
        ),
        new(
            DocumentMember: "state",
            Grammar: "sql { <the state SQL dialect> }",
            Keyword: "sql",
            RootArm: WorldRootArm.State,
            Shape: WorldConstructShape.EmbeddedLanguage,
            Snippet: "sql {\n    $0\n}",
            Sugar: new(Fallback: "the native constructs the block expanded into", Open: true, Printed: false),
            Summary: "A hermetic SQL spelling of rows and rules, parsed by its own dialect and lowered to the same document members the native constructs write."
        ),
    ];
}
