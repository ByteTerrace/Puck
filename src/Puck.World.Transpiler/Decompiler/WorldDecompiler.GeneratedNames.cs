using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

/// <summary>Refuses a document the decompiler cannot print as source: a name in the generated form
/// (<see cref="GeneratedName"/>) that no construct it prints would generate. Printing the name as an authored one
/// would write a source the compiler refuses (PUCK113); printing nothing would drop what the document says.</summary>
/// <param name="name">The generated-form name.</param>
/// <param name="pointer">The JSON pointer of the member that declares it.</param>
/// <param name="reason">What generates a name of this form, and where it can be printed from.</param>
public sealed class WorldDecompileRefusedException(string name, string pointer, string reason)
    : InvalidOperationException(message: $"'{name}' at {pointer} is a name Puck generates, and this document does not carry it where a construct prints it back: {reason}") {
    /// <summary>Gets the generated-form name the decompiler refused.</summary>
    public string Name { get; } = name;
    /// <summary>Gets the JSON pointer of the member that declares <see cref="Name"/>.</summary>
    public string Pointer { get; } = pointer;
}
// Every name the compiler generates into a document prints back as the construct that generated it, and nothing
// else does: a ground's prototype and placement print as the `ground` block, a rule scope's members and groups print
// inside `rules` scopes, a test world's verdict rows, witnesses, rules, recorded keys and schedule print as the
// `test` block, and a composition's link rows print as `border` and `door` (DecompileComposition). Whatever is left
// in the generated form once those are read back is refused by name.
public static partial class WorldDecompiler {
    // What the reverse pass read back out of a document: the statements to print ahead of every section in place of
    // the rows it removed, and a test block to print after them.
    private sealed class GeneratedSugar {
        public HashSet<JsonNode> Consumed { get; } = new(comparer: ReferenceEqualityComparer.Instance);
        public List<GroundSugar> Grounds { get; } = [];
        public List<Action<StringBuilder>> Leading { get; } = [];

        public string? Test { get; set; }
    }
    // One `ground` block read back: its name and the center and size it was written with.
    private sealed record GroundSugar(string Name, JsonArray Center, JsonArray Size) {
        public string Text => $"ground {PuckPrinter.PrintName(name: Name)} {{\n    center {FormatValue(indentLevel: 1, node: Center)}\n    size {FormatValue(indentLevel: 1, node: Size)}\n}}\n";
    }

    private const decimal GroundHalfThickness = 0.1m;
    private const string TestName = "decompiled";

    private static GeneratedSugar ReverseGenerated(JsonObject root) {
        var sugar = new GeneratedSugar();

        ReverseGrounds(
            root: root,
            sugar: sugar
        );
        sugar.Test = ReverseTest(
            consumed: sugar.Consumed,
            root: root
        );

        return sugar;
    }
    // ---- ground ---------------------------------------------------------------------------------------------------

    // A `ground` block appends its prototype and its placement where it stands, after whatever prototypes and
    // placements the statements before it appended. Each pair the block would write exactly, in the same order in
    // both sections, prints back as the block, preceded by the rows written before it; the rows after the last one
    // print where their sections do.
    private static void ReverseGrounds(JsonObject root, GeneratedSugar sugar) {
        if (
            (root["prototypes"] is not JsonArray prototypes) ||
            (root["placements"] is not JsonObject placements) ||
            (placements["rows"] is not JsonArray rows)
        ) {
            return;
        }

        var found = new List<(GroundSugar Ground, int Prototype, int Placement)>();

        for (var index = 0; (index < prototypes.Count); index++) {
            if (
                (prototypes[index]?["id"] is not JsonValue idValue) ||
                !idValue.TryGetValue<string>(value: out var prototypeId) ||
                !GeneratedName.TryStripHead(head: WorldDocumentEmitter.GroundHead, name: prototypeId, rest: out var name)
            ) {
                continue;
            }

            var placement = -1;

            for (var candidate = 0; (candidate < rows.Count); candidate++) {
                if ((rows[candidate]?["id"] is JsonValue placementId) && placementId.TryGetValue<string>(value: out var id) && (id == name)) {
                    placement = candidate;

                    break;
                }
            }

            if (
                (placement >= 0) &&
                ((found.Count == 0) || (placement > found[^1].Placement)) &&
                TryReadGround(
                ground: out var ground,
                placement: rows[placement],
                prototype: prototypes[index]
            )
            ) {
                found.Add(item: (ground, index, placement));
            }
        }

        if (found.Count == 0) {
            return;
        }

        var previousPrototype = 0;
        var previousPlacement = 0;

        foreach (var (ground, prototype, placement) in found) {
            var before = Segment(from: previousPrototype, rows: prototypes, to: prototype);
            var placed = Segment(from: previousPlacement, rows: rows, to: placement);

            if (before.Count > 0) {
                sugar.Leading.Add(item: sb => {
                    if (!TryDecompilePrototypes(sb: sb, value: before)) {
                        EmitField(sb, "prototypes", before, holder: typeof(WorldDefinition), indentLevel: 0);
                    }
                });
            }
            if (placed.Count > 0) {
                var section = new JsonObject { ["rows"] = placed };

                sugar.Leading.Add(item: sb => {
                    if (!TryDecompilePlacements(sb: sb, value: section)) {
                        EmitField(sb, "placements", section, holder: typeof(WorldDefinition), indentLevel: 0);
                    }
                });
            }

            sugar.Leading.Add(item: sb => sb.Append(value: ground.Text));
            sugar.Grounds.Add(item: ground);
            _ = sugar.Consumed.Add(item: prototypes[prototype]!);
            previousPrototype = (prototype + 1);
            previousPlacement = (placement + 1);
        }

        for (var index = 0; (index < previousPrototype); index++) {
            prototypes.RemoveAt(index: 0);
        }
        for (var index = 0; (index < previousPlacement); index++) {
            rows.RemoveAt(index: 0);
        }
        if (prototypes.Count == 0) {
            _ = root.Remove(propertyName: "prototypes");
        }
        if (rows.Count == 0) {
            _ = placements.Remove(propertyName: "rows");

            if (placements.Count == 0) {
                _ = root.Remove(propertyName: "placements");
            }
        }
    }
    private static JsonArray Segment(JsonArray rows, int from, int to) {
        var segment = new JsonArray();

        for (var index = from; (index < to); index++) {
            segment.Add(item: rows[index]?.DeepClone());
        }

        return segment;
    }
    private static bool TryReadGround(JsonNode? prototype, JsonNode? placement, out GroundSugar ground) {
        ground = null!;

        if (
            (prototype is not JsonObject prototypeRow) ||
            (placement is not JsonObject placementRow) ||
            (placementRow["id"] is not JsonValue idValue) ||
            !idValue.TryGetValue<string>(value: out var name) ||
            (name.Length == 0) ||
            name.Contains(value: GeneratedName.Joiner) ||
            (prototypeRow["id"] is not JsonValue prototypeIdValue) ||
            !prototypeIdValue.TryGetValue<string>(value: out var prototypeId) ||
            !GeneratedName.TryStripHead(
            head: WorldDocumentEmitter.GroundHead,
            name: prototypeId,
            rest: out var rest
        ) ||
            (rest != name) ||
            (placementRow["position"] is not JsonArray { Count: 3 } position) ||
            (prototypeRow["document"]?["shapes"] is not JsonArray { Count: 1 } shapes) ||
            (shapes[0]?["scale"] is not JsonArray { Count: 3 } scale) ||
            !DocumentNumbers.TryExact(node: position[1], number: out var placedY) ||
            !DocumentNumbers.TryExact(node: scale[0], number: out var halfWidth) ||
            !DocumentNumbers.TryExact(node: scale[2], number: out var halfDepth)
        ) {
            return false;
        }

        var center = new JsonArray(
            position[0]?.DeepClone(),
            DocumentNumbers.ExactNode(number: (placedY + GroundHalfThickness)),
            position[2]?.DeepClone()
        );
        var size = new JsonArray(
            DocumentNumbers.ExactNode(number: (halfWidth * 2m)),
            DocumentNumbers.ExactNode(number: (halfDepth * 2m))
        );

        var (expectedPrototype, expectedPlacement) = WorldDocumentEmitter.GroundRows(
            center: center,
            name: name,
            size: size
        );

        if (
            !DocumentValueEqualityComparer.Instance.Equals(x: expectedPrototype, y: prototypeRow) ||
            !DocumentValueEqualityComparer.Instance.Equals(x: expectedPlacement, y: placementRow)
        ) {
            return false;
        }

        ground = new GroundSugar(
            Center: center,
            Name: name,
            Size: size
        );

        return true;
    }
    // A whole, non-negative number however the document holds it: a tick, a settle, a seat.
    private static bool TryWhole(JsonNode? node, out ulong number) {
        number = 0UL;

        if (node is not JsonValue value) {
            return false;
        }
        if (value.TryGetValue<ulong>(value: out number)) {
            return true;
        }
        if (DocumentNumbers.TryInteger(node: node, number: out var whole) && (whole >= 0L)) {
            number = ((ulong)whole);

            return true;
        }

        return false;
    }
    // ---- test -----------------------------------------------------------------------------------------------------

    // A generated test world is the world the test stands in with the test's schedule, one verdict row (and its
    // witnesses) per expectation appended to its state, and one verdict rule per expectation appended to its rules.
    // Those print back as the `test` block that generates them over the rest of the document; what `given` wrote is
    // already the rest of the document's own boot values. A composed test's documents arm one another by file and
    // print back only from their source.
    private static string? ReverseTest(JsonObject root, HashSet<JsonNode> consumed) {
        if (
            (root["state"] is not JsonObject state) ||
            (state["world"] is not JsonArray rows)
        ) {
            return null;
        }

        var first = -1;

        for (var index = 0; (index < rows.Count); index++) {
            if (rows[index]?["verdict"] is not null) {
                first = index;

                break;
            }
        }

        if (first < 0) {
            return null;
        }

        var pointer = $"/state/world/{first.ToString(provider: CultureInfo.InvariantCulture)}/name";
        var verdictName = (rows[first]?["name"]?.GetValue<string>() ?? "");

        if (
            (root["schedule"] is not JsonObject schedule) ||
            !CarriesOnly(schedule, "rows", "settleTicks") ||
            (schedule["rows"] is not JsonArray steps) ||
            !TryWhole(node: schedule["settleTicks"], number: out var settle) ||
            (settle < 1UL)
        ) {
            throw new WorldDecompileRefusedException(
                name: verdictName,
                pointer: pointer,
                reason: "a test block generates verdict rows beside a schedule of seat steps alone, and this document's schedule is not one; a composed test's documents arm one another by file and print back only from the source that declared them"
            );
        }

        var gates = new List<string>();
        var index2 = first;

        while (index2 < rows.Count) {
            var expected = WorldDocumentEmitter.VerdictRow(line: (gates.Count + 1));

            if (
                (rows[index2] is not JsonObject verdictRow) ||
                (verdictRow["name"]?.GetValue<string>() != expected) ||
                (verdictRow["verdict"] is not JsonObject verdict) ||
                (verdict["status"]?.GetValue<string>() != WorldDocumentEmitter.VerdictStatusKey) ||
                (verdict["gate"]?.GetValue<string>() is not { } gate) ||
                (gate.Length >= WorldVerdict.MaxGateLength)
            ) {
                throw new WorldDecompileRefusedException(
                    name: (rows[index2]?["name"]?.ToString() ?? ""),
                    pointer: $"/state/world/{index2.ToString(provider: CultureInfo.InvariantCulture)}",
                    reason: $"a test block appends its verdict rows last, one per expectation, named '{expected}' onward, each carrying the whole gate it was written as; this row is not the next one"
                );
            }

            gates.Add(item: gate);
            index2++;

            while (
                (index2 < rows.Count) &&
                (rows[index2] is JsonObject witness) &&
                (witness["witness"]?.GetValue<string>() == expected) &&
                (witness["name"]?.GetValue<string>() is { } witnessName) &&
                GeneratedName.TryStripHead(head: expected, name: witnessName, rest: out _)
            ) {
                index2++;
            }
        }

        var exportTick = TestWhen(
            settle: settle,
            steps: steps,
            text: out var when
        );

        if (
            (root["rules"] is not JsonArray rules) ||
            (rules.Count < gates.Count)
        ) {
            throw new WorldDecompileRefusedException(
                name: verdictName,
                pointer: pointer,
                reason: "a test block appends one verdict rule per expectation, and this document carries fewer rules than verdict rows"
            );
        }

        for (var line = 0; (line < gates.Count); line++) {
            var rule = rules[((rules.Count - gates.Count) + line)];
            var expected = WorldDocumentEmitter.VerdictRow(line: (line + 1));

            if (
                (rule?["name"]?.GetValue<string>() != expected) ||
                (rule["gate"]?["state"]?.GetValue<string>() != RuleFacts.Tick) ||
                !TryWhole(node: rule["gate"]?["value"], number: out var tick) ||
                (tick != exportTick)
            ) {
                throw new WorldDecompileRefusedException(
                    name: expected,
                    pointer: "/rules",
                    reason: $"a test block's verdict rules come last, in expectation order, each gated on the export tick {exportTick.ToString(provider: CultureInfo.InvariantCulture)} its schedule reaches; this document's are not"
                );
            }
        }

        // Only now that the whole shape reads back is anything removed.
        for (var line = 0; (line < gates.Count); line++) {
            _ = consumed.Add(item: rules[^1]!);
            rules.RemoveAt(index: (rules.Count - 1));
        }
        while (rows.Count > first) {
            _ = consumed.Add(item: rows[^1]!);
            rows.RemoveAt(index: (rows.Count - 1));
        }
        _ = root.Remove(propertyName: "schedule");
        if (rules.Count == 0) {
            _ = root.Remove(propertyName: "rules");
        }
        if (rows.Count == 0) {
            _ = state.Remove(propertyName: "world");

            if (state.Count == 0) {
                _ = root.Remove(propertyName: "state");
            }
        }

        var block = new StringBuilder();

        block.Append(value: "test ").Append(value: PuckStrings.Write(value: TestName)).AppendLine(value: " {");
        if (when.Length > 0) {
            block.AppendLine(value: "    when {").Append(value: when).AppendLine(value: "    }");
        }
        block.AppendLine(value: "    expect {");
        foreach (var gate in gates) {
            block.Append(value: "        ").AppendLine(value: gate);
        }
        block.AppendLine(value: "    }").AppendLine(value: "}");

        return block.ToString();
    }
    // The `when` lines that lay the schedule's rows on the test's tick grid: the cursor opens at tick 1, `ticks n`
    // carries it forward, and the grid runs past the last step by the schedule's settle, which is never less than the
    // floor the lowering keeps. Returns the export tick the grid reaches.
    private static ulong TestWhen(JsonArray steps, ulong settle, out string text) {
        var lines = new StringBuilder();
        var cursor = 1UL;
        var last = 0UL;

        foreach (var item in steps) {
            if (
                (item is not JsonObject step) ||
                !CarriesOnly(step, "command", "principal", "tick") ||
                (step["command"]?.GetValue<string>() is not { Length: > 0 } command) ||
                command.Contains(value: '\n') ||
                (step["principal"]?.GetValue<string>() is not { } principal) ||
                !principal.StartsWith(comparisonType: StringComparison.Ordinal, value: "seat") ||
                !int.TryParse(provider: CultureInfo.InvariantCulture, result: out var seat, s: principal.AsSpan(start: 4), style: NumberStyles.None) ||
                !TryWhole(node: step["tick"], number: out var tick) ||
                (tick < cursor)
            ) {
                throw new WorldDecompileRefusedException(
                    name: "schedule",
                    pointer: "/schedule/rows",
                    reason: "a test block's when lines generate seat steps in tick order, each a seat's command at one tick; this schedule carries a row no when line writes"
                );
            }



            if (tick > cursor) {
                lines.Append(value: "        ticks ").AppendLine(value: (tick - cursor).ToString(provider: CultureInfo.InvariantCulture));
            }

            lines.Append(value: "        seat").Append(value: seat.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ": ").AppendLine(value: command);
            cursor = tick;
            last = tick;
        }

        if (last == 0UL) {
            if (settle > 1UL) {
                lines.Append(value: "        ticks ").AppendLine(value: (settle - 1UL).ToString(provider: CultureInfo.InvariantCulture));
            }
        } else if (settle > WorldDocumentEmitter.MinimumSettleTicks) {
            lines.Append(value: "        ticks ").AppendLine(value: settle.ToString(provider: CultureInfo.InvariantCulture));
        } else if (settle < WorldDocumentEmitter.MinimumSettleTicks) {
            throw new WorldDecompileRefusedException(
                name: "schedule",
                pointer: "/schedule/settleTicks",
                reason: $"a test block runs its grid at least {WorldDocumentEmitter.MinimumSettleTicks.ToString(provider: CultureInfo.InvariantCulture)} ticks past its last step, and this schedule settles in {settle.ToString(provider: CultureInfo.InvariantCulture)}"
            );
        }

        text = lines.ToString();

        return (last + settle);
    }
    // ---- rule scopes ----------------------------------------------------------------------------------------------

    // A rule scope joins its own name to each name it holds (`outer$inner`), so a rule or group whose name carries the
    // joiner prints inside one `rules` scope per part before its own: an empty scope adds no gate, local or property,
    // and the member lowers to the same rule under the same name.
    private static bool TrySplitScopedName(string name, out string[] scopes, out string local) {
        scopes = [];
        local = name;

        if (!GeneratedName.IsGenerated(name: name)) {
            return true;
        }

        var parts = name.Split(separator: GeneratedName.Joiner);

        if (parts.Any(predicate: static part => ((part.Length == 0) || !IsSpellableName(name: part)))) {
            return false;
        }

        scopes = parts[..^1];
        local = parts[^1];

        return true;
    }
    private static void AppendScoped(StringBuilder sb, IReadOnlyList<string> scopes, int indentLevel, Action<int> body) {
        for (var index = 0; (index < scopes.Count); index++) {
            sb.Append(repeatCount: ((indentLevel + index) * 4), value: ' ')
                .Append(value: "rules ")
                .Append(value: PuckPrinter.PrintName(name: scopes[index]))
                .AppendLine(value: " {");
        }

        body(obj: (indentLevel + scopes.Count));

        for (var index = (scopes.Count - 1); (index >= 0); index--) {
            sb.Append(repeatCount: ((indentLevel + index) * 4), value: ' ').AppendLine(value: "}");
        }
    }
    // ---- the sweep ------------------------------------------------------------------------------------------------

    // Every name the document declares, as the leaf that holds it and where it stood before anything was read back:
    // every authored name (WorldAuthoredNames, the walk the compiler and loader read too), the rows a composition's
    // links write, whose names are references rather than declarations but are generated all the same, and each
    // views.graphs row's instance name, which the synthesized root graph's own generated names share a namespace with.
    private static List<(JsonNode Leaf, string Pointer)> NamesToSweep(JsonObject root) {
        var names = new List<(JsonNode Leaf, string Pointer)>();

        // A name one of the document's own aliased imports declares is the composer's, restated or read here in the
        // spelling the document already carries.
        WorldAuthoredNames.Visit(
            document: root,
            visitor: (leaf, name) => {
                if (!WorldDocumentEmitter.IsImportQualified(document: root, name: name)) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        );
        foreach (var (section, member) in new[] { ("destinations", "reference"), ("adjacencies", "destination") }) {
            foreach (var row in ((root[section] as JsonArray) ?? []).OfType<JsonObject>()) {
                if (row[member] is JsonValue leaf) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        }
        foreach (var placement in ((root["placements"]?["rows"] as JsonArray) ?? []).OfType<JsonObject>()) {
            foreach (var face in ((placement["faceSources"] as JsonArray) ?? []).OfType<JsonObject>()) {
                if (face["portal"]?["destination"] is JsonValue leaf) {
                    names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
                }
            }
        }
        foreach (var graph in ((root["views"]?["graphs"] as JsonArray) ?? []).OfType<JsonObject>()) {
            if (graph["name"] is JsonValue leaf) {
                names.Add(item: (leaf, WorldDocumentEmitter.PointerOf(node: leaf)));
            }
        }

        return names;
    }
    // Refuses the first name still in the generated form that no construct about to print spells back: one the
    // reverse pass did not read back as a construct, and not a rule or group name its section prints through scopes.
    private static void RefuseUnprintedGeneratedNames(List<(JsonNode Leaf, string Pointer)> names, HashSet<JsonNode> consumed, bool rulesPrintAsSugar, bool groupsPrintAsSugar) {
        foreach (var (node, pointer) in names) {
            if (
                (node is not JsonValue leaf) ||
                !leaf.TryGetValue<string>(value: out var name) ||
                !GeneratedName.IsGenerated(name: name) ||
                ReadBack(consumed: consumed, node: node)
            ) {
                continue;
            }
            if (
                ((rulesPrintAsSugar && IsNameOfRow(pointer: pointer, section: "/rules/")) ||
                (groupsPrintAsSugar && IsNameOfRow(pointer: pointer, section: "/ruleGroups/"))) &&
                TrySplitScopedName(local: out _, name: name, scopes: out _)
            ) {
                continue;
            }

            throw new WorldDecompileRefusedException(
                name: name,
                pointer: pointer,
                reason: ((IsNameOfRow(pointer: pointer, section: "/rules/") || IsNameOfRow(pointer: pointer, section: "/ruleGroups/"))
                    ? "a rule scope joins its own name to each rule and group it holds, and prints back only where every part of the name is one and the section prints as rules"
                    : GeneratedNameOrigin(name: name))
            );
        }
    }
    private static bool ReadBack(JsonNode node, HashSet<JsonNode> consumed) {
        var current = node;

        while (current is not null) {
            if (consumed.Contains(item: current)) {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }
    private static bool IsNameOfRow(string pointer, string section) => (
        pointer.StartsWith(comparisonType: StringComparison.Ordinal, value: section) &&
        pointer.EndsWith(comparisonType: StringComparison.Ordinal, value: "/name") &&
        (pointer.AsSpan(start: section.Length, length: ((pointer.Length - section.Length) - "/name".Length)).IndexOf(value: '/') < 0)
    );
    // What generates a name of this form, read off its first part.
    private static string GeneratedNameOrigin(string name) => name[..Math.Max(val1: 1, val2: name.IndexOf(startIndex: 1, value: GeneratedName.Joiner))] switch {
        WorldDocumentEmitter.GroundHead => "a ground block generates it, and prints back only where its prototype and placement are exactly the rows the block writes, standing in the same order in both sections",
        WorldCompositionLinks.LinkHead or WorldCompositionLinks.ReturnHead => "a border or door between two worlds of one composition generates it; decompile the composition's documents together (WorldDecompiler.DecompileComposition), which prints the link back",
        WorldDocumentEmitter.ExpectHead => "a test block generates it, and prints back only from a generated test world whose verdict rows, rules and schedule are all the block's own",
        "$pool" => "the state catalog generates it from a pool declaration while the world runs; no document declares it",
        "identity" or "chat" or "controller" => "the engine seeds it into an owned identity's own document; no world source declares it",
        "arg" => "a module expansion stands it in for a row argument and replaces it before the document is written; no document declares it",
        _ => "a module instance used under an alias declares every name in this form ('use counter as left' declares 'left$score'), and a module's use does not print back from the document it expanded into; decompile the module's own document instead",
    };
}
