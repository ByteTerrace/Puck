namespace Puck.World.Transpiler.Tests;

/// <summary>One authored <c>.puck</c> source per registered construct, keyed by the name
/// <see cref="ConstructRegistry"/> gives it. Each source is the smallest document that puts its construct in the
/// compiled JSON, so the projection law's verdict names one construct rather than a whole world.</summary>
/// <remarks>KEEP IN SYNC with <see cref="ConstructRegistry"/>: the law refuses a registered name this table does
/// not carry, and a name here that the registry does not carry.</remarks>
internal static class ConstructCorpus {
    private const string Rows = """
        state {
            world {
                slot hp = 1
                slot flag = 0
                slot due = 0
                table bag
                table deck {
                    a = 1
                    b = 2
                }
            }
        }
        """;

    private static string Document(string body) => $"schema: \"puck.world.definition.v1\"\n\n{body}\n";
    private static string Rule(string body) => Document(body: $"{Rows}\n\nrule \"r\" {{\n{body}\n}}");
    private static string PoolRule(string body) => Document(body:
        (("state {\n    record Item { value: Int }\n    pool items of Item capacity(2)\n    pairPool links record Item left items right items maxLive 2 directed false allowSelf true\n}\n\nrule \"r\" {\n" + body) + "\n}"));
    private static string Gate(string gate) => Rule(body: $"    when {gate}\n    flag = 1");
    private static string Transform(string call) => Rule(body: $"    transform {call}");
    private static string Pattern(string match) => Document(body: $"pattern p : Int {{\n    symbols {{\n        a = 1\n        b = 2\n    }}\n    match: {match}\n}}");
    private static string Set(string expression) => Document(body: $"{Rows}\n\nset s: {expression}");

    /// <summary>Gets the authored source for each registered construct.</summary>
    public static IReadOnlyDictionary<string, string> Sources { get; } = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["effect/setState"] = Rule(body: "    hp = 1"),
        ["effect/addState"] = Rule(body: "    hp += 1"),
        ["effect/pushState"] = Rule(body: "    push hp = 1"),
        ["effect/removeStateCell"] = Rule(body: "    remove deck[a]"),
        ["effect/scheduleState"] = Rule(body: "    schedule due in 5s"),
        ["effect/transformState"] = Transform(call: "observe(row: deck)"),
        ["effect/transaction"] = Rule(body: "    transaction {\n        hp = 1\n    }"),
        ["effect/if"] = Rule(body: "    if hp == 1 {\n        flag = 1\n    } else {\n        flag = 0\n    }"),
        ["effect/generate"] = Rule(body: "    generate(row: deck)"),
        ["effect/claim"] = PoolRule(body: "    claim items as item {\n        item.value = 1\n    }"),
        ["effect/claimPair"] = PoolRule(body: "    claim items as left {\n        claim items as right {\n            claim pair links between left, right as link {\n                link.value = 1\n            }\n        }\n    }"),
        ["effect/forEachPool"] = PoolRule(body: "    for each item in items {\n        item.value = 1\n    }"),
        ["effect/release"] = PoolRule(body: "    claim items as item {\n        release item\n    }"),
        ["effect/rewindTurn"] = Document(body: $"{Rows}\n\nworkflow turn undo({{ rows [\"hp\"] depth: 2 }}) {{\n    step move {{ hp = 2 }}\n}}\n\nrule rewind {{\n    rewindTurn(turn)\n}}"),

        ["predicate/compareState"] = Gate(gate: "hp == 1"),
        ["predicate/compareValue"] = Gate(gate: "hp + 1 == 2"),
        ["predicate/all"] = Gate(gate: "hp == 1 and flag == 0"),
        ["predicate/any"] = Gate(gate: "hp == 1 or flag == 0"),
        ["predicate/not"] = Gate(gate: "not hp == 1"),

        ["transform/observe"] = Transform(call: "observe(row: deck)"),
        ["transform/transfer"] = Transform(call: "transfer(from: deck, to: bag)"),
        ["transform/setRay"] = Transform(call: "setRay(row: deck, from: a, direction: north, pattern: p, value: 1)"),
        ["transform/pushRay"] = Transform(call: "pushRay(pool: tokens, cell: cell, value: kind, from: mover.cell, topology: board, direction: east, pattern: pushable, pushPattern: push, stopPattern: stop, empty: 0)"),
        ["transform/shuffle"] = Transform(call: "shuffle(row: deck, draw: rng)"),
        ["transform/sort"] = Transform(call: "sort(row: deck, by: [{ row: deck, descending: false }])"),
        ["transform/writeSet"] = Transform(call: "writeSet(row: deck, set: s, value: 1)"),
        ["transform/boardCombine"] = Transform(call: "boardCombine(row: deck, operation: Or, left: deck, right: bag)"),
        ["transform/arrange"] = Transform(call: "arrange(row: deck, from: bag)"),
        ["transform/push"] = Transform(call: "push(row: deck, value: 1)"),
        ["transform/clearEnclosed"] = Transform(call: "clearEnclosed(row: deck, from: a, lower: 0, upper: 1)"),
        ["transform/mix"] = Transform(call: "mix(into: deck, terms: [{ from: bag, weight: 1 }])"),
        ["transform/mean"] = Transform(call: "mean(from: bag, into: deck)"),
        ["transform/nearest"] = Transform(call: "nearest(from: bag, query: deck, into: hp, k: 3)"),
        ["transform/remember"] = Transform(call: "remember(into: deck, key: a, from: bag, unlessWithin: \"hp\")"),

        // A slot row infers its domain, so the only document that carries the discriminator is one that authors it.
        ["domain/slot"] = Document(body: "state {\n    world {\n        row {\n            name: \"hp\"\n            kind: Int\n            domain: slot()\n        }\n    }\n}"),
        ["domain/keys"] = Document(body: "state {\n    world {\n        table bag\n    }\n}"),
        ["domain/keysOf"] = Document(body: "state {\n    world {\n        table deck {\n            a = 1\n        }\n        pile stock of deck {\n            a\n        }\n    }\n}"),
        ["domain/cellsOf"] = Document(body: "state {\n    world {\n        grid board dimensions(width: 2, depth: 2)\n    }\n}"),
        ["domain/ring"] = Document(body: "state {\n    world {\n        row {\n            name: \"hist\"\n            kind: Int\n            domain: ring(capacity: 8)\n        }\n    }\n}"),

        ["topology/grid"] = Document(body: "state {\n    world {\n        grid board dimensions(width: 2, depth: 2)\n    }\n}"),
        ["topology/ring"] = Topology(call: "ring(name: \"wheel\", origin: [0, 0, 0], cellSize: 1, width: 8)"),
        ["topology/hex"] = Topology(call: "hex(name: \"tiles\", origin: [0, 0, 0], cellSize: 1, radius: 2)"),
        ["topology/box"] = Topology(call: "box(name: \"stack\", origin: [0, 0, 0], cellSize: 1, width: 2, depth: 2, layers: 2, layerHeight: 1)"),
        ["topology/graph"] = Topology(call: "graph(name: \"web\", origin: [0, 0, 0], cellSize: 1, cells: [{ id: \"n0\", centre [0, 0, 0] }, { id: \"n1\", centre [1, 0, 0] }], directions: [{ name: \"east\", opposite: \"west\" }, { name: \"west\", opposite: \"east\" }], edges: [{ from: \"n0\", to: \"n1\", direction: east }])"),
        ["topology/tiling"] = Topology(call: "tiling(name: \"patch\", origin: [0, 0, 0], cellSize: 1, family: Penrose, radius: 2)"),

        ["cellSet/board"] = Document(body: "state {\n    world {\n        grid board dimensions(width: 2, depth: 2)\n    }\n}\n\nset s: board(board, 0..0)"),
        ["cellSet/zone"] = Set(expression: "zone(deck, 1..1)"),
        ["cellSet/family"] = Document(body: "state {\n    world {\n        slot Pile[0..1] = 0\n    }\n}\n\nset s: family(Pile, 0..1)"),
        ["cellSet/all"] = Set(expression: "all"),
        ["cellSet/none"] = Set(expression: "none"),
        ["cellSet/any"] = Set(expression: "all | none"),
        ["cellSet/both"] = Set(expression: "all & none"),
        ["cellSet/not"] = Set(expression: "~all"),

        ["pattern/symbol"] = Pattern(match: "a"),
        ["pattern/any"] = Pattern(match: "any"),
        ["pattern/except"] = Pattern(match: "except(a)"),
        ["pattern/empty"] = Pattern(match: "empty"),
        ["pattern/none"] = Pattern(match: "none"),
        ["pattern/sequence"] = Pattern(match: "a b"),
        ["pattern/choice"] = Pattern(match: "a | b"),
        ["pattern/all"] = Pattern(match: "a & b"),
        ["pattern/not"] = Pattern(match: "~a"),
        ["pattern/optional"] = Pattern(match: "a?"),
        ["pattern/star"] = Pattern(match: "a*"),
        ["pattern/plus"] = Pattern(match: "a+"),
        ["pattern/repeat"] = Pattern(match: "a{2}"),

        ["comparison/Equal"] = Gate(gate: "hp == 1"),
        ["comparison/NotEqual"] = Gate(gate: "hp != 1"),
        ["comparison/Less"] = Gate(gate: "hp < 1"),
        ["comparison/LessOrEqual"] = Gate(gate: "hp <= 1"),
        ["comparison/Greater"] = Gate(gate: "hp > 1"),
        ["comparison/GreaterOrEqual"] = Gate(gate: "hp >= 1"),

        ["comparisonKind/Int"] = Gate(gate: "(hp == flag : Int)"),
        ["comparisonKind/Fixed"] = Gate(gate: "(hp == flag : Fixed)"),
    };

    private static string Topology(string call) => Document(body: $"state {{\n    lattices [\n        {call}\n    ]\n}}");
}
