using System.Globalization;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.World.Transpiler.Sql;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    internal sealed class SqlTableSchema {
        public required string TableName { get; init; }
        public required string PrimaryKeyColumn { get; init; }
        public required string PrimaryKeyType { get; init; }
        public required bool IsOrdered { get; init; }
        public required string? KeyReferencesTable { get; init; }
        public required int? Capacity { get; init; }
        public required List<SqlColumnSchema> Columns { get; init; }
        public required List<SqlColumnSchema> AllDeclaredColumns { get; init; }
        public bool IsSlot { get; init; }
        public bool IsEvicts { get; init; }

        public HashSet<string> InsertedKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, JsonObject> ColumnRowObjects { get; } = new(StringComparer.OrdinalIgnoreCase);
        public JsonObject? PrimaryKeyRowObject { get; set; }

        public bool IsKeyOnly => (!IsSlot && Columns.Count == 0);

        public SqlColumnSchema? FindColumn(string colName) {
            return Columns.Find(c => string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));
        }

        public string GetRowNameForColumn(string colName) {
            if (IsSlot) {
                return TableName;
            }
            var col = FindColumn(colName);
            if (col?.ExplicitRowName is not null) {
                return col.ExplicitRowName;
            }
            if (IsKeyOnly) {
                return TableName;
            }
            return CombineTableAndColumn(TableName, colName);
        }

        public IReadOnlyList<string> GetAllRowNames() {
            if (IsKeyOnly || IsSlot) {
                return [TableName];
            }
            return Columns.Select(c => c.RowName).ToList();
        }
    }

    internal sealed class SqlColumnSchema {
        public required string ColumnName { get; init; }
        public required string RowName { get; init; }
        public required string Kind { get; init; }
        public required bool IsNotNull { get; init; }
        public required string? ExplicitRowName { get; init; }
        public required object? DefaultValue { get; init; }
        public required SqlCheckConstraint? Check { get; init; }
        public required SqlAdvanceClause? Advance { get; init; }
        public required string? DynamicsName { get; init; }
        public required string? OverflowPolicy { get; init; }
        public required string? ReferencesTable { get; init; }
        public required bool IsOrdered { get; init; }
        public required SourceSpan Span { get; init; }
        public string? Space { get; init; }
    }

    private static Dictionary<string, SqlTableSchema> GetOrCreateSqlTableRegistry(DocumentScope scope) {
        const string Key = "SqlTableRegistry";
        if (scope.Annotations.TryGetValue(Key, out var obj) && obj is Dictionary<string, SqlTableSchema> dict) {
            return dict;
        }

        var newDict = new Dictionary<string, SqlTableSchema>(StringComparer.OrdinalIgnoreCase);
        scope.Annotations[Key] = newDict;
        return newDict;
    }

    private static HashSet<string> GetOrCreateDeclaredRowNames(DocumentScope scope) {
        const string Key = "DeclaredWorldRowNames";
        if (scope.Annotations.TryGetValue(Key, out var obj) && obj is HashSet<string> set) {
            return set;
        }

        var newSet = new HashSet<string>(StringComparer.Ordinal);
        scope.Annotations[Key] = newSet;
        return newSet;
    }

    internal static void LowerStateSqlBlock(EmbeddedBlockNode block, JsonObject parent, DocumentScope scope) {
        using var evaluation = scope.Budget.Enter(span: block.Span);
        scope.Annotations["WorldDocumentRoot"] = parent;

        var lexer = new StateSqlLexer(
            source: block.Body,
            baseOffset: block.BodyOffset,
            baseLine: block.BodyLine,
            baseColumn: block.BodyColumn
        );
        var tokens = lexer.Tokenize(scope.Diagnostics);

        var parser = new StateSqlParser(tokens, scope.Diagnostics);
        var statements = parser.ParseStatements();

        var tableRegistry = GetOrCreateSqlTableRegistry(scope);
        var declaredRowNames = GetOrCreateDeclaredRowNames(scope);

        var hasStateStatements = statements.Any(s => s is SqlCreateTableStatement or SqlDeclareSlotStatement or SqlInsertStatement or SqlCreatePolicyStatement);
        JsonArray? worldArr = null;

        if (hasStateStatements) {
            if (parent["state"] is not JsonObject stateObj) {
                stateObj = [];
                parent["state"] = stateObj;
            }

            if (stateObj.ContainsKey("world")) {
                if (scope.Annotations.ContainsKey("StateWorldArrayForm") || (stateObj["world"] is not JsonArray existingArr)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateWorldSectionMixed,
                        message: "'state.world' is authored more than once — write it either as the array form ('world [ ]') or the declaration block ('world { }'), never both",
                        span: block.Span
                    );
                    return;
                }
                worldArr = existingArr;
            } else {
                worldArr = [];
                stateObj["world"] = worldArr;
            }

            scope.Annotations["StateWorldSqlForm"] = true;
        }

        JsonArray? rulesArr = null;
        if (statements.Any(s => s is SqlCreateRuleStatement)) {
            if (parent["rules"] is not JsonArray existingRules) {
                existingRules = [];
                parent["rules"] = existingRules;
            }
            rulesArr = existingRules;
        }

        // Process statements strictly in source author order!
        foreach (var stmt in statements) {
            switch (stmt) {
                case SqlCreateTableStatement ct:
                    LowerCreateTable(ct, worldArr!, tableRegistry, declaredRowNames, scope);
                    break;
                case SqlInsertStatement ins:
                    LowerInsertStatement(ins, tableRegistry, scope);
                    break;
                case SqlDeclareSlotStatement ds:
                    LowerDeclareSlot(ds, worldArr!, tableRegistry, declaredRowNames, scope);
                    break;
                case SqlCreatePolicyStatement cp:
                    LowerCreatePolicy(cp, worldArr!, tableRegistry, scope);
                    break;
                case SqlCreateRuleStatement cr:
                    LowerCreateRule(cr, rulesArr!, tableRegistry, scope);
                    break;
                case SqlInsertSelectStatement iss:
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlUnsupportedClause,
                        message: "Nearest queries (INSERT ... SELECT ...) are only permitted inside rule bodies.",
                        span: iss.Span
                    );
                    break;
            }
        }

        foreach (var table in tableRegistry.Values) {
            if (table.IsSlot || table.Capacity.HasValue || table.KeyReferencesTable is not null) {
                continue;
            }
            if (table.InsertedKeys.Count == 0) {
                if (table.IsKeyOnly && table.PrimaryKeyRowObject is not null) {
                    table.PrimaryKeyRowObject["domain"] = new JsonObject { ["$type"] = "keys" };
                } else {
                    foreach (var rowObj in table.ColumnRowObjects.Values) {
                        rowObj["domain"] = new JsonObject { ["$type"] = "keys" };
                    }
                }
            }
        }
    }

    private static void LowerCreateTable(
        SqlCreateTableStatement ct,
        JsonArray worldArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        HashSet<string> declaredRowNames,
        DocumentScope scope
    ) {
        SqlColumnDefinition? pkCol = null;
        var nonKeyCols = new List<SqlColumnDefinition>();

        foreach (var col in ct.Columns) {
            if (col.IsPrimaryKey) {
                pkCol = col;
            } else {
                nonKeyCols.Add(col);
            }
        }

        if (pkCol is null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Table '{ct.TableName}' must declare at least one column as PRIMARY KEY",
                span: ct.Span
            );
            return;
        }

        var colSchemas = new List<SqlColumnSchema>();
        var allDeclared = new List<SqlColumnSchema>();

        foreach (var col in ct.Columns) {
            var kind = MapSqlTypeToKind(col.DeclaredType);
            if (kind is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedType,
                    message: $"Column '{col.Name}' declared type '{col.DeclaredType}' is unrecognized or unsupported; admitted types are INT, FIXED, BOOL, TEXT, VECTOR.",
                    span: col.Span
                );
                kind = "Text";
            }

            var rowName = col.ExplicitRowName ?? (col.IsPrimaryKey ? ct.TableName : CombineTableAndColumn(ct.TableName, col.Name));
            var schema = new SqlColumnSchema {
                ColumnName = col.Name,
                RowName = rowName,
                Kind = kind,
                IsNotNull = col.IsNotNull,
                ExplicitRowName = col.ExplicitRowName,
                DefaultValue = col.DefaultValue,
                Check = col.Check,
                Advance = col.Advance,
                DynamicsName = col.DynamicsName,
                OverflowPolicy = col.OverflowPolicy,
                ReferencesTable = col.ReferencesTable,
                IsOrdered = col.IsOrdered,
                Span = col.Span,
                Space = col.Space
            };
            allDeclared.Add(schema);
            if (!col.IsPrimaryKey) {
                colSchemas.Add(schema);
            }
        }

        var tableSchema = new SqlTableSchema {
            TableName = ct.TableName,
            PrimaryKeyColumn = pkCol.Name,
            PrimaryKeyType = pkCol.DeclaredType,
            IsOrdered = ct.IsOrdered || pkCol.IsOrdered,
            KeyReferencesTable = pkCol.ReferencesTable,
            Capacity = ct.Capacity,
            Columns = colSchemas,
            AllDeclaredColumns = allDeclared,
            IsEvicts = ct.IsEvicts
        };
        tableRegistry[ct.TableName] = tableSchema;

        // Emit rows directly into worldArr in source statement order
        if (tableSchema.IsKeyOnly) {
            if (!declaredRowNames.Add(ct.TableName)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                    message: $"a state row named '{ct.TableName}' is already declared in this world",
                    span: ct.Span
                );
            }

            var rowObj = new JsonObject {
                ["name"] = ct.TableName,
                ["kind"] = "Bool"
            };

            if (tableSchema.KeyReferencesTable is not null) {
                var domainObj = new JsonObject {
                    ["$type"] = "keysOf",
                    ["row"] = tableSchema.KeyReferencesTable
                };
                if (tableSchema.IsOrdered) {
                    domainObj["ordered"] = true;
                    rowObj["cells"] = new JsonArray();
                }
                rowObj["domain"] = domainObj;
            }

            if (ct.Capacity.HasValue) {
                rowObj["capacity"] = ct.Capacity.Value;
            }
            if (ct.IsEvicts) {
                rowObj["evicts"] = true;
            }

            tableSchema.PrimaryKeyRowObject = rowObj;
            worldArr.AppendNode(item: rowObj);
        } else {
            foreach (var col in colSchemas) {
                if (!declaredRowNames.Add(col.RowName)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                        message: $"a state row named '{col.RowName}' is already declared in this world",
                        span: col.Span
                    );
                }

                var rowObj = new JsonObject {
                    ["name"] = col.RowName,
                    ["kind"] = col.Kind
                };

                if (string.Equals(col.Kind, "Vector", StringComparison.OrdinalIgnoreCase)) {
                    var space = col.Space ?? FindDefaultSpace(scope.Annotations["WorldDocumentRoot"] as JsonObject ?? new JsonObject());
                    if (space is not null) {
                        rowObj["space"] = space;
                    }
                }

                if (tableSchema.KeyReferencesTable is not null) {
                    var domainObj = new JsonObject {
                        ["$type"] = "keysOf",
                        ["row"] = tableSchema.KeyReferencesTable
                    };
                    if (col.IsOrdered || tableSchema.IsOrdered) {
                        domainObj["ordered"] = true;
                    }
                    rowObj["domain"] = domainObj;
                }

                if (col.Check is not null) {
                    ApplyCheckConstraint(col.Check, rowObj, col.Kind, col.Span, scope);
                }
                if (col.OverflowPolicy is not null && string.Equals(col.OverflowPolicy, "Saturate", StringComparison.OrdinalIgnoreCase)) {
                    rowObj["overflow"] = "Saturate";
                }
                if (col.Advance is not null) {
                    ApplyAdvanceClause(col.Advance, rowObj, scope);
                }
                if (col.DynamicsName is not null) {
                    rowObj["dynamics"] = new JsonObject {
                        ["row"] = col.DynamicsName
                    };
                }
                if (ct.Capacity.HasValue) {
                    rowObj["capacity"] = ct.Capacity.Value;
                }
                if (ct.IsEvicts) {
                    rowObj["evicts"] = true;
                }

                tableSchema.ColumnRowObjects[col.ColumnName] = rowObj;
                worldArr.AppendNode(item: rowObj);
            }
        }
    }

    private static void LowerInsertStatement(
        SqlInsertStatement ins,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        if (!tableRegistry.TryGetValue(ins.TableName, out var table)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"INSERT targets unknown table '{ins.TableName}'",
                span: ins.Span
            );
            return;
        }

        if (ins.Columns is not null) {
            foreach (var colName in ins.Columns) {
                if (!string.Equals(colName, table.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase) &&
                    table.FindColumn(colName) is null) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                        message: $"Table '{table.TableName}' has no column named '{colName}'",
                        span: ins.Span
                    );
                }
            }
        }

        foreach (var rowVals in ins.ValuesRows) {
            string? rowKey = null;
            var rowColMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            if (ins.Columns is not null) {
                if (rowVals.Count != ins.Columns.Count) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSyntaxError,
                        message: $"INSERT into '{table.TableName}' provides {rowVals.Count} values for {ins.Columns.Count} columns",
                        span: ins.Span
                    );
                }
                for (var i = 0; i < ins.Columns.Count && i < rowVals.Count; i++) {
                    var colName = ins.Columns[i];
                    var val = EvaluateSqlLiteral(rowVals[i]);
                    if (string.Equals(colName, table.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)) {
                        rowKey = FormatKeyString(val);
                    } else {
                        rowColMap[colName] = val;
                    }
                }
            } else {
                var declared = table.AllDeclaredColumns;
                if (rowVals.Count != declared.Count) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSyntaxError,
                        message: $"INSERT into '{table.TableName}' provides {rowVals.Count} values for {declared.Count} columns",
                        span: ins.Span
                    );
                }
                for (var i = 0; i < declared.Count && i < rowVals.Count; i++) {
                    var colDef = declared[i];
                    var val = EvaluateSqlLiteral(rowVals[i]);
                    if (string.Equals(colDef.ColumnName, table.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)) {
                        rowKey = FormatKeyString(val);
                    } else {
                        rowColMap[colDef.ColumnName] = val;
                    }
                }
            }

            if (rowKey is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlMissingRequiredColumn,
                    message: $"INSERT INTO '{table.TableName}' row is missing PRIMARY KEY column '{table.PrimaryKeyColumn}'",
                    span: ins.Span
                );
                continue;
            }

            if (rowKey.StartsWith('$')) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationReservedKey,
                    message: $"State cell key '{rowKey}' in table '{table.TableName}' uses reserved prefix '$'",
                    span: ins.Span
                );
            }
            if (rowKey.Contains('.')) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlSyntaxError,
                    message: $"State cell key '{rowKey}' in table '{table.TableName}' cannot contain '.'",
                    span: ins.Span
                );
            }

            if (!table.InsertedKeys.Add(rowKey)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                    message: $"Duplicate key '{rowKey}' inserted into table '{table.TableName}'",
                    span: ins.Span
                );
                continue;
            }

            if (table.Capacity.HasValue && table.InsertedKeys.Count > table.Capacity.Value) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationCapacityTooSmall,
                    message: $"table '{table.TableName}' declares capacity {table.Capacity.Value} smaller than its {table.InsertedKeys.Count} authored cells",
                    span: ins.Span
                );
            }

            if (table.IsKeyOnly) {
                if (table.PrimaryKeyRowObject is not null) {
                    var cellsArr = (table.PrimaryKeyRowObject["cells"] as JsonArray);
                    if (cellsArr is null) {
                        cellsArr = [];
                        table.PrimaryKeyRowObject["cells"] = cellsArr;
                    }
                    cellsArr.AppendNode(item: new JsonObject {
                        ["key"] = rowKey,
                        ["value"] = true
                    });
                }
            } else {
                foreach (var col in table.Columns) {
                    if (table.ColumnRowObjects.TryGetValue(col.ColumnName, out var rowObj)) {
                        var hasAuthored = rowColMap.TryGetValue(col.ColumnName, out var authoredVal);

                        if (hasAuthored) {
                            if (authoredVal is null) {
                                if (col.IsNotNull) {
                                    scope.Diagnostics.ReportError(
                                        code: PuckDiagnosticCodes.SqlMissingRequiredColumn,
                                        message: $"Table '{table.TableName}' row '{rowKey}' cannot set NOT NULL column '{col.ColumnName}' to NULL",
                                        span: ins.Span
                                    );
                                }
                                // NULL is absent cell: omit cell
                            } else {
                                var cellVal = LowerScalarJsonValue(authoredVal, col.Kind, col.Span, scope);
                                if (cellVal is not null) {
                                    var cellsArr = (rowObj["cells"] as JsonArray);
                                    if (cellsArr is null) {
                                        cellsArr = [];
                                        rowObj["cells"] = cellsArr;
                                    }
                                    cellsArr.AppendNode(item: new JsonObject {
                                        ["key"] = rowKey,
                                        ["value"] = cellVal
                                    });
                                }
                            }
                        } else if (col.DefaultValue is not null) {
                            var defVal = LowerScalarJsonValue(col.DefaultValue, col.Kind, col.Span, scope);
                            if (defVal is not null) {
                                var cellsArr = (rowObj["cells"] as JsonArray);
                                if (cellsArr is null) {
                                    cellsArr = [];
                                    rowObj["cells"] = cellsArr;
                                }
                                cellsArr.AppendNode(item: new JsonObject {
                                    ["key"] = rowKey,
                                    ["value"] = defVal
                                });
                            }
                        } else if (col.IsNotNull) {
                            scope.Diagnostics.ReportError(
                                code: PuckDiagnosticCodes.SqlMissingRequiredColumn,
                                message: $"Table '{table.TableName}' row '{rowKey}' is missing required NOT NULL column '{col.ColumnName}' with no DEFAULT value.",
                                span: col.Span
                            );
                        }
                    }
                }
            }
        }
    }

    private static void LowerDeclareSlot(
        SqlDeclareSlotStatement ds,
        JsonArray worldArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        HashSet<string> declaredRowNames,
        DocumentScope scope
    ) {
        if (!declaredRowNames.Add(ds.Name)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationDuplicateName,
                message: $"a state row named '{ds.Name}' is already declared in this world",
                span: ds.Span
            );
        }

        var slotSpace = ds.Space;
        var declType = ds.DeclaredType;
        if (slotSpace is null && declType.StartsWith("VECTOR(", StringComparison.OrdinalIgnoreCase) && declType.EndsWith(')')) {
            slotSpace = declType[7..^1].Trim();
            declType = "VECTOR";
        }

        var kind = MapSqlTypeToKind(declType);
        if (kind is null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedType,
                message: $"Slot '{ds.Name}' declared type '{ds.DeclaredType}' is unrecognized or unsupported; admitted types are INT, FIXED, BOOL, TEXT, VECTOR.",
                span: ds.Span
            );
            kind = "Text";
        }

        var rowObj = new JsonObject {
            ["name"] = ds.Name,
            ["kind"] = kind
        };

        if (string.Equals(kind, "Vector", StringComparison.OrdinalIgnoreCase)) {
            var space = slotSpace ?? FindDefaultSpace(scope.Annotations["WorldDocumentRoot"] as JsonObject ?? new JsonObject());
            if (space is not null) {
                rowObj["space"] = space;
            }
        }

        if (ds.DefaultValue is not null) {
            var rawVal = EvaluateSqlLiteral(ds.DefaultValue);
            if (rawVal is not null) {
                var cellVal = LowerScalarJsonValue(rawVal, kind, ds.Span, scope);
                if (cellVal is not null) {
                    rowObj["value"] = cellVal;
                }
            }
        }

        if (ds.Check is not null) {
            ApplyCheckConstraint(ds.Check, rowObj, kind, ds.Span, scope);
        }
        if (ds.Advance is not null) {
            ApplyAdvanceClause(ds.Advance, rowObj, scope);
        }

        var slotCol = new SqlColumnSchema {
            ColumnName = "value",
            RowName = ds.Name,
            Kind = kind,
            IsNotNull = false,
            ExplicitRowName = null,
            DefaultValue = null,
            Check = ds.Check,
            Advance = ds.Advance,
            DynamicsName = null,
            OverflowPolicy = null,
            ReferencesTable = null,
            IsOrdered = false,
            Span = ds.Span,
            Space = slotSpace
        };

        tableRegistry[ds.Name] = new SqlTableSchema {
            TableName = ds.Name,
            PrimaryKeyColumn = "",
            PrimaryKeyType = "",
            IsOrdered = false,
            KeyReferencesTable = null,
            Capacity = null,
            IsSlot = true,
            Columns = [slotCol],
            AllDeclaredColumns = [slotCol],
            IsEvicts = false
        };

        worldArr.AppendNode(item: rowObj);
    }

    private static void LowerCreatePolicy(
        SqlCreatePolicyStatement cp,
        JsonArray worldArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        var targetRows = new List<JsonObject>();

        if (tableRegistry.TryGetValue(cp.Target, out var tableSchema)) {
            var rowNames = new HashSet<string>(tableSchema.GetAllRowNames(), StringComparer.Ordinal);
            foreach (var node in worldArr) {
                if (node is JsonObject rowObj &&
                    rowObj["name"]?.ToString() is string rName &&
                    rowNames.Contains(rName)) {
                    targetRows.Add(rowObj);
                }
            }
        } else {
            foreach (var node in worldArr) {
                if (node is JsonObject rowObj &&
                    rowObj["name"]?.ToString() is string rName &&
                    string.Equals(rName, cp.Target, StringComparison.Ordinal)) {
                    targetRows.Add(rowObj);
                }
            }
        }

        if (targetRows.Count == 0) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"CREATE POLICY target '{cp.Target}' resolves to no declared row in state.world",
                span: cp.Span
            );
            return;
        }

        foreach (var rowObj in targetRows) {
            var visObj = (rowObj["visibility"] as JsonObject) ?? new JsonObject();

            if (cp.ReadersFrom is not null) {
                visObj["readersFrom"] = cp.ReadersFrom;
            } else if (cp.Readers is not null) {
                var readersArr = new JsonArray();
                foreach (var r in cp.Readers) {
                    readersArr.AppendNode(item: JsonValue.Create(r));
                }
                visObj["readers"] = readersArr;
            }

            rowObj["visibility"] = visObj;
        }
    }

    private static void LowerCreateRule(
        SqlCreateRuleStatement cr,
        JsonArray rulesArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        var ruleObj = new JsonObject {
            ["name"] = cr.Name,
            ["mode"] = cr.FiringMode
        };

        var setBasedStatements = new List<SqlRuleBodyStatement>();
        FindSetBasedStatements(cr.Statements, tableRegistry, setBasedStatements);

        if (setBasedStatements.Count > 0) {
            if (cr.Statements.Count != 1 || cr.Statements[0] is not (SqlUpdateStatement or SqlDeleteStatement)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedClause,
                    message: "A set-based UPDATE or DELETE must be the sole statement of the rule because 'forEach' and 'gate' apply to the entire rule in Puck",
                    span: cr.Span
                );
                return;
            }
        }

        var effectsArr = new JsonArray();
        string? forEachRow = null;
        JsonObject? ruleGate = null;

        foreach (var stmt in cr.Statements) {
            LowerRuleStatement(
                stmt: stmt,
                effectsArr: effectsArr,
                forEachRow: ref forEachRow,
                ruleGate: ref ruleGate,
                currentTable: null,
                tableRegistry: tableRegistry,
                scope: scope
            );
        }

        if (forEachRow is not null) {
            ruleObj["forEach"] = forEachRow;
        }
        if (ruleGate is not null) {
            ruleObj["gate"] = ruleGate;
        }
        ruleObj["effects"] = effectsArr;

        rulesArr.AppendNode(item: ruleObj);
    }

    private static void FindSetBasedStatements(
        IReadOnlyList<SqlRuleBodyStatement> stmts,
        Dictionary<string, SqlTableSchema> tableRegistry,
        List<SqlRuleBodyStatement> results
    ) {
        foreach (var stmt in stmts) {
            switch (stmt) {
                case SqlUpdateStatement u:
                    tableRegistry.TryGetValue(u.TableName, out var table);
                    var isSlot = (table?.IsSlot == true);
                    var pkCol = table?.PrimaryKeyColumn ?? "id";
                    if (!isSlot && !TryExtractSingleKeyLiteral(u.Where, pkCol, out _)) {
                        results.Add(u);
                    }
                    break;
                case SqlDeleteStatement d:
                    tableRegistry.TryGetValue(d.TableName, out var dTable);
                    var dPkCol = dTable?.PrimaryKeyColumn ?? "id";
                    if (!TryExtractSingleKeyLiteral(d.Where, dPkCol, out _)) {
                        results.Add(d);
                    }
                    break;
                case SqlAtomicStatement a:
                    FindSetBasedStatements(a.Statements, tableRegistry, results);
                    break;
                case SqlIfStatement i:
                    FindSetBasedStatements(i.ThenStatements, tableRegistry, results);
                    foreach (var (_, elifStmts) in i.ElsifStatements) {
                        FindSetBasedStatements(elifStmts, tableRegistry, results);
                    }
                    if (i.ElseStatements is not null) {
                        FindSetBasedStatements(i.ElseStatements, tableRegistry, results);
                    }
                    break;
            }
        }
    }

    private static void LowerRuleStatement(
        SqlRuleBodyStatement stmt,
        JsonArray effectsArr,
        ref string? forEachRow,
        ref JsonObject? ruleGate,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        switch (stmt) {
            case SqlUpdateStatement update:
                LowerUpdateStatement(update, effectsArr, ref forEachRow, ref ruleGate, tableRegistry, scope);
                break;
            case SqlInsertStatement insert:
                LowerInsertRuleStatement(insert, effectsArr, tableRegistry, scope);
                break;
            case SqlInsertSelectStatement insertSelect:
                LowerInsertSelectStatement(insertSelect, effectsArr, tableRegistry, scope);
                break;
            case SqlDeleteStatement delete:
                LowerDeleteStatement(delete, effectsArr, ref forEachRow, ref ruleGate, tableRegistry, scope);
                break;
            case SqlAtomicStatement atomic:
                LowerAtomicStatement(atomic, effectsArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
                break;
            case SqlIfStatement ifStmt:
                LowerIfRuleStatement(ifStmt, effectsArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
                break;
        }
    }

    private static void LowerUpdateStatement(
        SqlUpdateStatement update,
        JsonArray effectsArr,
        ref string? forEachRow,
        ref JsonObject? ruleGate,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        if (!tableRegistry.TryGetValue(update.TableName, out var table)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"UPDATE targets unknown table '{update.TableName}'",
                span: update.Span
            );
            return;
        }

        var pkCol = table.PrimaryKeyColumn;
        var isSlot = table.IsSlot;

        string? targetKey = null;
        var isSingleKeyLiteral = !isSlot && TryExtractSingleKeyLiteral(update.Where, pkCol, out targetKey);

        if (!isSingleKeyLiteral && !isSlot) {
            var notNullCol = table.Columns.FirstOrDefault(c => c.IsNotNull || c.DefaultValue is not null);
            if (notNullCol is null && table.Columns.Count > 0) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedClause,
                    message: $"Per-key rule iterating table '{table.TableName}' requires at least one NOT NULL column to iterate all keys; all columns are nullable.",
                    span: update.Span
                );
                return;
            }

            var chosenRow = notNullCol?.RowName ?? table.Columns.FirstOrDefault()?.RowName ?? table.TableName;
            forEachRow ??= chosenRow;
            targetKey = "$each";

            if (update.Where is not null) {
                var gatePred = LowerSqlExpressionToPredicate(
                    expr: update.Where,
                    currentTable: update.TableName,
                    tableRegistry: tableRegistry,
                    isForEach: true,
                    targetKey: "$each",
                    scope: scope
                );
                ruleGate = CombineRuleGate(ruleGate, gatePred);
            }

            var writtenCols = new HashSet<string>(update.Assignments.Select(a => a.Column), StringComparer.OrdinalIgnoreCase);
            CheckSelfReferentialUpdate(update.Where, update.TableName, writtenCols, scope);
            foreach (var assign in update.Assignments) {
                CheckSelfReferentialUpdate(assign.Value, update.TableName, writtenCols, scope);
            }
        } else if (isSlot && update.Where is not null) {
            var gatePred = LowerSqlExpressionToPredicate(
                expr: update.Where,
                currentTable: null,
                tableRegistry: tableRegistry,
                isForEach: false,
                targetKey: null,
                scope: scope
            );
            ruleGate = CombineRuleGate(ruleGate, gatePred);
        }

        var updateEffects = new JsonArray();

        foreach (var assign in update.Assignments) {
            string rowName;
            var destKind = "Int";
            if (isSlot) {
                rowName = update.TableName;
                destKind = table.Columns.FirstOrDefault()?.Kind ?? "Int";
            } else {
                if (string.Equals(assign.Column, table.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                        message: $"Cannot update PRIMARY KEY column '{assign.Column}' of table '{table.TableName}'",
                        span: assign.Span
                    );
                    continue;
                }
                var col = table.FindColumn(assign.Column);
                if (col is null) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                        message: $"Table '{table.TableName}' has no column named '{assign.Column}'",
                        span: assign.Span
                    );
                    continue;
                }
                rowName = col.RowName;
                destKind = col.Kind;
            }
            var effectType = (assign.Op == SqlAssignmentOp.Add) ? "addState" : "setState";

            var effectObj = new JsonObject {
                ["$type"] = effectType,
                ["state"] = rowName
            };
            if (targetKey is not null) {
                effectObj["key"] = targetKey;
            }

            ApplySqlExpressionToEffect(
                expr: assign.Value,
                effectObj: effectObj,
                currentTable: update.TableName,
                destKind: destKind,
                tableRegistry: tableRegistry,
                isForEach: (!isSingleKeyLiteral && !isSlot),
                targetKey: targetKey,
                scope: scope
            );
            updateEffects.AppendNode(item: effectObj);
        }

        if (updateEffects.Count > 1) {
            effectsArr.AppendNode(item: new JsonObject {
                ["$type"] = "transaction",
                ["effects"] = updateEffects
            });
        } else if (updateEffects.Count == 1) {
            var single = updateEffects[0];
            updateEffects.RemoveAt(0);
            effectsArr.AppendNode(item: single!);
        }
    }

    private static void LowerInsertRuleStatement(
        SqlInsertStatement insert,
        JsonArray effectsArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        if (!tableRegistry.TryGetValue(insert.TableName, out var table)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"INSERT targets unknown table '{insert.TableName}'",
                span: insert.Span
            );
            return;
        }
        var pkCol = table.PrimaryKeyColumn;

        foreach (var rowVals in insert.ValuesRows) {
            string? keyVal = null;
            var colVals = new Dictionary<string, SqlExpression>(StringComparer.OrdinalIgnoreCase);

            if (insert.Columns is not null) {
                for (var i = 0; i < insert.Columns.Count && i < rowVals.Count; i++) {
                    var colName = insert.Columns[i];
                    if (string.Equals(colName, pkCol, StringComparison.OrdinalIgnoreCase)) {
                        keyVal = FormatKeyString(EvaluateSqlLiteral(rowVals[i]));
                    } else {
                        colVals[colName] = rowVals[i];
                    }
                }
            } else {
                var allCols = table.AllDeclaredColumns;
                for (var i = 0; i < allCols.Count && i < rowVals.Count; i++) {
                    var colDef = allCols[i];
                    if (string.Equals(colDef.ColumnName, pkCol, StringComparison.OrdinalIgnoreCase)) {
                        keyVal = FormatKeyString(EvaluateSqlLiteral(rowVals[i]));
                    } else {
                        colVals[colDef.ColumnName] = rowVals[i];
                    }
                }
            }

            if (keyVal is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlMissingRequiredColumn,
                    message: $"INSERT into table '{insert.TableName}' requires PRIMARY KEY column '{pkCol}'",
                    span: insert.Span
                );
                continue;
            }

            var stepEffects = new JsonArray();

            if (table.IsKeyOnly) {
                stepEffects.AppendNode(item: new JsonObject {
                    ["$type"] = "setState",
                    ["state"] = insert.TableName,
                    ["key"] = keyVal,
                    ["value"] = 1L
                });
            } else {
                foreach (var col in table.Columns) {
                    if (colVals.TryGetValue(col.ColumnName, out var expr)) {
                        var eff = new JsonObject {
                            ["$type"] = "setState",
                            ["state"] = col.RowName,
                            ["key"] = keyVal
                        };
                        ApplySqlExpressionToEffect(expr, eff, insert.TableName, col.Kind, tableRegistry, isForEach: false, targetKey: keyVal, scope: scope);
                        stepEffects.AppendNode(item: eff);
                    } else if (col.DefaultValue is not null) {
                        var eff = new JsonObject {
                            ["$type"] = "setState",
                            ["state"] = col.RowName,
                            ["key"] = keyVal
                        };
                        var defNode = LowerScalarJsonValue(col.DefaultValue, col.Kind, col.Span, scope);
                        if (defNode is not null) {
                            eff["value"] = defNode;
                            stepEffects.AppendNode(item: eff);
                        }
                    } else if (col.IsNotNull) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.SqlMissingRequiredColumn,
                            message: $"Table '{table.TableName}' row '{keyVal}' is missing required NOT NULL column '{col.ColumnName}' with no DEFAULT value.",
                            span: insert.Span
                        );
                    }
                }
            }

            if (stepEffects.Count > 1) {
                effectsArr.AppendNode(item: new JsonObject {
                    ["$type"] = "transaction",
                    ["effects"] = stepEffects
                });
            } else if (stepEffects.Count == 1) {
                var single = stepEffects[0];
                stepEffects.RemoveAt(0);
                effectsArr.AppendNode(item: single!);
            }
        }
    }

    private static void LowerInsertSelectStatement(
        SqlInsertSelectStatement stmt,
        JsonArray effectsArr,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        tableRegistry.TryGetValue(stmt.TargetTable, out var targetTable);
        var nativeTarget = (targetTable is null) ? FindNativeWorldRow(stmt.TargetTable, scope) : null;
        if (targetTable is null && nativeTarget is null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"INSERT targets unknown table '{stmt.TargetTable}'",
                span: stmt.Span
            );
            return;
        }

        tableRegistry.TryGetValue(stmt.FromTable, out var fromTable);
        var nativeFrom = (fromTable is null) ? FindNativeWorldRow(stmt.FromTable, scope) : null;
        if (fromTable is null && nativeFrom is null) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"SELECT targets unknown table '{stmt.FromTable}'",
                span: stmt.Span
            );
            return;
        }

        if (stmt.SelectList.Count != 2) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: "Expected 2 items in SELECT list for nearest query: key column and score function (similarity or dot)",
                span: stmt.Span
            );
            return;
        }

        var scoreExpr = stmt.SelectList[1];
        if (scoreExpr is not SqlFunctionCallExpression scoreFunc ||
            (scoreFunc.FunctionName != "similarity" && scoreFunc.FunctionName != "dot")) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: "Second item in SELECT list must be score function 'similarity(...)' or 'dot(...)'",
                span: scoreExpr.Span
            );
            return;
        }

        var intoRowName = stmt.TargetTable;
        if (targetTable is not null) {
            if (targetTable.IsSlot) {
                intoRowName = targetTable.TableName;
            } else {
                SqlColumnSchema? scoreCol = null;
                if (stmt.TargetColumns is not null && stmt.TargetColumns.Count >= 2) {
                    scoreCol = targetTable.FindColumn(stmt.TargetColumns[1]);
                }
                scoreCol ??= targetTable.Columns.FirstOrDefault(c => c.Kind is "Fixed" or "Int");

                if (scoreCol is not null) {
                    intoRowName = scoreCol.RowName;
                    if (scoreFunc.FunctionName == "similarity" && scoreCol.Kind != "Fixed") {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.SqlUnsupportedType,
                            message: $"similarity(...) in nearest query must write into a FIXED column, but '{scoreCol.ColumnName}' is {scoreCol.Kind}",
                            span: scoreExpr.Span
                        );
                    } else if (scoreFunc.FunctionName == "dot" && scoreCol.Kind != "Int") {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.SqlUnsupportedType,
                            message: $"dot(...) in nearest query must write into an INT column, but '{scoreCol.ColumnName}' is {scoreCol.Kind}",
                            span: scoreExpr.Span
                        );
                    }
                }
            }
        } else if (nativeTarget is not null) {
            intoRowName = nativeTarget["name"]?.ToString() ?? stmt.TargetTable;
        }

        var fromRowName = stmt.FromTable;
        if (fromTable is not null) {
            var vecCol = fromTable.Columns.FirstOrDefault(c => string.Equals(c.Kind, "Vector", StringComparison.OrdinalIgnoreCase));
            fromRowName = vecCol?.RowName ?? fromTable.TableName;
        } else if (nativeFrom is not null) {
            fromRowName = nativeFrom["name"]?.ToString() ?? stmt.FromTable;
        }

        if (scoreFunc.Arguments.Count != 2) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Function '{scoreFunc.FunctionName}' requires 2 arguments",
                span: scoreFunc.Span
            );
            return;
        }

        var arg1 = scoreFunc.Arguments[0];
        var arg2 = scoreFunc.Arguments[1];

        SqlExpression queryExpr;
        if (fromTable is not null && IsColumnOfTable(arg1, fromTable)) {
            queryExpr = arg2;
        } else {
            queryExpr = arg1;
        }

        var queryString = FormatQueryOperand(queryExpr, tableRegistry, scope);

        string? threshold = null;
        string? whereRow = null;
        string? excludeKey = null;

        if (stmt.Where is not null) {
            ExtractNearestWhereConditions(stmt.Where, fromTable, tableRegistry, ref threshold, ref whereRow, ref excludeKey, scope);
        }

        var nearestObj = new JsonObject {
            ["$type"] = "nearest",
            ["from"] = fromRowName,
            ["query"] = queryString,
            ["into"] = intoRowName,
            ["k"] = stmt.Limit
        };

        if (threshold is not null) {
            nearestObj["threshold"] = threshold;
        }
        if (whereRow is not null) {
            nearestObj["where"] = whereRow;
        }
        if (excludeKey is not null) {
            nearestObj["exclude"] = excludeKey;
        }
        if (stmt.IsDescending) {
            nearestObj["farthest"] = true;
        }

        var transformStateObj = new JsonObject {
            ["$type"] = "transformState",
            ["transform"] = nearestObj
        };

        effectsArr.AppendNode(item: transformStateObj);
    }

    private static bool IsColumnOfTable(SqlExpression expr, SqlTableSchema table) {
        if (expr is SqlColumnRefExpression colRef) {
            if (colRef.TableName is not null && string.Equals(colRef.TableName, table.TableName, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
            if (colRef.TableName is null && table.FindColumn(colRef.ColumnName) is not null) {
                return true;
            }
        }
        return false;
    }

    private static string FormatQueryOperand(SqlExpression expr, Dictionary<string, SqlTableSchema> tableRegistry, DocumentScope scope) {
        if (expr is SqlColumnRefExpression colRef) {
            if (colRef.TableName is not null && tableRegistry.TryGetValue(colRef.TableName, out var tbl)) {
                if (tbl.IsSlot) {
                    return tbl.TableName;
                }
                return tbl.GetRowNameForColumn(colRef.ColumnName);
            }
            if (colRef.TableName is not null) {
                var native = FindNativeWorldRow(colRef.TableName, scope);
                if (native is not null) {
                    return native["name"]?.ToString() ?? colRef.TableName;
                }
                return colRef.TableName;
            }
            return colRef.ColumnName;
        }

        if (expr is SqlSubqueryExpression subq) {
            if (tableRegistry.TryGetValue(subq.TableName, out var tbl)) {
                var rName = tbl.GetRowNameForColumn(subq.ColumnName);
                return FormatRowRef(rName, subq.KeyValue);
            }
            return FormatRowRef(subq.TableName, subq.KeyValue);
        }

        if (expr is SqlVectorLiteralExpression vec) {
            if (vec.Kind == "embed") {
                return (vec.Space is not null) ? $"embed(\"{vec.Payload}\", space: {vec.Space})" : $"embed(\"{vec.Payload}\")";
            }
            return $"vector(\"{vec.Payload}\")";
        }

        return PrintSqlExpression(expr, null, tableRegistry, false, null, scope);
    }

    private static void ExtractNearestWhereConditions(
        SqlExpression expr,
        SqlTableSchema? fromTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        ref string? threshold,
        ref string? whereRow,
        ref string? excludeKey,
        DocumentScope scope
    ) {
        if (expr is SqlBinaryExpression { Operator: "AND" } andBin) {
            ExtractNearestWhereConditions(andBin.Left, fromTable, tableRegistry, ref threshold, ref whereRow, ref excludeKey, scope);
            ExtractNearestWhereConditions(andBin.Right, fromTable, tableRegistry, ref threshold, ref whereRow, ref excludeKey, scope);
            return;
        }

        // 1. threshold: scoreFunc(...) >= val or val <= scoreFunc(...)
        if (expr is SqlBinaryExpression binCmp && (binCmp.Operator is ">=" or ">" or "<=" or "<")) {
            if (binCmp.Left is SqlFunctionCallExpression { FunctionName: "similarity" or "dot" }) {
                var lit = EvaluateSqlLiteral(binCmp.Right);
                if (lit is not null) {
                    threshold = Convert.ToString(lit, CultureInfo.InvariantCulture);
                    return;
                }
            } else if (binCmp.Right is SqlFunctionCallExpression { FunctionName: "similarity" or "dot" }) {
                var lit = EvaluateSqlLiteral(binCmp.Left);
                if (lit is not null) {
                    threshold = Convert.ToString(lit, CultureInfo.InvariantCulture);
                    return;
                }
            }
        }

        // 2. exclude: key <> 'val' or key != 'val'
        if (expr is SqlBinaryExpression { Operator: "<>" or "!=" } neqBin) {
            if (neqBin.Left is SqlColumnRefExpression { ColumnName: "key" or "id" }) {
                var lit = EvaluateSqlLiteral(neqBin.Right);
                if (lit is not null) {
                    excludeKey = FormatKeyString(lit);
                    return;
                }
            } else if (neqBin.Right is SqlColumnRefExpression { ColumnName: "key" or "id" }) {
                var lit = EvaluateSqlLiteral(neqBin.Left);
                if (lit is not null) {
                    excludeKey = FormatKeyString(lit);
                    return;
                }
            }
        }

        // 3. where: column reference to a Bool column of fromTable (or another table)
        if (expr is SqlColumnRefExpression colRef) {
            if (colRef.TableName is not null && tableRegistry.TryGetValue(colRef.TableName, out var tbl)) {
                var col = tbl.FindColumn(colRef.ColumnName);
                if (col is not null) {
                    whereRow = col.RowName;
                    return;
                }
            } else if (fromTable is not null) {
                var col = fromTable.FindColumn(colRef.ColumnName);
                if (col is not null) {
                    whereRow = col.RowName;
                    return;
                }
            }
            whereRow = colRef.ColumnName;
            return;
        }
    }

    private static void LowerDeleteStatement(
        SqlDeleteStatement delete,
        JsonArray effectsArr,
        ref string? forEachRow,
        ref JsonObject? ruleGate,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        if (!tableRegistry.TryGetValue(delete.TableName, out var table)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: $"DELETE targets unknown table '{delete.TableName}'",
                span: delete.Span
            );
            return;
        }
        var pkCol = table.PrimaryKeyColumn;

        var isSingleKeyLiteral = TryExtractSingleKeyLiteral(delete.Where, pkCol, out var targetKey);

        if (!isSingleKeyLiteral) {
            var notNullCol = table.Columns.FirstOrDefault(c => c.IsNotNull || c.DefaultValue is not null);
            if (notNullCol is null && table.Columns.Count > 0) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedClause,
                    message: $"Per-key rule iterating table '{table.TableName}' requires at least one NOT NULL column to iterate all keys; all columns are nullable.",
                    span: delete.Span
                );
                return;
            }

            var chosenRow = notNullCol?.RowName ?? table.Columns.FirstOrDefault()?.RowName ?? table.TableName;
            forEachRow ??= chosenRow;
            targetKey = "$each";

            if (delete.Where is not null) {
                var gatePred = LowerSqlExpressionToPredicate(
                    expr: delete.Where,
                    currentTable: delete.TableName,
                    tableRegistry: tableRegistry,
                    isForEach: true,
                    targetKey: "$each",
                    scope: scope
                );
                ruleGate = CombineRuleGate(ruleGate, gatePred);
            }
        }

        var rowNames = table.GetAllRowNames();
        var deleteEffects = new JsonArray();

        foreach (var rName in rowNames) {
            deleteEffects.AppendNode(item: new JsonObject {
                ["$type"] = "removeStateCell",
                ["state"] = rName,
                ["key"] = targetKey!
            });
        }

        if (deleteEffects.Count > 1) {
            effectsArr.AppendNode(item: new JsonObject {
                ["$type"] = "transaction",
                ["effects"] = deleteEffects
            });
        } else if (deleteEffects.Count == 1) {
            var single = deleteEffects[0];
            deleteEffects.RemoveAt(0);
            effectsArr.AppendNode(item: single!);
        }
    }

    private static void LowerAtomicStatement(
        SqlAtomicStatement atomic,
        JsonArray effectsArr,
        ref string? forEachRow,
        ref JsonObject? ruleGate,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        var mainEffects = new JsonArray();
        foreach (var stmt in atomic.Statements) {
            LowerRuleStatement(stmt, mainEffects, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
        }

        var transactionObj = new JsonObject {
            ["$type"] = "transaction",
            ["effects"] = mainEffects
        };

        if (atomic.ExceptionStatements is not null) {
            var onFailureArr = new JsonArray();
            foreach (var stmt in atomic.ExceptionStatements) {
                LowerRuleStatement(stmt, onFailureArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
            }
            transactionObj["onFailure"] = onFailureArr;
        }

        effectsArr.AppendNode(item: transactionObj);
    }

    private static void LowerIfRuleStatement(
        SqlIfStatement ifStmt,
        JsonArray effectsArr,
        ref string? forEachRow,
        ref JsonObject? ruleGate,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        var conditionObj = LowerSqlExpressionToPredicate(
            expr: ifStmt.Condition,
            currentTable: currentTable,
            tableRegistry: tableRegistry,
            isForEach: (forEachRow is not null),
            targetKey: (forEachRow is not null ? "$each" : null),
            scope: scope
        );

        var thenArr = new JsonArray();
        foreach (var stmt in ifStmt.ThenStatements) {
            LowerRuleStatement(stmt, thenArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
        }

        var ifObj = new JsonObject {
            ["$type"] = "if",
            ["condition"] = conditionObj,
            ["then"] = thenArr
        };

        if (ifStmt.ElsifStatements.Count > 0) {
            var currentElseIfParent = ifObj;

            for (var i = 0; i < ifStmt.ElsifStatements.Count; i++) {
                var (elsifCond, elsifStmts) = ifStmt.ElsifStatements[i];
                var elsifCondObj = LowerSqlExpressionToPredicate(
                    expr: elsifCond,
                    currentTable: currentTable,
                    tableRegistry: tableRegistry,
                    isForEach: (forEachRow is not null),
                    targetKey: (forEachRow is not null ? "$each" : null),
                    scope: scope
                );

                var subThenArr = new JsonArray();
                foreach (var s in elsifStmts) {
                    LowerRuleStatement(s, subThenArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
                }

                var subIfObj = new JsonObject {
                    ["$type"] = "if",
                    ["condition"] = elsifCondObj,
                    ["then"] = subThenArr
                };

                var elseWrapper = new JsonArray();
                elseWrapper.AppendNode(item: subIfObj);
                currentElseIfParent["else"] = elseWrapper;
                currentElseIfParent = subIfObj;
            }

            if (ifStmt.ElseStatements is not null) {
                var elseArr = new JsonArray();
                foreach (var stmt in ifStmt.ElseStatements) {
                    LowerRuleStatement(stmt, elseArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
                }
                currentElseIfParent["else"] = elseArr;
            }
        } else if (ifStmt.ElseStatements is not null) {
            var elseArr = new JsonArray();
            foreach (var stmt in ifStmt.ElseStatements) {
                LowerRuleStatement(stmt, elseArr, ref forEachRow, ref ruleGate, currentTable, tableRegistry, scope);
            }
            ifObj["else"] = elseArr;
        }

        effectsArr.AppendNode(item: ifObj);
    }

    private static bool TryExtractSingleKeyLiteral(SqlExpression? expr, string pkCol, out string? keyVal) {
        keyVal = null;
        if (expr is SqlBinaryExpression { Operator: "=" } bin) {
            if (bin.Left is SqlColumnRefExpression colRef &&
                string.Equals(colRef.ColumnName, pkCol, StringComparison.OrdinalIgnoreCase)) {
                keyVal = FormatKeyString(EvaluateSqlLiteral(bin.Right));
                return (keyVal is not null);
            }
            if (bin.Right is SqlColumnRefExpression colRef2 &&
                string.Equals(colRef2.ColumnName, pkCol, StringComparison.OrdinalIgnoreCase)) {
                keyVal = FormatKeyString(EvaluateSqlLiteral(bin.Left));
                return (keyVal is not null);
            }
        }
        return false;
    }

    private static void CheckSelfReferentialUpdate(
        SqlExpression? expr,
        string targetTable,
        HashSet<string> writtenColumns,
        DocumentScope scope
    ) {
        if (expr is null) {
            return;
        }

        switch (expr) {
            case SqlSubqueryExpression subq:
                if (string.Equals(subq.TableName, targetTable, StringComparison.OrdinalIgnoreCase) &&
                    writtenColumns.Contains(subq.ColumnName)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSelfReferentialSetUpdate,
                        message: $"A set-based UPDATE on '{targetTable}' may not read column '{subq.ColumnName}' at other keys; forEach evaluation evaluates writes sequentially.",
                        span: subq.Span
                    );
                }
                break;
            case SqlAggregateExpression agg:
                if ((agg.TableName is null || string.Equals(agg.TableName, targetTable, StringComparison.OrdinalIgnoreCase)) &&
                    writtenColumns.Contains(agg.ColumnName)) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSelfReferentialSetUpdate,
                        message: $"A set-based UPDATE on '{targetTable}' may not aggregate column '{agg.ColumnName}' across keys; forEach evaluation evaluates writes sequentially.",
                        span: agg.Span
                    );
                }
                break;
            case SqlBinaryExpression bin:
                CheckSelfReferentialUpdate(bin.Left, targetTable, writtenColumns, scope);
                CheckSelfReferentialUpdate(bin.Right, targetTable, writtenColumns, scope);
                break;
            case SqlUnaryExpression un:
                CheckSelfReferentialUpdate(un.Operand, targetTable, writtenColumns, scope);
                break;
        }
    }

    private static JsonObject LowerSqlExpressionToPredicate(
        SqlExpression expr,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        bool isForEach,
        string? targetKey,
        DocumentScope scope
    ) {
        switch (expr) {
            case SqlBinaryExpression { Operator: "AND" } andExpr: {
                    var andArr = new JsonArray();
                    andArr.AppendNode(item: LowerSqlExpressionToPredicate(andExpr.Left, currentTable, tableRegistry, isForEach, targetKey, scope));
                    andArr.AppendNode(item: LowerSqlExpressionToPredicate(andExpr.Right, currentTable, tableRegistry, isForEach, targetKey, scope));
                    return new JsonObject {
                        ["$type"] = "all",
                        ["predicates"] = andArr
                    };
                }

            case SqlBinaryExpression { Operator: "OR" } orExpr: {
                    var orArr = new JsonArray();
                    orArr.AppendNode(item: LowerSqlExpressionToPredicate(orExpr.Left, currentTable, tableRegistry, isForEach, targetKey, scope));
                    orArr.AppendNode(item: LowerSqlExpressionToPredicate(orExpr.Right, currentTable, tableRegistry, isForEach, targetKey, scope));
                    return new JsonObject {
                        ["$type"] = "any",
                        ["predicates"] = orArr
                    };
                }

            case SqlUnaryExpression { Operator: "NOT" } notExpr:
                return new JsonObject {
                    ["$type"] = "not",
                    ["predicate"] = LowerSqlExpressionToPredicate(notExpr.Operand, currentTable, tableRegistry, isForEach, targetKey, scope)
                };

            case SqlBinaryExpression binCmp: {
                    var opName = binCmp.Operator switch {
                        "=" => "Equal",
                        "<>" or "!=" => "NotEqual",
                        "<" => "Less",
                        "<=" => "LessOrEqual",
                        ">" => "Greater",
                        ">=" => "GreaterOrEqual",
                        _ => null
                    };

                    if (opName is null) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.SqlUnsupportedClause,
                            message: $"Unsupported comparison operator '{binCmp.Operator}'",
                            span: binCmp.Span
                        );
                        opName = "Equal";
                    }

                    if (binCmp.Left is SqlColumnRefExpression leftColRef) {
                        var leftCol = ResolveColumnRef(leftColRef, currentTable, tableRegistry, scope);
                        var rightLiteral = EvaluateSqlLiteral(binCmp.Right);

                        if (leftCol is not null && rightLiteral is not null) {
                            if (leftCol.Value.IsKeyed && !isForEach && targetKey is null) {
                                scope.Diagnostics.ReportError(
                                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                                    message: $"Cannot read keyed column '{leftColRef.ColumnName}' in predicate without a key or per-key context",
                                    span: leftColRef.Span
                                );
                            }
                            var loweredVal = LowerScalarJsonValue(rightLiteral, leftCol.Value.Kind, binCmp.Right.Span, scope);
                            var obj = new JsonObject {
                                ["$type"] = "compareState",
                                ["state"] = leftCol.Value.RowName,
                                ["comparison"] = opName,
                                ["value"] = loweredVal
                            };
                            if (targetKey is not null && leftCol.Value.IsKeyed) {
                                obj["key"] = targetKey;
                            }
                            return obj;
                        }
                    }

                    if (binCmp.Right is SqlColumnRefExpression rightColRef) {
                        var rightCol = ResolveColumnRef(rightColRef, currentTable, tableRegistry, scope);
                        var leftLiteral = EvaluateSqlLiteral(binCmp.Left);

                        if (rightCol is not null && leftLiteral is not null) {
                            if (rightCol.Value.IsKeyed && !isForEach && targetKey is null) {
                                scope.Diagnostics.ReportError(
                                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                                    message: $"Cannot read keyed column '{rightColRef.ColumnName}' in predicate without a key or per-key context",
                                    span: rightColRef.Span
                                );
                            }
                            var flippedOp = FlipComparison(opName);
                            var loweredVal = LowerScalarJsonValue(leftLiteral, rightCol.Value.Kind, binCmp.Left.Span, scope);
                            var obj = new JsonObject {
                                ["$type"] = "compareState",
                                ["state"] = rightCol.Value.RowName,
                                ["comparison"] = flippedOp,
                                ["value"] = loweredVal
                            };
                            if (targetKey is not null && rightCol.Value.IsKeyed) {
                                obj["key"] = targetKey;
                            }
                            return obj;
                        }
                    }

                    return new JsonObject {
                        ["$type"] = "compareValue",
                        ["comparison"] = opName,
                        ["kind"] = (IsIntegerFunction(binCmp.Left) || IsIntegerFunction(binCmp.Right)) ? "Int" : "Fixed",
                        ["left"] = PrintSqlExpression(binCmp.Left, currentTable, tableRegistry, isForEach, targetKey, scope),
                        ["right"] = PrintSqlExpression(binCmp.Right, currentTable, tableRegistry, isForEach, targetKey, scope)
                    };
                }

            default:
                return new JsonObject {
                    ["$type"] = "compareValue",
                    ["comparison"] = "Equal",
                    ["kind"] = "Bool",
                    ["left"] = PrintSqlExpression(expr, currentTable, tableRegistry, isForEach, targetKey, scope),
                    ["right"] = "true"
                };
        }
    }

    private static JsonObject? FindNativeWorldRow(string name, DocumentScope scope) {
        if (scope.Annotations.TryGetValue("WorldDocumentRoot", out var r) && r is JsonObject rootObj) {
            if (rootObj["state"]?["world"] is JsonArray worldArr) {
                return worldArr.OfType<JsonObject>().FirstOrDefault(row =>
                    string.Equals(row["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)
                );
            }
        }
        return null;
    }

    private static (string RowName, bool IsKeyed, string Kind)? ResolveColumnRef(
        SqlColumnRefExpression colRef,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        DocumentScope scope
    ) {
        if (colRef.TableName is not null) {
            if (!tableRegistry.TryGetValue(colRef.TableName, out var schema)) {
                var nativeWorldRow = FindNativeWorldRow(colRef.TableName, scope);
                if (nativeWorldRow is not null) {
                    var rName = nativeWorldRow["name"]?.ToString() ?? colRef.TableName;
                    var isKeyed = nativeWorldRow["domain"] is not null;
                    var k = nativeWorldRow["kind"]?.ToString() ?? "Int";
                    return (rName, isKeyed, k);
                }

                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                    message: $"Unknown table '{colRef.TableName}' in column reference '{colRef.TableName}.{colRef.ColumnName}'",
                    span: colRef.Span
                );
                return null;
            }

            if (string.Equals(colRef.ColumnName, schema.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                    message: $"Primary key column '{colRef.ColumnName}' of table '{colRef.TableName}' is not a queryable state row",
                    span: colRef.Span
                );
                return null;
            }

            if (schema.IsSlot) {
                var kind = schema.Columns.FirstOrDefault()?.Kind ?? schema.PrimaryKeyType;
                return (schema.TableName, false, kind);
            }

            var col = schema.FindColumn(colRef.ColumnName);
            if (col is null) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                    message: $"Table '{colRef.TableName}' has no column named '{colRef.ColumnName}'",
                    span: colRef.Span
                );
                return null;
            }
            return (col.RowName, true, col.Kind);
        }

        if (currentTable is not null && tableRegistry.TryGetValue(currentTable, out var curSchema)) {
            if (string.Equals(colRef.ColumnName, curSchema.PrimaryKeyColumn, StringComparison.OrdinalIgnoreCase)) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                    message: $"Primary key column '{colRef.ColumnName}' of table '{currentTable}' is not a queryable state row",
                    span: colRef.Span
                );
                return null;
            }

            if (curSchema.IsSlot) {
                if (string.Equals(colRef.ColumnName, "value", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(colRef.ColumnName, curSchema.TableName, StringComparison.OrdinalIgnoreCase)) {
                    var kind = curSchema.Columns.FirstOrDefault()?.Kind ?? curSchema.PrimaryKeyType;
                    return (curSchema.TableName, false, kind);
                }
            }

            var col = curSchema.FindColumn(colRef.ColumnName);
            if (col is not null) {
                return (col.RowName, true, col.Kind);
            }
        }

        if (tableRegistry.TryGetValue(colRef.ColumnName, out var directSlot) && directSlot.IsSlot) {
            var kind = directSlot.Columns.FirstOrDefault()?.Kind ?? directSlot.PrimaryKeyType;
            return (directSlot.TableName, false, kind);
        }

        var nativeSlot = FindNativeWorldRow(colRef.ColumnName, scope);
        if (nativeSlot is not null) {
            var rName = nativeSlot["name"]?.ToString() ?? colRef.ColumnName;
            var isKeyed = nativeSlot["domain"] is not null;
            var k = nativeSlot["kind"]?.ToString() ?? "Int";
            return (rName, isKeyed, k);
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
            message: $"Unknown column or slot reference '{colRef.ColumnName}'",
            span: colRef.Span
        );
        return null;
    }

    private static string FormatRowRef(string name, string? key) =>
        ExpressionSpelling.Print(tokens: [new ValueToken.State(
            Key: key,
            Name: name
        )]);

    private static string FlipComparison(string op) => op switch {
        "Less" => "Greater",
        "LessOrEqual" => "GreaterOrEqual",
        "Greater" => "Less",
        "GreaterOrEqual" => "LessOrEqual",
        _ => op
    };

    private static JsonObject CombineRuleGate(JsonObject? existing, JsonObject newGate) {
        if (existing is null) {
            return newGate;
        }

        if (existing["$type"]?.ToString() == "all" && existing["predicates"] is JsonArray arr) {
            arr.AppendNode(item: newGate);
            return existing;
        }

        var newAll = new JsonArray();
        newAll.AppendNode(item: existing);
        newAll.AppendNode(item: newGate);
        return new JsonObject {
            ["$type"] = "all",
            ["predicates"] = newAll
        };
    }

    private static void ApplySqlExpressionToEffect(
        SqlExpression expr,
        JsonObject effectObj,
        string currentTable,
        string destKind,
        Dictionary<string, SqlTableSchema> tableRegistry,
        bool isForEach,
        string? targetKey,
        DocumentScope scope
    ) {
        if (expr is SqlLiteralExpression { Value: null }) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: "Cannot assign NULL in SET clause",
                span: expr.Span
            );
            return;
        }

        if (destKind == "Vector") {
            if (expr is SqlVectorLiteralExpression vecLit) {
                if (vecLit.Kind == "vector") {
                    effectObj["vector"] = vecLit.Payload;
                    return;
                }
                if (vecLit.Kind == "embed") {
                    if (TryResolveEmbeddedText(vecLit.Payload, vecLit.Space, scope, out var base64)) {
                        effectObj["vector"] = base64;
                        return;
                    }
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.EmbeddingLockMissing,
                        message: $"No embedding lock entry for \"{vecLit.Payload}\"; run puck embed.",
                        span: expr.Span
                    );
                    effectObj["vector"] = "";
                    return;
                }
            }
            var vectorExprText = PrintSqlExpression(expr, currentTable, tableRegistry, isForEach, targetKey, scope);
            effectObj["expression"] = vectorExprText;
            return;
        }

        var literalVal = EvaluateSqlLiteral(expr);
        if (literalVal is not null) {
            switch (destKind) {
                case "Bool":
                    if (literalVal is bool b) {
                        effectObj["value"] = b ? 1L : 0L;
                        return;
                    }
                    var bStr = literalVal.ToString()?.Trim() ?? "";
                    if (bStr == "1" || string.Equals(bStr, "true", StringComparison.OrdinalIgnoreCase)) {
                        effectObj["value"] = 1L;
                        return;
                    }
                    if (bStr == "0" || string.Equals(bStr, "false", StringComparison.OrdinalIgnoreCase)) {
                        effectObj["value"] = 0L;
                        return;
                    }
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                        message: $"Cannot assign non-boolean value '{literalVal}' to BOOL column",
                        span: expr.Span
                    );
                    effectObj["value"] = 0L;
                    return;

                case "Int":
                    if (literalVal is long l) {
                        effectObj["value"] = l;
                        return;
                    }
                    if (literalVal is int i) {
                        effectObj["value"] = (long)i;
                        return;
                    }
                    if (literalVal is decimal d) {
                        if (d != decimal.Truncate(d)) {
                            scope.Diagnostics.ReportError(
                                code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                                message: $"Non-integer decimal '{d}' cannot be assigned to an INT column",
                                span: expr.Span
                            );
                        }
                        effectObj["value"] = (long)d;
                        return;
                    }
                    if (long.TryParse(literalVal.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong)) {
                        effectObj["value"] = parsedLong;
                        return;
                    }
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                        message: $"Cannot assign value '{literalVal}' to INT column",
                        span: expr.Span
                    );
                    effectObj["value"] = 0L;
                    return;

                case "Fixed":
                    if (decimal.TryParse(literalVal.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var dec)) {
                        effectObj["value"] = dec;
                        return;
                    }
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                        message: $"Cannot assign value '{literalVal}' to FIXED column",
                        span: expr.Span
                    );
                    effectObj["value"] = 0m;
                    return;

                case "Text":
                    if (literalVal is string s) {
                        effectObj["text"] = s;
                        return;
                    }
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                        message: $"Cannot assign non-string value '{literalVal}' to TEXT column",
                        span: expr.Span
                    );
                    effectObj["text"] = "";
                    return;
            }
        }

        var exprText = PrintSqlExpression(expr, currentTable, tableRegistry, isForEach, targetKey, scope);
        ApplyOperandRhs(effectObj, exprText);
    }

    private static string PrintSqlExpression(
        SqlExpression expr,
        string? currentTable,
        Dictionary<string, SqlTableSchema> tableRegistry,
        bool isForEach,
        string? targetKey,
        DocumentScope scope
    ) {
        switch (expr) {
            case SqlLiteralExpression lit:
                if (lit.Value is null) {
                    return "null";
                }
                if (lit.Value is bool b) {
                    return b ? "1" : "0";
                }
                if (lit.Value is string s) {
                    return $"\"{s}\"";
                }
                return Convert.ToString(lit.Value, CultureInfo.InvariantCulture) ?? "0";

            case SqlVectorLiteralExpression vec:
                if (vec.Kind == "embed") {
                    return (vec.Space is not null) ? $"embed(\"{vec.Payload}\", space: {vec.Space})" : $"embed(\"{vec.Payload}\")";
                }
                return $"vector(\"{vec.Payload}\")";

            case SqlFunctionCallExpression fn: {
                var args = string.Join(", ", fn.Arguments.Select(a => PrintSqlExpression(a, currentTable, tableRegistry, isForEach, targetKey, scope)));
                return $"{fn.FunctionName}({args})";
            }

            case SqlColumnRefExpression col:
                var resolved = ResolveColumnRef(col, currentTable, tableRegistry, scope);
                if (resolved is null) {
                    return "0";
                }
                if (!resolved.Value.IsKeyed) {
                    return resolved.Value.RowName;
                }
                if (isForEach) {
                    return FormatRowRef(resolved.Value.RowName, "$each");
                }
                if (targetKey is not null) {
                    return FormatRowRef(resolved.Value.RowName, targetKey);
                }
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                    message: $"Cannot read keyed column '{col.ColumnName}' without a key",
                    span: col.Span
                );
                return FormatRowRef(resolved.Value.RowName, "unknown");

            case SqlSubqueryExpression subq:
                if (!tableRegistry.TryGetValue(subq.TableName, out var subSchema)) {
                    var nativeRow = FindNativeWorldRow(subq.TableName, scope);
                    if (nativeRow is not null) {
                        var nativeRowName = nativeRow["name"]?.ToString() ?? subq.TableName;
                        return FormatRowRef(nativeRowName, subq.KeyValue);
                    }

                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                        message: $"Unknown table '{subq.TableName}' in subquery",
                        span: subq.Span
                    );
                    return "0";
                }
                var subCol = subSchema.FindColumn(subq.ColumnName);
                if (subCol is null && !subSchema.IsSlot) {
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                        message: $"Table '{subq.TableName}' has no column named '{subq.ColumnName}'",
                        span: subq.Span
                    );
                    return "0";
                }
                var subRow = subSchema.GetRowNameForColumn(subq.ColumnName);
                return FormatRowRef(subRow, subq.KeyValue);

            case SqlUnaryExpression un:
                return $"{un.Operator} {PrintSqlExpression(un.Operand, currentTable, tableRegistry, isForEach, targetKey, scope)}";

            case SqlBinaryExpression bin:
                return $"({PrintSqlExpression(bin.Left, currentTable, tableRegistry, isForEach, targetKey, scope)} {bin.Operator} {PrintSqlExpression(bin.Right, currentTable, tableRegistry, isForEach, targetKey, scope)})";

            default:
                return "0";
        }
    }

    private static object? EvaluateSqlLiteral(SqlExpression expr) {
        if (expr is SqlLiteralExpression lit) {
            return lit.Value;
        }
        if (expr is SqlVectorLiteralExpression vec) {
            return vec;
        }
        if (expr is SqlUnaryExpression { Operator: "-" } un) {
            var inner = EvaluateSqlLiteral(un.Operand);
            if (inner is long l) {
                return -l;
            }
            if (inner is decimal d) {
                return -d;
            }
            if (inner is int i) {
                return -i;
            }
        }
        return null;
    }

    private static string? FormatKeyString(object? val) {
        if (val is null) {
            return null;
        }
        if (val is string s) {
            return s;
        }
        if (val is long l) {
            return l.ToString(CultureInfo.InvariantCulture);
        }
        if (val is int i) {
            return i.ToString(CultureInfo.InvariantCulture);
        }
        return val.ToString();
    }

    private static string? MapSqlTypeToKind(string typeName) => typeName.ToUpperInvariant() switch {
        "INT" or "INTEGER" or "SMALLINT" or "BIGINT" or "TINYINT" => "Int",
        "FIXED" or "DECIMAL" or "NUMERIC" => "Fixed",
        "BOOL" or "BOOLEAN" => "Bool",
        "TEXT" or "VARCHAR" or "CHAR" or "STRING" => "Text",
        "VECTOR" => "Vector",
        _ => null
    };

    private static JsonNode? LowerScalarJsonValue(object? value, string kind, SourceSpan span, DocumentScope scope) {
        if (value is null) {
            return null;
        }

        switch (kind) {
            case "Vector":
                if (value is SqlVectorLiteralExpression vecLit) {
                    if (vecLit.Kind == "vector") {
                        return JsonValue.Create(vecLit.Payload)!;
                    }
                    if (vecLit.Kind == "embed") {
                        if (TryResolveEmbeddedText(vecLit.Payload, vecLit.Space, scope, out var base64)) {
                            return JsonValue.Create(base64)!;
                        }
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.EmbeddingLockMissing,
                            message: $"No embedding lock entry for \"{vecLit.Payload}\"; run puck embed.",
                            span: span
                        );
                        return JsonValue.Create("")!;
                    }
                }
                if (value is string sVal) {
                    return JsonValue.Create(sVal)!;
                }
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.VectorLiteralInvalid,
                    message: $"Cannot convert value '{value}' to a Vector",
                    span: span
                );
                return JsonValue.Create("")!;

            case "Int":
                if (value is long l) {
                    return JsonValue.Create(value: l)!;
                }
                if (value is int i) {
                    return JsonValue.Create(value: (long)i)!;
                }
                if (value is decimal d) {
                    if (d != decimal.Truncate(d)) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                            message: $"Non-integer decimal '{d}' cannot be assigned to an INT column or bound",
                            span: span
                        );
                    }
                    return JsonValue.Create(value: (long)d)!;
                }
                if (long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong)) {
                    return JsonValue.Create(parsedLong)!;
                }
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                    message: $"Cannot convert value '{value}' to an INT",
                    span: span
                );
                return JsonValue.Create(0L)!;

            case "Fixed":
                if (value is decimal dec) {
                    return JsonValue.Create(dec.ToString(CultureInfo.InvariantCulture))!;
                }
                if (value is double dbl) {
                    return JsonValue.Create(dbl.ToString(CultureInfo.InvariantCulture))!;
                }
                if (value is long lg) {
                    return JsonValue.Create(lg.ToString(CultureInfo.InvariantCulture))!;
                }
                if (decimal.TryParse(value.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedDec)) {
                    return JsonValue.Create(parsedDec.ToString(CultureInfo.InvariantCulture))!;
                }
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                    message: $"Cannot convert value '{value}' to a FIXED decimal",
                    span: span
                );
                return JsonValue.Create("0")!;

            case "Bool":
                if (value is bool b) {
                    return JsonValue.Create(value: b)!;
                }
                var str = value.ToString()?.Trim() ?? "";
                if (str == "1" || string.Equals(str, "true", StringComparison.OrdinalIgnoreCase)) {
                    return JsonValue.Create(true)!;
                }
                if (str == "0" || string.Equals(str, "false", StringComparison.OrdinalIgnoreCase)) {
                    return JsonValue.Create(false)!;
                }
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.StateDeclarationInvalidDefault,
                    message: $"Cannot convert value '{value}' to a BOOL (expected true/false or 1/0)",
                    span: span
                );
                return JsonValue.Create(false)!;

            default: // Text
                return JsonValue.Create(value.ToString() ?? "")!;
        }
    }

    private static void ApplyCheckConstraint(SqlCheckConstraint check, JsonObject rowObj, string kind, SourceSpan span, DocumentScope scope) {
        if (check.Min.HasValue) {
            var minVal = LowerScalarJsonValue(check.Min.Value, kind, span, scope);
            if (minVal is not null) {
                rowObj["min"] = minVal;
            }
        }
        if (check.Max.HasValue) {
            var maxVal = LowerScalarJsonValue(check.Max.Value, kind, span, scope);
            if (maxVal is not null) {
                rowObj["max"] = maxVal;
            }
        }
    }

    private static void ApplyAdvanceClause(SqlAdvanceClause advance, JsonObject rowObj, DocumentScope scope) {
        var rateDec = advance.Rate;
        if (!TryReduceDecimalRate(text: rateDec.ToString(CultureInfo.InvariantCulture), numerator: out var numerator, denominator: out var denominator)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationRateInexact,
                message: $"ADVANCE rate '{rateDec}' does not reduce to an exact 64-bit fraction",
                span: advance.Span
            );
            return;
        }

        rowObj["advance"] = new JsonObject {
            ["perSecondNumerator"] = numerator,
            ["perSecondDenominator"] = denominator
        };
    }

    private static string CombineTableAndColumn(string tableName, string columnName) {
        if (string.IsNullOrEmpty(columnName)) {
            return tableName;
        }
        return tableName + char.ToUpperInvariant(columnName[0]) + columnName[1..];
    }

    private static bool IsIntegerFunction(SqlExpression expr) =>
        expr is SqlFunctionCallExpression { FunctionName: "dot" or "identical" };

    private static string? FindDefaultSpace(JsonObject parent) {
        if (parent["state"]?["spaces"] is JsonArray spacesArr && spacesArr.Count == 1) {
            return spacesArr[0]?["name"]?.ToString();
        }
        return null;
    }

    private static bool TryResolveEmbeddedText(string text, string? space, DocumentScope scope, out string base64) {
        base64 = "";
        if (scope.Annotations.TryGetValue("EmbeddingLock", out var lockObj)) {
            // Reserved for Step 7 EmbeddingLock integration
        }
        return false;
    }
}
