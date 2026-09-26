using Puck.World.Transpiler.Embeddings;

namespace Puck.World.Transpiler.Tests;

/// <summary>One authored <c>.puck</c> source per described construct, with the JSON pointer of the node that
/// construct lowers to, so a law can read the document a described member actually produced.</summary>
/// <param name="Elsewhere">The pointer of each other document member a member of this construct writes to, keyed
/// as <c>WorldConstructMember.DocumentNode</c> spells it.</param>
/// <param name="Pointer">The pointer of the construct's own document node in the compiled document.</param>
/// <param name="Source">The whole document.</param>
/// <param name="GeneratedWorld">The generated test world the pointer resolves in, or <see langword="null"/> for the
/// source's own document. A construct of the compile-time layer reaches no document of its own, so the only node it
/// can be read back from is the one its expansion produced.</param>
internal sealed record ConstructProbe(
    string Source,
    string Pointer,
    IReadOnlyDictionary<string, string>? Elsewhere = null,
    string? GeneratedWorld = null
);
/// <summary>The probes each described construct is exercised through. Every member of every construct is spelled by
/// at least one of its probes, which is what <c>ConstructLoweringLawTests</c> refuses when it is not.</summary>
/// <remarks>KEEP IN SYNC with <see cref="Puck.World.Transpiler.Vocabulary.WorldConstructs"/>: the law refuses a
/// described keyword this table carries no probe for, and a keyword here the table does not describe. A construct
/// whose document member is <c>(nothing)</c> is the compile-time layer: it needs no probe, and a probe it does
/// carry reads back the document its expansion produced through
/// <see cref="ConstructProbe.GeneratedWorld"/>.</remarks>
internal static class ConstructProbes {
    private const string Space = """
            spaces {
                space lore {
                    model: "puck-fixture"
                    revision: "1"
                    dimensions: 8
                }
            }
        """;

    // Locks the one text an `embeds` probe writes ("one"), so a probe compiles without a live embedding call.
    internal static EmbeddingLock Lock { get; } = WorldSources.LoreLock("puck-fixture", "one");

    private const string RuleRows = """
                slot hp = 1
                slot flag = 0
                table deck {
                    a = 1
                    b = 2
                }
                table bag
        """;
    // The ordered zones and the draw row the token-moving statements read.
    private const string ZoneRows = """
                table deck {
                    a = 1
                    b = 2
                }
                pile stock of deck capacity(2) {
                    a
                    b
                }
                pile waste of deck capacity(2) {
                }
                row {
                    name: "stream"
                    kind: Int
                    draw {
                        generator {
                            source: "StreamDraw"
                        }
                        timing: "Event"
                    }
                }
        """;

    private static string Doc(string body) => $"schema: \"puck.world.definition.v1\"\n\n{body}\n";
    private static string Rows(string body) => Doc(body: $"state {{\n    world {{\n{body}\n    }}\n}}");
    // The rows every rule probe reads, and the rule body under test after them.
    private static string Rule(string body) => $"{Rows(body: RuleRows)}\nrule \"r\" {{\n{body}\n}}\n";
    private static ConstructProbe Effect(string body) => new(
        Pointer: "/rules/0/effects/0",
        Source: Rule(body: body)
    );
    // An effect whose own arguments sit on the transform node rather than on the effect.
    private static ConstructProbe TransformEffect(string body) => new(
        Elsewhere: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
            ["rules[].effects[][transformState].transform"] = "/rules/0/effects/0/transform",
        },
        Pointer: "/rules/0/effects/0",
        Source: $"{Rows(body: ZoneRows)}\nrule \"r\" {{\n{body}\n}}\n"
    );

    /// <summary>Gets the probes for each described construct, keyed by its keyword.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ConstructProbe>> Sources { get; } = new Dictionary<string, IReadOnlyList<ConstructProbe>>(comparer: StringComparer.Ordinal) {
        ["host"] = [new(
            Pointer: "/host",
            Source: Doc(body: "host {\n}")
        )],
        ["views"] = [new(
            Pointer: "/views",
            Source: Doc(body: "views {\n}")
        )],
        ["layout"] = [new(
            Pointer: "/views/layouts/0",
            Source: Doc(body: "views {\n    layout \"main\" {\n    }\n}")
        )],
        ["graph"] = [new(
            Pointer: "/views/graphs/0",
            Source: Doc(body: "views {\n    graph \"main\" {\n    }\n}")
        )],
        ["post"] = [new(
            Pointer: "/views/post/0",
            Source: Doc(body: "views {\n    post \"grain\" {\n        package: \"sdf.film-grain\"\n        config {\n            intensity: 0.5\n        }\n    }\n}")
        )],
        ["seatRig"] = [new(
            Pointer: "/views/seatRig",
            Source: Doc(body: """
                views {
                    seatRig "main" {
                        operations [
                            orbit(distance: 2.5m, pitch: 0deg, yaw: 0deg)
                        ]
                    }
                }
                """)
        )],
        ["seatControl"] = [new(
            Pointer: "/views/seatControl",
            Source: Doc(body: "views {\n    seatControl {\n    }\n}")
        )],
        ["shape"] = [new(
            Pointer: "/shapes/0",
            Source: Doc(body: """
                shape Box "plate" {
                    id: 0
                    blend: "Union"
                    smooth: 0
                    rotation [0, 0, 0, 1]
                    scale [1, 1, 1]
                }
                """)
        )],
        ["material"] = [new(
            Pointer: "/materials/0",
            Source: Doc(body: "material \"steel\" {\n}")
        )],
        ["addon"] = [new(
            Pointer: "/addons/0",
            Source: Doc(body: "addon \"extra\" {\n    hash: \"0000\"\n}")
        )],
        ["request"] = [new(
            Pointer: "/addons/0/requests/0",
            Source: Doc(body: "addon \"extra\" {\n    request Mutate \"state.world\"\n}")
        )],
        ["watchMemory"] = [new(
            Pointer: "/addons/0/memoryWatches/0",
            Source: Doc(body: "addon \"extra\" {\n    watchMemory screen: 0, address: 0, length: 4\n}")
        )],
        ["placements"] = [new(
            Pointer: "/placements",
            Source: Doc(body: "placements {\n    placement \"p\" {\n    }\n}")
        )],
        ["placement"] = [new(
            Pointer: "/placements/rows/0",
            Source: Doc(body: """
                placements {
                    placement "p" {
                        yawDegrees: 90
                        scale: 2
                        solid
                    }
                }
                """)
        )],
        ["prototypes"] = [new(
            Pointer: "/prototypes",
            Source: Doc(body: "prototypes {\n    prototype \"p\" {\n        document {\n        }\n    }\n}")
        )],
        ["prototype"] = [new(
            Pointer: "/prototypes/0",
            Source: Doc(body: "prototypes {\n    prototype \"p\" {\n        document {\n        }\n    }\n}")
        )],
        ["cartridge"] = [new(
            Pointer: "/cartridge",
            Source: Doc(body: "cartridge {\n    hash: \"0000\"\n}")
        )],
        ["state"] = [new(
            Pointer: "/state",
            Source: Rows(body: "        slot hp = 1")
        )],
        ["world"] = [new(
            Pointer: "/state/world",
            Source: Rows(body: "        slot hp = 1")
        )],
        ["spaces"] = [new(
            Pointer: "/state/spaces",
            Source: Doc(body: $"state {{\n{Space}\n}}")
        )],
        ["space"] = [new(
            Pointer: "/state/spaces/0",
            Source: Doc(body: $"state {{\n{Space}\n}}")
        )],
        ["table"] = [
            new(
                Pointer: "/state/world/0",
                Source: Rows(body: """
                            table vitals capacity(4) bounds(0..9, overflow: Saturate) advance(perSecond: 1) evicts {
                                a = 1 advance(perSecond: 2)
                                b = 2 behavior(none)
                            }
                    """)
            ),
            new(
                Pointer: "/state/world/0",
                Source: Doc(body: $"state {{\n{Space}\n    world {{\n        table vecs space(lore)\n    }}\n}}")
            ),
            new(
                Pointer: "/state/world/0",
                Source: Doc(body: $"state {{\n{Space}\n    world {{\n        table lines embeds(lineVectors, space: lore) {{\n            a = \"one\"\n        }}\n    }}\n}}")
            ),
            new(
                Pointer: "/state/world/0",
                Source: Doc(body: "state {\n    enum Element { Nothing Air }\n    world {\n        table recipe: Element {\n            a = Air\n        }\n    }\n}")
            ),
        ],
        ["slot"] = [
            new(
                Pointer: "/state/world/0",
                Source: Rows(body: "        slot hp = 1 bounds(0..9, overflow: Saturate) advance(perSecond: 1)")
            ),
            new(
                Pointer: "/state/world/0",
                Source: Doc(body: $"state {{\n{Space}\n    world {{\n        slot situation space(lore)\n    }}\n}}")
            ),
            new(
                Pointer: "/state/world/0",
                Source: Doc(body: "state {\n    enum Element { Nothing Air }\n    world {\n        slot left: Element = Air\n    }\n}")
            ),
        ],
        ["pool"] = [new(
            Pointer: "/state/pools/0",
            Source: Doc(body: "let capacity = 2\nstate {\n    record Item { value: Int }\n    pool items of Item capacity(capacity) = [{ value: 1 }]\n}")
        )],
        ["pairPool"] = [new(
            Pointer: "/state/pairPools/0",
            Source: Doc(body: "state {\n    record Item { value: Int }\n    pool items of Item capacity(2)\n    pairPool links record Item left items right items maxLive 2 directed false allowSelf true\n}")
        )],
        ["claim"] = [new(
            Pointer: "/rules/0/effects/0",
            Source: Doc(body: "state {\n    record Item { value: Int }\n    pool items of Item capacity(1)\n}\nrule \"r\" {\n    claim items as item { item.value = 1 }\n}")
        )],
        ["claim pair"] = [new(
            Pointer: "/rules/0/effects/0/effects/0/effects/0",
            Source: Doc(body: "state {\n    record Item { value: Int }\n    pool items of Item capacity(2)\n    pairPool links record Item left items right items maxLive 1 directed false allowSelf true\n}\nrule \"r\" {\n    claim items as left {\n        claim items as right {\n            claim pair links between left, right as link { link.value = 1 }\n        }\n    }\n}")
        )],
        ["for each"] = [new(
            Pointer: "/rules/0/effects/0",
            Source: Doc(body: "state {\n    record Item { value: Int }\n    pool items of Item capacity(1)\n}\nrule \"r\" {\n    for each item in items { item.value = 1 }\n}")
        )],
        ["release"] = [new(
            Pointer: "/rules/0/effects/0/effects/0",
            Source: Doc(body: "state {\n    record Item { value: Int }\n    pool items of Item capacity(1)\n}\nrule \"r\" {\n    claim items as item { release item }\n}")
        )],
        ["pile"] = [new(
            Pointer: "/state/world/1",
            Source: Rows(body: """
                        table deck {
                            a = 1
                            b = 2
                        }
                        pile stock of deck capacity(2) {
                            a
                            b
                        }
                """)
        )],
        ["grid"] = [
            new(
                Elsewhere: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                    ["state.lattices[]"] = "/state/lattices/0",
                    ["state.world[]"] = "/state/world/2",
                },
                Pointer: "/state/world/0",
                Source: Rows(body: """
                            grid board dimensions(width: 2, depth: 2) wrap(Both) cellSize(2) origin(1, 0, 1) band(0.5) empty(-1) bounds(-1..9, overflow: Saturate) positions(ordinals) {
                                "0" = 1
                            }
                            table deck {
                                a = 1
                            }
                            row {
                                name: "ordinals"
                                kind: Int
                                domain: keysOf(row: deck)
                            }
                    """)
            ),
            new(
                Elsewhere: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                    ["state.lattices[]"] = "/state/lattices/0",
                },
                Pointer: "/state/world/0",
                Source: Rows(body: """
                            grid board dimensions(width: 1, depth: 1) inverse(tokens: tokensRow, codes: codesRow)
                            table tokensRow {
                                a = 1
                            }
                            table codesRow {
                                a = 1
                            }
                    """)
            ),
            new(
                Elsewhere: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                    ["state.lattices[]"] = "/state/lattices/0",
                },
                Pointer: "/state/world/0",
                Source: Doc(body: "state {\n    enum Element { Nothing Air }\n    world {\n        grid field: Element dimensions(width: 2, depth: 2)\n    }\n}")
            ),
        ],
        ["row"] = [new(
            Pointer: "/state/world/0",
            Source: Rows(body: """
                        row {
                            name: "hp"
                            kind: Int
                            domain: slot()
                        }
                """)
        )],
        ["sql"] = [new(
            Pointer: "/state",
            Source: Doc(body: """
                sql {
                    DECLARE gold INT DEFAULT 10 CHECK (gold >= 0);
                }
                """)
        )],
        ["rule"] = [new(
            Pointer: "/rules/0",
            Source: Rule(body: """
                    mode: Edge
                    forEach: deck
                    when hp == 1
                    flag = 1
                """)
        )],
        ["rules"] = [new(
            Pointer: "/rules/0",
            Source: Doc(body: """
                state {
                    world {
                        slot hp = 1
                        slot flag = 0
                    }
                }

                rules scope when hp == 1 {
                    rule "m" {
                        flag = 1
                    }
                }
                """)
        )],
        ["stabilize"] = [new(
            Pointer: "/ruleGroups/0",
            Source: Doc(body: """
                state {
                    world {
                        slot hp = 1
                        slot flag = 0
                    }
                }

                stabilize grp undo({ rows ["hp"] depth: 2 }) maxPasses(4) until hp > 0 {
                    rule "m" {
                        flag = 1
                    }
                }
                """)
        )],
        ["workflow"] = [new(
            Pointer: "/ruleGroups/0",
            Source: Doc(body: """
                state {
                    world {
                        slot flag = 0
                    }
                }

                workflow wf undo({ rows ["flag"] depth: 2 }) {
                    step first {
                        flag = 1
                    }
                }
                """)
        )],
        ["step"] = [new(
            Elsewhere: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
                ["ruleGroups[].steps[]"] = "/ruleGroups/0/steps/0",
            },
            Pointer: "/rules/0",
            Source: Doc(body: """
                state {
                    world {
                        slot flag = 0
                    }
                }

                workflow wf {
                    step first skip {
                        flag = 1
                    }
                }
                """)
        )],
        ["when"] = [new(
            Pointer: "/rules/0/gate",
            Source: Rule(body: "    when hp == 1\n    flag = 1")
        )],
        ["local"] = [new(
            Pointer: "/rules/0/locals/0",
            Source: Rule(body: "    local acc = hp + 1\n    flag = acc")
        )],
        ["decision"] = [new(
            Pointer: "/rules/0/decision",
            Source: Rule(body: """
                    decision {
                        periodSeconds: 1s
                        option "o" {
                            score: 1
                        }
                    }
                """)
        )],
        ["option"] = [new(
            Pointer: "/rules/0/decision/options/0",
            Source: Rule(body: """
                    decision {
                        periodSeconds: 1s
                        option "o" {
                            score: 1
                            flag = 1
                        }
                    }
                """)
        )],
        ["interrupt"] = [new(
            Pointer: "/rules/0/decision/interrupt",
            Source: Rule(body: """
                    decision {
                        periodSeconds: 1s
                        interrupt hp == 1
                        option "o" {
                            score: 1
                        }
                    }
                """)
        )],
        ["onNoChoice"] = [new(
            Pointer: "/rules/0/decision/onNoChoice",
            Source: Rule(body: """
                    decision {
                        periodSeconds: 1s
                        option "o" {
                            score: 1
                        }
                        onNoChoice {
                            flag = 1
                        }
                    }
                """)
        )],
        ["transaction"] = [Effect(body: "    transaction {\n        hp = 1\n    }")],
        ["onFailure"] = [new(
            Pointer: "/rules/0/effects/0/onFailure",
            Source: Rule(body: "    transaction {\n        hp = 1\n    } onFailure {\n        flag = 1\n    }")
        )],
        ["if"] = [Effect(body: "    if hp == 1 {\n        flag = 1\n    } else {\n        flag = 0\n    }")],
        ["push"] = [
            Effect(body: "    push bag = hp"),
            Effect(body: "    push bag = hp + 1"),
        ],
        ["remove"] = [Effect(body: "    remove deck[a]")],
        ["schedule"] = [Effect(body: "    schedule deck[a] in 5s")],
        ["transform"] = [Effect(body: "    transform observe(row: deck)")],
        ["draw"] = [TransformEffect(body: "    draw stock to waste")],
        ["deal"] = [TransformEffect(body: "    deal 1 from stock to waste")],
        ["shuffle"] = [TransformEffect(body: "    shuffle stock with stream")],
        ["pattern"] = [
            new(
                Pointer: "/patterns/0",
                Source: Doc(body: """
                    pattern p : Int {
                        attribute: "deck"
                        maxStates: 8
                        symbols {
                            a = 1
                            b = 2
                        }
                        match: a b
                    }

                    state {
                        world {
                            table deck {
                                a = 1
                            }
                        }
                    }
                    """)
            ),
            new(
                Pointer: "/patterns/0",
                Source: Doc(body: """
                    pattern q : Int {
                        value: "hp + 1"
                        symbols {
                            a = 1
                        }
                        match: a
                    }

                    state {
                        world {
                            slot hp = 1
                        }
                    }
                    """)
            ),
        ],
        ["test"] = [new(
            GeneratedWorld: "world~a-slot-a-seat-wrote-reads-back",
            Pointer: "/schedule",
            Source: Doc(body: """
                grants [
                    {
                        capability: Edit
                        principal: "seat1"
                        subject: "all"
                    }
                    {
                        capability: Mutate
                        principal: "seat1"
                        subject: "section:state"
                    }
                ]

                state {
                    world {
                        slot hp = 1
                    }
                }

                test "a slot a seat wrote reads back" {
                    given {
                        hp = 3
                    }
                    when {
                        seat1: world.state.cell.set hp $value 7
                        ticks 2
                    }
                    expect {
                        hp == 7
                    }
                }
                """)
        )],
        ["set"] = [new(
            Pointer: "/sets/0",
            Source: Doc(body: """
                state {
                    world {
                        table deck {
                            a = 1
                        }
                    }
                }

                set s: zone(deck, 0..0)
                """)
        )],
    };
}
