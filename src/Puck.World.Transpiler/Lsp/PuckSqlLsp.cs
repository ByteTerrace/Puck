using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Sql;

namespace Puck.World.Transpiler.Lsp;

/// <summary>Language Server Protocol support for the embedded State SQL dialect.</summary>
internal static class PuckSqlLsp {
    private static void AddCompletion(JsonArray items, string label, string insertText, string detail, int kind) {
        items.Add(item: new JsonObject {
            ["label"] = label,
            ["kind"] = kind,
            ["detail"] = detail,
            ["insertText"] = insertText,
            ["insertTextFormat"] = 2,
        });
    }

    private static string Capitalize(string text) {
        if (string.IsNullOrEmpty(value: text)) {
            return text;
        }

        return (char.ToUpperInvariant(c: text[0]) + text.Substring(startIndex: 1));
    }

    private static string GetColTraits(SqlColumnDefinition col) {
        var traits = new List<string>();

        if (col.Check is { } chk) {
            if (
                (chk.Min is not null) &&
                (chk.Max is not null)
            ) {
                traits.Add(item: $"{chk.Min}..{chk.Max}");
            } else if (chk.Min is not null) {
                traits.Add(item: $">={chk.Min}");
            } else if (chk.Max is not null) {
                traits.Add(item: $"<={chk.Max}");
            }
        }

        if (col.OverflowPolicy is not null) {
            traits.Add(item: $"overflow: {col.OverflowPolicy}");
        }

        if (col.Advance is not null) {
            traits.Add(item: $"rate: {col.Advance.Rate}/{col.Advance.Unit}");
        }

        if (col.DynamicsName is not null) {
            traits.Add(item: $"dynamics: {col.DynamicsName}");
        }

        return (((traits.Count > 0)
            ? $" [{string.Join(
                separator: ", ",
                values: traits
            )}]"
            : ""));
    }

    private static string GetMappedRowName(string tableName, SqlColumnDefinition col) {
        if (col.ExplicitRowName is not null) {
            return col.ExplicitRowName;
        }

        if (col.IsPrimaryKey) {
            return tableName;
        }

        return $"{tableName}{Capitalize(text: col.Name)}";
    }

    private static List<SqlStatement> ParseSqlBlock(EmbeddedBlockNode eb) {
        try {
            var lexer = new StateSqlLexer(
                baseColumn: eb.BodyColumn,
                baseLine: eb.BodyLine,
                baseOffset: eb.BodyOffset,
                source: eb.Body
            );
            var tokens = lexer.Tokenize(diagnostics: new DiagnosticBag());
            var parser = new StateSqlParser(
                diagnostics: new DiagnosticBag(),
                tokens: tokens
            );

            return parser.ParseStatements().ToList();
        } catch {
            return [];
        }
    }

    private static List<SqlStatement> ParseAllSqlStatements(string text, DocumentVocabularyResolver? resolver = null) {
        var statements = new List<SqlStatement>();

        try {
            var vocabulary = (resolver?.Resolve(text) ?? WorldDocumentVocabulary.Instance);
            var document = PuckParser.ParseDocumentWithDiagnostics(
                source: text,
                vocabulary: vocabulary
            ).Value;

            if (document is null) {
                return statements;
            }

            foreach (var stmt in document.Statements) {
                if (
                    (stmt is EmbeddedBlockNode eb) &&
                    string.Equals(
                        a: eb.Language,
                        b: "sql",
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    )
                ) {
                    statements.AddRange(collection: ParseSqlBlock(eb: eb));
                }
            }
        } catch {
            // Ignore parse failures
        }

        return statements;
    }

    internal static bool IsCursorInsideSqlBlock(string text, int cursorOffset, DocumentVocabularyResolver? resolver = null) {
        try {
            var vocabulary = (resolver?.Resolve(text) ?? WorldDocumentVocabulary.Instance);
            var document = PuckParser.ParseDocumentWithDiagnostics(
                source: text,
                vocabulary: vocabulary
            ).Value;

            if (document is not null) {
                foreach (var stmt in document.Statements) {
                    if (
                        (stmt is EmbeddedBlockNode eb) &&
                        string.Equals(
                            a: eb.Language,
                            b: "sql",
                            comparisonType: StringComparison.OrdinalIgnoreCase
                        )
                    ) {
                        if (
                            (cursorOffset >= eb.Offset) &&
                            (cursorOffset <= (eb.Offset + eb.Length))
                        ) {
                            return true;
                        }
                    }
                }
            }
        } catch {
            // Ignore parse failures
        }

        return false;
    }

    internal static JsonArray GetSqlCompletions(string text, DocumentVocabularyResolver? resolver = null) {
        var items = new JsonArray();

        // 1. Statements & Keywords
        AddCompletion(
            detail: "SQL statement: Query rows",
            insertText: "SELECT ${1:*} FROM ${2:table};",
            items: items,
            kind: 14,
            label: "SELECT"
        );
        AddCompletion(
            detail: "SQL statement: Insert row(s)",
            insertText: "INSERT INTO ${1:table} (${2:columns}) VALUES (${3:values});",
            items: items,
            kind: 14,
            label: "INSERT INTO"
        );
        AddCompletion(
            detail: "SQL statement: Update row(s)",
            insertText: "UPDATE ${1:table} SET ${2:col} = ${3:val} WHERE ${4:cond};",
            items: items,
            kind: 14,
            label: "UPDATE"
        );
        AddCompletion(
            detail: "SQL statement: Delete row(s)",
            insertText: "DELETE FROM ${1:table} WHERE ${2:cond};",
            items: items,
            kind: 14,
            label: "DELETE FROM"
        );
        AddCompletion(
            detail: "SQL statement: Create table",
            insertText: "CREATE TABLE ${1:name} (\n    ${2:id} KEY(${3:16}),\n    $0\n);",
            items: items,
            kind: 14,
            label: "CREATE TABLE"
        );
        AddCompletion(
            detail: "SQL statement: Declare scalar slot",
            insertText: "DECLARE ${1:name} ${2|INT,FLOAT,BOOL,TEXT|};",
            items: items,
            kind: 14,
            label: "DECLARE"
        );
        AddCompletion(
            detail: "SQL block: Atomic transaction",
            insertText: "BEGIN TRANSACTION;\n    $0\nCOMMIT;",
            items: items,
            kind: 14,
            label: "BEGIN TRANSACTION"
        );
        AddCompletion(
            detail: "SQL keyword: Commit transaction",
            insertText: "COMMIT;",
            items: items,
            kind: 14,
            label: "COMMIT"
        );
        AddCompletion(
            detail: "SQL keyword: Rollback transaction",
            insertText: "ROLLBACK;",
            items: items,
            kind: 14,
            label: "ROLLBACK"
        );
        AddCompletion(
            detail: "SQL clause: Filter condition",
            insertText: "WHERE ${1:condition}",
            items: items,
            kind: 14,
            label: "WHERE"
        );
        AddCompletion(
            detail: "SQL clause: Assign column values",
            insertText: "SET ${1:col} = ${2:val}",
            items: items,
            kind: 14,
            label: "SET"
        );
        AddCompletion(
            detail: "SQL clause: Row values",
            insertText: "VALUES (${1:values})",
            items: items,
            kind: 14,
            label: "VALUES"
        );
        AddCompletion(
            detail: "SQL clause: Copy values from table",
            insertText: "VALUES FROM ${1:table}",
            items: items,
            kind: 14,
            label: "VALUES FROM"
        );
        AddCompletion(
            detail: "SQL clause: Event gate condition",
            insertText: "WHEN ${1:event}",
            items: items,
            kind: 14,
            label: "WHEN"
        );
        AddCompletion(
            detail: "SQL clause: Level gate condition",
            insertText: "IF ${1:condition}",
            items: items,
            kind: 14,
            label: "IF"
        );
        AddCompletion(
            detail: "SQL table option: Ordered pile",
            insertText: "ORDERED",
            items: items,
            kind: 14,
            label: "ORDERED"
        );
        AddCompletion(
            detail: "SQL option: Table key capacity",
            insertText: "CAPACITY ${1:16}",
            items: items,
            kind: 14,
            label: "CAPACITY"
        );
        AddCompletion(
            detail: "SQL constraint: Primary key",
            insertText: "PRIMARY KEY",
            items: items,
            kind: 14,
            label: "PRIMARY KEY"
        );
        AddCompletion(
            detail: "SQL constraint: Foreign table reference",
            insertText: "REFERENCES ${1:table}",
            items: items,
            kind: 14,
            label: "REFERENCES"
        );
        AddCompletion(
            detail: "SQL constraint: Default column value",
            insertText: "DEFAULT ${1:value}",
            items: items,
            kind: 14,
            label: "DEFAULT"
        );
        AddCompletion(
            detail: "SQL constraint: Check expression",
            insertText: "CHECK (${1:condition})",
            items: items,
            kind: 14,
            label: "CHECK"
        );
        AddCompletion(
            detail: "SQL predicate: Range between",
            insertText: "BETWEEN ${1:min} AND ${2:max}",
            items: items,
            kind: 14,
            label: "BETWEEN"
        );
        AddCompletion(
            detail: "SQL trait: Saturating overflow",
            insertText: "ON OVERFLOW SATURATE",
            items: items,
            kind: 14,
            label: "ON OVERFLOW SATURATE"
        );
        AddCompletion(
            detail: "SQL trait: Rate advancement",
            insertText: "ADVANCE ${1:1} PER SECOND",
            items: items,
            kind: 14,
            label: "ADVANCE"
        );
        AddCompletion(
            detail: "SQL trait: Dynamics physics source",
            insertText: "DYNAMICS ${1:row}",
            items: items,
            kind: 14,
            label: "DYNAMICS"
        );
        AddCompletion(
            detail: "SQL type: 64-bit integer",
            insertText: "INT",
            items: items,
            kind: 14,
            label: "INT"
        );
        AddCompletion(
            detail: "SQL type: 32-bit floating point",
            insertText: "FLOAT",
            items: items,
            kind: 14,
            label: "FLOAT"
        );
        AddCompletion(
            detail: "SQL type: Boolean flag",
            insertText: "BOOL",
            items: items,
            kind: 14,
            label: "BOOL"
        );
        AddCompletion(
            detail: "SQL type: UTF-8 text string",
            insertText: "TEXT",
            items: items,
            kind: 14,
            label: "TEXT"
        );
        AddCompletion(
            detail: "SQL type: Primary key with capacity",
            insertText: "KEY(${1:16})",
            items: items,
            kind: 14,
            label: "KEY"
        );

        // 2. Declared tables, columns, and slots
        var stmts = ParseAllSqlStatements(text: text);

        foreach (var stmt in stmts) {
            switch (stmt) {
                case SqlCreateTableStatement table:
                    AddCompletion(
                        detail: $"SQL table ({table.Columns.Count} columns)",
                        insertText: table.TableName,
                        items: items,
                        kind: 7,
                        label: table.TableName
                    );

                    foreach (var col in table.Columns) {
                        AddCompletion(
                            detail: $"SQL column — {table.TableName}.{col.Name} ({col.DeclaredType})",
                            insertText: col.Name,
                            items: items,
                            kind: 5,
                            label: col.Name
                        );
                    }
                    break;
                case SqlDeclareSlotStatement slot:
                    AddCompletion(
                        detail: $"SQL scalar slot — {slot.Name} ({slot.DeclaredType})",
                        insertText: slot.Name,
                        items: items,
                        kind: 6,
                        label: slot.Name
                    );
                    break;
            }
        }

        return items;
    }

    internal static JsonArray? GetSqlTableColumnCompletions(string text, string tableName, DocumentVocabularyResolver? resolver = null) {
        var stmts = ParseAllSqlStatements(text: text, resolver: resolver);

        foreach (var stmt in stmts) {
            if (
                (stmt is SqlCreateTableStatement table) &&
                string.Equals(
                    a: table.TableName,
                    b: tableName,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )
            ) {
                var items = new JsonArray();

                foreach (var col in table.Columns) {
                    AddCompletion(
                        detail: $"SQL column — {table.TableName}.{col.Name} ({col.DeclaredType})",
                        insertText: col.Name,
                        items: items,
                        kind: 5,
                        label: col.Name
                    );
                }

                return items;
            }
        }

        return null;
    }

    internal static string? GetSqlHoverCard(string text, string word, int offset, DocumentVocabularyResolver? resolver = null) {
        if (!IsCursorInsideSqlBlock(text: text, cursorOffset: offset, resolver: resolver)) {
            return null;
        }

        var stmts = ParseAllSqlStatements(text: text, resolver: resolver);

        // 1. Check declared tables
        foreach (var stmt in stmts) {
            if (stmt is SqlCreateTableStatement table) {
                if (string.Equals(
                    a: table.TableName,
                    b: word,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    var sb = new StringBuilder();
                    var pkCol = table.Columns.FirstOrDefault(predicate: c => c.IsPrimaryKey);
                    var capStr = ((table.Capacity is int cap)
                        ? $" (capacity: {cap})"
                        : "");

                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"**`{table.TableName}`** — SQL table\n"
                    );

                    if (pkCol is not null) {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- Primary key: `{pkCol.Name}`{capStr}"
                        );
                    }

                    if (table.IsOrdered) {
                        sb.AppendLine("- Mode: `ORDERED` (pile)");
                    }

                    sb.AppendLine("- Columns:");
                    foreach (var col in table.Columns) {
                        var mapped = GetMappedRowName(
                            col: col,
                            tableName: table.TableName
                        );
                        var traits = GetColTraits(col: col);

                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"  - `{col.Name}`: `{col.DeclaredType}` → state row `{mapped}`{traits}"
                        );
                    }

                    return sb.ToString().TrimEnd();
                }

                // Check columns
                foreach (var col in table.Columns) {
                    if (string.Equals(
                        a: col.Name,
                        b: word,
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    )) {
                        var sb = new StringBuilder();
                        var mapped = GetMappedRowName(
                            col: col,
                            tableName: table.TableName
                        );

                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"**`{col.Name}`** — SQL column (`{table.TableName}.{col.Name}`)\n"
                        );
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- Table: `{table.TableName}`"
                        );
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- Type: `{col.DeclaredType}`"
                        );
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- State row: `{mapped}`"
                        );

                        if (col.IsPrimaryKey) {
                            var capStr = ((table.Capacity is int cap)
                                ? $" (capacity: {cap})"
                                : "");
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Primary key{capStr}"
                            );
                            if (col.ReferencesTable is not null) {
                                sb.AppendLine(
                                    CultureInfo.InvariantCulture,
                                    $"- References: `{col.ReferencesTable}`"
                                );
                            }
                        }

                        if (col.Check is { } chk) {
                            if (
                                (chk.Min is not null) &&
                                (chk.Max is not null)
                            ) {
                                sb.AppendLine(
                                    CultureInfo.InvariantCulture,
                                    $"- Check: `BETWEEN {chk.Min} AND {chk.Max}`"
                                );
                            } else if (chk.Min is not null) {
                                sb.AppendLine(
                                    CultureInfo.InvariantCulture,
                                    $"- Check: `>= {chk.Min}`"
                                );
                            } else if (chk.Max is not null) {
                                sb.AppendLine(
                                    CultureInfo.InvariantCulture,
                                    $"- Check: `<= {chk.Max}`"
                                );
                            }
                        }

                        if (col.OverflowPolicy is not null) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Overflow: `{col.OverflowPolicy}`"
                            );
                        }

                        if (col.Advance is not null) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Advance: `{col.Advance.Rate}/{col.Advance.Unit}`"
                            );
                        }

                        if (col.DynamicsName is not null) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Dynamics: `{col.DynamicsName}`"
                            );
                        }

                        return sb.ToString().TrimEnd();
                    }
                }
            } else if (stmt is SqlDeclareSlotStatement slot) {
                if (string.Equals(
                    a: slot.Name,
                    b: word,
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    var sb = new StringBuilder();

                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"**`{slot.Name}`** — SQL scalar slot\n"
                    );
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"- Type: `{slot.DeclaredType}`"
                    );
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"- State row: `{slot.Name}`"
                    );

                    if (slot.DefaultValue is not null) {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- Default: `{slot.DefaultValue}`"
                        );
                    }

                    if (slot.Check is { } chk) {
                        if (
                            (chk.Min is not null) &&
                            (chk.Max is not null)
                        ) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Check: `BETWEEN {chk.Min} AND {chk.Max}`"
                            );
                        } else if (chk.Min is not null) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Check: `>= {chk.Min}`"
                            );
                        } else if (chk.Max is not null) {
                            sb.AppendLine(
                                CultureInfo.InvariantCulture,
                                $"- Check: `<= {chk.Max}`"
                            );
                        }
                    }

                    if (slot.Advance is not null) {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"- Advance: `{slot.Advance.Rate}/{slot.Advance.Unit}`"
                        );
                    }

                    return sb.ToString().TrimEnd();
                }
            }
        }

        // 2. Check SQL keywords when inside a SQL block
        if (IsCursorInsideSqlBlock(
            cursorOffset: offset,
            text: text
        )) {
            var keywordDoc = word.ToUpperInvariant() switch {
                "SELECT" => ("SELECT", "Projects or reads cell and slot values in rules and state expressions."),
                "INSERT" => ("INSERT INTO", "Inserts initial rows into a table or emits push effects into a pile."),
                "UPDATE" => ("UPDATE", "Modifies state row values matching a key predicate or condition."),
                "DELETE" => ("DELETE FROM", "Removes rows from a table or elements from a pile."),
                "CREATE" => ("CREATE TABLE", "Declares a keyed table whose columns map to prefix-named state rows."),
                "TABLE" => ("TABLE", "Declares a keyed table whose columns map to prefix-named state rows."),
                "DECLARE" => ("DECLARE", "Declares a single scalar state row with optional type and constraints."),
                "TRANSACTION" => ("TRANSACTION", "Atomic block of state mutations that commit together or roll back."),
                "COMMIT" => ("COMMIT", "Marks successful completion of a transaction block."),
                "ROLLBACK" => ("ROLLBACK", "Rolls back mutations within a transaction on condition failure."),
                "ORDERED" => ("ORDERED", "Designates an ordered sequence table (spells a native pile)."),
                "CAPACITY" => ("CAPACITY", "Specifies maximum key count or entry capacity for a table."),
                "PRIMARY" => ("PRIMARY KEY", "Identifies the unique index column for a state table."),
                "KEY" => ("KEY(capacity)", "Primary key type declaring the maximum addressable row capacity."),
                "REFERENCES" => ("REFERENCES", "Points an ORDERED table to its referenced domain table."),
                "DEFAULT" => ("DEFAULT", "Specifies initial scalar value or default column state."),
                "CHECK" => ("CHECK", "Constrains allowed values using range or comparison bounds."),
                "BETWEEN" => ("BETWEEN", "Specifies inclusive minimum and maximum numeric bounds."),
                "SATURATE" => ("ON OVERFLOW SATURATE", "Clamps values at range bounds instead of erroring or wrapping."),
                "ADVANCE" => ("ADVANCE", "Specifies continuous time integration rate per second."),
                "DYNAMICS" => ("DYNAMICS", "Binds a state row to a physics motion state register."),
                _ => (((string, string)?)null)
            };

            if (keywordDoc is { } kw) {
                return $"**`{kw.Item1}`** — SQL dialect keyword\n\n{kw.Item2}";
            }
        }

        return null;
    }
}
