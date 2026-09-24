using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    // Called by LowerBlock before the ordinary root-arm table. These are composition-source conveniences whose
    // runtime output is ordinary creation/placement/spawn data; only the ground descriptor survives until link
    // lowering derives its sides, then WorldCompositionLinks removes it.
    private static bool TryLowerCompositionEndpointBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        if (block.Identifier is not ("ground" or "spawn")) {
            return false;
        }
        var name = DocumentLowering.ResolveBlockName(block: block, scope: scope);

        if (string.IsNullOrWhiteSpace(value: name)) {
            Refuse(message: $"{block.Identifier} requires a name.", node: block, scope: scope);
            return true;
        }
        var value = LowerEndpointObject(block: block, scope: scope);

        if (block.Identifier == "spawn") {
            if (value.Any(predicate: property => (property.Key is not ("at" or "yaw")))) {
                Refuse(message: $"spawn '{name}' admits only at and yaw.", node: block, scope: scope);
                return true;
            }
            var position = value["at"];

            if ((position is not JsonArray { Count: 3 } spawnPosition) || !TryVector(spawnPosition, positive: false)) {
                Refuse(message: $"spawn '{name}' requires at [x, y, z].", node: block, scope: scope);
                return true;
            }
            if ((value["yaw"] is { } yaw) && !DocumentNumbers.TryExact(node: yaw, number: out _)) {
                Refuse(message: $"spawn '{name}' requires a finite numeric yaw.", node: block, scope: scope);
                return true;
            }
            if (!TryEndpointRows(block: block, member: "spawnPoints", parent: parent, rows: out var spawnRows, scope: scope)) {
                return true;
            }
            if (HasMalformedName(member: "id", rows: spawnRows)) {
                Refuse(message: $"spawn '{name}' cannot inspect a spawnPoints row whose id is not text.", node: block, scope: scope);
                return true;
            }
            if (FindNamed(member: "id", name: name, rows: spawnRows) is not null) {
                Refuse(message: $"spawn '{name}' is declared more than once.", node: block, scope: scope);
                return true;
            }

            scope.SourceMap?.Register(jsonPointer: $"{scope.CurrentPointer}/spawnPoints/{spawnRows.Count}", span: block.Span);
            spawnRows.AppendNode(item: new JsonObject {
                ["id"] = name,
                ["position"] = position.DeepClone(),
                ["yawDegrees"] = (value["yaw"]?.DeepClone() ?? JsonValue.Create(0)),
            });
            return true;
        }

        var center = (value["center"] ?? new JsonArray(0m, 0m, 0m));
        var size = value["size"];

        if (value.Any(predicate: property => (property.Key is not ("center" or "size"))) ||
            (center is not JsonArray { Count: 3 } centerArray) || !TryVector(centerArray, positive: false) ||
            (size is not JsonArray { Count: 2 } sizeArray) || !TryVector(sizeArray, positive: true)) {
            Refuse(message: $"ground '{name}' requires size [width, depth] and an optional three-component center.", node: block, scope: scope);
            return true;
        }
        if (!GroundSectionShapesAreValid(block: block, parent: parent, scope: scope) ||
            !TryEndpointRows(block: block, member: "prototypes", parent: parent, rows: out var prototypes, scope: scope) ||
            !TryPlacementRows(block: block, parent: parent, rows: out var placements, scope: scope) ||
            !TryCompositionGroundRows(block: block, parent: parent, rows: out var groundRows, scope: scope)) {
            return true;
        }
        if (HasMalformedName(member: "id", rows: prototypes) || HasMalformedName(member: "id", rows: placements)) {
            Refuse(message: $"ground '{name}' cannot inspect a prototype or placement whose id is not text.", node: block, scope: scope);
            return true;
        }
        var prototypeId = Generated(
            name: GroundPrototypeId(name: name),
            scope: scope
        );

        if ((FindNamed(member: "id", name: prototypeId, rows: prototypes) is not null) || (FindNamed(member: "id", name: name, rows: placements) is not null)) {
            Refuse(message: $"ground '{name}' collides with an existing generated prototype or placement.", node: block, scope: scope);
            return true;
        }

        var (prototype, placement) = GroundRows(
            center: centerArray,
            name: name,
            size: sizeArray
        );

        scope.SourceMap?.Register(jsonPointer: $"{scope.CurrentPointer}/prototypes/{prototypes.Count}", span: block.Span);
        prototypes.AppendNode(item: prototype);
        scope.SourceMap?.Register(jsonPointer: $"{scope.CurrentPointer}/placements/rows/{placements.Count}", span: block.Span);
        placements.AppendNode(item: placement);
        groundRows.AppendNode(item: new JsonObject { ["name"] = name, ["center"] = center.DeepClone(), ["size"] = size.DeepClone() });
        return true;
    }

    /// <summary>The prototype and placement rows a <c>ground name { size [w, d] center [x, y, z] }</c> block writes:
    /// the generated prototype <c>ground$name</c> — a grey box <c>w × 0.2 × d</c> — and the solid placement
    /// <c>name</c> that stands it with its top face at the center's height. The decompiler regenerates the pair from
    /// the values it reads back, so the block prints back only where it would write exactly these rows.</summary>
    /// <param name="name">The ground's name, which is its placement's id.</param>
    /// <param name="center">The ground's three-component center.</param>
    /// <param name="size">The ground's width and depth.</param>
    /// <returns>The prototype row and the placement row.</returns>
    internal static (JsonObject Prototype, JsonObject Placement) GroundRows(string name, JsonArray center, JsonArray size) {
        var prototypeId = GroundPrototypeId(name: name);
        var placedCenter = ((JsonArray)center.DeepClone());

        placedCenter[1] = (ReadNumber(node: center[1]) - GroundHalfThickness);

        return (
            new JsonObject {
                ["id"] = prototypeId,
                ["document"] = new JsonObject {
                    ["schema"] = "puck.creation.v1",
                    ["name"] = prototypeId,
                    ["palette"] = new JsonArray(new JsonObject { ["color"] = "#808080" }),
                    ["shapes"] = new JsonArray(new JsonObject {
                        ["id"] = 0,
                        ["type"] = "Box",
                        ["position"] = new JsonArray(0, 0, 0),
                        ["rotation"] = new JsonArray(0, 0, 0, 1),
                        ["scale"] = new JsonArray(Half(node: size[0]), GroundHalfThickness, Half(node: size[1])),
                        ["material"] = 0,
                        ["blend"] = "Union",
                        ["smooth"] = 0,
                    }),
                },
            },
            new JsonObject {
                ["id"] = name,
                ["prototypeId"] = prototypeId,
                ["position"] = placedCenter,
                ["yawDegrees"] = 0,
                ["scale"] = 1,
                ["solid"] = new JsonObject { ["margin"] = 0 },
            }
        );
    }
    /// <summary>The generated id of a ground's prototype: <c>ground$name</c>.</summary>
    /// <param name="name">The ground's name.</param>
    /// <returns>The prototype id.</returns>
    internal static string GroundPrototypeId(string name) => GeneratedName.Join(GroundHead, name);

    // The first part of a ground's generated prototype id, and half the thickness of the box it stands.
    internal const string GroundHead = "ground";

    private const decimal GroundHalfThickness = 0.1m;

    private static JsonNode Half(JsonNode? node) => JsonValue.Create((ReadNumber(node: node) * 0.5m))!;
    private static decimal ReadNumber(JsonNode? node) => (DocumentNumbers.TryExact(node: node, number: out var number) ? number : 0m);
    private static bool TryVector(JsonArray values, bool positive) {
        foreach (var node in values) {
            if (!DocumentNumbers.TryExact(node: node, number: out var number) || (positive && (number <= 0m))) {
                return false;
            }
        }
        return true;
    }
    private static JsonArray Rows(JsonObject parent, string member) {
        var rows = ((parent[member] as JsonArray) ?? []);

        parent[member] = rows;
        return rows;
    }
    private static bool TryEndpointRows(JsonObject parent, string member, BlockNode block, DocumentScope scope, out JsonArray rows) {
        if (parent[member] is not null and not JsonArray) {
            Refuse(message: $"{block.Identifier} '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed section '{member}'.", node: block, scope: scope);
            rows = null!;
            return false;
        }
        rows = Rows(member: member, parent: parent);
        return true;
    }
    private static bool GroundSectionShapesAreValid(JsonObject parent, BlockNode block, DocumentScope scope) {
        var valid = ((parent["prototypes"] is null or JsonArray) &&
            (parent["placements"] is null or JsonObject) &&
            ((parent["placements"] is not JsonObject placements) || (placements["rows"] is null or JsonArray)) &&
            (parent["$composition"] is null or JsonObject) &&
            ((parent["$composition"] is not JsonObject composition) || (composition["grounds"] is null or JsonArray)));

        if (!valid) {
            Refuse(message: $"ground '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed prototypes, placements, or composition metadata.", node: block, scope: scope);
        }
        return valid;
    }
    private static bool TryGetOrInitSectionRows(
        BlockNode block,
        JsonObject parent,
        string rowsKey,
        string rowsMalformedMessage,
        DocumentScope scope,
        string sectionKey,
        string sectionMalformedMessage,
        out JsonArray rows
    ) {
        if (parent[sectionKey] is not null and not JsonObject) {
            Refuse(message: sectionMalformedMessage, node: block, scope: scope);
            rows = null!;
            return false;
        }
        var section = ((parent[sectionKey] as JsonObject) ?? []);

        if (section[rowsKey] is not null and not JsonArray) {
            Refuse(message: rowsMalformedMessage, node: block, scope: scope);
            rows = null!;
            return false;
        }
        parent[sectionKey] = section;
        rows = Rows(member: rowsKey, parent: section);
        return true;
    }
    private static bool TryPlacementRows(JsonObject parent, BlockNode block, DocumentScope scope, out JsonArray rows) =>
        TryGetOrInitSectionRows(
            block: block,
            parent: parent,
            rowsKey: "rows",
            rowsMalformedMessage: $"ground '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed section 'placements.rows'.",
            scope: scope,
            sectionKey: "placements",
            sectionMalformedMessage: $"ground '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed section 'placements'.",
            rows: out rows
        );
    private static bool TryCompositionGroundRows(JsonObject parent, BlockNode block, DocumentScope scope, out JsonArray rows) =>
        TryGetOrInitSectionRows(
            block: block,
            parent: parent,
            rowsKey: "grounds",
            rowsMalformedMessage: $"ground '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed composition grounds.",
            scope: scope,
            sectionKey: "$composition",
            sectionMalformedMessage: $"ground '{DocumentLowering.ResolveBlockName(block: block, scope: scope)}' cannot extend malformed composition metadata.",
            rows: out rows
        );
    private static JsonObject LowerEndpointObject(BlockNode block, DocumentScope scope) {
        var result = new JsonObject();

        foreach (var statement in block.Statements) {
            if ((statement is not PropertyNode property) || result.ContainsKey(propertyName: property.Name)) {
                Refuse(message: $"{block.Identifier} properties must be distinct named values.", node: statement, scope: scope);
                continue;
            }
            var fieldKey = property.Name switch {
                "at" or "center" or "position" or "size" => "position",
                "yaw" => "yawDegrees",
                _ => property.Name,
            };

            result[property.Name] = DocumentLowering.LowerValue(property.Value, scope, fieldKey);
        }
        return result;
    }

    /// <summary>Qualifies each ground a module instance declares by the instance's alias, the way the instance's
    /// placements and prototypes already are (<see cref="WorldModuleNamespace"/>): the ground <c>floor</c> of
    /// <c>use patch as wing</c> stands the placement <c>wing$floor</c>, and a border addresses it
    /// <c>west.wing.floor.east</c>, so two aliased uses of one module stand two grounds.</summary>
    /// <param name="module">The expanded module instance; mutated.</param>
    /// <param name="alias">The instance's alias.</param>
    internal static void QualifyCompositionGrounds(JsonObject module, string alias) {
        if ((module["$composition"] is not JsonObject composition) || (composition["grounds"] is not JsonArray grounds)) {
            return;
        }
        foreach (var ground in grounds.OfType<JsonObject>()) {
            if (StringValue(node: ground["name"]) is { } name) {
                ground["name"] = GeneratedName.Qualify(head: alias, name: name);
            }
        }
    }

    private static JsonObject? FindNamed(JsonArray rows, string member, string name) => rows.OfType<JsonObject>().FirstOrDefault(predicate: row => string.Equals(a: StringValue(node: row[member]), b: name, comparisonType: StringComparison.Ordinal));
    private static bool HasMalformedName(JsonArray rows, string member) => rows.OfType<JsonObject>().Any(predicate: row => ((row[member] is not null) && (StringValue(node: row[member]) is null)));
    private static string? StringValue(JsonNode? node) => (((node is JsonValue value) && value.TryGetValue<string>(value: out var text)) ? text : null);
}
