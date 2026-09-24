namespace Puck.World.Transpiler.Vocabulary;

// The rule vocabulary: the rule and its groups, the gate, the locals, the decision, and the effect statements. An
// effect's `Enclosing` is `rule`, which stands for a rule body wherever one appears.
public static partial class WorldConstructs {
    private static WorldConstructMember EffectTarget() => new(
        DocumentKeys: ["state", "key"],
        Kind: WorldMemberKind.Reference,
        Name: "target",
        Position: WorldMemberPosition.Header,
        Required: true,
        Summary: "The cell the effect writes, read as one state token."
    );

    // The node a `transformState` effect's own arguments sit on: the effect carries one `transform` key, and the
    // arm's fields are that value's.
    private const string TransformNode = "rules[].effects[][transformState].transform";

    private static WorldConstructMember TransformArgument(string name, string summary, WorldMemberKind kind = WorldMemberKind.Reference) => new(
        DocumentKeys: [name],
        DocumentNode: TransformNode,
        Kind: kind,
        Name: name,
        Position: WorldMemberPosition.Header,
        Required: true,
        Summary: summary
    );
    // A statement that writes a `transformState` effect under its own keyword rather than through `transform`: the
    // arm and the fields it fixes are the spelling, so the printer takes it only for a node matching them.
    private static WorldConstruct TransformSugar(string grammar, string keyword, IReadOnlyList<WorldConstructMember> members, string snippet, WorldConstructSugar sugar, string summary) => new(
        DocumentMember: "rules[].effects[][transformState]",
        Enclosing: "rule",
        Grammar: grammar,
        Keyword: keyword,
        Members: members,
        Shape: WorldConstructShape.Statement,
        Snippet: snippet,
        Sugar: sugar,
        Summary: summary
    );
    private static WorldConstruct Effect(string documentMember, string grammar, string keyword, string snippet, string summary, IReadOnlyList<WorldConstructMember>? members = null) => new(
        DocumentMember: documentMember,
        Enclosing: "rule",
        Grammar: grammar,
        Keyword: keyword,
        Members: (members ?? [EffectTarget()]),
        Shape: WorldConstructShape.Statement,
        Snippet: snippet,
        Sugar: new(
            Condition: "its target round-trips through `ExpressionSpelling` unchanged",
            Open: true,
            Fallback: "the call-form escape hatch, `type(k: v, …)`"
        ),
        Summary: summary
    );
    private static IReadOnlyList<WorldConstruct> Rules() => [
        new(
            DocumentMember: "rules[]",
            Grammar: "rule \"name\" { [when Gate] [mode: …] [forEach: …] [local …] [decision { … }] <effect>* }",
            Keyword: "rule",
            Members: [
                QuotedName(key: "name", summary: "The rule's name, which a group claims it by and a trace reports."),
                new(
                    Choices: ["Edge", "Level"],
                    DocumentKeys: ["mode"],
                    Kind: WorldMemberKind.Enumeration,
                    Name: "mode",
                    Position: WorldMemberPosition.Property,
                    Summary: "Whether the rule fires on the gate becoming true or on every tick it is true."
                ),
                new(
                    DocumentKeys: ["forEach"],
                    Kind: WorldMemberKind.Reference,
                    Name: "forEach",
                    Position: WorldMemberPosition.Property,
                    Summary: "The row whose live keys the rule iterates, one firing per key."
                ),
                new(
                    DocumentKeys: ["effects"],
                    Kind: WorldMemberKind.Statements,
                    Name: "effects",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The effect statements the rule fires, in written order; a rule with none is refused."
                ),
            ],
            RootArm: WorldRootArm.Rules,
            Shape: WorldConstructShape.Block,
            Snippet: "rule \"${1:name}\" {\n    when $0\n}",
            Sugar: new(
                Condition: "every predicate and effect target in it has a sugar spelling that reparses to the same tree",
                Open: true,
                Fallback: "the generic value path, for the whole `rules` array"
            ),
            Summary: "One reactive rule: a gate, its working values, and the effects it fires."
        ),
        new(
            DocumentMember: "rules[]",
            Grammar: "rules name [when Gate] { rule \"name\" { … } … }",
            Keyword: "rules",
            Members: [
                RowName(summary: "The scope's name, prefixed onto every rule it claims."),
                new(
                    DocumentKeys: ["gate"],
                    Kind: WorldMemberKind.Gate,
                    Name: "when",
                    Position: WorldMemberPosition.Header,
                    Summary: "A gate applied to every member, conjoined with the member's own."
                ),
            ],
            RootArm: WorldRootArm.Rules,
            Shape: WorldConstructShape.Block,
            Snippet: "rules ${1:name} {\n    rule \"${2:member}\" {\n        $0\n    }\n}",
            Sugar: WorldConstructSugar.ReadBack(condition: "a rule's or group's name joins scope names to its own with `$` and every part is a name: each member prints inside one `rules` scope per part, carrying its own copy of the shared header"),
            Summary: "A shared header for several rules: its gate, locals and properties apply to every member."
        ),
        new(
            DocumentMember: "ruleGroups[]",
            Grammar: "stabilize name [undo({ rows [row] depth: n })] [maxPasses(n)] [until Gate] { rule \"name\" { … } … }",
            Keyword: "stabilize",
            Members: [
                RowName(summary: "The group's name, prefixed onto every rule it claims."),
                new(DocumentKeys: ["undo"], Kind: WorldMemberKind.Value, Name: "undo", Position: WorldMemberPosition.Modifier, Summary: "The rows and bounded number of completed turns retained for rewindGroup."),
                new(
                    DocumentKeys: ["passes"],
                    Kind: WorldMemberKind.Number,
                    Name: "maxPasses",
                    Position: WorldMemberPosition.Modifier,
                    Summary: "The pass ceiling whose breach is one counted refusal naming the group."
                ),
                new(
                    DocumentKeys: ["trigger"],
                    Kind: WorldMemberKind.Gate,
                    Name: "until",
                    Position: WorldMemberPosition.Header,
                    Summary: "Arms the group while the gate reads false, so it lowers as that gate's negation."
                ),
                new(
                    DocumentKeys: ["steps"],
                    Kind: WorldMemberKind.Statements,
                    Name: "members",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The rules the group claims, named `<group>$<rule>`; a group with no member is refused."
                ),
            ],
            RootArm: WorldRootArm.RuleGroups,
            Shape: WorldConstructShape.Block,
            Snippet: "stabilize ${1:name} maxPasses(${2:32}) {\n    rule \"${3:member}\" {\n        $0\n    }\n}",
            Sugar: new(
                AdmittedKeys: ["shape"],
                Condition: "every rule it claims is in the document's own `rules` array and no other group claims it",
                Open: true,
                Fallback: "the explicit `ruleGroups` array beside an unclaimed `rules` array",
                RequiredKeys: ["shape"]
            ),
            Summary: "A fixpoint group: its members re-fire until the board settles or the pass ceiling refuses."
        ),
        new(
            DocumentMember: "ruleGroups[]",
            Grammar: "workflow name [undo({ rows [row] depth: n })] { step name [skip] { … } … }",
            Keyword: "workflow",
            Members: [
                RowName(summary: "The group's name, prefixed onto every step it claims."),
                new(DocumentKeys: ["undo"], Kind: WorldMemberKind.Value, Name: "undo", Position: WorldMemberPosition.Modifier, Summary: "The rows and bounded number of completed turns retained for rewindGroup."),
                new(
                    DocumentKeys: ["steps"],
                    Kind: WorldMemberKind.Statements,
                    Name: "steps",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The steps the cursor advances through, one per committed firing."
                ),
            ],
            RootArm: WorldRootArm.RuleGroups,
            Shape: WorldConstructShape.Block,
            Snippet: "workflow ${1:name} {\n    step ${2:first} {\n        $0\n    }\n}",
            Sugar: new(
                AdmittedKeys: ["shape"],
                Condition: "every rule it claims is in the document's own `rules` array and no other group claims it",
                Open: true,
                Fallback: "the explicit `ruleGroups` array beside an unclaimed `rules` array",
                RequiredKeys: ["shape"]
            ),
            Summary: "A staged group: a cursor that advances one step per committed firing."
        ),
        new(
            DocumentMember: "rules[]",
            Enclosing: "workflow",
            Grammar: "step name [skip] { [when Gate] <effect>* }",
            Keyword: "step",
            Members: [
                RowName(summary: "The step's name, which the group prefixes."),
                new(
                    DocumentKeys: ["onRefusal"],
                    DocumentNode: "ruleGroups[].steps[]",
                    Kind: WorldMemberKind.Flag,
                    Name: "skip",
                    Position: WorldMemberPosition.Header,
                    Summary: "Advances the cursor past this step's own refusal instead of stalling on it."
                ),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "step ${1:name} {\n    $0\n}",
            Sugar: new(Fallback: "one `rule` block, printed outside the group", Open: true),
            Summary: "One stage of a workflow; its body is a rule body, so a nested `rule` block is refused."
        ),
        new(
            DocumentMember: "rules[].gate",
            Enclosing: "rule",
            Grammar: "when Operand cmp Operand [: Int|Fixed] | when Gate and|or Gate | when not Gate | when (Gate)",
            Keyword: "when",
            Members: [
                new(
                    DocumentKeys: ["gate"],
                    Kind: WorldMemberKind.Gate,
                    Name: "gate",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The predicate; a second `when` in one rule or option is refused."
                ),
            ],
            Shape: WorldConstructShape.Statement,
            Snippet: "when ${1:condition}",
            Sugar: new(
                Condition: "no `compareValue` in it opens its left operand with `(` and no `all`/`any` in it carries fewer than two children",
                Open: true,
                Fallback: "`gate: <call-form>`, an ordinary rule-body property"
            ),
            Summary: "What must hold before the rule fires."
        ),
        new(
            DocumentMember: "rules[].locals[]",
            Enclosing: "rule",
            Grammar: "local name = <operand>",
            Keyword: "local",
            Members: [
                RowName(summary: "The local's name, read bare in every operand after it."),
                new(
                    DocumentKeys: ["expression"],
                    Kind: WorldMemberKind.Operand,
                    Name: "expression",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "What the local evaluates to, once per firing, before any effect runs."
                ),
            ],
            Shape: WorldConstructShape.Statement,
            Snippet: "local ${1:name} = ${2:expression}",
            Sugar: new(AdmittedKeys: ["kind"], Fallback: "the explicit `locals` array", Open: true),
            Summary: "One working value the rule computes before its effects."
        ),
        new(
            DocumentMember: "rules[].decision",
            Enclosing: "rule",
            Grammar: "decision { periodSeconds: … [mode: …] [interrupt Gate] [onNoChoice { … }] option \"name\" { … } }",
            Keyword: "decision",
            Members: [
                new(
                    DocumentKeys: ["periodSeconds"],
                    Kind: WorldMemberKind.Seconds,
                    Name: "periodSeconds",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "How often the choice is reconsidered."
                ),
                new(
                    DocumentKeys: ["options"],
                    Kind: WorldMemberKind.Statements,
                    Name: "options",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The candidates, each with its own gate, score and effects."
                ),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "decision {\n    periodSeconds: ${1:1s}\n    option \"${2:name}\" {\n        score: $0\n    }\n}",
            Sugar: new(Fallback: "the explicit `decision` object", Open: true),
            Summary: "A reconsidered choice among scored options."
        ),
        new(
            DocumentMember: "rules[].decision.options[]",
            Enclosing: "decision",
            Grammar: "option \"name\" { [when Gate] score: <operand> [neighbors { … }] <effect>* }",
            Keyword: "option",
            Members: [
                QuotedName(key: "name", summary: "The option's name, which the chosen result reports."),
                new(
                    DocumentKeys: ["score"],
                    Kind: WorldMemberKind.Operand,
                    Name: "score",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "What the option is worth this period; an option without one is refused."
                ),
                new(
                    DocumentKeys: ["effects"],
                    Kind: WorldMemberKind.Statements,
                    Name: "effects",
                    Position: WorldMemberPosition.Body,
                    Summary: "What fires when this option is the one chosen."
                ),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "option \"${1:name}\" {\n    score: $0\n}",
            Sugar: new(
                Condition: "its gate has a `when` spelling",
                Open: true,
                Fallback: "`gate: <call-form>`, an ordinary option-body property"
            ),
            Summary: "One candidate of a decision."
        ),
        new(
            DocumentMember: "rules[].decision.interrupt",
            Enclosing: "decision",
            Grammar: "interrupt Gate",
            Keyword: "interrupt",
            Members: [
                new(
                    DocumentKeys: ["interrupt"],
                    Kind: WorldMemberKind.Gate,
                    Name: "interrupt",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "Reconsiders the choice before the period elapses."
                ),
            ],
            Shape: WorldConstructShape.Statement,
            Snippet: "interrupt ${1:condition}",
            Sugar: new(
                Condition: "the gate has a `when` spelling",
                Open: true,
                Fallback: "`interrupt: <call-form>`, an ordinary decision-body property"
            ),
            Summary: "What cuts a commitment short."
        ),
        new(
            DocumentMember: "rules[].decision.onNoChoice",
            Enclosing: "decision",
            Grammar: "onNoChoice { <effect>* }",
            Keyword: "onNoChoice",
            Shape: WorldConstructShape.Block,
            Snippet: "onNoChoice {\n    $0\n}",
            Sugar: new(Fallback: "the explicit `onNoChoice` array", Open: true),
            Summary: "What fires when no option's gate admits it."
        ),
        new(
            DocumentMember: "rules[].effects[][transaction]",
            Enclosing: "rule",
            Grammar: "transaction { <effect>* } [onFailure { <effect>* }]",
            Keyword: "transaction",
            Members: [
                new(
                    DocumentKeys: ["effects"],
                    Kind: WorldMemberKind.Statements,
                    Name: "effects",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The effects that commit together or not at all; a nested transaction is refused."
                ),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "transaction {\n    $0\n}",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "A batch of effects that commits as one and rewinds as one."
        ),
        new(
            DocumentMember: "rules[].effects[][claim]",
            Enclosing: "rule",
            Grammar: "claim pool as binding { <effect>* }",
            Keyword: "claim",
            Members: [
                new(DocumentKeys: ["pool"], Kind: WorldMemberKind.Reference, Name: "pool", Position: WorldMemberPosition.Header, Required: true, Summary: "The pool to allocate from."),
                new(DocumentKeys: ["binding"], Kind: WorldMemberKind.Reference, Name: "binding", Position: WorldMemberPosition.Header, Required: true, Summary: "The lexical name for the claimed instance."),
                new(DocumentKeys: ["effects"], Kind: WorldMemberKind.Statements, Name: "effects", Position: WorldMemberPosition.Body, Required: true, Summary: "The effects evaluated with the claimed binding in scope."),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "claim ${1:pool} as ${2:item} {\n    $0\n}",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "Claims one available instance and binds its fields for nested effects."
        ),
        new(
            DocumentMember: "rules[].effects[][claimPair]",
            Enclosing: "rule",
            Grammar: "claim pair pool between left, right as binding { <effect>* }",
            Keyword: "claim pair",
            Members: [
                new(DocumentKeys: ["pool"], Kind: WorldMemberKind.Reference, Name: "pool", Position: WorldMemberPosition.Header, Required: true, Summary: "The pair pool to allocate from."),
                new(DocumentKeys: ["left", "right"], Kind: WorldMemberKind.Reference, Name: "endpoints", Position: WorldMemberPosition.Header, Required: true, Summary: "The bound instances used as relationship endpoints."),
                new(DocumentKeys: ["binding"], Kind: WorldMemberKind.Reference, Name: "binding", Position: WorldMemberPosition.Header, Required: true, Summary: "The lexical name for the claimed relationship."),
                new(DocumentKeys: ["effects"], Kind: WorldMemberKind.Statements, Name: "effects", Position: WorldMemberPosition.Body, Required: true, Summary: "The effects evaluated with the relationship binding in scope."),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "claim pair ${1:pool} between ${2:left}, ${3:right} as ${4:pair} {\n    $0\n}",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "Claims one relationship between two bound record instances."
        ),
        new(
            DocumentMember: "rules[].effects[][forEachPool]",
            Enclosing: "rule",
            Grammar: "for each binding in pool { <effect>* }",
            Keyword: "for each",
            Members: [
                new(DocumentKeys: ["pool"], Kind: WorldMemberKind.Reference, Name: "pool", Position: WorldMemberPosition.Header, Required: true, Summary: "The pool whose live instances are visited."),
                new(DocumentKeys: ["binding"], Kind: WorldMemberKind.Reference, Name: "binding", Position: WorldMemberPosition.Header, Required: true, Summary: "The lexical name for the current instance."),
                new(DocumentKeys: ["effects"], Kind: WorldMemberKind.Statements, Name: "effects", Position: WorldMemberPosition.Body, Required: true, Summary: "The effects evaluated once for each live instance."),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "for each ${1:item} in ${2:pool} {\n    $0\n}",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "Visits every live instance in ascending slot order."
        ),
        new(
            DocumentMember: "rules[].effects[][release]",
            Enclosing: "rule",
            Grammar: "release binding",
            Keyword: "release",
            Members: [new(DocumentKeys: ["binding"], Kind: WorldMemberKind.Reference, Name: "binding", Position: WorldMemberPosition.Header, Required: true, Summary: "The lexical instance binding to release.")],
            Shape: WorldConstructShape.Statement,
            Snippet: "release $0",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "Releases the instance named by a lexical pool binding."
        ),
        new(
            DocumentMember: "rules[].effects[][transaction].onFailure",
            Enclosing: "transaction",
            Grammar: "onFailure { <effect>* }",
            Keyword: "onFailure",
            Shape: WorldConstructShape.Block,
            Snippet: "onFailure {\n    $0\n}",
            Sugar: new(Fallback: "the call-form escape hatch", Open: true),
            Summary: "What fires after a transaction rewinds; more than one on a transaction is refused."
        ),
        new(
            DocumentMember: "rules[].effects[][if]",
            Enclosing: "rule",
            Grammar: "if Gate { <effect>* } [else if Gate { … }]* [else { … }]",
            Keyword: "if",
            Members: [
                new(
                    DocumentKeys: ["condition"],
                    Kind: WorldMemberKind.Gate,
                    Name: "condition",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "Read through the same predicate lowering `when` uses."
                ),
                new(
                    DocumentKeys: ["then", "else"],
                    Kind: WorldMemberKind.Statements,
                    Name: "branches",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "One nested `if` node per `else if` level, so a chain of any length lowers as a single branch does."
                ),
            ],
            Shape: WorldConstructShape.Block,
            Snippet: "if ${1:condition} {\n    $0\n}",
            Sugar: new(
                Condition: "the condition has a `when` spelling, since an `if` has no property-style fallback: the parser reads `if(…)` at statement position as this same grammar, never as a generic call",
                Open: true,
                Fallback: "nothing — decompiling the document fails rather than emitting a source that means something else"
            ),
            Summary: "A conditional effect."
        ),
        Effect(
            documentMember: "rules[].effects[][pushState]",
            grammar: "push row = <rhs>",
            keyword: "push",
            members: [
                new(
                    DocumentKeys: ["state"],
                    Kind: WorldMemberKind.Reference,
                    Name: "target",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The ordered row the value is appended to; the key is minted, so the target carries none."
                ),
                new(
                    DocumentKeys: ["fromState", "expression"],
                    Kind: WorldMemberKind.Operand,
                    Name: "value",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "What is appended: a bare row read lowers as `fromState`, anything else as `expression`."
                ),
            ],
            snippet: "push ${1:row} = ${2:value}",
            summary: "Appends a value to a history ring, overwriting the oldest slot when the ring is full."
        ),
        Effect(
            documentMember: "rules[].effects[][removeStateCell]",
            grammar: "remove row[key]",
            keyword: "remove",
            snippet: "remove ${1:row}",
            summary: "Drops a cell from its row; a later read of that key reads zero."
        ),
        Effect(
            documentMember: "rules[].effects[][scheduleState]",
            grammar: "schedule row[key] in Ns",
            keyword: "schedule",
            members: [
                EffectTarget(),
                new(
                    DocumentKeys: ["delaySeconds"],
                    Kind: WorldMemberKind.Seconds,
                    Name: "in",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "How far ahead the write lands; the time unit is required."
                ),
            ],
            snippet: "schedule ${1:row} in ${2:1s}",
            summary: "Writes a cell a fixed time from now."
        ),
        Effect(
            documentMember: "rules[].effects[][transformState]",
            grammar: "transform call(…)",
            keyword: "transform",
            members: [
                new(
                    DocumentKeys: ["transform"],
                    Kind: WorldMemberKind.Value,
                    Name: "transform",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The `StateTransform` arm, written call-form; its own argument names the destination."
                ),
            ],
            snippet: "transform ${1:boardCombine}(${2:args})",
            summary: "Runs one state transform."
        ),
        TransformSugar(
            grammar: "draw fromZone [to] toZone",
            keyword: "draw",
            members: [
                TransformArgument(
                    name: "from",
                    summary: "The ordered zone the token leaves, taken from its first position."
                ),
                TransformArgument(
                    name: "to",
                    summary: "The ordered zone the token is appended to."
                ),
            ],
            snippet: "draw ${1:from} to ${2:to}",
            sugar: new(
                Condition: "the transform is a `transfer` selecting `First` with no key, no `insertFirst` and no count of its own",
                Fallback: "`transform transfer(…)`",
                Open: true
            ),
            summary: "Moves one token from the front of an ordered zone to another."
        ),
        TransformSugar(
            grammar: "deal count [from] fromZone [to] toZone",
            keyword: "deal",
            members: [
                TransformArgument(
                    kind: WorldMemberKind.Number,
                    name: "count",
                    summary: "How many tokens move in this one transfer, each selected afresh from what remains."
                ),
                TransformArgument(
                    name: "from",
                    summary: "The ordered zone the tokens leave, taken from its first position."
                ),
                TransformArgument(
                    name: "to",
                    summary: "The ordered zone the tokens are appended to."
                ),
            ],
            snippet: "deal ${1:1} from ${2:from} to ${3:to}",
            sugar: new(
                Condition: "the transform is a `transfer` selecting `First` with no key and no `insertFirst`, carrying a count",
                Fallback: "`transform transfer(…)`",
                Open: true
            ),
            summary: "Moves a counted run of tokens from the front of an ordered zone to another."
        ),
        TransformSugar(
            grammar: "shuffle row [with] drawRow",
            keyword: "shuffle",
            members: [
                TransformArgument(
                    name: "row",
                    summary: "The ordered zone or keyed row whose cells are reordered in place."
                ),
                TransformArgument(
                    name: "draw",
                    summary: "The integer `StreamDraw` row supplying the samples one Fisher-Yates pass consumes."
                ),
            ],
            snippet: "shuffle ${1:row} with ${2:draw}",
            sugar: new(
                Condition: "the transform is a `shuffle` naming both its row and its draw row",
                Fallback: "`transform shuffle(…)`",
                Open: true
            ),
            summary: "Reorders a row's cells by one pass over a draw row."
        ),
        new(
            DocumentMember: "patterns[]",
            Grammar: "pattern name : Kind { [attribute: … | value: …] [maxStates: n] symbols { name = value … } match: <language> }",
            Keyword: "pattern",
            Members: [
                RowName(summary: "The pattern's name, which a match operand and a transform read it by."),
                RowKind(choices: CellKinds),
                new(
                    DocumentKeys: ["attribute"],
                    Kind: WorldMemberKind.Reference,
                    Name: "attribute",
                    Position: WorldMemberPosition.Property,
                    Summary: "The row supplying each token's word, in place of `value`."
                ),
                new(
                    DocumentKeys: ["value"],
                    Kind: WorldMemberKind.Operand,
                    Name: "value",
                    Position: WorldMemberPosition.Property,
                    Summary: "An infix expression evaluated once per token to produce its word."
                ),
                new(
                    DocumentKeys: ["maxStates"],
                    Kind: WorldMemberKind.Number,
                    Name: "maxStates",
                    Position: WorldMemberPosition.Property,
                    Summary: "The derivative machine's state budget, inside 1 to 1,024."
                ),
                new(
                    DocumentKeys: ["symbols"],
                    Kind: WorldMemberKind.Statements,
                    Name: "symbols",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The alphabet, each entry a name standing for one value or a band."
                ),
                new(
                    DocumentKeys: ["pattern"],
                    Kind: WorldMemberKind.Text,
                    Name: "match",
                    Position: WorldMemberPosition.Property,
                    Required: true,
                    Summary: "The pattern language, loosest operator first: choice, intersection, sequence, complement, repetition; it lowers to the compiled tree."
                ),
            ],
            RootArm: WorldRootArm.Patterns,
            Shape: WorldConstructShape.Block,
            Snippet: "pattern ${1:name} : ${2|Int,Fixed,Bool,Text|} {\n    symbols {\n        ${3:a} = ${4:1}\n    }\n    match: $0\n}",
            Sugar: new(
                Condition: "its name reads as a bare identifier, its kind is spelled exactly as its enum member, its alphabet is non-empty and every symbol name is bare, and every n-ary node of its language carries two or more items",
                Fallback: "the generic value path, for the whole `patterns` array"
            ),
            Summary: "One pattern over a row's tokens, compiled while it lowers so a refusal names the line that declared it."
        ),
        new(
            DocumentMember: "sets[]",
            Grammar: "set name: <expression>",
            Keyword: "set",
            Members: [
                RowName(summary: "The set's name, which a transform and a search plan read it by."),
                new(
                    DocumentKeys: ["set"],
                    Kind: WorldMemberKind.Text,
                    Name: "set",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The cell-set algebra, loosest operator first: union, intersection, complement, over `all`, `none`, `board`, `zone` and `family`."
                ),
            ],
            RootArm: WorldRootArm.Sets,
            Shape: WorldConstructShape.Statement,
            Snippet: "set ${1:name}: $0",
            Sugar: new(
                Condition: "the algebra reads the row back and prints it, and its name reads as a bare identifier",
                Fallback: "the generic value path, for the whole `sets` array"
            ),
            Summary: "One named set over the positions of a board, a zone, or a family, which `boardCombine` and `writeSet` read by name."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "enum Name { member … }",
            Keyword: "enum",
            Members: [
                RowName(summary: "The enumeration's name, which qualifies each member as `Name.member`."),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Statements,
                    Lowering: "the literals each read of a member expands to",
                    Name: "members",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The members, each a compile-time constant equal to its own 0-based position."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "enum ${1:Name} {\n    $0\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "Names a run of integers at compile time; the document carries the numbers, never the enumeration."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "record Name { field: Kind … }",
            Keyword: "record",
            Members: [
                RowName(summary: "The record's name, which a `pool name of Name` declaration instantiates."),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Statements,
                    Lowering: "one field descriptor in `state.records[]`",
                    Name: "fields",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The fields that every instance of a pool using this record carries."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "record ${1:Name} {\n    ${2:field}: ${3|Int,Fixed,Bool,Text|}\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "A typed field set instantiated by pools."
        ),
        new(
            DocumentMember: "state.pools[]",
            Grammar: "pool name of Record capacity(capacity) = [{ field: value … } …]",
            Keyword: "pool",
            Members: [
                RowName(summary: "The pool's stable name."),
                new(DocumentKeys: ["record"], Kind: WorldMemberKind.Reference, Name: "record", Position: WorldMemberPosition.Header, Required: true, Summary: "The record type shared by every instance."),
                new(DocumentKeys: ["capacity"], Kind: WorldMemberKind.Number, Name: "slots", Position: WorldMemberPosition.Header, Required: true, Summary: "The positive maximum number of simultaneously live instances."),
                new(DocumentKeys: ["initial"], Kind: WorldMemberKind.Value, Name: "initial", Position: WorldMemberPosition.Body, Summary: "Static slot seeds, each overriding selected record fields."),
            ],
            RootArm: WorldRootArm.State,
            Shape: WorldConstructShape.Declaration,
            Snippet: "pool ${1:name} of ${2:Record} capacity(${3:capacity}) = [\n    $0\n]",
            Sugar: WorldConstructSugar.None,
            Summary: "Declares a bounded typed instance pool with optional static seeds."
        ),
        new(
            DocumentMember: "state.pairPools[]",
            Grammar: "pairPool name record Record left Pool right Pool maxLive n directed flag allowSelf flag",
            Keyword: "pairPool",
            Members: [
                RowName(summary: "The pair pool's stable name."),
                new(DocumentKeys: ["record"], Kind: WorldMemberKind.Reference, Name: "record", Position: WorldMemberPosition.Header, Required: true, Summary: "The record type shared by every relationship."),
                new(DocumentKeys: ["leftPool"], Kind: WorldMemberKind.Reference, Name: "left", Position: WorldMemberPosition.Header, Required: true, Summary: "The pool supplying each left endpoint."),
                new(DocumentKeys: ["rightPool"], Kind: WorldMemberKind.Reference, Name: "right", Position: WorldMemberPosition.Header, Required: true, Summary: "The pool supplying each right endpoint."),
                new(DocumentKeys: ["maxLive"], Kind: WorldMemberKind.Number, Name: "maxLive", Position: WorldMemberPosition.Header, Required: true, Summary: "The positive maximum number of live relationships."),
                new(DocumentKeys: ["directed"], Kind: WorldMemberKind.Flag, Name: "directed", Position: WorldMemberPosition.Header, Summary: "Whether endpoint order distinguishes relationships."),
                new(DocumentKeys: ["allowSelf"], Kind: WorldMemberKind.Flag, Name: "allowSelf", Position: WorldMemberPosition.Header, Summary: "Whether both endpoints may name the same instance."),
            ],
            RootArm: WorldRootArm.State,
            Shape: WorldConstructShape.Declaration,
            Snippet: "pairPool ${1:name} record ${2:Record} left ${3:left} right ${4:right} maxLive ${5:capacity} directed true allowSelf false",
            Sugar: WorldConstructSugar.None,
            Summary: "Declares a bounded pool of relationships between two ordinary pools."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "derive name = <expression>",
            Keyword: "derive",
            Members: [
                RowName(summary: "The derived name, substituted wherever an operand reads it."),
                new(
                    DocumentKeys: [],
                    Kind: WorldMemberKind.Operand,
                    Lowering: "the operand text substituted wherever the name is read",
                    Name: "expression",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The operand text every read of the name expands to."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Statement,
            Snippet: "derive ${1:name} = $0",
            Sugar: WorldConstructSugar.None,
            Summary: "Names an operand at compile time; the document carries the expansion, never the name."
        ),
    ];
}
