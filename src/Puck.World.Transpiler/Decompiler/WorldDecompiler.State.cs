using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

// `state { world { ... } }` — prints each row as `table`/`slot` sugar when the whole row is representable without
// loss, and falls back to `row { }` (the explicit form) otherwise. See src/Puck.World.Transpiler/README.md's
// syntax-contract section for the exact eligibility rule this mirrors from the emitter.
public static partial class WorldDecompiler {
    // What each declaration's printer may carry is its construct's own description, so the guard below and the
    // emitter that writes these keys cannot disagree about which ones have a spelling.
    private static readonly HashSet<string> TableNodeKeys = WorldConstructs.NodeKeysOf(enclosing: "world", keyword: "table");
    private static readonly HashSet<string> SlotNodeKeys = WorldConstructs.NodeKeysOf(enclosing: "world", keyword: "slot");
    private static readonly HashSet<string> TableCellKeys = WorldConstructs.CellKeysOf(enclosing: "world", keyword: "table");
    private static readonly HashSet<string> PileNodeKeys = WorldConstructs.NodeKeysOf(enclosing: "world", keyword: "pile");
    private static readonly HashSet<string> PileSugarDomainKeys = new(comparer: StringComparer.Ordinal) { "$type", "row", "ordered" };
    private static readonly HashSet<string> GridNodeKeys = WorldConstructs.NodeKeysOf(enclosing: "world", keyword: "grid");
    private static readonly HashSet<string> GridSugarDomainKeys = new(comparer: StringComparer.Ordinal) { "$type", "topology", "empty" };
    // The topology a `grid` mints carries what its own shape modifiers can spell, plus the discriminator and the
    // name the declaration takes from the row.
    private static readonly HashSet<string> GridTopologyKeys = [.. WorldConstructs.KeysOn(
        documentNode: "state.lattices[]",
        enclosing: "world",
        keyword: "grid"
    ), "$type", "name"];
    private static readonly HashSet<string> GridWrapNames = WorldConstructs.Choices(
        enclosing: "world",
        keyword: "grid",
        member: "wrap"
    );

    private static void DecompileStateBlock(StringBuilder sb, JsonObject state, int indentLevel, EmbeddingLock? embeddings = null) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}state {{"
        );

        var world = (state["world"] as JsonArray);
        var lattices = (state["lattices"] as JsonArray);
        var spaces = (state["spaces"] as JsonArray);
        var spaceCount = (spaces?.Count ?? 0);

        // A `grid` declaration mints its own `state.lattices` entry, so a topology it will reproduce is left out of
        // the plain `lattices [ ]` field printed alongside — printing both would author the same topology twice.
        var consumedTopologies = ComputeGridSugarConsumedTopologies(
            lattices: lattices,
            world: world
        );
        var first = true;

        if ((spaces is not null) && (spaces.Count > 0)) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}    spaces {{"
            );
            foreach (var spaceNode in spaces) {
                if (spaceNode is JsonObject spaceObj) {
                    var spaceName = (spaceObj["name"]?.ToString() ?? "default");
                    var model = (spaceObj["model"]?.ToString() ?? "");
                    var revision = (spaceObj["revision"]?.ToString() ?? "");
                    // A document read from text backs its numbers with JsonElement, one the emitter just produced
                    // backs them with the boxed CLR type, so the read must name the widest integral form.
                    var dims = ((
                        (spaceObj["dimensions"] is JsonValue dimensionsValue) &&
                        dimensionsValue.TryGetValue<long>(value: out var dimensions)
                    )
                        ? ((int)dimensions)
                        : 0
                    );

                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"{indent}        space {spaceName} {{ model: \"{EscapeString(s: model)}\"  revision: \"{EscapeString(s: revision)}\"  dimensions: {dims} }}"
                    );
                }
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}    }}"
            );
            first = false;
        }

        foreach (var (k, v) in state) {
            if (string.Equals(a: k, b: "spaces", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                continue;
            }

            if (string.Equals(a: k, b: "enums", comparisonType: StringComparison.OrdinalIgnoreCase) && (v is JsonArray enums) && EnumsHaveSugar(enums: enums, records: (state["records"] as JsonArray))) {
                if (!first) {
                    sb.AppendLine();
                }
                first = false;
                DecompileEnums(enums: enums, indentLevel: (indentLevel + 1), sb: sb);
                continue;
            }

            if (
                string.Equals(
                a: k,
                b: "lattices",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (v is JsonArray latticesArr)
            ) {
                var remaining = new JsonArray();

                foreach (var topologyNode in latticesArr) {
                    if (
                        (topologyNode is JsonObject topologyObj) &&
                        (topologyObj["name"] is JsonValue topologyNameVal) &&
                        topologyNameVal.TryGetValue<string>(value: out var topologyName) &&
                        consumedTopologies.Contains(item: topologyName)
                    ) {
                        continue;
                    }

                    remaining.Add(item: topologyNode?.DeepClone());
                }

                if (remaining.Count == 0) {
                    continue;
                }
                if (!first) {
                    sb.AppendLine();
                }

                first = false;

                EmitField(
                    indentLevel: (indentLevel + 1),
                    key: k,
                    sb: sb,
                    value: remaining
                );

                continue;
            }

            if (string.Equals(a: k, b: "records", comparisonType: StringComparison.OrdinalIgnoreCase) && (v is JsonArray records) && RecordsHaveSugar(records: records)) {
                if (!first) {
                    sb.AppendLine();
                }
                first = false;
                DecompileRecords(indentLevel: (indentLevel + 1), records: records, sb: sb);
                continue;
            }
            if (string.Equals(a: k, b: "pools", comparisonType: StringComparison.OrdinalIgnoreCase) && (v is JsonArray pools) && PoolsHaveSugar(pools: pools)) {
                if (!first) {
                    sb.AppendLine();
                }
                first = false;
                DecompilePools(indentLevel: (indentLevel + 1), pools: pools, sb: sb);
                continue;
            }
            if (string.Equals(a: k, b: "pairPools", comparisonType: StringComparison.OrdinalIgnoreCase) && (v is JsonArray pairPools) && PairPoolsHaveSugar(pools: pairPools)) {
                if (!first) {
                    sb.AppendLine();
                }
                first = false;
                DecompilePairPools(indentLevel: (indentLevel + 1), pairPools: pairPools, sb: sb);
                continue;
            }

            if (!first) {
                sb.AppendLine();
            }
            first = false;

            if (
                string.Equals(
                a: k,
                b: "world",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (v is JsonArray worldArr)
            ) {
                DecompileStateWorldBlock(
                    embeddings: embeddings,
                    indentLevel: (indentLevel + 1),
                    lattices: lattices,
                    sb: sb,
                    spaceCount: spaceCount,
                    world: worldArr
                );
            } else {
                EmitField(
                    indentLevel: (indentLevel + 1),
                    key: k,
                    sb: sb,
                    value: v
                );
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static bool EnumsHaveSugar(JsonArray enums, JsonArray? records) {
        foreach (var node in enums) {
            if ((node is not JsonObject { Count: 2 } declaration) || (declaration["name"] is not JsonValue name) || !name.TryGetValue<string>(value: out var enumName) || (declaration["members"] is not JsonArray members)) {
                return false;
            }
            if (members.Any(predicate: static member => ((member is not JsonValue value) || !value.TryGetValue<string>(value: out _)))) {
                return false;
            }
            if (!(records ?? []).OfType<JsonObject>()
                .SelectMany(selector: static record => ((record["fields"] as JsonArray) ?? []).OfType<JsonObject>())
                .Any(predicate: field => string.Equals(a: field["enum"]?.ToString(), b: enumName, comparisonType: StringComparison.Ordinal))) {
                return false;
            }
        }
        return true;
    }
    private static void DecompileEnums(JsonArray enums, int indentLevel, StringBuilder sb) {
        var indent = new string(c: ' ', count: (indentLevel * 4));

        foreach (var node in enums.OfType<JsonObject>()) {
            var name = node["name"]!.GetValue<string>();
            var members = ((JsonArray)node["members"]!);

            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}enum {name} {{");
            foreach (var member in members) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {member!.GetValue<string>()}");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
    }
    private static void DecompileRecords(StringBuilder sb, JsonArray records, int indentLevel) {
        var indent = new string(c: ' ', count: (indentLevel * 4));

        foreach (var node in records) {
            if (node is not JsonObject record) {
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}record {(record["name"]?.ToString() ?? "Record")} {{");
            if (record["fields"] is JsonArray fields) {
                foreach (var fieldNode in fields) {
                    if (fieldNode is not JsonObject field) {
                        continue;
                    }
                    var fieldType = (field["enum"]?.ToString() ?? (field["kind"]?.ToString() ?? "Int"));
                    var line = $"{indent}    {(field["name"]?.ToString() ?? "field")}: {fieldType}";

                    if ((field["min"] is not null) || (field["max"] is not null) || (field["overflow"] is not null)) {
                        var bounds = new List<string>();

                        bounds.Add(item: $"{((field["min"] is { } min) ? FormatRecordBound(kind: fieldType, node: min) : "")}..{((field["max"] is { } max) ? FormatRecordBound(kind: fieldType, node: max) : "")}");
                        if (field["overflow"] is { } overflow) {
                            bounds.Add(item: $"overflow: {overflow}");
                        }
                        line += $" bounds({string.Join(separator: ", ", values: bounds)})";
                    }
                    if (field["space"] is { } space) {
                        line += $" space({FormatValue(indentLevel: 0, node: space)})";
                    }
                    if (field["advance"] is JsonObject advance) {
                        line += $" advance(perSecond: {FormatStateRate(advance: advance)})";
                    }
                    if (field["default"] is { } value) {
                        line += $" = {FormatCellValue(node: value)}";
                    }
                    sb.AppendLine(value: line);
                }
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
        }
    }
    private static void DecompilePools(StringBuilder sb, JsonArray pools, int indentLevel) {
        var indent = new string(c: ' ', count: (indentLevel * 4));

        foreach (var node in pools) {
            if (node is not JsonObject pool) {
                continue;
            }
            var capacity = pool["capacity"]?.ToString();
            var header = $"{indent}pool {(pool["name"]?.ToString() ?? "Pool")} of {(pool["record"]?.ToString() ?? "Record")}";

            if (!string.IsNullOrEmpty(value: capacity)) {
                header += $" capacity({capacity})";
            }
            if (pool["initial"] is JsonArray initial) {
                header += " = [";
                sb.AppendLine(value: header);
                foreach (var seedNode in initial) {
                    if (seedNode is not JsonObject seed) {
                        continue;
                    }
                    var values = new List<string>();

                    if (seed["values"] is JsonArray entries) {
                        foreach (var valueNode in entries) {
                            if (valueNode is JsonObject entry) {
                                values.Add(item: $"{entry["field"]}: {FormatCellValue(node: entry["value"])}");
                            }
                        }
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}    {{ {string.Join(separator: ", ", values: values)} }}");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}]");
            } else {
                sb.AppendLine(value: header);
            }
        }
    }
    private static void DecompilePairPools(StringBuilder sb, JsonArray pairPools, int indentLevel) {
        var indent = new string(c: ' ', count: (indentLevel * 4));

        foreach (var node in pairPools) {
            if (node is not JsonObject pool) {
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}pairPool {(pool["name"]?.ToString() ?? "PairPool")} record {(pool["record"]?.ToString() ?? "Record")} left {(pool["leftPool"]?.ToString() ?? "left")} right {(pool["rightPool"]?.ToString() ?? "right")} maxLive {(pool["maxLive"]?.ToString() ?? "1")} directed {(pool["directed"]?.ToString() ?? "true")} allowSelf {(pool["allowSelf"]?.ToString() ?? "false")}");
        }
    }
    private static bool PoolsHaveSugar(JsonArray pools) => pools.All(predicate: node =>
        ((node is JsonObject pool) && !pool.ContainsKey(propertyName: "snapshot") &&
        pool.All(predicate: pair => (pair.Key is "name" or "record" or "capacity" or "initial")) &&
        ((pool["initial"] is not JsonArray initial) || initial.Select(selector: (seed, index) =>
            ((seed is JsonObject seedObject) && (seedObject["slot"]?.GetValue<int>() == index))).All(predicate: value => value))));
    private static bool RecordsHaveSugar(JsonArray records) => records.All(predicate: node =>
        ((node is JsonObject record) &&
        record.All(predicate: pair => (pair.Key is "name" or "fields")) &&
        (record["fields"] is JsonArray fields) &&
        fields.All(predicate: fieldNode => ((fieldNode is JsonObject field) && field.All(predicate: pair =>
            (pair.Key is "name" or "kind" or "enum" or "min" or "max" or "overflow" or "space" or "advance" or "default")) &&
            ((field["advance"] is null) || ((field["advance"] is JsonObject advance) && CanSugarAdvance(advance: advance)))))));
    private static bool PairPoolsHaveSugar(JsonArray pools) => pools.All(predicate: node =>
        ((node is JsonObject pool) && pool.All(predicate: pair => (pair.Key is "name" or "record" or "leftPool" or "rightPool" or "maxLive" or "directed" or "allowSelf"))));
    private static string FormatCellValue(JsonNode? node) {
        if ((node is not JsonObject tagged) || (tagged["kind"] is not JsonValue kindNode) || (tagged["value"] is not { } value)) {
            return FormatValue(indentLevel: 0, node: node);
        }
        var kind = kindNode.ToString();

        if ((kind == "Fixed") && (value is JsonValue fixedNode) && fixedNode.TryGetValue<long>(value: out var raw)) {
            return Puck.Maths.FixedQ4816.FromRawBits(value: raw).ToString();
        }
        return FormatValue(indentLevel: 0, node: value);
    }
    private static string FormatRecordBound(string kind, JsonNode node) =>
        (((kind == "Fixed") && (node is JsonValue value) && value.TryGetValue<long>(value: out var raw))
            ? Puck.Maths.FixedQ4816.FromRawBits(value: raw).ToString()
            : FormatValue(indentLevel: 0, node: node));
    private static void DecompileStateWorldBlock(StringBuilder sb, JsonArray world, JsonArray? lattices, int indentLevel, int spaceCount = 0, EmbeddingLock? embeddings = null) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}world {{"
        );

        var embedsPairs = DetectEmbedsPairs(embeddings: embeddings, world: world);
        var consumedVectorRows = new HashSet<string>(embedsPairs.Values.Select(selector: p => p.VectorRowName), comparer: StringComparer.Ordinal);
        var first = true;

        foreach (var rowNode in world) {
            if (rowNode is not JsonObject rowObj) {
                continue;
            }
            var rowName = rowObj["name"]?.ToString();

            if ((rowName is not null) && consumedVectorRows.Contains(item: rowName)) {
                continue;
            }

            if (!first) {
                sb.AppendLine();
            }
            first = false;

            embedsPairs.TryGetValue(key: (rowName ?? ""), value: out var pair);

            DecompileStateRow(
                embeddings: embeddings,
                embedsPair: pair,
                indentLevel: (indentLevel + 1),
                lattices: lattices,
                row: rowObj,
                sb: sb,
                spaceCount: spaceCount,
                world: world
            );
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static void DecompileStateRow(
        StringBuilder sb,
        JsonObject row,
        JsonArray world,
        JsonArray? lattices,
        int indentLevel,
        (string? VectorRowName, string? SpaceName) embedsPair = default,
        EmbeddingLock? embeddings = null,
        int spaceCount = 0
    ) {
        if (CanSugarPileRow(
            row: row,
            tokenRow: out var tokenRow
        )) {
            DecompileStatePileRow(
                indentLevel: indentLevel,
                row: row,
                sb: sb,
                tokenRow: tokenRow!
            );

            return;
        }
        if (CanSugarGridRow(
            lattices: lattices,
            positionsRowName: out var positionsRowName,
            row: row,
            topology: out var topology,
            world: world
        )) {
            DecompileStateGridRow(
                indentLevel: indentLevel,
                positionsRowName: positionsRowName,
                row: row,
                sb: sb,
                topology: topology!
            );

            return;
        }
        if (!CanSugarStateRow(
            isTable: out var isTable,
            row: row
        )) {
            DecompileNamedBlock(
                sb,
                "row",
                null,
                row,
                indentLevel
            );

            return;
        }

        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (row["name"]!.ToString());
        var kind = (row["kind"]!.ToString());
        var rowSpace = row["space"]?.ToString();

        if (isTable) {
            var header = new StringBuilder(value: $"{indent}table {name}");

            if (!string.IsNullOrEmpty(value: embedsPair.VectorRowName)) {
                if ((spaceCount > 1) && !string.IsNullOrEmpty(value: embedsPair.SpaceName)) {
                    header.Append(value: $" embeds({embedsPair.VectorRowName}, space: {embedsPair.SpaceName})");
                } else {
                    header.Append(value: $" embeds({embedsPair.VectorRowName})");
                }
            }

            AppendStateRowModifiers(
                header: header,
                includeCapacity: true,
                kind: kind,
                row: row
            );

            var cells = ((row["cells"] as JsonArray) ?? []);

            if (cells.Count == 0) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{header} {{ }}"
                );

                return;
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{header} {{"
            );

            var cellIndent = new string(
                c: ' ',
                count: ((indentLevel + 1) * 4)
            );

            foreach (var cellNode in cells) {
                var cellObj = ((JsonObject)cellNode!);
                var key = (cellObj["key"]!.ToString());
                var line = new StringBuilder(value: $"{cellIndent}{FormatStateCellKey(key: key)} = {FormatStateScalarLiteral(
                    embeddings: embeddings,
                    kind: kind,
                    node: cellObj["value"],
                    space: rowSpace
                )}");

                if (cellObj["advance"] is JsonObject cellAdvance) {
                    line.Append(value: $" advance(perSecond: {FormatStateRate(advance: cellAdvance)})");
                }
                if (
                    (cellObj["behavior"] is JsonValue behaviorVal) &&
                    behaviorVal.TryGetValue<string>(value: out var behavior) &&
                    (behavior == nameof(Puck.State.StateCellBehavior.None))
                ) {
                    line.Append(value: " behavior(none)");
                }

                sb.AppendLine(value: line.ToString());
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}}}"
            );
        } else {
            var header = new StringBuilder(value: $"{indent}slot {name}");

            if (row["value"] is { } valueNode) {
                header.Append(value: $" = {FormatStateScalarLiteral(
                    embeddings: embeddings,
                    kind: kind,
                    node: valueNode,
                    space: rowSpace
                )}");
            }

            AppendStateRowModifiers(
                header: header,
                includeCapacity: false,
                kind: kind,
                row: row
            );

            sb.AppendLine(value: header.ToString());
        }
    }
    // A lowered capacity reaches the decompiler as whatever JSON numeric kind carried it — `row { capacity: 8 }`
    // arrives as a long, an authored `8.0` as a double — so two rows are compared on the integer the sugar prints,
    // not on two spellings of one number.
    private static string? CapacityText(JsonNode? node) => (DocumentNumbers.TryInteger(
        node: node,
        number: out var capacity
    )
        ? capacity.ToString(provider: CultureInfo.InvariantCulture)
        : node?.ToString()
    );
    private static void AppendStateRowModifiers(StringBuilder header, JsonObject row, string kind, bool includeCapacity) {
        if (
            includeCapacity &&
            DocumentNumbers.TryInteger(
            node: row["capacity"],
            number: out var capacity
        )
        ) {
            header.Append(value: $" capacity({capacity})");
        }

        if (
            (row["evicts"] is JsonValue evictsVal) &&
            evictsVal.TryGetValue<bool>(value: out var evicts) &&
            evicts
        ) {
            header.Append(value: " evicts");
        }

        // Always printed for a Vector row, never left to the document's single-space default: the kind is inferred
        // from a `space(...)` modifier's own presence, so an omitted one would decompile as some other kind.
        if (
            (kind == "Vector") &&
            (row["space"] is JsonValue spaceVal) &&
            spaceVal.TryGetValue<string>(value: out var space) &&
            !string.IsNullOrWhiteSpace(value: space)
        ) {
            header.Append(value: $" space({space})");
        }

        if (
            row.ContainsKey(propertyName: "min") ||
            row.ContainsKey(propertyName: "max") ||
            row.ContainsKey(propertyName: "overflow")
        ) {
            var args = new List<string>();

            args.Add(item: $"{((row["min"] is { } min) ? FormatStateScalarLiteral(kind: kind, node: min) : "")}..{((row["max"] is { } max) ? FormatStateScalarLiteral(kind: kind, node: max) : "")}");
            if (
                (row["overflow"] is JsonValue overflowVal) &&
                overflowVal.TryGetValue<string>(value: out var overflow)
            ) {
                args.Add(item: $"overflow: {overflow}");
            }

            header.Append(value: $" bounds({string.Join(
                separator: ", ",
                values: args
            )})");
        }

        if (row["advance"] is JsonObject rowAdvance) {
            header.Append(value: $" advance(perSecond: {FormatStateRate(advance: rowAdvance)})");
        }
    }
    private static string FormatStateScalarLiteral(JsonNode? node, string kind, string? space = null, EmbeddingLock? embeddings = null) {
        if (node is null) {
            return "null";
        }

        switch (kind) {
            case "Vector":
                var vecStr = node.ToString();
                if ((embeddings is not null) && embeddings.TryFindText(spaceName: space, text: out var text, vectorBase64Url: vecStr)) {
                    return $"embed(\"{EscapeString(s: text)}\")";
                }
                return $"vector(\"{EscapeString(s: vecStr)}\")";
            case "Fixed":
                // Already a decimal-text JSON string (see the emitter's LowerStateFixedValue). A whole-number
                // value's exact expansion carries no point (FixedPointText.TryFormatRaw skips a zero fraction), so a
                // point is added when the text lacks one — the kind is inferred from a literal's own shape, and a
                // bare "0" reads as Int.
                var fixedText = node.ToString();

                return (fixedText.Contains(value: '.') ? fixedText : $"{fixedText}.0");
            case "Text":
                return $"\"{EscapeString(s: node.ToString())}\"";
            case "Bool":
                return (((node is JsonValue boolVal) && boolVal.TryGetValue<bool>(value: out var b))
                    ? (b ? "true" : "false")
                    : node.ToString()
                );
            default:
                return (((node is JsonValue intVal) && intVal.TryGetValue<long>(value: out var l))
                    ? l.ToString(provider: CultureInfo.InvariantCulture)
                    : node.ToString()
                );
        }
    }
    private static Dictionary<string, (string VectorRowName, string SpaceName)> DetectEmbedsPairs(JsonArray world, EmbeddingLock? embeddings) {
        var result = new Dictionary<string, (string VectorRowName, string SpaceName)>(comparer: StringComparer.Ordinal);

        if (embeddings is null) {
            return result;
        }

        var textTables = new List<JsonObject>();
        var vectorTables = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var rowNode in world) {
            if (
                (rowNode is JsonObject rowObj) &&
                (rowObj["name"]?.ToString() is { } name) &&
                (rowObj["kind"]?.ToString() is { } kind)
            ) {
                if ((kind == "Text") && (rowObj["cells"] is JsonArray)) {
                    textTables.Add(item: rowObj);
                } else if ((kind == "Vector") && (rowObj["cells"] is JsonArray)) {
                    vectorTables[name] = rowObj;
                }
            }
        }

        foreach (var textTable in textTables) {
            var textName = textTable["name"]!.ToString();
            var textCells = ((textTable["cells"] as JsonArray) ?? []);
            var textCap = CapacityText(node: textTable["capacity"]);
            var textEvicts = textTable["evicts"]?.ToString();
            var textVis = textTable["visibility"]?.ToString();

            foreach (var (vecName, vecTable) in vectorTables) {
                var vecSpace = vecTable["space"]?.ToString();

                if (string.IsNullOrEmpty(value: vecSpace)) {
                    continue;
                }

                if (
                    (CapacityText(node: vecTable["capacity"]) != textCap) ||
                    (vecTable["evicts"]?.ToString() != textEvicts) ||
                    (vecTable["visibility"]?.ToString() != textVis)
                ) {
                    continue;
                }

                var vecCells = ((vecTable["cells"] as JsonArray) ?? []);

                if ((vecCells.Count != textCells.Count) || (textCells.Count == 0)) {
                    continue;
                }

                var matches = true;

                for (var i = 0; (i < textCells.Count); i++) {
                    if ((textCells[i] is not JsonObject tCell) || (vecCells[i] is not JsonObject vCell)) {
                        matches = false;
                        break;
                    }
                    if (tCell["key"]?.ToString() != vCell["key"]?.ToString()) {
                        matches = false;
                        break;
                    }

                    var textVal = (tCell["value"]?.ToString() ?? "");
                    var vecVal = (vCell["value"]?.ToString() ?? "");

                    if (!embeddings.TryFindText(spaceName: vecSpace, text: out var lockedText, vectorBase64Url: vecVal) || (lockedText != textVal)) {
                        matches = false;
                        break;
                    }
                }

                if (matches) {
                    result[textName] = (VectorRowName: vecName, SpaceName: vecSpace);
                    break;
                }
            }
        }

        return result;
    }
    private static string FormatStateRate(JsonObject advance) {
        var numerator = (((advance["perSecondNumerator"] is JsonValue nv) && nv.TryGetValue<long>(value: out var n))
            ? n
            : 0L
        );
        var denominator = (((advance["perSecondDenominator"] is JsonValue dv) && dv.TryGetValue<long>(value: out var d))
            ? d
            : 1L
        );

        if (denominator == 1L) {
            return numerator.ToString(provider: CultureInfo.InvariantCulture);
        }

        // Eligibility (CanSugarAdvance) already proved this reduces to a terminating decimal, so exact `decimal`
        // division reproduces the same fraction `TryReduceDecimalRate` would parse back from the printed text.
        return (((decimal)numerator) / denominator).ToString(provider: CultureInfo.InvariantCulture);
    }
    // Whether `denominator` (already reduced against `numerator`'s magnitude) is a power of 2 and/or 5 — the exact
    // condition under which the rate has a finite decimal expansion the DSL's own literal grammar can spell.
    private static bool CanExpressRateAsLiteral(long numerator, long denominator) {
        if (denominator <= 0L) {
            return false;
        }
        if (denominator == 1L) {
            return true;
        }

        var gcd = GreatestCommonDivisor(
            a: Math.Abs(value: numerator),
            b: denominator
        );
        var reduced = (denominator / Math.Max(
            val1: gcd,
            val2: 1L
        ));

        while ((reduced % 2L) == 0L) {
            reduced /= 2L;
        }
        while ((reduced % 5L) == 0L) {
            reduced /= 5L;
        }

        return (reduced == 1L);
    }
    private static long GreatestCommonDivisor(long a, long b) {
        while (b != 0L) {
            (a, b) = (b, (a % b));
        }

        return Math.Abs(value: a);
    }
    private static bool CanSugarAdvance(JsonObject advance) {
        if (advance.Count != 2) {
            return false;
        }
        if (
            (advance["perSecondNumerator"] is not JsonValue nv) ||
            !nv.TryGetValue<long>(value: out var numerator)
        ) {
            return false;
        }
        if (
            (advance["perSecondDenominator"] is not JsonValue dv) ||
            !dv.TryGetValue<long>(value: out var denominator)
        ) {
            return false;
        }

        return CanExpressRateAsLiteral(
            denominator: denominator,
            numerator: numerator
        );
    }
    // The whole row must be representable without loss — every member spellable as a modifier, every cell
    // spellable as `key = value [modifier]*`, and every `advance` rate spellable as one literal.
    // A declaration names its row with a bare identifier; a cell key that is not one is written as a string.
    private static bool IsDeclarationIdentifier(string text) =>
        ((text.Length > 0) && (char.IsLetter(c: text[0]) || (text[0] == '_') || (text[0] == '$')) && text.All(predicate: static character => (char.IsLetterOrDigit(c: character) || (character == '_') || (character == '$'))));
    private static string FormatStateCellKey(string key) => (IsDeclarationIdentifier(text: key)
        ? key
        : $"\"{EscapeString(s: key)}\""
    );
    // Whether a stored value prints as a literal the declaration lowering admits and lowers back to the same JSON.
    private static bool IsSugarScalar(string kind, JsonNode? node) => kind switch {
        "Int" => ((node is JsonValue intValue) && intValue.TryGetValue<long>(value: out _)),
        "Bool" => ((node is JsonValue boolValue) && boolValue.TryGetValue<bool>(value: out _)),
        "Text" => ((node is JsonValue textValue) && textValue.TryGetValue<string>(value: out _)),
        "Vector" => ((node is JsonValue vecValue) && vecValue.TryGetValue<string>(value: out _)),
        _ => ((node is JsonValue fixedValue) && fixedValue.TryGetValue<string>(value: out var text) && Puck.Maths.FixedQ4816.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var parsed,
            s: text
        ) && (parsed.ToString() == text)),
    };
    private static bool CanSugarStateRow(JsonObject row, out bool isTable) {
        isTable = false;

        if (
            (row["name"] is not JsonValue nameVal) ||
            !nameVal.TryGetValue<string>(value: out var rowName) ||
            !IsDeclarationIdentifier(text: rowName)
        ) {
            return false;
        }
        if (
            (row["kind"] is not JsonValue kindVal) ||
            !kindVal.TryGetValue<string>(value: out var kind) ||
            (kind is not ("Int" or "Fixed" or "Bool" or "Text" or "Vector"))
        ) {
            return false;
        }

        var hasCapacity = row.ContainsKey(propertyName: "capacity");
        var hasCells = (row["cells"] is JsonArray);
        var hasValue = row.ContainsKey(propertyName: "value");

        // An omitted-kind declaration has no source signal for an empty non-Int row. Keep the explicit row form so
        // an empty Bool/Text/Fixed/Vector table or slot does not silently re-infer as Int.
        if (!hasCells && !hasValue && (kind != "Int")) {
            return false;
        }

        if (hasValue && hasCells) {
            return false;
        }

        var domainObj = (row["domain"] as JsonObject);
        var domainIsKeys = (
            (domainObj is not null) &&
            (domainObj.Count == 1) &&
            (domainObj["$type"] is JsonValue domainType) &&
            domainType.TryGetValue<string>(value: out var domainTypeName) &&
            (domainTypeName == "keys")
        );

        if (row.ContainsKey(propertyName: "domain") && !domainIsKeys) {
            return false;
        }

        var cells = (hasCells
            ? ((JsonArray)row["cells"]!)
            : []
        );
        // A table writes "cells" only when it has some, so an explicit empty array has no declaration spelling.
        if (
            hasCells &&
            (cells.Count == 0)
        ) {
            return false;
        }

        var inferredKeys = (hasCapacity || (cells.Count > 1) || ((cells.Count == 1) && ((cells[0] as JsonObject)?["key"]?.ToString() != "$value")));

        // The lowering spells a keys domain only where inference would otherwise read a slot, so a stored domain
        // round-trips only when it matches that rule exactly.
        if (
            domainIsKeys
                ? inferredKeys
                : (hasCells && !inferredKeys)
        ) {
            return false;
        }

        isTable = (inferredKeys || domainIsKeys);

        var allowed = (isTable
            ? TableNodeKeys
            : SlotNodeKeys
        );

        foreach (var (k, _) in row) {
            if (!allowed.Contains(item: k)) {
                return false;
            }
        }

        if (
            ((row["min"] is { } minNode) && !IsSugarScalar(kind: kind, node: minNode)) ||
            ((row["max"] is { } maxNode) && !IsSugarScalar(kind: kind, node: maxNode)) ||
            ((row["value"] is { } valueNode) && !IsSugarScalar(kind: kind, node: valueNode)) ||
            ((row["overflow"] is { } overflowNode) && (overflowNode.ToString() is not (nameof(Puck.State.StateOverflow.Refuse) or nameof(Puck.State.StateOverflow.Saturate))))
        ) {
            return false;
        }

        if (
            (row["advance"] is JsonObject rowAdvance) &&
            !CanSugarAdvance(advance: rowAdvance)
        ) {
            return false;
        }

        if (hasCells) {
            foreach (var cellNode in ((JsonArray)row["cells"]!)) {
                if (cellNode is not JsonObject cellObj) {
                    return false;
                }
                if (
                    (cellObj["key"] is not JsonValue cellKeyVal) ||
                    !cellKeyVal.TryGetValue<string>(value: out var cellKey) ||
                    cellKey.StartsWith(value: '$')
                ) {
                    return false;
                }
                if (
                    !cellObj.ContainsKey(propertyName: "value") ||
                    !IsSugarScalar(kind: kind, node: cellObj["value"])
                ) {
                    return false;
                }
                if (
                    (cellObj["behavior"] is { } behaviorNode) &&
                    (behaviorNode.ToString() != nameof(Puck.State.StateCellBehavior.None))
                ) {
                    return false;
                }

                foreach (var (ck, _) in cellObj) {
                    if (!TableCellKeys.Contains(item: ck)) {
                        return false;
                    }
                }

                if (
                    (cellObj["advance"] is JsonObject cellAdvance) &&
                    !CanSugarAdvance(advance: cellAdvance)
                ) {
                    return false;
                }
            }
        }

        return true;
    }
    private static bool CanSugarPileRow(JsonObject row, out string? tokenRow) {
        tokenRow = null;

        if (
            (row["name"] is not JsonValue nameVal) ||
            !nameVal.TryGetValue<string>(value: out var rowName) ||
            !IsDeclarationIdentifier(text: rowName)
        ) {
            return false;
        }
        if (
            (row["kind"] is not JsonValue kindVal) ||
            !kindVal.TryGetValue<string>(value: out var kind) ||
            (kind != "Bool")
        ) {
            return false;
        }
        if (row["domain"] is not JsonObject domainObj) {
            return false;
        }
        if (
            (domainObj["$type"] is not JsonValue typeVal) ||
            !typeVal.TryGetValue<string>(value: out var type) ||
            (type != "keysOf")
        ) {
            return false;
        }
        if (
            (domainObj["ordered"] is not JsonValue orderedVal) ||
            !orderedVal.TryGetValue<bool>(value: out var ordered) ||
            !ordered
        ) {
            return false;
        }
        if (
            (domainObj["row"] is not JsonValue rowVal) ||
            !rowVal.TryGetValue<string>(value: out var domainRow) ||
            !IsDeclarationIdentifier(text: domainRow)
        ) {
            return false;
        }

        foreach (var (dk, _) in domainObj) {
            if (!PileSugarDomainKeys.Contains(item: dk)) {
                return false;
            }
        }
        foreach (var (k, _) in row) {
            if (!PileNodeKeys.Contains(item: k)) {
                return false;
            }
        }

        // A pile always lowers its body to a cells array, so a row without one has no pile spelling.
        if (row["cells"] is not JsonArray pileCells) {
            return false;
        }

        foreach (var cellNode in pileCells) {
            if (
                (cellNode is not JsonObject cellObj) ||
                (cellObj.Count != 2) ||
                (cellObj["key"] is not JsonValue cellKeyVal) ||
                !cellKeyVal.TryGetValue<string>(value: out var cellKey) ||
                cellKey.StartsWith(value: '$') ||
                (cellObj["value"] is not JsonValue cellValueVal) ||
                !cellValueVal.TryGetValue<bool>(value: out var cellValue) ||
                !cellValue
            ) {
                return false;
            }
        }

        tokenRow = domainRow;

        return true;
    }
    private static void DecompileStatePileRow(StringBuilder sb, JsonObject row, string tokenRow, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (row["name"]!.ToString());
        var header = new StringBuilder(value: $"{indent}pile {name} of {tokenRow}");

        if (DocumentNumbers.TryInteger(
            node: row["capacity"],
            number: out var capacity
        )) {
            header.Append(value: $" capacity({capacity})");
        }

        var cells = ((row["cells"] as JsonArray) ?? []);

        if (cells.Count == 0) {
            sb.AppendLine(value: $"{header} {{ }}");

            return;
        }

        sb.AppendLine(value: $"{header} {{");

        var cellIndent = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );

        foreach (var cellNode in cells) {
            var key = (((JsonObject)cellNode!)["key"]!.ToString());

            sb.AppendLine(value: $"{cellIndent}{FormatStateCellKey(key: key)}");
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static HashSet<string> ComputeGridSugarConsumedTopologies(JsonArray? world, JsonArray? lattices) {
        var consumed = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (
            (world is null) ||
            (lattices is null)
        ) {
            return consumed;
        }

        foreach (var rowNode in world) {
            if (
                (rowNode is JsonObject rowObj) &&
                CanSugarGridRow(
                lattices: lattices,
                positionsRowName: out _,
                row: rowObj,
                topology: out var topology,
                world: world
            ) &&
                (topology?["name"] is JsonValue nameVal) &&
                nameVal.TryGetValue<string>(value: out var name)
            ) {
                consumed.Add(item: name);
            }
        }

        return consumed;
    }
    // A board row is grid-sugarable only when it is the SOLE `state.world` row lying over its topology (a
    // `cellsOf` row a second row also references falls back to `row { }`, since one `grid` statement can mint only
    // one topology and one occupancy row) and its topology carries nothing a `grid` declaration cannot spell
    // (custom directions or element aliases). `positionsRowName` names another row this row's topology's own name
    // reaches through `valuesFrom`, printed as this grid's `positions(...)` — the first such row found, in document
    // order, when more than one exists (the others keep their own `valuesFrom` and decompile independently).
    private static bool CanSugarGridRow(JsonObject row, JsonArray? lattices, JsonArray world, out JsonObject? topology, out string? positionsRowName) {
        topology = null;
        positionsRowName = null;

        if (
            (row["name"] is not JsonValue nameVal) ||
            !nameVal.TryGetValue<string>(value: out var rowName) ||
            !IsDeclarationIdentifier(text: rowName)
        ) {
            return false;
        }
        if (
            (row["kind"] is not JsonValue kindVal) ||
            !kindVal.TryGetValue<string>(value: out var kind) ||
            (kind is not ("Int" or "Bool"))
        ) {
            return false;
        }

        foreach (var (k, _) in row) {
            if (!GridNodeKeys.Contains(item: k)) {
                return false;
            }
        }

        if (row["domain"] is not JsonObject domainObj) {
            return false;
        }
        if (
            (domainObj["$type"] is not JsonValue typeVal) ||
            !typeVal.TryGetValue<string>(value: out var type) ||
            (type != "cellsOf")
        ) {
            return false;
        }
        if (
            (domainObj["topology"] is not JsonValue topologyVal) ||
            !topologyVal.TryGetValue<string>(value: out var topologyName) ||
            (topologyName != rowName)
        ) {
            return false;
        }

        foreach (var (dk, _) in domainObj) {
            if (!GridSugarDomainKeys.Contains(item: dk)) {
                return false;
            }
        }

        JsonObject? found = null;

        foreach (var topologyNode in (lattices ?? [])) {
            if (
                (topologyNode is JsonObject candidate) &&
                (candidate["name"] is JsonValue candidateNameVal) &&
                candidateNameVal.TryGetValue<string>(value: out var candidateName) &&
                (candidateName == topologyName)
            ) {
                found = candidate;

                break;
            }
        }
        if (found is null) {
            return false;
        }
        if (
            (found["$type"] is not JsonValue topologyTypeVal) ||
            !topologyTypeVal.TryGetValue<string>(value: out var topologyType) ||
            (topologyType != "grid")
        ) {
            return false;
        }

        foreach (var (tk, _) in found) {
            if (!GridTopologyKeys.Contains(item: tk)) {
                return false;
            }
        }
        if (
            (found["origin"] is not JsonArray originArr) ||
            (originArr.Count != 3) ||
            !originArr.All(predicate: IsSugarFloat)
        ) {
            return false;
        }
        if (!IsSugarFloat(node: found["cellSize"])) {
            return false;
        }
        if (
            (found["width"] is not JsonValue widthVal) ||
            !widthVal.TryGetValue<long>(value: out var width) ||
            (width <= 0)
        ) {
            return false;
        }
        if (
            (found["depth"] is not JsonValue depthVal) ||
            !depthVal.TryGetValue<long>(value: out var depth) ||
            (depth <= 0)
        ) {
            return false;
        }
        if (
            (found["wrap"] is { } wrapNode) &&
            !GridWrapNames.Contains(item: wrapNode.ToString())
        ) {
            return false;
        }
        if (
            (found["band"] is { } bandNode) &&
            !IsSugarFloat(node: bandNode)
        ) {
            return false;
        }

        foreach (var otherNode in world) {
            if (
                (otherNode is not JsonObject otherObj) ||
                ReferenceEquals(objA: otherObj, objB: row)
            ) {
                continue;
            }
            if (
                ((otherObj["domain"] as JsonObject)?["$type"] is JsonValue otherTypeVal) &&
                otherTypeVal.TryGetValue<string>(value: out var otherType) &&
                (otherType == "cellsOf") &&
                ((otherObj["domain"] as JsonObject)?["topology"] is JsonValue otherTopologyVal) &&
                otherTopologyVal.TryGetValue<string>(value: out var otherTopologyName) &&
                (otherTopologyName == topologyName)
            ) {
                return false;
            }
        }

        var hasCells = (row["cells"] is JsonArray);
        var hasInverse = (row["inverse"] is JsonObject);

        // `grid` inference reads its authored cells; an empty Bool grid has no source kind signal and must retain the
        // explicit row representation rather than coming back as an Int grid.
        if (!hasCells && (kind == "Bool")) {
            return false;
        }

        if (hasCells && hasInverse) {
            return false;
        }
        if (hasInverse) {
            var inverseObj = ((JsonObject)row["inverse"]!);

            if (
                (inverseObj.Count != 2) ||
                (inverseObj["tokens"] is not JsonValue inverseTokensVal) ||
                !inverseTokensVal.TryGetValue<string>(value: out _) ||
                (inverseObj["codes"] is not JsonValue inverseCodesVal) ||
                !inverseCodesVal.TryGetValue<string>(value: out _)
            ) {
                return false;
            }
        }
        if (hasCells) {
            var cells = ((JsonArray)row["cells"]!);

            if (cells.Count == 0) {
                return false;
            }

            var cellCeiling = (width * depth);

            foreach (var cellNode in cells) {
                if (
                    (cellNode is not JsonObject cellObj) ||
                    (cellObj.Count != 2) ||
                    (cellObj["key"] is not JsonValue cellKeyVal) ||
                    !cellKeyVal.TryGetValue<string>(value: out var cellKey) ||
                    !long.TryParse(
                    result: out var ordinal,
                    s: cellKey
                ) ||
                    (ordinal < 0) ||
                    (ordinal >= cellCeiling) ||
                    !cellObj.ContainsKey(propertyName: "value") ||
                    !IsSugarScalar(kind: kind, node: cellObj["value"])
                ) {
                    return false;
                }
            }
        }

        if (
            ((row["min"] is { } minNode) && !IsSugarScalar(kind: kind, node: minNode)) ||
            ((row["max"] is { } maxNode) && !IsSugarScalar(kind: kind, node: maxNode)) ||
            ((row["overflow"] is { } overflowNode) && (overflowNode.ToString() is not (nameof(Puck.State.StateOverflow.Refuse) or nameof(Puck.State.StateOverflow.Saturate))))
        ) {
            return false;
        }

        topology = found;

        foreach (var otherNode in world) {
            if (
                (otherNode is JsonObject otherObj) &&
                !ReferenceEquals(objA: otherObj, objB: row) &&
                (otherObj["valuesFrom"] is JsonValue valuesFromVal) &&
                valuesFromVal.TryGetValue<string>(value: out var valuesFromName) &&
                (valuesFromName == topologyName) &&
                (otherObj["name"] is JsonValue otherNameVal) &&
                otherNameVal.TryGetValue<string>(value: out var otherName)
            ) {
                positionsRowName = otherName;

                break;
            }
        }

        return true;
    }
    private static bool IsSugarFloat(JsonNode? node) => ((node is JsonValue floatValue) && (floatValue.TryGetValue<double>(value: out _) || floatValue.TryGetValue<long>(value: out _)));
    private static string FormatBoardEmptyLiteral(JsonNode? node, string kind) {
        var raw = (((node is JsonValue rawVal) && rawVal.TryGetValue<long>(value: out var l))
            ? l
            : 0L
        );

        return ((kind == "Bool")
            ? ((raw != 0L) ? "true" : "false")
            : raw.ToString(provider: CultureInfo.InvariantCulture)
        );
    }
    private static void DecompileStateGridRow(StringBuilder sb, JsonObject row, JsonObject topology, string? positionsRowName, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (row["name"]!.ToString());
        var kind = (row["kind"]!.ToString());
        var header = new StringBuilder(value: $"{indent}grid {name} dimensions(width: {topology["width"]}, depth: {topology["depth"]})");

        if (topology["wrap"] is { } wrapNode) {
            header.Append(value: $" wrap({wrapNode})");
        }

        header.Append(value: $" cellSize({topology["cellSize"]})");

        var origin = ((JsonArray)topology["origin"]!);

        if (
            !origin.All(predicate: n => (IsSugarFloat(node: n) && (n!.GetValue<double>() == 0.0)))
        ) {
            header.Append(value: $" origin({origin[0]}, {origin[1]}, {origin[2]})");
        }
        if (topology["band"] is { } bandNode) {
            header.Append(value: $" band({bandNode})");
        }

        var domainObj = ((JsonObject)row["domain"]!);

        if (domainObj["empty"] is { } emptyNode) {
            header.Append(value: $" empty({FormatBoardEmptyLiteral(kind: kind, node: emptyNode)})");
        }
        if (positionsRowName is not null) {
            header.Append(value: $" positions({positionsRowName})");
        }
        if (row["inverse"] is JsonObject inverseObj) {
            header.Append(value: $" inverse(tokens: {inverseObj["tokens"]}, codes: {inverseObj["codes"]})");
        }

        AppendStateRowModifiers(
            header: header,
            includeCapacity: false,
            kind: kind,
            row: row
        );

        var cells = ((row["cells"] as JsonArray) ?? []);

        if (cells.Count == 0) {
            sb.AppendLine(value: header.ToString());

            return;
        }

        sb.AppendLine(value: $"{header} {{");

        var cellIndent = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );

        foreach (var cellNode in cells) {
            var cellObj = ((JsonObject)cellNode!);
            var key = (cellObj["key"]!.ToString());

            sb.AppendLine(value: $"{cellIndent}{FormatStateCellKey(key: key)} = {FormatStateScalarLiteral(
                kind: kind,
                node: cellObj["value"]
            )}");
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
}
