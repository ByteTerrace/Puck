using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Sql;

namespace Puck.World.Transpiler.Decompiler;

public static partial class WorldDecompiler {
    // The dialect has no quoted identifier, so a name the SQL lexer would not read back as one identifier token keeps
    // its row out of the projection.
    private static bool IsValidSqlIdentifier(string? name) => (
        IdentifierSpelling.IsIdentifier(text: name) &&
        !StateSqlLexer.IsReservedWord(word: name!)
    );

    /// <summary>Decompiles a root <see cref="JsonObject"/> into formatted Puck source code, optionally projecting
    /// representable state rows and rules into an embedded SQL block.</summary>
    /// <param name="root">The root JSON object representing the world definition.</param>
    /// <param name="sql">When <see langword="true"/>, emits representable state tables, slots, and rules inside a <c>sql { ... }</c> block.</param>
    /// <param name="embeddings">Optional companion embedding lock file for resolving vector literals.</param>
    /// <returns>Formatted Puck DSL source code.</returns>
    public static string Decompile(JsonObject root, bool sql, EmbeddingLock? embeddings = null) {
        if (!sql) {
            return Decompile(embeddings: embeddings, root: root);
        }

        var clonedRoot = ((JsonObject)root.DeepClone());
        var sqlBlock = ExtractSqlProjection(embeddings: embeddings, root: clonedRoot);

        if (string.IsNullOrWhiteSpace(value: sqlBlock)) {
            return Decompile(embeddings: embeddings, root: root);
        }

        var decompiled = Decompile(embeddings: embeddings, root: clonedRoot);

        if (string.IsNullOrWhiteSpace(value: decompiled)) {
            return sqlBlock;
        }

        var sb = new StringBuilder(value: decompiled.TrimEnd());

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(value: sqlBlock);

        return sb.ToString();
    }
    /// <summary>Decompiles a JSON text string into formatted Puck source code, optionally projecting
    /// representable state rows and rules into an embedded SQL block.</summary>
    /// <param name="jsonText">The raw or canonical JSON text.</param>
    /// <param name="sql">When <see langword="true"/>, emits representable state tables, slots, and rules inside a <c>sql { ... }</c> block.</param>
    /// <param name="embeddings">Optional companion embedding lock file for resolving vector literals.</param>
    /// <returns>Formatted Puck DSL source code.</returns>
    public static string Decompile(string jsonText, bool sql, EmbeddingLock? embeddings = null) {
        ArgumentNullException.ThrowIfNull(jsonText);

        var node = JsonNode.Parse(jsonText);

        if (node is not JsonObject rootObj) {
            throw new ArgumentException(message: "Root JSON must be an object", paramName: nameof(jsonText));
        }

        return Decompile(embeddings: embeddings, root: rootObj, sql: sql);
    }

    private sealed record SqlItem(
        int MinIndex,
        List<int> ConsumedIndices,
        string SqlText,
        Dictionary<string, (string TableName, string ColumnName)> RowMappings
    );

    private static string ExtractSqlProjection(JsonObject root, EmbeddingLock? embeddings = null) {
        var stateObj = (root["state"] as JsonObject);
        var worldArr = (stateObj?["world"] as JsonArray);
        var rulesArr = (root["rules"] as JsonArray);

        var rowToTableColumn = new Dictionary<string, (string TableName, string ColumnName)>(comparer: StringComparer.Ordinal);
        var rowKinds = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        if (worldArr is not null) {
            foreach (var r in worldArr.OfType<JsonObject>()) {
                if ((r["name"]?.ToString() is string n) && (r["kind"]?.ToString() is string k)) {
                    rowKinds[n] = k;
                }
            }
        }
        var admittedSqlItems = new List<SqlItem>();

        // 1. Process state.world rows into SQL tables & slots preserving contiguous index order
        if ((worldArr is not null) && (worldArr.Count > 0)) {
            var candidateItems = new List<SqlItem>();
            var claimedIndices = new HashSet<int>();

            // 1a. Contiguous multi-column candidate groups
            var candidateGroups = FindMultiColumnCandidateGroups(worldArr: worldArr);

            foreach (var group in candidateGroups) {
                var (sqlTable, rowMap) = DecompileMultiColumnTable(group.Rows, embeddings);
                if ((sqlTable is not null) && VerifyCandidateTable(sqlTable, group.Rows, embeddings)) {
                    candidateItems.Add(item: new SqlItem(
                        MinIndex: group.Indices[0],
                        ConsumedIndices: group.Indices,
                        SqlText: sqlTable,
                        RowMappings: rowMap
                    ));
                    foreach (var idx in group.Indices) {
                        claimedIndices.Add(item: idx);
                    }
                }
            }

            bool TryClaimCandidateSql(
                int index,
                JsonObject rowObj,
                string? sqlTable,
                string valueColumn) {
                if ((sqlTable is null) || !VerifyCandidateTable(embeddings: embeddings, originalRows: [rowObj], sqlText: sqlTable)) {
                    return false;
                }

                var name = (rowObj["name"]?.ToString() ?? string.Empty);
                var map = new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal) {
                    [name] = (name, valueColumn),
                };

                candidateItems.Add(item: new SqlItem(
                    ConsumedIndices: [index],
                    MinIndex: index,
                    RowMappings: map,
                    SqlText: sqlTable
                ));
                claimedIndices.Add(item: index);

                return true;
            }

            // 1b. Remaining individual rows for slots, piles, and key-only tables
            for (var i = 0; (i < worldArr.Count); i++) {
                if (claimedIndices.Contains(item: i)) {
                    continue;
                }

                if ((worldArr[i] is not JsonObject rowObj) || HasUnsupportedTraits(row: rowObj)) {
                    continue;
                }

                if (IsPileRow(row: rowObj) && TryClaimCandidateSql(
                    index: i,
                    rowObj: rowObj,
                    sqlTable: DecompilePileToSql(row: rowObj),
                    valueColumn: string.Empty
                )) {
                    continue;
                }

                if (IsKeyOnlyRow(row: rowObj) && TryClaimCandidateSql(
                    index: i,
                    rowObj: rowObj,
                    sqlTable: DecompileKeyOnlyToSql(row: rowObj),
                    valueColumn: string.Empty
                )) {
                    continue;
                }

                if (IsSlotRow(row: rowObj) && TryClaimCandidateSql(
                    index: i,
                    rowObj: rowObj,
                    sqlTable: DecompileSlotToSql(embeddings: embeddings, row: rowObj),
                    valueColumn: "value"
                )) {
                    continue;
                }
            }

            // Find the highest index of a native row that could not be projected to SQL
            var maxNativeIndex = -1;

            for (var i = 0; (i < worldArr.Count); i++) {
                if (!claimedIndices.Contains(item: i)) {
                    maxNativeIndex = Math.Max(val1: maxNativeIndex, val2: i);
                }
            }

            // Only admit SQL items that appear strictly AFTER all native rows, or when all rows are SQL
            var consumedRowIndices = new HashSet<int>();

            foreach (var item in candidateItems) {
                if (item.MinIndex > maxNativeIndex) {
                    admittedSqlItems.Add(item: item);
                    foreach (var idx in item.ConsumedIndices) {
                        consumedRowIndices.Add(item: idx);
                    }
                    foreach (var (rName, mapping) in item.RowMappings) {
                        rowToTableColumn[rName] = mapping;
                    }
                }
            }

            // Sort admitted items by their original appearance in worldArr
            admittedSqlItems.Sort(comparison: (a, b) => a.MinIndex.CompareTo(value: b.MinIndex));

            // Remove consumed rows from worldArr in reverse index order
            var sortedIndices = consumedRowIndices.OrderByDescending(keySelector: x => x).ToList();

            foreach (var idx in sortedIndices) {
                worldArr.RemoveAt(index: idx);
            }

            if (worldArr.Count == 0) {
                stateObj!.Remove(propertyName: "world");
                if (stateObj.Count == 0) {
                    root.Remove(propertyName: "state");
                }
            }
        }

        var sqlStatements = new List<string>(collection: admittedSqlItems.Select(selector: item => item.SqlText));

        // 2. Process rules into SQL rules only if all their state references belong to decompiled tables/slots
        if ((rulesArr is not null) && (rulesArr.Count > 0)) {
            var originalRules = ((JsonArray)rulesArr.DeepClone());
            var candidateRules = new List<(int Index, string SqlText)>();
            var claimedRuleIndices = new HashSet<int>();

            for (var i = 0; (i < rulesArr.Count); i++) {
                if (rulesArr[i] is not JsonObject ruleObj) {
                    continue;
                }

                var ruleName = ruleObj["name"]?.ToString();

                if (!IsValidSqlIdentifier(name: ruleName)) {
                    continue;
                }

                if (CanDecompileRuleToSql(rowToTableColumn: rowToTableColumn, rule: ruleObj)) {
                    var sqlRule = DecompileRuleToSql(rowKinds: rowKinds, rowToTableColumn: rowToTableColumn, rule: ruleObj);

                    if (sqlRule is not null) {
                        candidateRules.Add(item: (i, sqlRule));
                        claimedRuleIndices.Add(item: i);
                    }
                }
            }

            var maxNativeRuleIndex = -1;

            for (var i = 0; (i < rulesArr.Count); i++) {
                if (!claimedRuleIndices.Contains(item: i)) {
                    maxNativeRuleIndex = Math.Max(val1: maxNativeRuleIndex, val2: i);
                }
            }

            var activeCandidateRules = candidateRules
                .Where(predicate: r => (r.Index > maxNativeRuleIndex))
                .OrderBy(keySelector: r => r.Index)
                .ToList();

            while (activeCandidateRules.Count > 0) {
                var candidateRoot = ((JsonObject)root.DeepClone());
                var candidateRulesArr = (candidateRoot["rules"] as JsonArray);

                if (candidateRulesArr is not null) {
                    foreach (var (idx, _) in activeCandidateRules.OrderByDescending(keySelector: r => r.Index)) {
                        candidateRulesArr.RemoveAt(index: idx);
                    }
                    if (candidateRulesArr.Count == 0) {
                        candidateRoot.Remove(propertyName: "rules");
                    }
                }

                var nativeText = Decompile(embeddings: embeddings, root: candidateRoot);
                var sbCandidate = new StringBuilder();

                sbCandidate.AppendLine(value: "sql {");
                foreach (var item in admittedSqlItems) {
                    sbCandidate.AppendLine(value: item.SqlText);
                }
                foreach (var (_, sqlRule) in activeCandidateRules) {
                    sbCandidate.AppendLine(value: sqlRule);
                }
                sbCandidate.Append(value: '}');
                var sqlBlockText = sbCandidate.ToString();

                var candidateDoc = (string.IsNullOrWhiteSpace(value: nativeText)
                    ? $"schema: \"{WorldDocumentVocabulary.Schema}\"\n\n{sqlBlockText}"
                    : $"{nativeText.TrimEnd()}\n\n{sqlBlockText}");

                var diagnostics = new DiagnosticBag();
                var parseResult = PuckParser.ParseDocumentWithDiagnostics(
                    source: candidateDoc,
                    diagnostics: diagnostics,
                    vocabulary: WorldDocumentVocabulary.Instance
                );

                if (diagnostics.HasErrors || (parseResult.Value is null)) {
                    activeCandidateRules.Clear();
                    break;
                }

                var loweringDiagnostics = new DiagnosticBag();
                var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
                    document: parseResult.Value,
                    diagnostics: loweringDiagnostics,
                    embeddings: embeddings
                ).Value;

                if (loweringDiagnostics.HasErrors || (lowered is null)) {
                    activeCandidateRules.Clear();
                    break;
                }

                var loweredRules = (lowered["rules"] as JsonArray);

                if ((loweredRules is null) || (loweredRules.Count != originalRules.Count)) {
                    activeCandidateRules.Clear();
                    break;
                }

                var rulesToDemote = new List<int>();

                for (var j = 0; (j < activeCandidateRules.Count); j++) {
                    var ruleIndex = activeCandidateRules[j].Index;

                    if (!JsonNode.DeepEquals(node1: loweredRules[ruleIndex], node2: originalRules[ruleIndex])) {
                        rulesToDemote.Add(item: ruleIndex);
                    }
                }

                if (rulesToDemote.Count == 0) {
                    break;
                }

                foreach (var demotedIndex in rulesToDemote) {
                    maxNativeRuleIndex = Math.Max(val1: maxNativeRuleIndex, val2: demotedIndex);
                }

                activeCandidateRules.RemoveAll(match: r => (r.Index <= maxNativeRuleIndex));
            }

            foreach (var (_, sqlRule) in activeCandidateRules) {
                sqlStatements.Add(item: sqlRule);
            }

            var consumedRuleIndices = new HashSet<int>(collection: activeCandidateRules.Select(selector: r => r.Index));
            var sortedRuleIndices = consumedRuleIndices.OrderByDescending(keySelector: x => x).ToList();

            foreach (var idx in sortedRuleIndices) {
                rulesArr.RemoveAt(index: idx);
            }

            if (rulesArr.Count == 0) {
                root.Remove(propertyName: "rules");
            }
        }

        if (sqlStatements.Count == 0) {
            return string.Empty;
        }

        var sb = new StringBuilder();

        sb.AppendLine(value: "sql {");
        for (var i = 0; (i < sqlStatements.Count); i++) {
            if (i > 0) {
                sb.AppendLine();
            }
            sb.Append(value: sqlStatements[i].TrimEnd());
            sb.AppendLine();
        }
        sb.Append(value: '}');

        return sb.ToString();
    }
    private static bool HasUnsupportedTraits(JsonObject row) {
        foreach (var (prop, _) in row) {
            if (prop is not ("name" or "kind" or "cells" or "capacity" or "min" or "max" or "overflow" or "advance" or "dynamics" or "domain" or "value" or "space" or "evicts")) {
                return true;
            }
        }
        return false;
    }
    private static bool VerifyCandidateTable(string sqlText, IReadOnlyList<JsonObject> originalRows, EmbeddingLock? embeddings = null) {
        try {
            var puckSource = $"schema: \"puck.world.definition.v1\"\n\nsql {{\n{sqlText}\n}}\n";
            var diagnostics = new DiagnosticBag();
            var parseResult = PuckParser.ParseDocumentWithDiagnostics(
                source: puckSource,
                diagnostics: diagnostics,
                vocabulary: WorldDocumentVocabulary.Instance
            );

            if (diagnostics.HasErrors || (parseResult.Value is null)) {
                return false;
            }

            var loweringDiagnostics = new DiagnosticBag();
            var lowered = WorldDocumentEmitter.LowerWithDiagnostics(
                document: parseResult.Value,
                diagnostics: loweringDiagnostics,
                embeddings: embeddings
            ).Value;

            if (loweringDiagnostics.HasErrors || (lowered is null)) {
                return false;
            }

            var loweredRows = (lowered["state"]?["world"] as JsonArray);

            if ((loweredRows is null) || (loweredRows.Count != originalRows.Count)) {
                return false;
            }

            for (var i = 0; (i < originalRows.Count); i++) {
                if (!JsonNode.DeepEquals(node1: loweredRows[i], node2: originalRows[i])) {
                    return false;
                }
            }

            return true;
        } catch {
            return false;
        }
    }

    private sealed record MultiColumnCandidateGroup(
        List<int> Indices,
        List<JsonObject> Rows,
        List<string> Keys
    );

    private static List<string>? ExtractKeys(JsonArray cellsArr) {
        var keys = new List<string>();

        foreach (var c in cellsArr) {
            if (c is not JsonObject cellObj) {
                return null;
            }
            if (cellObj.Any(predicate: p => (p.Key is not ("key" or "value")))) {
                return null;
            }
            if (cellObj["key"]?.ToString() is string k) {
                keys.Add(item: k);
            } else {
                return null;
            }
        }
        return keys;
    }
    private static bool KeysEqual(List<string> a, List<string> b) {
        if (a.Count != b.Count) {
            return false;
        }
        for (var i = 0; (i < a.Count); i++) {
            if (!string.Equals(a: a[i], b: b[i], comparisonType: StringComparison.Ordinal)) {
                return false;
            }
        }
        return true;
    }
    private static List<MultiColumnCandidateGroup> FindMultiColumnCandidateGroups(JsonArray worldArr) {
        var groups = new List<MultiColumnCandidateGroup>();

        var i = 0;

        while (i < worldArr.Count) {
            if ((worldArr[i] is not JsonObject firstRow) ||
                HasUnsupportedTraits(row: firstRow) ||
                (firstRow["domain"] is not null)) {
                i++;
                continue;
            }

            var firstCells = (firstRow["cells"] as JsonArray);
            var hasCapacity = (firstRow["capacity"] is not null);

            if (((firstCells is null) || (firstCells.Count == 0)) && !hasCapacity) {
                i++;
                continue;
            }

            var firstKeys = (((firstCells is not null) && (firstCells.Count > 0)) ? ExtractKeys(cellsArr: firstCells) : []);

            if (firstKeys is null) {
                i++;
                continue;
            }

            var run = new List<(int Index, JsonObject Row)> { (i, firstRow) };
            var j = (i + 1);

            while (j < worldArr.Count) {
                if ((worldArr[j] is not JsonObject nextRow) ||
                    HasUnsupportedTraits(row: nextRow) ||
                    (nextRow["domain"] is not null)) {
                    break;
                }
                var nextCells = (nextRow["cells"] as JsonArray);
                var nextHasCap = (nextRow["capacity"] is not null);

                if (((nextCells is null) || (nextCells.Count == 0)) && !nextHasCap) {
                    break;
                }
                var nextKeys = (((nextCells is not null) && (nextCells.Count > 0)) ? ExtractKeys(cellsArr: nextCells) : []);

                if ((nextKeys is null) || !KeysEqual(a: firstKeys, b: nextKeys)) {
                    break;
                }
                if (!JsonNode.DeepEquals(node1: firstRow["capacity"], node2: nextRow["capacity"]) ||
                    !JsonNode.DeepEquals(node1: firstRow["evicts"], node2: nextRow["evicts"])) {
                    break;
                }
                var nextName = (nextRow["name"]?.ToString() ?? "");
                var firstName = (firstRow["name"]?.ToString() ?? "");
                var common = FindCommonPrefix(strings: [firstName, nextName]);

                if ((common.Length < 2) || !IsValidSqlIdentifier(name: common)) {
                    break;
                }
                run.Add(item: (j, nextRow));
                j++;
            }

            if (run.Count >= 2) {
                var rowNames = run.Select(selector: x => (x.Row["name"]?.ToString() ?? "")).ToList();
                var prefix = FindCommonPrefix(strings: rowNames);

                if ((prefix.Length >= 2) && IsValidSqlIdentifier(name: prefix)) {
                    var allSuffixesValid = true;

                    foreach (var name in rowNames) {
                        if ((name.Length <= prefix.Length) || !char.IsUpper(c: name[prefix.Length])) {
                            allSuffixesValid = false;
                            break;
                        }
                        var colName = (char.ToLowerInvariant(c: name[prefix.Length]) + name[(prefix.Length + 1)..]);

                        if (!IsValidSqlIdentifier(name: colName)) {
                            allSuffixesValid = false;
                            break;
                        }
                    }

                    if (allSuffixesValid) {
                        groups.Add(item: new MultiColumnCandidateGroup(
                            Indices: run.Select(selector: x => x.Index).ToList(),
                            Rows: run.Select(selector: x => x.Row).ToList(),
                            Keys: firstKeys
                        ));
                        i = j;
                        continue;
                    }
                }
            } else if (run.Count == 1) {
                var name = (run[0].Row["name"]?.ToString() ?? "");
                var splitIdx = -1;

                for (var k = 1; (k < name.Length); k++) {
                    if (char.IsUpper(c: name[k])) {
                        splitIdx = k;
                        break;
                    }
                }
                if (splitIdx > 0) {
                    var prefix = name[..splitIdx];
                    var colName = (char.ToLowerInvariant(c: name[splitIdx]) + name[(splitIdx + 1)..]);

                    if ((prefix.Length >= 2) && IsValidSqlIdentifier(name: prefix) && IsValidSqlIdentifier(name: colName)) {
                        groups.Add(item: new MultiColumnCandidateGroup(
                            Indices: [run[0].Index],
                            Rows: [run[0].Row],
                            Keys: firstKeys
                        ));
                        i = j;
                        continue;
                    }
                }
            }

            i++;
        }

        return groups;
    }
    private static (string? Sql, Dictionary<string, (string TableName, string ColumnName)> RowMap) DecompileMultiColumnTable(List<JsonObject> rows, EmbeddingLock? embeddings = null) {
        var emptyMap = new Dictionary<string, (string TableName, string ColumnName)>(comparer: StringComparer.Ordinal);

        if (rows.Count == 0) {
            return (null, emptyMap);
        }

        var rowNames = rows.Select(selector: r => (r["name"]?.ToString() ?? "")).ToList();

        if (rowNames.Any(predicate: string.IsNullOrEmpty)) {
            return (null, emptyMap);
        }

        string tableName;
        string? singleColName = null;

        if (rows.Count == 1) {
            var rName = rowNames[0];
            var splitIdx = -1;

            for (var k = 1; (k < rName.Length); k++) {
                if (char.IsUpper(c: rName[k])) {
                    splitIdx = k;
                    break;
                }
            }
            if (splitIdx <= 0) {
                return (null, emptyMap);
            }
            tableName = rName[..splitIdx];
            singleColName = (char.ToLowerInvariant(c: rName[splitIdx]) + rName[(splitIdx + 1)..]);
            if ((tableName.Length < 2) || !IsValidSqlIdentifier(name: tableName) || !IsValidSqlIdentifier(name: singleColName)) {
                return (null, emptyMap);
            }
        } else {
            var commonPrefix = FindCommonPrefix(strings: rowNames);

            if ((commonPrefix.Length < 2) || !IsValidSqlIdentifier(name: commonPrefix)) {
                return (null, emptyMap);
            }
            tableName = commonPrefix;
        }

        var rowMap = new Dictionary<string, (string TableName, string ColumnName)>(comparer: StringComparer.Ordinal);
        var colDefs = new List<string>();
        var colNames = new List<string>();

        for (var i = 0; (i < rows.Count); i++) {
            var row = rows[i];
            var rName = rowNames[i];
            string colName;

            if (rows.Count == 1) {
                colName = singleColName!;
            } else if ((rName.Length > tableName.Length) && rName.StartsWith(comparisonType: StringComparison.Ordinal, value: tableName)) {
                var suffix = rName[tableName.Length..];

                if (char.IsUpper(c: suffix[0])) {
                    colName = (char.ToLowerInvariant(c: suffix[0]) + suffix[1..]);
                } else {
                    return (null, emptyMap);
                }
            } else {
                return (null, emptyMap);
            }

            if (!IsValidSqlIdentifier(name: colName)) {
                return (null, emptyMap);
            }

            rowMap[rName] = (tableName, colName);
            colNames.Add(item: colName);

            var kind = (row["kind"]?.ToString() ?? "Int");
            var space = row["space"]?.ToString();
            var sqlType = (((kind == "Vector") && (space is not null)) ? $"VECTOR({space})" : MapKindToSqlType(kind: kind));

            var colDef = new StringBuilder(value: $"        {colName} {sqlType}");

            var allNonNull = ((rows[i]["cells"] is JsonArray ca) && (ca.Count > 0));

            if (allNonNull) {
                foreach (var cellNode in ((JsonArray)rows[i]["cells"]!)) {
                    if ((cellNode is not JsonObject co) || (co["value"] is null)) {
                        allNonNull = false;
                        break;
                    }
                }
            }
            if (allNonNull) {
                colDef.Append(value: " NOT NULL");
            }

            var hasMin = (row["min"] is not null);
            var hasMax = (row["max"] is not null);

            if (hasMin && hasMax) {
                colDef.Append(CultureInfo.InvariantCulture, $" CHECK ({colName} BETWEEN {row["min"]} AND {row["max"]})");
            } else if (hasMin) {
                colDef.Append(CultureInfo.InvariantCulture, $" CHECK ({colName} >= {row["min"]})");
            } else if (hasMax) {
                colDef.Append(CultureInfo.InvariantCulture, $" CHECK ({colName} <= {row["max"]})");
            }

            if (string.Equals(a: row["overflow"]?.ToString(), b: "Saturate", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                colDef.Append(value: " ON OVERFLOW SATURATE");
            }

            if ((row["advance"] is JsonObject advObj) &&
                (advObj["perSecondNumerator"] is JsonValue numVal) && numVal.TryGetValue<long>(value: out var num) &&
                (advObj["perSecondDenominator"] is JsonValue denVal) && denVal.TryGetValue<long>(value: out var den)) {
                if (!CanExpressRateAsLiteral(denominator: den, numerator: num)) {
                    return (null, emptyMap);
                }
                var rateStr = FormatStateRate(advance: advObj);

                colDef.Append(CultureInfo.InvariantCulture, $" ADVANCE {rateStr} PER SECOND");
            }

            if ((row["dynamics"] is JsonObject dynObj) && (dynObj["row"]?.ToString() is string dynRow)) {
                colDef.Append(CultureInfo.InvariantCulture, $" DYNAMICS {dynRow}");
            }

            colDefs.Add(item: colDef.ToString());
        }

        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"    CREATE TABLE {tableName} (");
        sb.AppendLine(CultureInfo.InvariantCulture, $"        id TEXT PRIMARY KEY,");

        for (var i = 0; (i < colDefs.Count); i++) {
            var sep = ((i < (colDefs.Count - 1)) ? "," : "");

            sb.AppendLine(handler: $"{colDefs[i]}{sep}");
        }

        int? cap = null;

        if ((rows[0]["capacity"] is JsonValue capVal) && capVal.TryGetValue<int>(value: out var c)) {
            cap = c;
        }

        var isEvicts = ((rows[0]["evicts"] is JsonValue evVal) && evVal.TryGetValue<bool>(value: out var ev) && ev);

        if (cap.HasValue && isEvicts) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    ) CAPACITY {cap.Value} EVICTS;");
        } else if (cap.HasValue) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    ) CAPACITY {cap.Value};");
        } else {
            sb.AppendLine(value: "    );");
        }

        if ((rows[0]["cells"] is JsonArray firstRowCells) && (firstRowCells.Count > 0)) {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"    INSERT INTO {tableName} (id, {string.Join(separator: ", ", values: colNames)}) VALUES\n");

            for (var kIdx = 0; (kIdx < firstRowCells.Count); kIdx++) {
                var key = (firstRowCells[kIdx]?["key"]?.ToString() ?? "");
                var vals = new List<string> { $"'{EscapeSqlString(text: key)}'" };

                foreach (var r in rows) {
                    var cells = ((JsonArray)r["cells"]!);
                    var cVal = cells[kIdx]?["value"];

                    vals.Add(item: FormatSqlLiteral(cVal, (r["kind"]?.ToString() ?? "Int"), r["space"]?.ToString(), embeddings));
                }

                var sep = ((kIdx < (firstRowCells.Count - 1)) ? "," : ";");

                sb.AppendLine(CultureInfo.InvariantCulture, $"        ({string.Join(separator: ", ", values: vals)}){sep}");
            }
        }

        return (sb.ToString(), rowMap);
    }
    private static bool IsSlotRow(JsonObject row) {
        return ((row["cells"] is null) &&
               (row["capacity"] is null) &&
               (row["domain"] is null) &&
               !HasUnsupportedTraits(row: row));
    }
    private static string? DecompileSlotToSql(JsonObject row, EmbeddingLock? embeddings = null) {
        var name = row["name"]?.ToString();

        if (!IsValidSqlIdentifier(name: name)) {
            return null;
        }

        var kind = (row["kind"]?.ToString() ?? "Int");
        var space = row["space"]?.ToString();
        var sqlType = (((kind == "Vector") && (space is not null)) ? $"VECTOR({space})" : MapKindToSqlType(kind: kind));

        var sb = new StringBuilder(value: $"    DECLARE {name} {sqlType}");

        if (row["value"] is JsonNode valNode) {
            sb.Append(CultureInfo.InvariantCulture, $" DEFAULT {FormatSqlLiteral(embeddings: embeddings, kind: kind, node: valNode, space: space)}");
        }

        var hasMin = (row["min"] is not null);
        var hasMax = (row["max"] is not null);

        if (hasMin && hasMax) {
            sb.Append(CultureInfo.InvariantCulture, $" CHECK ({name} BETWEEN {row["min"]} AND {row["max"]})");
        } else if (hasMin) {
            sb.Append(CultureInfo.InvariantCulture, $" CHECK ({name} >= {row["min"]})");
        } else if (hasMax) {
            sb.Append(CultureInfo.InvariantCulture, $" CHECK ({name} <= {row["max"]})");
        }

        if (string.Equals(a: row["overflow"]?.ToString(), b: "Saturate", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            sb.Append(value: " ON OVERFLOW SATURATE");
        }

        if ((row["advance"] is JsonObject advObj) &&
            (advObj["perSecondNumerator"] is JsonValue numVal) && numVal.TryGetValue<long>(value: out var num) &&
            (advObj["perSecondDenominator"] is JsonValue denVal) && denVal.TryGetValue<long>(value: out var den)) {
            if (!CanExpressRateAsLiteral(denominator: den, numerator: num)) {
                return null;
            }
            var rateStr = FormatStateRate(advance: advObj);

            sb.Append(CultureInfo.InvariantCulture, $" ADVANCE {rateStr} PER SECOND");
        }

        if ((row["dynamics"] is JsonObject dynObj) && (dynObj["row"]?.ToString() is string dynRow)) {
            sb.Append(CultureInfo.InvariantCulture, $" DYNAMICS {dynRow}");
        }

        sb.AppendLine(value: ";");
        return sb.ToString();
    }
    private static bool IsPileRow(JsonObject row) {
        if ((row["domain"] is JsonObject domainObj) &&
            string.Equals(a: domainObj["$type"]?.ToString(), b: "keysOf", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            (domainObj["ordered"] is JsonValue ordVal) && ordVal.TryGetValue<bool>(value: out var isOrd) && isOrd &&
            ((row["cells"] is null) || ((row["cells"] is JsonArray ca) && (ca.Count == 0))) &&
            (row["visibility"] is null)) {
            var name = row["name"]?.ToString();
            var refTable = domainObj["row"]?.ToString();

            if (!IsValidSqlIdentifier(name: name) || !IsValidSqlIdentifier(name: refTable)) {
                return false;
            }
            return true;
        }
        return false;
    }
    private static string? DecompilePileToSql(JsonObject row) {
        var name = row["name"]?.ToString();
        var domainObj = (row["domain"] as JsonObject);
        var refTable = domainObj?["row"]?.ToString();

        if (string.IsNullOrEmpty(value: name) || string.IsNullOrEmpty(value: refTable)) {
            return null;
        }

        var sb = new StringBuilder(value: $"    CREATE TABLE {name} (id TEXT PRIMARY KEY REFERENCES {refTable}) ORDERED");

        if ((row["capacity"] is JsonValue capVal) && capVal.TryGetValue<int>(value: out var cap)) {
            sb.Append(CultureInfo.InvariantCulture, $" CAPACITY {cap}");
        }
        sb.AppendLine(value: ";");
        return sb.ToString();
    }
    private static bool IsKeyOnlyRow(JsonObject row) {
        if (string.Equals(a: row["kind"]?.ToString(), b: "Bool", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            (row["domain"] is null) &&
            (row["visibility"] is null) &&
            (row["cells"] is JsonArray cellsArr)) {
            if ((cellsArr.Count == 0) && (row["capacity"] is null)) {
                return false;
            }
            var name = row["name"]?.ToString();

            if (!IsValidSqlIdentifier(name: name)) {
                return false;
            }
            foreach (var c in cellsArr) {
                if (c is not JsonObject cellObj) {
                    return false;
                }
                if (cellObj.Any(predicate: p => (p.Key is not ("key" or "value")))) {
                    return false;
                }
                if ((cellObj["value"] is JsonValue v) && v.TryGetValue<bool>(value: out var b) && !b) {
                    return false;
                }
            }
            return true;
        }
        return false;
    }
    private static string? DecompileKeyOnlyToSql(JsonObject row) {
        var name = row["name"]?.ToString();

        if (string.IsNullOrEmpty(value: name)) {
            return null;
        }

        var sb = new StringBuilder(value: $"    CREATE TABLE {name} (id TEXT PRIMARY KEY)");

        if ((row["capacity"] is JsonValue capVal) && capVal.TryGetValue<int>(value: out var cap)) {
            sb.Append(CultureInfo.InvariantCulture, $" CAPACITY {cap}");
        }
        sb.AppendLine(value: ";");

        if ((row["cells"] is JsonArray cellsArr) && (cellsArr.Count > 0)) {
            sb.Append(CultureInfo.InvariantCulture, $"    INSERT INTO {name} (id) VALUES\n");
            for (var i = 0; (i < cellsArr.Count); i++) {
                var k = (cellsArr[i]?["key"]?.ToString() ?? "");
                var sep = ((i < (cellsArr.Count - 1)) ? "," : ";");

                sb.AppendLine(CultureInfo.InvariantCulture, $"        ('{EscapeSqlString(text: k)}'){sep}");
            }
        }

        return sb.ToString();
    }
    private static bool CanDecompileRuleToSql(
        JsonObject rule,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        if ((rule["decision"] is not null) ||
            (rule["zones"] is not null) ||
            (rule["locals"] is not null)) {
            return false;
        }

        var mode = rule["mode"]?.ToString();

        if (mode is not ("Edge" or "Level")) {
            return false;
        }

        if ((rule["effects"] is not JsonArray effects) || (effects.Count == 0)) {
            return false;
        }

        if (effects.Count > 1) {
            return false;
        }

        var forEach = rule["forEach"]?.ToString();

        if (forEach is not null) {
            return false;
        }

        var gate = (rule["gate"] as JsonObject);

        if ((gate is not null) && !CanDecompilePredicate(gate, isForEach: false, rowToTableColumn)) {
            return false;
        }

        if ((effects.Count == 1) && (effects[0] is JsonObject txnObj) && string.Equals(a: txnObj["$type"]?.ToString(), b: "transaction", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if ((gate is not null) || (forEach is not null)) {
                return false;
            }
            if ((txnObj["effects"] is not JsonArray txnEffects) || (txnEffects.Count == 0)) {
                return false;
            }
            foreach (var te in txnEffects) {
                if ((te is not JsonObject teObj) || !CanDecompileEffect(teObj, forEach: null, rowToTableColumn)) {
                    return false;
                }
            }
            if (txnObj["onFailure"] is JsonArray failEffects) {
                foreach (var fe in failEffects) {
                    if ((fe is not JsonObject feObj) || !CanDecompileEffect(feObj, forEach: null, rowToTableColumn)) {
                        return false;
                    }
                }
            }
            return true;
        }

        var singleEff = (effects[0] as JsonObject);

        if (singleEff is null) {
            return false;
        }

        var singleType = singleEff["$type"]?.ToString();

        if (singleType is "setState" or "addState" or "removeStateCell") {
            return CanDecompileEffect(singleEff, forEach: null, rowToTableColumn);
        }
        if (singleType is "if") {
            if (gate is not null) {
                return false;
            }
            return CanDecompileEffect(singleEff, forEach: null, rowToTableColumn);
        }
        if (singleType is "transformState") {
            if (gate is not null) {
                return false;
            }
            return CanDecompileEffect(singleEff, forEach: null, rowToTableColumn);
        }

        return false;
    }
    private static bool CanDecompileEffect(
        JsonObject eff,
        string? forEach,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        var type = eff["$type"]?.ToString();

        switch (type) {
            case "transformState":
                if ((eff["transform"] is JsonObject innerNearest) && (innerNearest["$type"]?.ToString() is "nearest")) {
                    return CanDecompileNearest(eff: innerNearest, rowToTableColumn: rowToTableColumn);
                }
                return false;
            case "setState":
            case "addState": {
                    if ((eff["fromState"] is not null) || (eff["fromKey"] is not null) || (eff["valueSeconds"] is not null)) {
                        return false;
                    }
                    var state = eff["state"]?.ToString();

                    if ((state is null) || !rowToTableColumn.ContainsKey(key: state)) {
                        return false;
                    }
                    var key = eff["key"]?.ToString();

                    if ((forEach is not null) && (key != "$each")) {
                        return false;
                    }
                    if ((forEach is null) && (key == "$each")) {
                        return false;
                    }
                    if ((WorldExpressionJson.Text(node: eff["expression"]) is { Length: > 0 } expr) && expr.Contains(value: '$') && !expr.Contains(value: "$each")) {
                        return false;
                    }
                    return true;
                }
            case "removeStateCell": {
                    var state = eff["state"]?.ToString();

                    if ((state is null) || !rowToTableColumn.TryGetValue(key: state, value: out var target)) {
                        return false;
                    }
                    var key = eff["key"]?.ToString();

                    if ((forEach is not null) && (key != "$each")) {
                        return false;
                    }
                    if ((forEach is null) && (key == "$each")) {
                        return false;
                    }
                    var tableRowCount = rowToTableColumn.Values.Count(predicate: v => (v.TableName == target.TableName));

                    if (tableRowCount > 1) {
                        return false;
                    }
                    return true;
                }
            case "if":
                if ((eff["condition"] is JsonObject cond) && !CanDecompilePredicate(cond, isForEach: (forEach is not null), rowToTableColumn)) {
                    return false;
                }
                if (eff["then"] is JsonArray thenArr) {
                    foreach (var t in thenArr) {
                        if ((t is not JsonObject tObj) || !CanDecompileEffect(eff: tObj, forEach: forEach, rowToTableColumn: rowToTableColumn)) {
                            return false;
                        }
                    }
                }
                if (eff["else"] is JsonArray elseArr) {
                    foreach (var el in elseArr) {
                        if ((el is not JsonObject elObj) || !CanDecompileEffect(eff: elObj, forEach: forEach, rowToTableColumn: rowToTableColumn)) {
                            return false;
                        }
                    }
                }
                return true;
            default:
                return false;
        }
    }
    private static bool CanDecompilePredicate(
        JsonObject pred,
        bool isForEach,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        var type = pred["$type"]?.ToString();

        switch (type) {
            case "compareState": {
                    var state = pred["state"]?.ToString();

                    if ((state is null) || !rowToTableColumn.ContainsKey(key: state) || state.StartsWith(value: '$')) {
                        return false;
                    }
                    if (pred["comparandState"] is not null) {
                        return false;
                    }
                    var key = pred["key"]?.ToString();

                    if (isForEach) {
                        if ((key is not null) && (key != "$each")) {
                            return false;
                        }
                    } else if (key == "$each") {
                        return false;
                    }
                    return true;
                }
            case "all":
            case "any":
                if (pred["predicates"] is JsonArray arr) {
                    foreach (var p in arr) {
                        if ((p is not JsonObject pObj) || !CanDecompilePredicate(isForEach: isForEach, pred: pObj, rowToTableColumn: rowToTableColumn)) {
                            return false;
                        }
                    }
                    return true;
                }
                return false;
            case "not":
                return ((pred["predicate"] is JsonObject inner) && CanDecompilePredicate(isForEach: isForEach, pred: inner, rowToTableColumn: rowToTableColumn));
            case "compareValue":
                var kind = pred["kind"]?.ToString();
                if (!string.Equals(a: kind, b: "Fixed", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(a: kind, b: "Int", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(a: kind, b: "Bool", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return false;
                }
                var left = WorldExpressionJson.Text(node: pred["left"]);
                var right = WorldExpressionJson.Text(node: pred["right"]);
                if (left.Contains(value: '$') || right.Contains(value: '$')) {
                    return false;
                }
                return true;
            default:
                return false;
        }
    }
    private static string? DecompileRuleToSql(
        JsonObject rule,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn,
        Dictionary<string, string> rowKinds
    ) {
        var name = rule["name"]?.ToString();

        if (string.IsNullOrEmpty(value: name)) {
            return null;
        }

        var mode = rule["mode"]?.ToString();
        var firingMode = (string.Equals(a: mode, b: "Edge", comparisonType: StringComparison.OrdinalIgnoreCase) ? "ON ENTER" : "EVERY TICK");

        var sb = new StringBuilder();

        sb.Append(CultureInfo.InvariantCulture, $"    CREATE RULE {name} {firingMode} AS");

        var effects = ((JsonArray)rule["effects"]!);
        var forEach = rule["forEach"]?.ToString();
        var gate = (rule["gate"] as JsonObject);

        if ((effects.Count == 1) && (effects[0] is JsonObject singleEff) && string.Equals(a: singleEff["$type"]?.ToString(), b: "transaction", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            sb.AppendLine();
            sb.AppendLine(value: "    BEGIN ATOMIC");
            if (singleEff["effects"] is JsonArray txnEffects) {
                foreach (var te in txnEffects) {
                    if (te is JsonObject teObj) {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"        {DecompileEffectStatement(teObj, forEach: null, gate: null, rowToTableColumn, rowKinds)};");
                    }
                }
            }
            if (singleEff["onFailure"] is JsonArray failEffects) {
                sb.AppendLine(value: "    EXCEPTION");
                foreach (var fe in failEffects) {
                    if (fe is JsonObject feObj) {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"        {DecompileEffectStatement(feObj, forEach: null, gate: null, rowToTableColumn, rowKinds)};");
                    }
                }
            }
            sb.Append(value: "    END;");
        } else if (effects.Count == 1) {
            var effObj = ((JsonObject)effects[0]!);

            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"        {DecompileEffectStatement(eff: effObj, forEach: forEach, gate: gate, rowKinds: rowKinds, rowToTableColumn: rowToTableColumn)};");
        } else {
            return null;
        }

        return sb.ToString();
    }
    private static string DecompileEffectStatement(
        JsonObject eff,
        string? forEach,
        JsonObject? gate,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn,
        Dictionary<string, string> rowKinds
    ) {
        var type = eff["$type"]?.ToString();
        var state = (eff["state"]?.ToString() ?? "");
        var key = eff["key"]?.ToString();

        rowToTableColumn.TryGetValue(key: state, value: out var target);
        var tName = (target.TableName ?? state);
        var cName = (string.IsNullOrEmpty(value: target.ColumnName) ? "value" : target.ColumnName);

        rowKinds.TryGetValue(key: state, value: out var stateKind);

        switch (type) {
            case "transformState":
                if ((eff["transform"] is JsonObject innerNearest) && (innerNearest["$type"]?.ToString() is "nearest")) {
                    return DecompileNearestToSql(eff: innerNearest, rowToTableColumn: rowToTableColumn);
                }
                throw new NotSupportedException(message: $"Cannot decompile transform '{eff["transform"]?["$type"]}' to SQL.");

            case "setState": {
                    var valStr = FormatEffectValue(eff: eff, kind: stateKind);

                    if ((key == "$each") || (forEach is not null)) {
                        var whereClause = ((gate is not null) ? $" WHERE {DecompilePredicate(gate: gate, rowToTableColumn: rowToTableColumn)}" : "");

                        return $"UPDATE {tName} SET {cName} = {valStr}{whereClause}";
                    } else if (key is not null) {
                        return $"UPDATE {tName} SET {cName} = {valStr} WHERE id = '{EscapeSqlString(text: key)}'";
                    }
                    var slotWhere = ((gate is not null) ? $" WHERE {DecompilePredicate(gate: gate, rowToTableColumn: rowToTableColumn)}" : "");

                    return $"UPDATE {tName} SET {cName} = {valStr}{slotWhere}";
                }

            case "addState": {
                    var valStr = FormatEffectValue(eff: eff, kind: stateKind);

                    if ((key == "$each") || (forEach is not null)) {
                        var whereClause = ((gate is not null) ? $" WHERE {DecompilePredicate(gate: gate, rowToTableColumn: rowToTableColumn)}" : "");

                        return $"UPDATE {tName} SET {cName} = {cName} + {valStr}{whereClause}";
                    } else if (key is not null) {
                        return $"UPDATE {tName} SET {cName} = {cName} + {valStr} WHERE id = '{EscapeSqlString(text: key)}'";
                    }
                    var slotWhere = ((gate is not null) ? $" WHERE {DecompilePredicate(gate: gate, rowToTableColumn: rowToTableColumn)}" : "");

                    return $"UPDATE {tName} SET {cName} = {cName} + {valStr}{slotWhere}";
                }

            case "removeStateCell": {
                    if ((key == "$each") || (forEach is not null)) {
                        var whereClause = ((gate is not null) ? $" WHERE {DecompilePredicate(gate: gate, rowToTableColumn: rowToTableColumn)}" : "");

                        return $"DELETE FROM {tName}{whereClause}";
                    }
                    if (key is not null) {
                        return $"DELETE FROM {tName} WHERE id = '{EscapeSqlString(text: key)}'";
                    }
                    return $"DELETE FROM {tName}";
                }

            case "if": {
                    var cond = (eff["condition"] as JsonObject);
                    var condStr = ((cond is not null) ? DecompilePredicate(gate: cond, rowToTableColumn: rowToTableColumn) : "TRUE");
                    var ifSb = new StringBuilder(value: $"IF {condStr} THEN\n");

                    if (eff["then"] is JsonArray thenArr) {
                        foreach (var t in thenArr) {
                            if (t is JsonObject tObj) {
                                ifSb.AppendLine(CultureInfo.InvariantCulture, $"            {DecompileEffectStatement(eff: tObj, forEach: forEach, gate: null, rowKinds: rowKinds, rowToTableColumn: rowToTableColumn)};");
                            }
                        }
                    }
                    if (eff["else"] is JsonArray elseArr) {
                        ifSb.AppendLine(value: "        ELSE");
                        foreach (var e in elseArr) {
                            if (e is JsonObject eObj) {
                                ifSb.AppendLine(CultureInfo.InvariantCulture, $"            {DecompileEffectStatement(eff: eObj, forEach: forEach, gate: null, rowKinds: rowKinds, rowToTableColumn: rowToTableColumn)};");
                            }
                        }
                    }
                    ifSb.Append(value: "        END IF");
                    return ifSb.ToString();
                }

            default:
                throw new NotSupportedException(message: $"Cannot decompile effect type '{type}' to SQL.");
        }
    }
    private static string DecompilePredicate(
        JsonObject gate,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        var type = gate["$type"]?.ToString();

        switch (type) {
            case "compareState": {
                    var state = (gate["state"]?.ToString() ?? "");

                    rowToTableColumn.TryGetValue(key: state, value: out var target);
                    var colName = (string.IsNullOrEmpty(value: target.ColumnName) ? state : target.ColumnName);

                    var cmp = gate["comparison"]?.ToString() switch {
                        "Equal" => "=",
                        "NotEqual" => "<>",
                        "Less" => "<",
                        "LessOrEqual" => "<=",
                        "Greater" => ">",
                        "GreaterOrEqual" => ">=",
                        _ => "="
                    };
                    var val = (gate["value"]?.ToString() ?? "0");

                    return $"{colName} {cmp} {val}";
                }
            case "compareValue": {
                    var left = WorldExpressionJson.Text(node: gate["left"]);
                    var right = WorldExpressionJson.Text(node: gate["right"]);
                    var cmp = gate["comparison"]?.ToString() switch {
                        "Equal" => "=",
                        "NotEqual" => "<>",
                        "Less" => "<",
                        "LessOrEqual" => "<=",
                        "Greater" => ">",
                        "GreaterOrEqual" => ">=",
                        _ => "="
                    };

                    return $"{left} {cmp} {right}";
                }
            case "all": {
                    if ((gate["predicates"] is JsonArray arr) && (arr.Count > 0)) {
                        return string.Join(separator: " AND ", values: arr.OfType<JsonObject>().Select(selector: p => DecompilePredicate(gate: p, rowToTableColumn: rowToTableColumn)));
                    }
                    return "TRUE";
                }
            case "any": {
                    if ((gate["predicates"] is JsonArray arr) && (arr.Count > 0)) {
                        return string.Join(separator: " OR ", values: arr.OfType<JsonObject>().Select(selector: p => DecompilePredicate(gate: p, rowToTableColumn: rowToTableColumn)));
                    }
                    return "FALSE";
                }
            case "not": {
                    if (gate["predicate"] is JsonObject pred) {
                        return $"NOT ({DecompilePredicate(gate: pred, rowToTableColumn: rowToTableColumn)})";
                    }
                    return "NOT (TRUE)";
                }
            default:
                return "TRUE";
        }
    }
    private static string FormatEffectValue(JsonObject eff, string? kind) {
        if (eff["vector"] is JsonNode vec) {
            return $"vector('{EscapeSqlString(text: vec.ToString())}')";
        }
        if (eff["value"] is JsonNode v) {
            if (kind == "Bool") {
                if ((v is JsonValue jv) && jv.TryGetValue<bool>(value: out var b)) {
                    return (b ? "TRUE" : "FALSE");
                }
                var s = v.ToString().Trim();

                if ((s == "1") || string.Equals(a: s, b: "true", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return "TRUE";
                }
                if ((s == "0") || string.Equals(a: s, b: "false", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    return "FALSE";
                }
            }
            return v.ToString();
        }
        if (eff["text"] is JsonNode t) {
            return $"'{EscapeSqlString(text: t.ToString())}'";
        }
        if (eff["expression"] is JsonNode expr) {
            return WorldExpressionJson.Text(node: expr);
        }
        return "0";
    }
    private static string FindCommonPrefix(List<string> strings) {
        if (strings.Count == 0) {
            return string.Empty;
        }

        var prefix = strings[0];

        for (var i = 1; (i < strings.Count); i++) {
            while (!strings[i].StartsWith(comparisonType: StringComparison.Ordinal, value: prefix)) {
                prefix = prefix[..^1];
                if (prefix.Length == 0) {
                    return string.Empty;
                }
            }
        }
        return prefix;
    }
    private static string MapKindToSqlType(string kind) => kind switch {
        "Int" => "INT",
        "Fixed" => "FIXED",
        "Bool" => "BOOL",
        "Text" => "TEXT",
        "Vector" => "VECTOR",
        _ => "INT"
    };
    private static string FormatSqlLiteral(JsonNode? node, string kind, string? space = null, EmbeddingLock? embeddings = null) {
        if (node is null) {
            return "NULL";
        }

        if (kind == "Vector") {
            var raw = node.ToString();

            if ((embeddings is not null) && embeddings.TryFindText(spaceName: space, text: out var sourceText, vectorBase64Url: raw)) {
                return $"embed('{EscapeSqlString(text: sourceText)}')";
            }
            return $"vector('{EscapeSqlString(text: raw)}')";
        }

        if (kind == "Bool") {
            if ((node is JsonValue jv) && jv.TryGetValue<bool>(value: out var b)) {
                return (b ? "TRUE" : "FALSE");
            }
            return ((node.ToString().Equals(comparisonType: StringComparison.OrdinalIgnoreCase, value: "true")) ? "TRUE" : "FALSE");
        }

        if (kind == "Text") {
            return $"'{EscapeSqlString(text: node.ToString())}'";
        }

        return node.ToString();
    }
    private static string EscapeSqlString(string text) => text.Replace(newValue: "''", oldValue: "'");
    private static bool CanDecompileNearest(
        JsonObject eff,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        var from = eff["from"]?.ToString();
        var into = eff["into"]?.ToString();
        var query = eff["query"]?.ToString();

        if (string.IsNullOrEmpty(value: from) || string.IsNullOrEmpty(value: into) || string.IsNullOrEmpty(value: query)) {
            return false;
        }

        if ((eff["k"] is not JsonValue kVal) || !kVal.TryGetValue<int>(value: out var k) || (k <= 0)) {
            return false;
        }

        if ((eff["where"]?.ToString() is string whereRow) && !string.IsNullOrEmpty(value: whereRow)) {
            if (!rowToTableColumn.ContainsKey(key: whereRow)) {
                return false;
            }
        }

        return true;
    }
    private static string DecompileNearestToSql(
        JsonObject eff,
        Dictionary<string, (string TableName, string ColumnName)> rowToTableColumn
    ) {
        var from = (eff["from"]?.ToString() ?? "");
        var into = (eff["into"]?.ToString() ?? "");
        var query = (eff["query"]?.ToString() ?? "");
        var k = (eff["k"]?.ToString() ?? "1");
        var isFarthest = ((eff["farthest"] is JsonValue fVal) && fVal.TryGetValue<bool>(value: out var f) && f);

        rowToTableColumn.TryGetValue(key: into, value: out var intoTarget);
        var intoTable = (intoTarget.TableName ?? into);
        var scoreCol = (string.IsNullOrEmpty(value: intoTarget.ColumnName) ? "score" : intoTarget.ColumnName);

        rowToTableColumn.TryGetValue(key: from, value: out var fromTarget);
        var fromTable = (fromTarget.TableName ?? from);
        var fromVecCol = (string.IsNullOrEmpty(value: fromTarget.ColumnName) ? "embedding" : fromTarget.ColumnName);

        var scoreFunc = "similarity";

        if ((eff["threshold"]?.ToString() is string thStr) && int.TryParse(thStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && !thStr.Contains(value: '.')) {
            scoreFunc = "dot";
        }

        var queryExpr = query;

        if (rowToTableColumn.TryGetValue(key: query, value: out var qTarget)) {
            queryExpr = ((string.IsNullOrEmpty(value: qTarget.ColumnName) || (qTarget.ColumnName == "value")) ? qTarget.TableName : $"{qTarget.TableName}.{qTarget.ColumnName}");
        }

        var whereParts = new List<string>();

        if ((eff["where"]?.ToString() is string whereRow) && !string.IsNullOrEmpty(value: whereRow)) {
            if (rowToTableColumn.TryGetValue(key: whereRow, value: out var wTarget)) {
                whereParts.Add(item: ((string.IsNullOrEmpty(value: wTarget.ColumnName) || (wTarget.ColumnName == "value")) ? wTarget.TableName : wTarget.ColumnName));
            } else {
                whereParts.Add(item: whereRow);
            }
        }

        if ((eff["exclude"]?.ToString() is string excludeKey) && !string.IsNullOrEmpty(value: excludeKey)) {
            whereParts.Add(item: $"id <> '{EscapeSqlString(text: excludeKey)}'");
        }

        if ((eff["threshold"]?.ToString() is string threshold) && !string.IsNullOrEmpty(value: threshold)) {
            var cmp = (isFarthest ? "<=" : ">=");

            whereParts.Add(item: $"{scoreFunc}({fromVecCol}, {queryExpr}) {cmp} {threshold}");
        }

        var whereClause = ((whereParts.Count > 0) ? $" WHERE {string.Join(separator: " AND ", values: whereParts)}" : "");
        var orderClause = $" ORDER BY {fromVecCol} <=> {queryExpr}{(isFarthest ? " DESC" : "")}";

        return $"INSERT INTO {intoTable} (id, {scoreCol}) SELECT id, {scoreFunc}({fromVecCol}, {queryExpr}) FROM {fromTable}{whereClause}{orderClause} LIMIT {k}";
    }
}
