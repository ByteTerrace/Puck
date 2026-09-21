using System.Globalization;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Embeddings;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    internal static string? FindDefaultSpace(JsonObject parent) {
        if ((parent["state"]?["spaces"] is JsonArray spacesArr) && (spacesArr.Count == 1)) {
            return spacesArr[0]?["name"]?.ToString();
        }
        return null;
    }
    internal static string? FindRowSpace(JsonObject? rootObj, string rowName) {
        if ((rootObj is not null) && (rootObj["state"]?["world"] is JsonArray worldArr)) {
            foreach (var row in worldArr) {
                if ((row is JsonObject obj) && string.Equals(a: obj["name"]?.ToString(), b: rowName, comparisonType: StringComparison.Ordinal)) {
                    return obj["space"]?.ToString();
                }
            }
        }
        return null;
    }

    private static int ReadDimensions(JsonNode? node) {
        if (node is JsonValue v) {
            if (v.TryGetValue<int>(value: out var i)) {
                return i;
            }
            if (v.TryGetValue<long>(value: out var l)) {
                return ((int)l);
            }
        }
        return 0;
    }

    internal static (string Name, string Model, string Revision, int Dimensions)? FindSpaceInfo(JsonObject? parent, string? spaceName) {
        if ((parent is null) || (parent["state"]?["spaces"] is not JsonArray spacesArr)) {
            return null;
        }

        if (string.IsNullOrEmpty(value: spaceName)) {
            if ((spacesArr.Count == 1) && (spacesArr[0] is JsonObject defaultObj)) {
                var name = (defaultObj["name"]?.ToString() ?? "");
                var model = (defaultObj["model"]?.ToString() ?? "");
                var rev = (defaultObj["revision"]?.ToString() ?? "");
                var dims = ReadDimensions(node: defaultObj["dimensions"]);

                return (name, model, rev, dims);
            }
            return null;
        }

        foreach (var node in spacesArr) {
            if ((node is JsonObject obj) && string.Equals(a: obj["name"]?.ToString(), b: spaceName, comparisonType: StringComparison.Ordinal)) {
                var name = (obj["name"]?.ToString() ?? "");
                var model = (obj["model"]?.ToString() ?? "");
                var rev = (obj["revision"]?.ToString() ?? "");
                var dims = ReadDimensions(node: obj["dimensions"]);

                return (name, model, rev, dims);
            }
        }

        return null;
    }
    internal static bool TryResolveEmbeddedText(string text, string? space, DocumentScope scope, SourceSpan span, out string base64) {
        base64 = "";
        var resolvedSpace = space;

        if (string.IsNullOrEmpty(value: resolvedSpace)) {
            if (scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var pObj) && (pObj is JsonObject parent)) {
                resolvedSpace = FindDefaultSpace(parent: parent);
            }
        }

        if (!string.IsNullOrEmpty(value: resolvedSpace)) {
            RecordDiscoveredEmbeddingText(scope: scope, spaceName: resolvedSpace, text: text);
        }

        if (!scope.Annotations.TryGetValue(key: "EmbeddingLock", value: out var lockObj) || (lockObj is not EmbeddingLock lockFile)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbeddingLockMissing,
                message: $"No embedding lock entry for \"{text}\"; run puck embed.",
                span: span
            );
            return false;
        }

        if (string.IsNullOrEmpty(value: resolvedSpace)) {
            if (lockFile.Spaces.Count == 1) {
                resolvedSpace = lockFile.Spaces.Keys.First();
                RecordDiscoveredEmbeddingText(scope: scope, spaceName: resolvedSpace, text: text);
            } else {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.EmbeddingSpaceAmbiguous,
                    message: $"Embedding literal \"{text}\" has no space source.",
                    span: span
                );
                return false;
            }
        }

        if (!lockFile.Spaces.TryGetValue(key: resolvedSpace, value: out var lockSpace)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbeddingLockMissing,
                message: $"No embedding lock entry for \"{text}\"; run puck embed.",
                span: span
            );
            return false;
        }

        // Check if space is stale against document declaration
        if (scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rootObj) && (rootObj is JsonObject rootParent) &&
            (rootParent["state"]?["spaces"] is JsonArray spacesArr)) {
            foreach (var spNode in spacesArr) {
                if ((spNode is JsonObject spObj) && string.Equals(a: spObj["name"]?.ToString(), b: resolvedSpace, comparisonType: StringComparison.Ordinal)) {
                    var docModel = (spObj["model"]?.ToString() ?? "");
                    var docRev = (spObj["revision"]?.ToString() ?? "");
                    var docDims = (((spObj["dimensions"] is JsonValue dv) && (dv.TryGetValue<int>(value: out var dVal) || (dv.TryGetValue<long>(value: out var lVal) && ((dVal = ((int)lVal)) == dVal)))) ? dVal : 0);

                    if (lockFile.IsSpaceStale(dimensions: docDims, model: docModel, revision: docRev, spaceName: resolvedSpace)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbeddingLockStale,
                            message: $"Embedding space '{resolvedSpace}' in lock is stale; run puck embed.",
                            span: span
                        );
                        return false;
                    }
                }
            }
        }

        if (lockFile.TryGet(spaceName: resolvedSpace, text: text, vectorBase64Url: out var foundVector)) {
            base64 = foundVector;
            return true;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.EmbeddingLockMissing,
            message: $"No embedding lock entry for \"{text}\"; run puck embed.",
            span: span
        );
        return false;
    }

    private static void ValidateAndLowerSpacesBlock(BlockNode block, JsonObject stateObj, DocumentScope scope, string statePointer) {
        if (stateObj["spaces"] is not JsonArray spacesArr) {
            spacesArr = [];
            stateObj["spaces"] = spacesArr;
        }

        var spacesPointer = $"{statePointer}/spaces";

        scope.SourceMap?.Register(jsonPointer: spacesPointer, span: block.Span);

        var seenSpaceNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var stmt in block.Statements) {
            if ((stmt is BlockNode spaceBlock) && string.Equals(a: spaceBlock.Identifier, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if (spacesArr.Count >= 16) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: "A document can declare at most 16 embedding spaces.",
                        span: spaceBlock.Span
                    );
                    continue;
                }

                var resolvedName = DocumentLowering.ResolveBlockName(block: spaceBlock, scope: scope);

                if (string.IsNullOrEmpty(value: resolvedName) || !CellName.TryParse(candidate: resolvedName, name: out _, reason: out _)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: $"Embedding space name '{resolvedName}' is invalid — expected a valid cell name.",
                        span: spaceBlock.Span
                    );
                    continue;
                }

                if (!seenSpaceNames.Add(item: resolvedName!)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: $"Embedding space '{resolvedName}' is declared more than once.",
                        span: spaceBlock.Span
                    );
                    continue;
                }

                var spaceIdx = spacesArr.Count;
                var spacePointer = $"{spacesPointer}/{spaceIdx}";
                var oldPointer = scope.CurrentPointer;

                scope.SourceMap?.Register(
                    jsonPointer: spacePointer,
                    span: spaceBlock.Span
                );
                scope.CurrentPointer = spacePointer;

                var spaceObj = LowerBlockToObject(block: spaceBlock, scope: scope);

                scope.CurrentPointer = oldPointer;

                spaceObj["name"] = resolvedName;

                // Validate fields: model, revision, dimensions
                string? model = null;
                string? revision = null;
                int? dimensions = null;

                foreach (var s in spaceBlock.Statements) {
                    if (s is PropertyNode prop) {
                        switch (prop.Name) {
                            case "model":
                                if ((prop.Value is LiteralExpressionNode { Value: string mStr }) && (mStr.Length is > 0 and <= 128)) {
                                    model = mStr;
                                } else {
                                    scope.Diagnostics.ReportError(
                                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                                        message: "Embedding space 'model' must be a non-empty string of at most 128 characters.",
                                        span: prop.Span
                                    );
                                }
                                break;
                            case "revision":
                                if ((prop.Value is LiteralExpressionNode { Value: string rStr }) && (rStr.Length is > 0 and <= 128)) {
                                    revision = rStr;
                                } else {
                                    scope.Diagnostics.ReportError(
                                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                                        message: "Embedding space 'revision' must be a non-empty string of at most 128 characters.",
                                        span: prop.Span
                                    );
                                }
                                break;
                            case "dimensions":
                                if ((prop.Value is LiteralExpressionNode { Value: long dVal }) && (dVal is >= 8 and <= 1024)) {
                                    dimensions = ((int)dVal);
                                } else {
                                    scope.Diagnostics.ReportError(
                                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                                        message: "Embedding space 'dimensions' must be an integer in [8, 1024].",
                                        span: prop.Span
                                    );
                                }
                                break;
                            case "name":
                                break;
                            default:
                                scope.Diagnostics.ReportError(
                                    code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                                    message: $"Embedding space declares unrecognized field '{prop.Name}'.",
                                    span: prop.Span
                                );
                                break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(value: model)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: $"Embedding space '{resolvedName}' requires a 'model' field.",
                        span: spaceBlock.Span
                    );
                }
                if (string.IsNullOrEmpty(value: revision)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: $"Embedding space '{resolvedName}' requires a 'revision' field.",
                        span: spaceBlock.Span
                    );
                }
                if (!dimensions.HasValue) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingSpaceInvalid,
                        message: $"Embedding space '{resolvedName}' requires a 'dimensions' field in [8, 1024].",
                        span: spaceBlock.Span
                    );
                }

                spacesArr.AppendNode(item: spaceObj);
            }
        }
    }
    private static JsonNode? LowerVectorCellValue(
        ExpressionNode expr,
        string context,
        string spaceName,
        JsonObject rootObj,
        DocumentScope scope
    ) {
        var spaceInfo = FindSpaceInfo(parent: rootObj, spaceName: spaceName);
        var dims = (spaceInfo?.Dimensions ?? 256);

        if (expr is LiteralExpressionNode { Unit: null, Value: string text }) {
            if (TryResolveEmbeddedText(text, spaceName, scope, expr.Span, out var b64)) {
                return JsonValue.Create(b64)!;
            }
            return null;
        }

        if (expr is CallExpressionNode call) {
            if (string.Equals(a: call.Name, b: "embed", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if ((call.Arguments.Count == 0) || (call.Arguments[0].Value is not LiteralExpressionNode { Value: string embedText })) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorLiteralMisplaced,
                        message: $"embed(...) in {context} expects a string literal argument.",
                        span: call.Span
                    );
                    return null;
                }

                var embedSpace = spaceName;

                if (call.Arguments.Count > 1) {
                    var spaceArg = (call.Arguments.FirstOrDefault(predicate: a => string.Equals(a: a.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))
                                   ?? call.Arguments[1]);

                    if (spaceArg.Value is LiteralExpressionNode { Value: string spVal }) {
                        embedSpace = spVal;
                    } else if (spaceArg.Value is IdentifierExpressionNode ident) {
                        embedSpace = ident.Name;
                    }
                }

                if (!string.Equals(a: embedSpace, b: spaceName, comparisonType: StringComparison.Ordinal)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorOperandMismatch,
                        message: $"embed space '{embedSpace}' does not match row space '{spaceName}'.",
                        span: call.Span
                    );
                    return null;
                }

                if (TryResolveEmbeddedText(embedText, embedSpace, scope, call.Span, out var b64)) {
                    return JsonValue.Create(b64)!;
                }
                return null;
            }

            if (string.Equals(a: call.Name, b: "vector", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if ((call.Arguments.Count == 0) || (call.Arguments[0].Value is not LiteralExpressionNode { Value: string vecBase64 })) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorLiteralInvalid,
                        message: $"vector(...) in {context} expects a base64url string argument.",
                        span: call.Span
                    );
                    return null;
                }

                if (!StateVector.TryParseBase64Url(dimensions: dims, error: out var error, text: vecBase64, vector: out _)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.VectorLiteralInvalid,
                        message: $"vector(...) literal in {context} is invalid: {error}",
                        span: call.Span
                    );
                    return null;
                }

                return JsonValue.Create(vecBase64)!;
            }
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.VectorLiteralMisplaced,
            message: $"Value in {context} must be a string, embed(...), or vector(...) literal.",
            span: expr.Span
        );
        return null;
    }
    private static void ValidateVectorCeilings(
        string rowName,
        int? capacity,
        int cellCount,
        int dimensions,
        DocumentScope scope,
        SourceSpan span,
        ref long totalVectorBytes
    ) {
        var rowCeiling = Math.Max(val1: 1, val2: (capacity ?? cellCount));
        var rowBytes = (((long)rowCeiling) * dimensions);

        if (rowBytes > 65536) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.VectorRowTooLarge,
                message: $"Vector row '{rowName}' byte ceiling ({rowCeiling} × {dimensions} = {rowBytes}) exceeds limit of 65536 bytes.",
                span: span
            );
        }

        totalVectorBytes += rowBytes;
        if (totalVectorBytes > ((4L * 1024) * 1024)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.VectorRowTooLarge,
                message: $"Total vector section size ({totalVectorBytes} bytes) exceeds limit of 4194304 bytes (4 MiB).",
                span: span
            );
        }
    }
    private static JsonObject? CreateEmbedsCompanionRow(
        StateTableDeclarationNode textTable,
        StateModifierNode embedsMod,
        JsonObject textRowObj,
        JsonObject rootObj,
        DocumentScope scope,
        ref long totalVectorBytes
    ) {
        if (embedsMod.Arguments.Count == 0) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbedsInvalid,
                message: "embeds modifier requires a companion vector table name.",
                span: embedsMod.Span
            );
            return null;
        }

        string? companionName = null;

        if (embedsMod.Arguments[0].Value is IdentifierExpressionNode ident) {
            companionName = ident.Name;
        } else if (embedsMod.Arguments[0].Value is LiteralExpressionNode { Value: string strName }) {
            companionName = strName;
        }

        if (string.IsNullOrEmpty(value: companionName) || !CellName.TryParse(candidate: companionName, name: out _, reason: out _)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbedsInvalid,
                message: $"Companion vector table name '{companionName}' is invalid.",
                span: embedsMod.Span
            );
            return null;
        }

        string? spaceName = null;

        if (embedsMod.Arguments.Count > 1) {
            var spaceArg = (embedsMod.Arguments.FirstOrDefault(predicate: a => string.Equals(a: a.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))
                           ?? embedsMod.Arguments[1]);

            if (spaceArg.Value is IdentifierExpressionNode spIdent) {
                spaceName = spIdent.Name;
            } else if (spaceArg.Value is LiteralExpressionNode { Value: string spStr }) {
                spaceName = spStr;
            }
        }

        if (string.IsNullOrEmpty(value: spaceName)) {
            spaceName = FindDefaultSpace(parent: rootObj);
        }

        if (string.IsNullOrEmpty(value: spaceName)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                message: $"Companion vector row '{companionName}' names no space and there is no default space in the document.",
                span: embedsMod.Span
            );
            return null;
        }

        var spaceInfo = FindSpaceInfo(parent: rootObj, spaceName: spaceName);

        if (!spaceInfo.HasValue) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.EmbeddingSpaceUnknown,
                message: $"Embedding space '{spaceName}' declared on companion row '{companionName}' was not found in 'spaces'.",
                span: embedsMod.Span
            );
            return null;
        }

        var companionObj = new JsonObject {
            ["name"] = companionName,
            ["kind"] = "Vector",
            ["space"] = spaceName,
        };

        if (textRowObj["capacity"] is JsonValue capVal) {
            companionObj["capacity"] = capVal.DeepClone();
        }
        if (textRowObj["evicts"] is JsonValue evictsVal) {
            companionObj["evicts"] = evictsVal.DeepClone();
        }
        if (textRowObj["visibility"] is JsonValue visVal) {
            companionObj["visibility"] = visVal.DeepClone();
        }

        var cellsArr = new JsonArray();

        foreach (var cell in textTable.Cells) {
            var cellObj = new JsonObject { ["key"] = cell.Key };

            if (cell.Value is LiteralExpressionNode { Value: string textVal }) {
                if (TryResolveEmbeddedText(textVal, spaceName, scope, cell.Span, out var b64)) {
                    cellObj["value"] = b64;
                }
            }
            cellsArr.AppendNode(item: cellObj);
        }

        if (cellsArr.Count > 0) {
            companionObj["cells"] = cellsArr;
        } else if (!companionObj.ContainsKey(propertyName: "capacity")) {
            companionObj["domain"] = new JsonObject { ["$type"] = "keys" };
        }

        int? capInt = (((textRowObj["capacity"] is JsonValue cv) && cv.TryGetValue<int>(value: out var ci)) ? ci : null);

        ValidateVectorCeilings(companionName!, capInt, cellsArr.Count, spaceInfo.Value.Dimensions, scope, embedsMod.Span, ref totalVectorBytes);

        return companionObj;
    }
    private static JsonObject LowerTransformStatement(TransformStatementNode transform, DocumentScope scope) {
        var call = transform.Transform;

        if (call.Name is "mix" or "mean" or "nearest" or "remember") {
            DocumentLowering.RefuseMisspelledArguments(call: call, scope: scope);
        }

        var inner = DocumentLowering.At(scope: scope, context: typeof(StateTransform), lower: () => call.Name switch {
            "mix" => LowerMixTransform(call: call, scope: scope),
            "mean" => LowerMeanTransform(call: call, scope: scope),
            "nearest" => LowerNearestTransform(call: call, scope: scope),
            "pushRay" => LowerPushRayTransform(call: call, scope: scope),
            "remember" => LowerRememberTransform(call: call, scope: scope),
            _ => ((JsonObject)LowerExpression(call, scope)!),
        });

        return new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = inner,
        };
    }
    private static JsonObject LowerPushRayTransform(CallExpressionNode call, DocumentScope scope) {
        var lowered = ((JsonObject)LowerExpression(call, scope)!);

        if (
            (lowered["from"] is JsonValue fromValue) &&
            fromValue.TryGetValue<string>(value: out var from) &&
            IsPoolBindingField(scope: scope, text: from)
        ) {
            var dot = from.IndexOf(value: '.');

            lowered["from"] = StateChannelRefJsonConverter.ToNode(value: StateChannelRef.OfBindingField(
                binding: from[..dot],
                field: from[(dot + 1)..]
            ));
        }
        return lowered;
    }
    private static JsonObject LowerMixTransform(CallExpressionNode call, DocumentScope scope) {
        var into = "";
        ExpressionNode? termsExpr = null;

        foreach (var arg in call.Arguments) {
            if (string.Equals(a: arg.Name, b: "into", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                into = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
            } else if (string.Equals(a: arg.Name, b: "terms", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                termsExpr = arg.Value;
            }
        }

        if (string.IsNullOrEmpty(value: into) && (call.Arguments.Count > 0)) {
            into = (GetStringOrIdent(expr: call.Arguments[0].Value, scope: scope) ?? "");
        }
        if ((termsExpr is null) && (call.Arguments.Count > 1)) {
            termsExpr = call.Arguments[1].Value;
        }

        var termsArr = new JsonArray();

        if (termsExpr is ArrayExpressionNode arr) {
            if (arr.Elements.Count is < 1 or > 8) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorMixInvalid,
                    message: $"'mix' requires 1 to 8 terms, got {arr.Elements.Count}.",
                    span: call.Span
                );
            }

            foreach (var elem in arr.Elements) {
                if (elem is ObjectExpressionNode obj) {
                    var fromStr = "";
                    var weight = 0;
                    var hasWeight = false;

                    foreach (var prop in obj.Properties) {
                        if (string.Equals(a: prop.Name, b: "from", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                            fromStr = FormatTransformOperand(expr: prop.Value, scope: scope);
                        } else if (string.Equals(a: prop.Name, b: "weight", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                            if (prop.Value is LiteralExpressionNode { Value: long wVal }) {
                                weight = ((int)wVal);
                                hasWeight = true;
                            } else if (prop.Value is UnaryExpressionNode { Operator: "-", Operand: LiteralExpressionNode { Value: long posW } }) {
                                weight = -((int)posW);
                                hasWeight = true;
                            }
                        }
                    }

                    if (!hasWeight || (weight == 0) || (weight < -1000) || (weight > 1000)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.VectorMixInvalid,
                            message: $"mix term weight must be a non-zero integer in [-1000, 1000], got {weight}.",
                            span: elem.Span
                        );
                    }

                    termsArr.AppendNode(item: new JsonObject {
                        ["from"] = fromStr,
                        ["weight"] = weight,
                    });
                }
            }
        } else {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.VectorMixInvalid,
                message: "'mix' requires a 'terms' array.",
                span: call.Span
            );
        }

        return new JsonObject {
            ["$type"] = "mix",
            ["into"] = into,
            ["terms"] = termsArr,
        };
    }
    private static JsonObject LowerMeanTransform(CallExpressionNode call, DocumentScope scope) {
        var from = "";
        var into = "";
        string? where = null;

        foreach (var arg in call.Arguments) {
            if (string.Equals(a: arg.Name, b: "from", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                from = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
            } else if (string.Equals(a: arg.Name, b: "into", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                into = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
            } else if (string.Equals(a: arg.Name, b: "where", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                where = GetStringOrIdent(expr: arg.Value, scope: scope);
            }
        }

        var result = new JsonObject {
            ["$type"] = "mean",
            ["from"] = from,
            ["into"] = into,
        };

        if (!string.IsNullOrEmpty(value: where)) {
            result["where"] = where;
        }

        return result;
    }
    private static JsonObject LowerNearestTransform(CallExpressionNode call, DocumentScope scope) {
        var from = "";
        var query = "";
        var into = "";
        var k = 1;
        string? threshold = null;
        string? where = null;
        string? exclude = null;
        var farthest = false;

        foreach (var arg in call.Arguments) {
            switch (arg.Name?.ToLowerInvariant()) {
                case "from":
                    from = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
                    break;
                case "query":
                    query = FormatTransformOperand(expr: arg.Value, scope: scope);
                    break;
                case "into":
                    into = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
                    break;
                case "k":
                    if (arg.Value is LiteralExpressionNode { Value: long kVal }) {
                        k = ((int)kVal);
                    }
                    break;
                case "threshold":
                    if (arg.Value is LiteralExpressionNode { Value: var tVal }) {
                        threshold = Convert.ToString(tVal, CultureInfo.InvariantCulture);
                    }
                    break;
                case "where":
                    where = GetStringOrIdent(expr: arg.Value, scope: scope);
                    break;
                case "exclude":
                    exclude = GetStringOrIdent(expr: arg.Value, scope: scope);
                    break;
                case "farthest":
                    if (arg.Value is LiteralExpressionNode { Value: bool fVal }) {
                        farthest = fVal;
                    }
                    break;
            }
        }

        if (k is < 1 or > 64) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.VectorOperandMismatch,
                message: $"'nearest' k must be in [1, 64], got {k}.",
                span: call.Span
            );
        }

        var result = new JsonObject {
            ["$type"] = "nearest",
            ["from"] = from,
            ["query"] = query,
            ["into"] = into,
            ["k"] = k,
        };

        if (threshold is not null) {
            result["threshold"] = threshold;
        }
        if (where is not null) {
            result["where"] = where;
        }
        if (exclude is not null) {
            result["exclude"] = exclude;
        }
        if (farthest) {
            result["farthest"] = true;
        }

        return result;
    }
    private static JsonObject LowerRememberTransform(CallExpressionNode call, DocumentScope scope) {
        var into = "";
        var key = "";
        var from = "";
        var unlessWithin = "0.9";

        foreach (var arg in call.Arguments) {
            switch (arg.Name?.ToLowerInvariant()) {
                case "into":
                    into = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
                    break;
                case "key":
                    key = (GetStringOrIdent(expr: arg.Value, scope: scope) ?? "");
                    break;
                case "from":
                    from = FormatTransformOperand(expr: arg.Value, scope: scope);
                    break;
                case "unlesswithin":
                    if (arg.Value is LiteralExpressionNode { Value: var uwVal }) {
                        unlessWithin = (Convert.ToString(uwVal, CultureInfo.InvariantCulture) ?? "0.9");
                    }
                    break;
            }
        }

        if (decimal.TryParse(unlessWithin, NumberStyles.Number, CultureInfo.InvariantCulture, out var uwDec)) {
            if (uwDec is < 0m or > 1m) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorOperandMismatch,
                    message: $"'remember' unlessWithin must be in [0, 1], got {unlessWithin}.",
                    span: call.Span
                );
            }
        }

        return new JsonObject {
            ["$type"] = "remember",
            ["into"] = into,
            ["key"] = key,
            ["from"] = from,
            ["unlessWithin"] = unlessWithin,
        };
    }
    private static string FormatTransformOperand(ExpressionNode expr, DocumentScope scope) {
        // A vector operand written bare is held as text; its literal forms are calls, read as one.
        if (
            (expr is OperandExpressionNode { Text: var written }) &&
            (written.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "embed(") || written.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "vector("))
        ) {
            expr = Puck.Transpiler.Parsing.PuckParser.ParseExpression(source: written);
        }
        if (expr is CallExpressionNode call) {
            if (string.Equals(a: call.Name, b: "embed", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if ((call.Arguments.Count > 0) && (call.Arguments[0].Value is LiteralExpressionNode { Value: string text })) {
                    string? sp = null;

                    if (call.Arguments.Count > 1) {
                        var spArg = (call.Arguments.FirstOrDefault(predicate: a => string.Equals(a: a.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))
                                    ?? call.Arguments[1]);

                        sp = GetStringOrIdent(expr: spArg.Value, scope: scope);
                    }
                    if (TryResolveEmbeddedText(text, sp, scope, call.Span, out var b64)) {
                        return $"vector(\"{b64}\")";
                    }
                }
            } else if (string.Equals(a: call.Name, b: "vector", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                if ((call.Arguments.Count > 0) && (call.Arguments[0].Value is LiteralExpressionNode { Value: string b64 })) {
                    return $"vector(\"{b64}\")";
                }
            }
        }

        // A term the compile-time grammar parsed (`stance[$each]`) is an operand, read as the text it prints to.
        return (GetStringOrIdent(expr: expr, scope: scope) ?? (GetStringOrIdent(
            expr: Puck.Transpiler.Parsing.PuckParser.CreateOperand(column: expr.Column, form: DocumentValueForm.Expression, length: expr.Length, line: expr.Line, offset: expr.Offset, text: Puck.Transpiler.Formatting.PuckPrinter.PrintExpression(expression: expr)),
            scope: scope
        ) ?? ""));
    }
    private static string? GetStringOrIdent(ExpressionNode? expr, DocumentScope scope) => expr switch {
        LiteralExpressionNode { Value: string s } => s,
        IdentifierExpressionNode ident => ident.Name,
        OperandExpressionNode operand => (((LowerOperandArgument(operand: operand, scope: scope) is JsonValue lowered) && lowered.TryGetValue<string>(value: out var spelled)) ? spelled : operand.Text),
        _ => null,
    };

    // Resolve vector payloads in the program, including fold bodies. Preserve the subprogram table and avoid
    // allocating a replacement instruction array when no literal needs embedding.
    private static ExpressionProgram ResolveEmbeddedOperands(ExpressionProgram program, string? expectedSpace, DocumentScope scope, SourceSpan span) {
        var instructions = Resolve(instructions: program.Instructions);
        Subprogram[]? subprograms = null;
        for (var index = 0; (index < program.Subprograms.Count); index++) {
            var subprogram = program.Subprograms[index];
            var resolved = Resolve(instructions: subprogram.Instructions);
            if (!ReferenceEquals(objA: resolved, objB: subprogram.Instructions)) {
                subprograms ??= program.Subprograms.ToArray();
                subprograms[index] = subprogram with { Instructions = resolved };
            }
        }
        return ((ReferenceEquals(objA: instructions, objB: program.Instructions) && (subprograms is null))
            ? program : program with { Instructions = instructions, Subprograms = (subprograms ?? program.Subprograms) });

        IReadOnlyList<Instruction> Resolve(IReadOnlyList<Instruction> instructions) {
            Instruction[]? newTokens = null;
            for (var index = 0; (index < instructions.Count); index++) {
                var token = instructions[index];
                if (token.Payload is InstructionPayload.Vector vecCall) {
                    var left = vecCall.Left;
                    var right = vecCall.Right;

                    string? leftRowSpace = null;
                    string? rightRowSpace = null;

                    if (scope.Annotations.TryGetValue(key: "WorldDocumentRoot", value: out var rObj) && (rObj is JsonObject root)) {
                        if (left is VectorOperand.Cell lc) {
                            leftRowSpace = FindRowSpace(rootObj: root, rowName: lc.Name.Spelling);
                        }
                        if (right is VectorOperand.Cell rc) {
                            rightRowSpace = FindRowSpace(rootObj: root, rowName: rc.Name.Spelling);
                        }
                    }

                    if (left is VectorOperand.Embed leftEmbed) {
                        var sp = (leftEmbed.Space ?? (expectedSpace ?? rightRowSpace));

                        if (TryResolveEmbeddedText(leftEmbed.Text, sp, scope, span, out var b64)) {
                            left = new VectorOperand.Literal(Value: b64);
                            newTokens ??= instructions.ToArray();
                        }
                    }

                    if (right is VectorOperand.Embed rightEmbed) {
                        var sp = (rightEmbed.Space ?? (expectedSpace ?? leftRowSpace));

                        if (TryResolveEmbeddedText(rightEmbed.Text, sp, scope, span, out var b64)) {
                            right = new VectorOperand.Literal(Value: b64);
                            newTokens ??= instructions.ToArray();
                        }
                    }

                    if (newTokens is not null) { newTokens[index] = Instruction.Vector(operation: token.Operation, left: left, right: right); }
                }
            }

            return (newTokens ?? instructions);
        }
    }
}
