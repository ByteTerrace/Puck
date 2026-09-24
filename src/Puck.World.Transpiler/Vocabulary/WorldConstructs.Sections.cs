namespace Puck.World.Transpiler.Vocabulary;

// The document's own sections and the row constructs inside them. A section whose body is nothing but ordinary
// properties is described by its keyword and its document member only: what its fields mean is `puck schema`'s.
public static partial class WorldConstructs {
    private static WorldConstructMember Body(string summary, IReadOnlyList<string>? keys = null, string? lowering = null) => new(
        DocumentKeys: (keys ?? []),
        Kind: WorldMemberKind.Statements,
        Lowering: lowering,
        Name: "body",
        Position: WorldMemberPosition.Body,
        Summary: summary
    );
    private static WorldConstructMember QuotedName(string key, string summary) => new(
        DocumentKeys: [key],
        Kind: WorldMemberKind.Name,
        Name: "name",
        Position: WorldMemberPosition.Header,
        Required: true,
        Summary: summary
    );
    private static IReadOnlyList<WorldConstruct> Sections() => [
        new(
            DocumentMember: "host",
            Grammar: "host { presentation: … width: … height: … fullscreen: … }",
            Keyword: "host",
            RootArm: WorldRootArm.Field,
            Shape: WorldConstructShape.Section,
            Snippet: "host {\n    width: ${1:1280}\n    height: ${2:720}\n    targetHertz: ${3:60}\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The window, its presentation mode, and the simulation tick rate."
        ),
        new(
            DocumentMember: "views",
            Grammar: "views { layout … pipeline … graph … seatRig … seatControl { … } }",
            Keyword: "views",
            RootArm: WorldRootArm.Views,
            Shape: WorldConstructShape.Section,
            Snippet: "views {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "Camera layouts, render pipelines, frame-graph instances, and the seat rig a participant looks through."
        ),
        new(
            DocumentMember: "views.layouts[]",
            Enclosing: "views",
            Grammar: "layout \"name\" { … }",
            Keyword: "layout",
            Members: [QuotedName(key: "name", summary: "The layout's name, which a session selects it by.")],
            Shape: WorldConstructShape.Row,
            Snippet: "layout \"${1:main}\" {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One viewport arrangement."
        ),
        new(
            DocumentMember: "views.pipelines[]",
            Enclosing: "views",
            Grammar: "pipeline \"name\" { … }",
            Keyword: "pipeline",
            Members: [QuotedName(key: "name", summary: "The pipeline's name, which a layout slot names it by.")],
            Shape: WorldConstructShape.Row,
            Snippet: "pipeline \"${1:main}\" {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One named post-render pipeline."
        ),
        new(
            DocumentMember: "views.graphs[]",
            Enclosing: "views",
            Grammar: "graph \"name\" { … }",
            Keyword: "graph",
            Members: [QuotedName(key: "name", summary: "The instance's name, which another instance's input names.")],
            Shape: WorldConstructShape.Row,
            Snippet: "graph \"${1:main}\" {\n    source: \"$2\"\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One named instance of a frame graph."
        ),
        new(
            DocumentMember: "views.seatRig",
            Enclosing: "views",
            Grammar: "seatRig \"name\" { version: … operations [ … ] }",
            Keyword: "seatRig",
            Members: [
                QuotedName(key: "name", summary: "The rig's name."),
                new(
                    DocumentKeys: ["operations"],
                    Kind: WorldMemberKind.Value,
                    Name: "operations",
                    Position: WorldMemberPosition.Property,
                    Summary: "The camera program, each operation written call-form."
                ),
            ],
            Shape: WorldConstructShape.Section,
            Snippet: "seatRig \"${1:main}\" {\n    version: \"puck.camera.program.v1\"\n    operations [\n        orbit(distance: ${2:2.5m}, pitch: 0deg, yaw: 0deg)\n    ]\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The camera program a seated participant sees through."
        ),
        new(
            DocumentMember: "views.seatControl",
            Enclosing: "views",
            Grammar: "seatControl { … }",
            Keyword: "seatControl",
            Shape: WorldConstructShape.Section,
            Snippet: "seatControl {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "How a participant's input moves the seat rig."
        ),
        new(
            DocumentMember: "shapes[]",
            Grammar: "shape Type [\"name\"] { … }",
            Keyword: "shape",
            Members: [
                new(
                    DocumentKeys: ["type"],
                    Kind: WorldMemberKind.Name,
                    Name: "type",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The signed-distance primitive or blend the shape is."
                ),
                new(
                    DocumentKeys: ["name"],
                    Kind: WorldMemberKind.Name,
                    Name: "name",
                    Position: WorldMemberPosition.Header,
                    Summary: "The shape's name, which a sibling's `parent` reads it by; an authored JSON null has no header spelling and prints as a property."
                ),
                new(
                    Default: WorldConstructMember.RowIndexDefault,
                    DocumentKeys: ["id"],
                    Kind: WorldMemberKind.Number,
                    Name: "id",
                    Position: WorldMemberPosition.Property,
                    Summary: "The shape's stable identity inside its document."
                ),
                new(
                    Default: "\"Union\"",
                    DocumentKeys: ["blend"],
                    Kind: WorldMemberKind.Name,
                    Name: "blend",
                    Position: WorldMemberPosition.Property,
                    Summary: "How the shape combines with what is already there."
                ),
                new(
                    Default: "0",
                    DocumentKeys: ["smooth"],
                    Kind: WorldMemberKind.Fixed,
                    Name: "smooth",
                    Position: WorldMemberPosition.Property,
                    Summary: "The blend's smoothing radius."
                ),
                new(
                    Default: "[0, 0, 0, 1]",
                    DocumentKeys: ["rotation"],
                    Kind: WorldMemberKind.Value,
                    Name: "rotation",
                    Position: WorldMemberPosition.Property,
                    Summary: "The shape's orientation quaternion; a binding reference in its place never elides."
                ),
                new(
                    Default: "[1, 1, 1]",
                    DocumentKeys: ["scale"],
                    Kind: WorldMemberKind.Value,
                    Name: "scale",
                    Position: WorldMemberPosition.Property,
                    Summary: "The shape's per-axis scale."
                ),
            ],
            RootArm: WorldRootArm.Shapes,
            Shape: WorldConstructShape.Row,
            Snippet: "shape ${1:Box} \"${2:name}\" {\n    $0\n}",
            Sugar: new(
                Condition: "every row in the array carries its own `type`, so none of them is a basis-merge patch naming only the facets it overrides",
                Open: true,
                Fallback: "the generic value path, for the whole `shapes` array",
                RequiredKeys: ["type"]
            ),
            Summary: "One signed-distance shape of a creation document."
        ),
        new(
            DocumentMember: "materials[]",
            Grammar: "material [\"name\"] { … }",
            Keyword: "material",
            Members: [
                new(
                    DocumentKeys: ["name"],
                    Kind: WorldMemberKind.Name,
                    Name: "name",
                    Position: WorldMemberPosition.Header,
                    Summary: "The material's name, which a shape's `material` reads it by."
                ),
            ],
            RootArm: WorldRootArm.Materials,
            Shape: WorldConstructShape.Row,
            Snippet: "material \"${1:name}\" {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One surface material."
        ),
        new(
            DocumentMember: "addons[]",
            Grammar: "addon [\"name\"] { modulePath: … hash: … request … watchMemory … }",
            Keyword: "addon",
            Members: [
                new(
                    DocumentKeys: ["name"],
                    Kind: WorldMemberKind.Name,
                    Name: "name",
                    Position: WorldMemberPosition.Header,
                    Summary: "The addon's name."
                ),
                new(
                    DocumentKeys: ["hash"],
                    Kind: WorldMemberKind.Text,
                    Name: "hash",
                    Position: WorldMemberPosition.Property,
                    Summary: "The module's content hash; `auto` is filled in from the module beside the source."
                ),
            ],
            RootArm: WorldRootArm.Addons,
            Shape: WorldConstructShape.Row,
            Snippet: "addon \"${1:name}\" {\n    modulePath: \"${2:addon.wasm}\"\n    hash: \"auto\"\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One WebAssembly extension, its capability requests, and its machine-memory watches."
        ),
        new(
            DocumentMember: "addons[].requests[]",
            Enclosing: "addon",
            Grammar: "request Capability \"subject\"",
            Keyword: "request",
            Members: [
                new(
                    DocumentKeys: ["capability"],
                    Kind: WorldMemberKind.Name,
                    Name: "capability",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "The capability the addon asks for."
                ),
                new(
                    DocumentKeys: ["subject"],
                    Kind: WorldMemberKind.Text,
                    Name: "subject",
                    Position: WorldMemberPosition.Header,
                    Required: true,
                    Summary: "What the capability is asked over."
                ),
            ],
            Shape: WorldConstructShape.Statement,
            Snippet: "request ${1|Mutate,Observe,Emit|} \"${2:subject}\"",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "One capability an addon declares it needs."
        ),
        new(
            DocumentMember: "addons[].memoryWatches[]",
            Enclosing: "addon",
            Grammar: "watchMemory screen: n, address: n, length: n",
            Keyword: "watchMemory",
            Members: [
                new(
                    DocumentKeys: ["screen"],
                    Kind: WorldMemberKind.Number,
                    Name: "screen",
                    Position: WorldMemberPosition.Property,
                    Summary: "The hosted machine's screen index."
                ),
                new(
                    DocumentKeys: ["address"],
                    Kind: WorldMemberKind.Number,
                    Name: "address",
                    Position: WorldMemberPosition.Property,
                    Summary: "The machine address the watch starts at."
                ),
                new(
                    DocumentKeys: ["length"],
                    Kind: WorldMemberKind.Number,
                    Name: "length",
                    Position: WorldMemberPosition.Property,
                    Summary: "How many bytes the watch reads."
                ),
            ],
            Shape: WorldConstructShape.Statement,
            Snippet: "watchMemory screen: ${1:0}, address: ${2:0x02000000}, length: ${3:4}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "A span of a hosted machine's memory an addon observes."
        ),
        new(
            DocumentMember: "placements",
            Grammar: "placements { policy { … } placement \"id\" { … } }",
            Keyword: "placements",
            Members: [Body(
                keys: ["rows"],
                summary: "Placement rows and the section's own properties; anything else in it is refused rather than dropped."
            )],
            RootArm: WorldRootArm.Placements,
            Shape: WorldConstructShape.Section,
            Snippet: "placements {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Condition: "every row in it sugars as `placement`", Open: true),
            Summary: "Where prototypes stand in the world."
        ),
        new(
            DocumentMember: "placements.rows[]",
            Enclosing: "placements",
            Grammar: "placement \"id\" { prototype: … position: … [solid] }",
            Keyword: "placement",
            Members: [
                QuotedName(key: "id", summary: "The row's identity, which a child row's `parent` reads it by."),
                new(
                    Default: "0",
                    DocumentKeys: ["yawDegrees"],
                    Kind: WorldMemberKind.Fixed,
                    Name: "yawDegrees",
                    Position: WorldMemberPosition.Property,
                    Summary: "The row's heading about +Y."
                ),
                new(
                    Default: "1",
                    DocumentKeys: ["scale"],
                    Kind: WorldMemberKind.Fixed,
                    Name: "scale",
                    Position: WorldMemberPosition.Property,
                    Summary: "The row's uniform scale."
                ),
                new(
                    DocumentKeys: ["solid"],
                    Kind: WorldMemberKind.Flag,
                    Name: "solid",
                    Position: WorldMemberPosition.Property,
                    Summary: "Stands for `solid { margin: 0 }`; authoring both the flag and the block is refused."
                ),
            ],
            Shape: WorldConstructShape.Row,
            Snippet: "placement \"${1:id}\" {\n    prototype: $0\n}",
            Sugar: new(Fallback: "the generic value path, for the whole section", Open: true),
            Summary: "One placed prototype."
        ),
        new(
            DocumentMember: "prototypes",
            Grammar: "prototypes { prototype \"id\" { document { … } } }",
            Keyword: "prototypes",
            Members: [Body(
                lowering: "the entries of the `prototypes` array itself",
                summary: "Prototype rows only — the section lowers to a bare array, so there is no object for a property to land on."
            )],
            RootArm: WorldRootArm.Prototypes,
            Shape: WorldConstructShape.Section,
            Snippet: "prototypes {\n    $0\n}",
            Sugar: new(Fallback: "the generic value path", Condition: "every row in it sugars as `prototype`", Open: true),
            Summary: "The creation documents this world places."
        ),
        new(
            DocumentMember: "prototypes[]",
            Enclosing: "prototypes",
            Grammar: "prototype \"id\" { document { … } }",
            Keyword: "prototype",
            Members: [
                QuotedName(key: "id", summary: "The prototype's identity, which a placement's `prototype` reads it by."),
                new(
                    DocumentKeys: ["document"],
                    Kind: WorldMemberKind.Statements,
                    Name: "document",
                    Position: WorldMemberPosition.Body,
                    Required: true,
                    Summary: "The creation document itself, an ordinary nested block, so a `shape` inside it reaches the same lowering a root creation document's does."
                ),
            ],
            Shape: WorldConstructShape.Row,
            Snippet: "prototype \"${1:id}\" {\n    document {\n        $0\n    }\n}",
            Sugar: new(
                Condition: "its `document` is an object",
                Open: true,
                Fallback: "the generic value path, for the whole `prototypes` array"
            ),
            Summary: "One creation document under an identity."
        ),
        new(
            DocumentMember: "cartridge",
            Grammar: "cartridge { rom: … hash: … }",
            Keyword: "cartridge",
            Members: [
                new(
                    DocumentKeys: ["hash"],
                    Kind: WorldMemberKind.Text,
                    Name: "hash",
                    Position: WorldMemberPosition.Property,
                    Summary: "The ROM's content hash; `auto` is filled in from the file beside the source."
                ),
            ],
            RootArm: WorldRootArm.Cartridge,
            Shape: WorldConstructShape.Section,
            Snippet: "cartridge {\n    rom: \"${1:game.gb}\"\n    hash: \"auto\"\n}",
            Sugar: new(Fallback: "the generic value path", Open: true),
            Summary: "The cartridge a hosted machine in this world runs."
        ),
    ];
}
