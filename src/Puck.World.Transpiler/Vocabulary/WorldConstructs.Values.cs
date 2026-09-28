namespace Puck.World.Transpiler.Vocabulary;

public static partial class WorldConstructs {
    private static IReadOnlyList<WorldConstruct> Values() => [
        new(
            DocumentMember: "render",
            Grammar: "render { lighting { … } sky { … } environment { … } }",
            Keyword: "render",
            RootArm: WorldRootArm.Field,
            Shape: WorldConstructShape.Section,
            Snippet: "render {\n    ${1}\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The world's authored lighting, sky and environment."
        ),
        new(
            DocumentMember: "timeline",
            Grammar: "timeline { clock name { … } }",
            Keyword: "timeline",
            RootArm: WorldRootArm.Field,
            Shape: WorldConstructShape.Section,
            Snippet: "timeline {\n    clock ${1:day} { periodSeconds: ${2:20min}, spanSeconds: ${3:24h} }\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The world's named presentation clocks, driven by ticks, state or another clock's phase."
        ),
        new(
            DocumentMember: "timeline.clocks[]",
            Enclosing: "timeline",
            Grammar: "clock name { periodSeconds: … spanSeconds: … startSeconds: … state: … phase: … }",
            Keyword: "clock",
            Members: [new(Name: "name", Position: WorldMemberPosition.Header, Kind: WorldMemberKind.Name, DocumentKeys: ["name"], Required: true,
                Summary: "The clock's authored name, unique within the timeline.")],
            Shape: WorldConstructShape.Row,
            Snippet: "clock ${1:day} { periodSeconds: ${2:20min}, spanSeconds: ${3:24h} }",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One named source of wrapped presentation phase."
        ),
        new(
            DocumentMember: "render.keys",
            Enclosing: "render",
            Grammar: "keys(clock: name) [ { at: time, value: value, ease: linear|smooth|step } … ]",
            Keyword: "keys",
            Members: [
                new(Name: "clock", Position: WorldMemberPosition.Header, Kind: WorldMemberKind.Name, DocumentKeys: ["clock"], Required: true,
                    Summary: "The named timeline clock whose wrapped phase selects the keys."),
                new(Name: "keys", Position: WorldMemberPosition.Body, Kind: WorldMemberKind.Value, DocumentKeys: ["keys"], Required: true,
                    Summary: "Ordered keys with ordinary literals or state bindings; a section key carries named partial records."),
            ],
            Shape: WorldConstructShape.Call,
            Snippet: "keys(clock: ${1:day}) [\n    { at: 0s, value: ${2:0} }\n    { at: ${3:1s}, value: ${4:1} }\n]",
            Sugar: new(Fallback: "the generic value path", Open: false),
            Summary: "Interpolated values selected by a named clock, on any bindable value or a presentation section."
        ),
    ];
}
