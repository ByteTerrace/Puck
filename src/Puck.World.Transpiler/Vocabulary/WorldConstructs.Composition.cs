namespace Puck.World.Transpiler.Vocabulary;

// Composition constructs exist only while a source package is compiled. They expand into ordinary world
// document fields, so a decompiler prints those fields rather than trying to reconstruct the composition source.
public static partial class WorldConstructs {
    private static WorldConstructMember CompositionMember(
        string name,
        WorldMemberPosition position,
        WorldMemberKind kind,
        string lowering,
        string summary,
        bool required = false
    ) => new(
        DocumentKeys: [],
        Kind: kind,
        Lowering: lowering,
        Name: name,
        Position: position,
        Required: required,
        Summary: summary
    );
    private static IReadOnlyList<WorldConstruct> Composition() => [
        new(
            DocumentMember: "(nothing)",
            Grammar: "world name = module(arguments)",
            Keyword: "world",
            Members: [
                CompositionMember(
                    kind: WorldMemberKind.Name,
                    lowering: "the output document's identity and file name",
                    name: "name",
                    position: WorldMemberPosition.Header,
                    required: true,
                    summary: "The output world's name, written as an identifier, a string, or an interpolated string evaluated in the current lexical scope."
                ),
                CompositionMember(
                    kind: WorldMemberKind.Value,
                    lowering: "the complete generated world document",
                    name: "module",
                    position: WorldMemberPosition.Header,
                    required: true,
                    summary: "The module invocation expanded with these arguments to produce this world's document."
                ),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Statement,
            Snippet: "world ${1:name} = ${2:module}()",
            Sugar: WorldConstructSugar.None,
            Summary: "Emits one named world document by invoking a module."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "border left.endpoint, right.endpoint { [center [x, y, z]] [yaw: angle] [pitch: angle] [width: length] [height: length] [hysteresis: length] }",
            Keyword: "border",
            Members: [
                CompositionMember("left", WorldMemberPosition.Header, WorldMemberKind.Reference, "adjacency and boundary fields in the two endpoint worlds", "The source world and its boundary endpoint.", required: true),
                CompositionMember("right", WorldMemberPosition.Header, WorldMemberKind.Reference, "adjacency and boundary fields in the two endpoint worlds", "The destination world and its boundary endpoint.", required: true),
                CompositionMember("center", WorldMemberPosition.Property, WorldMemberKind.Point, "the border window's center", "The optional center of the border window."),
                CompositionMember("yaw", WorldMemberPosition.Property, WorldMemberKind.Value, "the border window's yaw", "The optional heading of the border window."),
                CompositionMember("pitch", WorldMemberPosition.Property, WorldMemberKind.Value, "the border window's pitch", "The optional pitch of the border window."),
                CompositionMember("width", WorldMemberPosition.Property, WorldMemberKind.Length, "the border window's width", "The positive width of the border window."),
                CompositionMember("height", WorldMemberPosition.Property, WorldMemberKind.Length, "the border window's height", "The positive height of the border window."),
                CompositionMember("hysteresis", WorldMemberPosition.Property, WorldMemberKind.Length, "the border transition's hysteresis", "The nonnegative distance that stabilizes transitions across the border."),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "border ${1:left}.${2:endpoint}, ${3:right}.${4:endpoint} {\n    width: ${5:4m}\n    height: ${6:3m}\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "Connects two world boundaries with a bidirectional border window."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "door left.endpoint, right.spawn",
            Keyword: "door",
            Members: [
                CompositionMember("left", WorldMemberPosition.Header, WorldMemberKind.Reference, "door topology and travel fields in the two worlds", "The source world and its door face.", required: true),
                CompositionMember("right", WorldMemberPosition.Header, WorldMemberKind.Reference, "door topology and travel fields in the two worlds", "The destination world and its named spawn point.", required: true),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Statement,
            Snippet: "door ${1:left}.${2:face}, ${3:right}.${4:spawn}",
            Sugar: WorldConstructSugar.None,
            Summary: "Connects a door face to a named arrival point in another world."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "asset \"package-relative/path\"",
            Keyword: "asset",
            Members: [CompositionMember(
                kind: WorldMemberKind.Text,
                lowering: "the locked content path stored in the surrounding document field",
                name: "path",
                position: WorldMemberPosition.Header,
                required: true,
                summary: "The source-relative asset path resolved through the package's asset lock."
            )],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Statement,
            Snippet: "asset \"${1:path}\"",
            Sugar: WorldConstructSugar.None,
            Summary: "Resolves a package asset wherever a document value is accepted."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "ground name { size [width, depth] [center [x, y, z]] }",
            Keyword: "ground",
            Members: [
                CompositionMember("name", WorldMemberPosition.Header, WorldMemberKind.Name, "the generated prototype and placement identities", "The ground patch's identity and the name used by automatic border endpoints.", required: true),
                CompositionMember("size", WorldMemberPosition.Property, WorldMemberKind.Value, "the generated box scale and boundary dimensions", "The required positive width and depth.", required: true),
                CompositionMember("center", WorldMemberPosition.Property, WorldMemberKind.Point, "the generated placement position and boundary center", "The optional center, defaulting to the origin."),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "ground ${1:name} {\n    size [${2:10m}, ${3:10m}]\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "Generates a solid ground prototype, its placement, and boundary geometry."
        ),
        new(
            DocumentMember: "(nothing)",
            Grammar: "spawn name { at [x, y, z] [yaw: angle] }",
            Keyword: "spawn",
            Members: [
                CompositionMember("name", WorldMemberPosition.Header, WorldMemberKind.Name, "the generated spawn point's id", "The arrival point's identity.", required: true),
                CompositionMember("at", WorldMemberPosition.Property, WorldMemberKind.Point, "the generated spawn point's position", "The required arrival position.", required: true),
                CompositionMember("position", WorldMemberPosition.Property, WorldMemberKind.Point, "the generated spawn point's position", "An alternative spelling of `at`."),
                CompositionMember("yaw", WorldMemberPosition.Property, WorldMemberKind.Value, "the generated spawn point's yawDegrees", "The optional arrival heading, defaulting to zero."),
            ],
            RootArm: WorldRootArm.CompileTime,
            Shape: WorldConstructShape.Block,
            Snippet: "spawn ${1:name} {\n    at [${2:0m}, ${3:0m}, ${4:0m}]\n    yaw: ${5:0deg}\n}",
            Sugar: WorldConstructSugar.None,
            Summary: "Generates a named arrival point that a door can target."
        ),
    ];
}
