using System.Globalization;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Sql;

/// <summary>
/// Recursive-descent parser for the bounded SQL-flavored state authoring dialect.
/// Adheres strictly to the dialect constraints and reports refusals by name with exact token spans.
/// </summary>
public sealed class StateSqlParser {
    private readonly IReadOnlyList<SqlToken> m_tokens;
    private readonly DiagnosticBag m_diagnostics;
    private int m_index;

    public StateSqlParser(IReadOnlyList<SqlToken> tokens, DiagnosticBag diagnostics) {
        m_tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        m_diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        m_index = 0;
    }

    private SqlToken Current => (m_index < m_tokens.Count) ? m_tokens[m_index] : m_tokens[^1];
    private bool IsAtEnd => (Current.Kind == SqlTokenKind.Eof);

    private SqlToken Peek(int offset = 1) {
        var pos = m_index + offset;
        return (pos < m_tokens.Count) ? m_tokens[pos] : m_tokens[^1];
    }

    private SqlToken Advance() {
        var tok = Current;
        if (!IsAtEnd) {
            m_index++;
        }
        return tok;
    }

    private bool Check(SqlTokenKind kind) => (Current.Kind == kind);

    private bool CheckKeyword(string keyword) =>
        string.Equals(Current.Text, keyword, StringComparison.OrdinalIgnoreCase);

    private bool Match(SqlTokenKind kind) {
        if (Check(kind)) {
            Advance();
            return true;
        }
        return false;
    }

    private bool MatchKeyword(string keyword) {
        if (CheckKeyword(keyword)) {
            Advance();
            return true;
        }
        return false;
    }

    private SqlToken Consume(SqlTokenKind kind, string message) {
        if (Check(kind)) {
            return Advance();
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: message,
            span: Current.Span
        );
        return Current;
    }

    private SqlToken ConsumeKeyword(string keyword, string message) {
        if (CheckKeyword(keyword)) {
            return Advance();
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: message,
            span: Current.Span
        );
        return Current;
    }

    private SqlToken ConsumeWord(string message) {
        if ((Current.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword)) {
            return Advance();
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: message,
            span: Current.Span
        );
        return Current;
    }

    public IReadOnlyList<SqlStatement> ParseStatements() {
        var statements = new List<SqlStatement>();

        while (!IsAtEnd) {
            if (Match(SqlTokenKind.Semicolon)) {
                continue;
            }

            var startTok = Current;

            if (CheckKeyword("CREATE")) {
                var stmt = ParseCreate();
                if (stmt is not null) {
                    statements.Add(stmt);
                }
            } else if (CheckKeyword("INSERT")) {
                var stmt = ParseInsert();
                if (stmt is not null) {
                    statements.Add(stmt);
                }
            } else if (CheckKeyword("DECLARE")) {
                var stmt = ParseDeclareSlot();
                if (stmt is not null) {
                    statements.Add(stmt);
                }
            } else if (CheckKeyword("UPDATE") || CheckKeyword("DELETE") || CheckKeyword("BEGIN") || CheckKeyword("IF")) {
                var stmt = ParseRuleBodyStatement();
                if (stmt is not null) {
                    statements.Add(stmt);
                }
            } else if (IsUnsupportedClause(Current.Text, out var reason)) {
                var tok = Advance();
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedClause,
                    message: $"'{tok.Text}' is unsupported in Puck state SQL dialect — {reason}",
                    span: tok.Span
                );
                Synchronize();
            } else {
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlSyntaxError,
                    message: $"Unexpected token '{Current.Text}' in SQL dialect",
                    span: Current.Span
                );
                Advance();
                Synchronize();
            }
        }

        return statements;
    }

    private static bool IsUnsupportedClause(string text, out string reason) {
        var upper = text.ToUpperInvariant();
        switch (upper) {
            case "GROUP":
            case "HAVING":
                reason = "state holds keyed rows; groupings belong in views or native queries";
                return true;
            case "ORDER":
                reason = "state evaluation is order-invariant except for piles; use ORDERED table trait instead";
                return true;
            case "LIMIT":
                reason = "state updates apply to the whole matched key domain; use explicit rule gates instead";
                return true;
            case "WINDOW":
                reason = "state carries no window function evaluator";
                return true;
            case "UNION":
                reason = "state collections are single tables; multi-table unions are not admitted";
                return true;
            case "TRIGGER":
                reason = "use CREATE RULE EVERY TICK or ON ENTER instead of SQL triggers";
                return true;
            case "VIEW":
                reason = "state holds persisted rows; views are not part of world definition";
                return true;
            case "ALTER":
            case "DROP":
                reason = "schema is immutable once compiled; ALTER and DROP are not admitted";
                return true;
            case "JOIN":
            case "CROSS":
            case "APPLY":
                reason = "cross-table joins are not admitted; read other tables via dot access or scalar subqueries";
                return true;
            default:
                reason = string.Empty;
                return false;
        }
    }

    private SqlStatement? ParseCreate() {
        var createTok = Advance(); // CREATE

        if (MatchKeyword("TABLE")) {
            return ParseCreateTable(createTok.Span);
        }
        if (MatchKeyword("POLICY")) {
            return ParseCreatePolicy(createTok.Span);
        }
        if (MatchKeyword("RULE")) {
            return ParseCreateRule(createTok.Span);
        }

        if (CheckKeyword("VIEW") || CheckKeyword("TRIGGER")) {
            var tok = Advance();
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"'CREATE {tok.Text}' is unsupported in Puck state SQL dialect — use native declarations or rules instead",
                span: tok.Span
            );
            Synchronize();
            return null;
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: $"Expected TABLE, POLICY, or RULE after CREATE, but found '{Current.Text}'",
            span: Current.Span
        );
        Synchronize();
        return null;
    }

    private SqlCreateTableStatement? ParseCreateTable(SourceSpan startSpan) {
        var nameTok = ConsumeWord("Expected table name in CREATE TABLE");
        var tableName = nameTok.Text;

        Consume(SqlTokenKind.OpenParen, "Expected '(' after table name");

        var columns = new List<SqlColumnDefinition>();
        var hasPrimaryKey = false;

        while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
            // Check for table constraint: PRIMARY KEY (col1, col2, ...)
            if (CheckKeyword("PRIMARY")) {
                var pkTok = Advance();
                ConsumeKeyword("KEY", "Expected 'KEY' after 'PRIMARY'");
                var parenTok = Consume(SqlTokenKind.OpenParen, "Expected '(' after 'PRIMARY KEY'");

                var pkCols = new List<string>();
                while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                    var colTok = ConsumeWord("Expected column name in PRIMARY KEY constraint");
                    pkCols.Add(colTok.Text);
                    if (!Match(SqlTokenKind.Comma)) {
                        break;
                    }
                }
                var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' closing PRIMARY KEY constraint");

                var pkSpan = SourceSpan.Combine(pkTok.Span, closeParen.Span);

                if (pkCols.Count > 1) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlCompositePrimaryKey,
                        message: "Composite PRIMARY KEY is unsupported; a state cell has exactly one key.",
                        span: pkSpan
                    );
                } else if (hasPrimaryKey) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlCompositePrimaryKey,
                        message: "Multiple PRIMARY KEY definitions on table are unsupported; a state cell has exactly one key.",
                        span: pkSpan
                    );
                } else if (pkCols.Count == 1) {
                    // Mark existing column as primary key
                    var found = false;
                    for (var i = 0; i < columns.Count; i++) {
                        if (string.Equals(columns[i].Name, pkCols[0], StringComparison.OrdinalIgnoreCase)) {
                            columns[i] = columns[i] with { IsPrimaryKey = true };
                            hasPrimaryKey = true;
                            found = true;
                            break;
                        }
                    }
                    if (!found) {
                        m_diagnostics.ReportError(
                            code: PuckDiagnosticCodes.SqlSyntaxError,
                            message: $"PRIMARY KEY column '{pkCols[0]}' was not declared in table columns",
                            span: pkSpan
                        );
                    }
                }

                Match(SqlTokenKind.Comma);
                continue;
            }

            var colDef = ParseColumnDefinition(tableName, ref hasPrimaryKey);
            if (colDef is not null) {
                columns.Add(colDef);
            }

            if (!Match(SqlTokenKind.Comma)) {
                break;
            }
        }

        var closeParenTok = Consume(SqlTokenKind.CloseParen, "Expected ')' closing table column definitions");

        // Table options after ')'
        int? capacity = null;
        var isOrdered = false;
        var isEvicts = false;

        while (!Check(SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword("CREATE") && !CheckKeyword("INSERT") && !CheckKeyword("DECLARE")) {
            if (MatchKeyword("CAPACITY")) {
                var capTok = Consume(SqlTokenKind.NumberLiteral, "Expected integer capacity value");
                if (capTok.Value is long l) {
                    capacity = (int)l;
                } else if (int.TryParse(capTok.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCap)) {
                    capacity = parsedCap;
                }
                continue;
            }

            if (MatchKeyword("ORDERED")) {
                isOrdered = true;
                continue;
            }

            if (MatchKeyword("EVICTS")) {
                isEvicts = true;
                continue;
            }

            break;
        }

        Match(SqlTokenKind.Semicolon);

        var endSpan = closeParenTok.Span;
        var totalLength = (endSpan.Offset + endSpan.Length) - startSpan.Offset;
        var fullSpan = new SourceSpan(startSpan.Offset, totalLength, startSpan.Line, startSpan.Column);

        if (!hasPrimaryKey) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Table '{tableName}' must declare a column as PRIMARY KEY",
                span: fullSpan
            );
        }

        var pkCol = columns.Find(c => c.IsPrimaryKey);
        if (isOrdered && (pkCol?.ReferencesTable is null)) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"ORDERED table '{tableName}' requires a PRIMARY KEY REFERENCES clause pointing to the referenced table (spells a native pile)",
                span: fullSpan
            );
        }

        if (isEvicts && !capacity.HasValue) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Table '{tableName}' option 'EVICTS' requires 'CAPACITY'",
                span: fullSpan
            );
        }

        return new SqlCreateTableStatement(
            TableName: tableName,
            Columns: columns,
            Capacity: capacity,
            IsOrdered: isOrdered,
            Span: fullSpan,
            IsEvicts: isEvicts
        );
    }

    private SqlColumnDefinition? ParseColumnDefinition(string tableName, ref bool hasPrimaryKey) {
        var colNameTok = ConsumeWord("Expected column name");
        var colName = colNameTok.Text;

        var typeTok = ConsumeWord("Expected column type (INT, FIXED, BOOL, TEXT, etc.)");
        var declaredType = typeTok.Text;
        var typeSpan = typeTok.Span;

        string? space = null;
        if (string.Equals(declaredType, "VECTOR", StringComparison.OrdinalIgnoreCase)) {
            if (Match(SqlTokenKind.OpenParen)) {
                var spaceTok = ConsumeWord("Expected space name in VECTOR(...)");
                space = spaceTok.Text;
                Consume(SqlTokenKind.CloseParen, "Expected ')' closing VECTOR(space)");
            }
        } else if (Match(SqlTokenKind.OpenParen)) {
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(SqlTokenKind.CloseParen, "Expected ')' closing type parameters");
        }

        // Validate type against state model
        ValidateType(declaredType, typeSpan);

        var isPrimaryKey = false;
        var isNotNull = false;
        string? explicitRowName = null;
        object? defaultValue = null;
        SqlCheckConstraint? check = null;
        SqlAdvanceClause? advance = null;
        string? dynamicsName = null;
        string? overflowPolicy = null;
        string? referencesTable = null;
        var isOrdered = false;

        while (!Check(SqlTokenKind.Comma) && !Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
            if (MatchKeyword("PRIMARY")) {
                ConsumeKeyword("KEY", "Expected 'KEY' after 'PRIMARY'");
                if (hasPrimaryKey) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlCompositePrimaryKey,
                        message: "Table has multiple PRIMARY KEY columns; a state cell has exactly one key.",
                        span: colNameTok.Span
                    );
                }
                isPrimaryKey = true;
                hasPrimaryKey = true;
                continue;
            }

            if (MatchKeyword("NOT")) {
                ConsumeKeyword("NULL", "Expected 'NULL' after 'NOT'");
                isNotNull = true;
                continue;
            }

            if (MatchKeyword("NULL")) {
                continue;
            }

            if (MatchKeyword("ROW") || MatchKeyword("AS")) {
                var rowNameTok = ConsumeWord("Expected custom row name after 'ROW' or 'AS'");
                explicitRowName = rowNameTok.Text;
                continue;
            }

            if (MatchKeyword("DEFAULT")) {
                defaultValue = ParseDefaultValue();
                continue;
            }

            if (MatchKeyword("CHECK")) {
                check = ParseCheckConstraint(colName, declaredType);
                continue;
            }

            if (MatchKeyword("ON")) {
                ConsumeKeyword("OVERFLOW", "Expected 'OVERFLOW' after 'ON'");
                var policyTok = ConsumeWord("Expected 'SATURATE' or 'REFUSE' after 'ON OVERFLOW'");
                if (!string.Equals(policyTok.Text, "SATURATE", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(policyTok.Text, "REFUSE", StringComparison.OrdinalIgnoreCase)) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSyntaxError,
                        message: $"ON OVERFLOW policy must be SATURATE or REFUSE, found '{policyTok.Text}'",
                        span: policyTok.Span
                    );
                }
                overflowPolicy = policyTok.Text;
                continue;
            }

            if (MatchKeyword("ADVANCE")) {
                advance = ParseAdvanceClause();
                continue;
            }

            if (MatchKeyword("DYNAMICS")) {
                var dynTok = ConsumeWord("Expected dynamics row name after 'DYNAMICS'");
                dynamicsName = dynTok.Text;
                continue;
            }

            if (MatchKeyword("REFERENCES")) {
                var refTok = ConsumeWord("Expected referenced table name after 'REFERENCES'");
                if (!isPrimaryKey) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlUnsupportedClause,
                        message: $"REFERENCES clause is only valid on the PRIMARY KEY column of an ORDERED table/pile, not on column '{colName}'",
                        span: refTok.Span
                    );
                }
                referencesTable = refTok.Text;
                continue;
            }

            if (MatchKeyword("ORDERED")) {
                if (!isPrimaryKey) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlUnsupportedClause,
                        message: $"ORDERED modifier is only supported on a table or its PRIMARY KEY column with a REFERENCES clause (spells a native pile), not on column '{colName}'",
                        span: colNameTok.Span
                    );
                }
                isOrdered = true;
                continue;
            }

            // Unrecognized column modifier
            break;
        }

        var colSpan = new SourceSpan(
            colNameTok.Span.Offset,
            (Current.Span.Offset - colNameTok.Span.Offset),
            colNameTok.Span.Line,
            colNameTok.Span.Column
        );

        return new SqlColumnDefinition(
            Name: colName,
            DeclaredType: declaredType,
            IsPrimaryKey: isPrimaryKey,
            IsNotNull: isNotNull,
            ExplicitRowName: explicitRowName,
            DefaultValue: defaultValue,
            Check: check,
            Advance: advance,
            DynamicsName: dynamicsName,
            OverflowPolicy: overflowPolicy,
            ReferencesTable: referencesTable,
            IsOrdered: isOrdered,
            Span: colSpan,
            TypeSpan: typeSpan,
            Space: space
        );
    }

    private void ValidateType(string typeName, SourceSpan span) {
        var upper = typeName.ToUpperInvariant();
        switch (upper) {
            case "REAL":
            case "FLOAT":
            case "DOUBLE":
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedType,
                    message: $"Type '{typeName}' is unsupported; state holds no floats — use 'FIXED' instead.",
                    span: span
                );
                break;
            case "INT":
            case "INTEGER":
            case "SMALLINT":
            case "BIGINT":
            case "TINYINT":
            case "FIXED":
            case "DECIMAL":
            case "NUMERIC":
            case "BOOL":
            case "BOOLEAN":
            case "TEXT":
            case "VARCHAR":
            case "CHAR":
            case "STRING":
            case "VECTOR":
                // Admitted
                break;
            default:
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlUnsupportedType,
                    message: $"Type '{typeName}' is unrecognized or unsupported; admitted types are INT, FIXED, BOOL, TEXT, VECTOR.",
                    span: span
                );
                break;
        }
    }

    private object? ParseDefaultValue() {
        if (Check(SqlTokenKind.NumberLiteral)) {
            return Advance().Value;
        }
        if (Check(SqlTokenKind.StringLiteral)) {
            return Advance().Value;
        }
        if (Check(SqlTokenKind.BooleanLiteral)) {
            return Advance().Value;
        }
        if (Match(SqlTokenKind.NullLiteral)) {
            return null;
        }

        // Unary minus for negative number
        if (Match(SqlTokenKind.Minus)) {
            var numTok = Consume(SqlTokenKind.NumberLiteral, "Expected number after '-'");
            if (numTok.Value is long l) {
                return -l;
            }
            if (numTok.Value is decimal d) {
                return -d;
            }
        }

        var tok = Advance();
        return tok.Text;
    }

    private SqlCheckConstraint? ParseCheckConstraint(string expectedColName, string colType) {
        var openParen = Consume(SqlTokenKind.OpenParen, "Expected '(' starting CHECK constraint");

        var colTok = ConsumeWord("Expected column name in CHECK constraint");
        var colName = colTok.Text;

        SqlCheckKind kind;
        decimal? min = null;
        decimal? max = null;

        var isInt = string.Equals(colType, "INT", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(colType, "INTEGER", StringComparison.OrdinalIgnoreCase);

        if (MatchKeyword("IS")) {
            var isNot = MatchKeyword("NOT");
            var nullTok = ConsumeKeyword("NULL", "Expected 'NULL' after 'IS'");
            var isSpan = CombineSpan(colTok.Span, nullTok.Span);
            var clause = isNot ? "IS NOT NULL" : "IS NULL";
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"'{clause}' is unsupported in Puck state SQL dialect — state facts hold absent cells rather than three-valued NULL logic.",
                span: isSpan
            );
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(SqlTokenKind.CloseParen, "Expected ')' closing CHECK constraint");
            return null;
        }

        if (MatchKeyword("BETWEEN")) {
            kind = SqlCheckKind.Between;
            min = ParseDecimalValue("Expected minimum value in BETWEEN clause");
            ConsumeKeyword("AND", "Expected 'AND' in BETWEEN clause");
            max = ParseDecimalValue("Expected maximum value in BETWEEN clause");
        } else if (Match(SqlTokenKind.GreaterOrEqual)) {
            min = ParseDecimalValue("Expected number after '>=' in CHECK constraint");
            if (MatchKeyword("AND")) {
                var col2 = ConsumeWord("Expected column name after 'AND' in CHECK constraint");
                Consume(SqlTokenKind.LessOrEqual, "Expected '<=' after column in CHECK constraint");
                max = ParseDecimalValue("Expected maximum value after '<=' in CHECK constraint");
                kind = SqlCheckKind.Between;
            } else {
                kind = SqlCheckKind.GreaterOrEqual;
            }
        } else if (Match(SqlTokenKind.LessOrEqual)) {
            max = ParseDecimalValue("Expected number after '<=' in CHECK constraint");
            kind = SqlCheckKind.LessOrEqual;
        } else if (Match(SqlTokenKind.Greater)) {
            var val = ParseDecimalValue("Expected number after '>' in CHECK constraint");
            if (isInt) {
                kind = SqlCheckKind.Greater;
                min = (val.HasValue ? val.Value + 1 : null);
            } else {
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlInvalidCheckShape,
                    message: "CHECK constraint with '>' is only admitted for INT columns; use '>=' instead.",
                    span: Current.Span
                );
                kind = SqlCheckKind.GreaterOrEqual;
                min = val;
            }
        } else if (Match(SqlTokenKind.Less)) {
            var val = ParseDecimalValue("Expected number after '<' in CHECK constraint");
            if (isInt) {
                kind = SqlCheckKind.Less;
                max = (val.HasValue ? val.Value - 1 : null);
            } else {
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlInvalidCheckShape,
                    message: "CHECK constraint with '<' is only admitted for INT columns; use '<=' instead.",
                    span: Current.Span
                );
                kind = SqlCheckKind.LessOrEqual;
                max = val;
            }
        } else {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlInvalidCheckShape,
                message: $"CHECK constraint shape on '{expectedColName}' is invalid — admitted shapes are 'col BETWEEN min AND max', 'col >= min', or 'col <= max' (or '>'/'<' for INT).",
                span: openParen.Span
            );
            // Skip until ')'
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(SqlTokenKind.CloseParen, "Expected ')' closing CHECK constraint");
            return null;
        }

        var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' closing CHECK constraint");

        var span = new SourceSpan(
            openParen.Span.Offset,
            (closeParen.Span.Offset + closeParen.Span.Length) - openParen.Span.Offset,
            openParen.Span.Line,
            openParen.Span.Column
        );

        return new SqlCheckConstraint(colName, kind, min, max, span);
    }

    private decimal? ParseDecimalValue(string errorMessage) {
        var negative = false;
        if (Match(SqlTokenKind.Minus)) {
            negative = true;
        } else if (Match(SqlTokenKind.Plus)) {
            negative = false;
        }

        if (Check(SqlTokenKind.NumberLiteral)) {
            var tok = Advance();
            decimal val;
            if (tok.Value is decimal d) {
                val = d;
            } else if (tok.Value is long l) {
                val = l;
            } else if (decimal.TryParse(tok.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) {
                val = parsed;
            } else {
                val = 0m;
            }
            return negative ? -val : val;
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: errorMessage,
            span: Current.Span
        );
        return null;
    }

    private SqlAdvanceClause? ParseAdvanceClause() {
        var advanceTok = Current;
        var rateVal = ParseDecimalValue("Expected advance rate number");
        if (!rateVal.HasValue) {
            return null;
        }

        ConsumeKeyword("PER", "Expected 'PER' in ADVANCE clause");
        var unitTok = ConsumeWord("Expected unit (e.g. 'SECOND') in ADVANCE clause");
        if (!string.Equals(unitTok.Text, "SECOND", StringComparison.OrdinalIgnoreCase)) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"ADVANCE unit '{unitTok.Text}' is not supported; only 'SECOND' is admitted",
                span: unitTok.Span
            );
        }

        var span = new SourceSpan(
            advanceTok.Span.Offset,
            (unitTok.Span.Offset + unitTok.Span.Length) - advanceTok.Span.Offset,
            advanceTok.Span.Line,
            advanceTok.Span.Column
        );

        return new SqlAdvanceClause(rateVal.Value, unitTok.Text, span);
    }

    private SqlRuleBodyStatement? ParseInsert() {
        var insertTok = Advance(); // INSERT
        ConsumeKeyword("INTO", "Expected 'INTO' after 'INSERT'");

        var tableTok = ConsumeWord("Expected table name in INSERT statement");
        var tableName = tableTok.Text;

        List<string>? columns = null;

        if (Match(SqlTokenKind.OpenParen)) {
            columns = new List<string>();
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                var colTok = ConsumeWord("Expected column name in column list");
                columns.Add(colTok.Text);
                if (!Match(SqlTokenKind.Comma)) {
                    break;
                }
            }
            Consume(SqlTokenKind.CloseParen, "Expected ')' closing column list");
        }

        if (MatchKeyword("SELECT")) {
            var selectList = new List<SqlExpression>();
            while (!CheckKeyword("FROM") && !IsAtEnd) {
                selectList.Add(ParseExpression());
                if (!Match(SqlTokenKind.Comma)) {
                    break;
                }
            }

            ConsumeKeyword("FROM", "Expected 'FROM' in nearest query");
            var fromTok = ConsumeWord("Expected source table name after 'FROM'");
            var fromTable = fromTok.Text;

            SqlExpression? whereExpr = null;
            if (MatchKeyword("WHERE")) {
                whereExpr = ParseExpression();
            }

            ConsumeKeyword("ORDER", "Expected 'ORDER BY' in nearest query");
            ConsumeKeyword("BY", "Expected 'BY' after 'ORDER'");
            var orderExpr = ParseExpression();

            var isDescending = false;
            if (MatchKeyword("DESC")) {
                isDescending = true;
            } else {
                MatchKeyword("ASC");
            }

            ConsumeKeyword("LIMIT", "Expected 'LIMIT' in nearest query");
            var limitTok = Consume(SqlTokenKind.NumberLiteral, "Expected integer limit in nearest query");
            var limit = Convert.ToInt32(limitTok.Value ?? int.Parse(limitTok.Text, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

            Match(SqlTokenKind.Semicolon);

            var fullSpan = new SourceSpan(
                insertTok.Span.Offset,
                (Current.Span.Offset - insertTok.Span.Offset),
                insertTok.Span.Line,
                insertTok.Span.Column
            );

            return new SqlInsertSelectStatement(
                TargetTable: tableName,
                TargetColumns: columns,
                SelectList: selectList,
                FromTable: fromTable,
                Where: whereExpr,
                OrderBy: orderExpr,
                IsDescending: isDescending,
                Limit: limit,
                Span: fullSpan
            );
        }

        ConsumeKeyword("VALUES", "Expected 'VALUES' in INSERT statement");

        var valuesRows = new List<IReadOnlyList<SqlExpression>>();

        while (!Check(SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword("CREATE") && !CheckKeyword("INSERT") && !CheckKeyword("DECLARE")) {
            var openParen = Consume(SqlTokenKind.OpenParen, "Expected '(' starting values row");
            var rowVals = new List<SqlExpression>();

            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                var expr = ParseExpression();
                rowVals.Add(expr);
                if (!Match(SqlTokenKind.Comma)) {
                    break;
                }
            }

            Consume(SqlTokenKind.CloseParen, "Expected ')' closing values row");
            valuesRows.Add(rowVals);

            if (!Match(SqlTokenKind.Comma)) {
                break;
            }
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            insertTok.Span.Offset,
            (Current.Span.Offset - insertTok.Span.Offset),
            insertTok.Span.Line,
            insertTok.Span.Column
        );

        return new SqlInsertStatement(tableName, columns, valuesRows, span);
    }

    private SqlDeclareSlotStatement? ParseDeclareSlot() {
        var declareTok = Advance(); // DECLARE

        var nameTok = ConsumeWord("Expected slot name after DECLARE");
        var slotName = nameTok.Text;

        var typeTok = ConsumeWord("Expected slot type (INT, FIXED, BOOL, TEXT, etc.)");
        var declaredType = typeTok.Text;
        var typeSpan = typeTok.Span;

        string? space = null;
        if (string.Equals(declaredType, "VECTOR", StringComparison.OrdinalIgnoreCase) && Match(SqlTokenKind.OpenParen)) {
            if (!Check(SqlTokenKind.CloseParen)) {
                var spaceTok = ConsumeWord("Expected space name in VECTOR(...)");
                space = spaceTok.Text;
                Consume(SqlTokenKind.CloseParen, "Expected ')' closing VECTOR(space)");
            }
        } else if (Match(SqlTokenKind.OpenParen)) {
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(SqlTokenKind.CloseParen, "Expected ')' closing type parameters");
        }

        ValidateType(declaredType, typeSpan);

        SqlExpression? defaultValue = null;
        SqlCheckConstraint? check = null;
        SqlAdvanceClause? advance = null;

        while (!Check(SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword("CREATE") && !CheckKeyword("INSERT") && !CheckKeyword("DECLARE")) {
            if (MatchKeyword("DEFAULT")) {
                defaultValue = ParseExpression();
                continue;
            }

            if (MatchKeyword("CHECK")) {
                check = ParseCheckConstraint(slotName, declaredType);
                continue;
            }

            if (MatchKeyword("ADVANCE")) {
                advance = ParseAdvanceClause();
                continue;
            }

            break;
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            declareTok.Span.Offset,
            (Current.Span.Offset - declareTok.Span.Offset),
            declareTok.Span.Line,
            declareTok.Span.Column
        );

        return new SqlDeclareSlotStatement(
            Name: slotName,
            DeclaredType: declaredType,
            DefaultValue: defaultValue,
            Check: check,
            Advance: advance,
            Span: span,
            TypeSpan: typeSpan,
            Space: space
        );
    }

    private SqlCreatePolicyStatement? ParseCreatePolicy(SourceSpan startSpan) {
        // Optional policy name: CREATE POLICY [name] ON target ...
        string? policyName = null;
        if ((Current.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword) &&
            !CheckKeyword("ON")) {
            var nameTok = Advance();
            policyName = nameTok.Text;
        }

        ConsumeKeyword("ON", "Expected 'ON' in CREATE POLICY");
        var targetTok = ConsumeWord("Expected target table or row name in CREATE POLICY");
        var target = targetTok.Text;

        ConsumeKeyword("FOR", "Expected 'FOR SELECT' in CREATE POLICY");
        ConsumeKeyword("SELECT", "Expected 'SELECT' in CREATE POLICY");
        ConsumeKeyword("TO", "Expected 'TO' in CREATE POLICY");

        List<string>? readers = null;
        string? readersFrom = null;

        if (MatchKeyword("READERS")) {
            ConsumeKeyword("FROM", "Expected 'FROM' after 'READERS'");
            var fromTok = ConsumeWord("Expected readersFrom row name");
            readersFrom = fromTok.Text;
        } else {
            readers = new List<string>();
            while (!Check(SqlTokenKind.Semicolon) && !IsAtEnd) {
                var rTok = ConsumeWord("Expected reader name");
                readers.Add(rTok.Text);
                if (!Match(SqlTokenKind.Comma)) {
                    break;
                }
            }
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            startSpan.Offset,
            (Current.Span.Offset - startSpan.Offset),
            startSpan.Line,
            startSpan.Column
        );

        return new SqlCreatePolicyStatement(
            Target: target,
            Role: "SELECT",
            Readers: readers,
            ReadersFrom: readersFrom,
            Span: span
        );
    }

    private SqlCreateRuleStatement? ParseCreateRule(SourceSpan startSpan) {
        var nameTok = ConsumeWord("Expected rule name after CREATE RULE");
        var ruleName = nameTok.Text;

        string firingMode;
        if (MatchKeyword("EVERY")) {
            ConsumeKeyword("TICK", "Expected 'TICK' after 'EVERY'");
            firingMode = "Level";
        } else if (MatchKeyword("ON")) {
            ConsumeKeyword("ENTER", "Expected 'ENTER' after 'ON'");
            firingMode = "Edge";
        } else {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: "Expected 'EVERY TICK' or 'ON ENTER' firing mode in CREATE RULE",
                span: Current.Span
            );
            firingMode = "Level";
        }

        ConsumeKeyword("AS", "Expected 'AS' starting rule body");

        var statements = new List<SqlRuleBodyStatement>();

        if (CheckKeyword("BEGIN")) {
            var atomicStmt = ParseAtomicStatement();
            if (atomicStmt is not null) {
                statements.Add(atomicStmt);
            }
        } else {
            var bodyStmt = ParseRuleBodyStatement();
            if (bodyStmt is not null) {
                statements.Add(bodyStmt);
            }
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            startSpan.Offset,
            (Current.Span.Offset - startSpan.Offset),
            startSpan.Line,
            startSpan.Column
        );

        return new SqlCreateRuleStatement(
            Name: ruleName,
            FiringMode: firingMode,
            Statements: statements,
            Span: span
        );
    }

    public SqlRuleBodyStatement? ParseRuleBodyStatement() {
        if (CheckKeyword("UPDATE")) {
            return ParseUpdate();
        }
        if (CheckKeyword("DELETE")) {
            return ParseDelete();
        }
        if (CheckKeyword("INSERT")) {
            return ParseInsert();
        }
        if (CheckKeyword("BEGIN")) {
            return ParseAtomicStatement();
        }
        if (CheckKeyword("IF")) {
            return ParseIfStatement();
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: $"Expected UPDATE, DELETE, INSERT, BEGIN ATOMIC, or IF in rule body, found '{Current.Text}'",
            span: Current.Span
        );
        Advance();
        return null;
    }

    private SqlUpdateStatement? ParseUpdate() {
        var updateTok = Advance(); // UPDATE

        var tableTok = ConsumeWord("Expected table name in UPDATE statement");
        var tableName = tableTok.Text;

        ConsumeKeyword("SET", "Expected 'SET' in UPDATE statement");

        var assignments = new List<SqlAssignment>();

        while (!Check(SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword("WHERE")) {
            var colTok = ConsumeWord("Expected column name in SET clause");
            var colName = colTok.Text;

            Consume(SqlTokenKind.Equal, "Expected '=' after column name in SET clause");

            var valExpr = ParseExpression();

            // Detect `col = col + expr` or `col = col - expr`
            var op = SqlAssignmentOp.Assign;
            if (valExpr is SqlBinaryExpression { Operator: "+" or "-" } binExpr) {
                if (binExpr.Left is SqlColumnRefExpression colRef &&
                    string.Equals(colRef.ColumnName, colName, StringComparison.OrdinalIgnoreCase)) {
                    op = SqlAssignmentOp.Add;
                    if (binExpr.Operator == "-") {
                        valExpr = new SqlUnaryExpression("-", binExpr.Right, binExpr.Span);
                    } else {
                        valExpr = binExpr.Right;
                    }
                }
            }

            var assignSpan = new SourceSpan(
                colTok.Span.Offset,
                (valExpr.Span.Offset + valExpr.Span.Length) - colTok.Span.Offset,
                colTok.Span.Line,
                colTok.Span.Column
            );

            assignments.Add(new SqlAssignment(colName, op, valExpr, assignSpan));

            if (!Match(SqlTokenKind.Comma)) {
                break;
            }
        }

        SqlExpression? whereExpr = null;
        if (MatchKeyword("WHERE")) {
            whereExpr = ParseExpression();
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            updateTok.Span.Offset,
            (Current.Span.Offset - updateTok.Span.Offset),
            updateTok.Span.Line,
            updateTok.Span.Column
        );

        return new SqlUpdateStatement(tableName, assignments, whereExpr, span);
    }

    private SqlDeleteStatement? ParseDelete() {
        var deleteTok = Advance(); // DELETE
        ConsumeKeyword("FROM", "Expected 'FROM' in DELETE statement");

        var tableTok = ConsumeWord("Expected table name in DELETE statement");
        var tableName = tableTok.Text;

        SqlExpression? whereExpr = null;
        if (MatchKeyword("WHERE")) {
            whereExpr = ParseExpression();
        }

        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            deleteTok.Span.Offset,
            (Current.Span.Offset - deleteTok.Span.Offset),
            deleteTok.Span.Line,
            deleteTok.Span.Column
        );

        return new SqlDeleteStatement(tableName, whereExpr, span);
    }

    private SqlAtomicStatement? ParseAtomicStatement() {
        var beginTok = Advance(); // BEGIN
        ConsumeKeyword("ATOMIC", "Expected 'ATOMIC' after 'BEGIN'");

        var mainStatements = new List<SqlRuleBodyStatement>();

        while (!CheckKeyword("EXCEPTION") && !CheckKeyword("END") && !IsAtEnd) {
            var stmt = ParseRuleBodyStatement();
            if (stmt is not null) {
                mainStatements.Add(stmt);
            }
            Match(SqlTokenKind.Semicolon);
        }

        List<SqlRuleBodyStatement>? exceptionStatements = null;

        if (MatchKeyword("EXCEPTION")) {
            exceptionStatements = new List<SqlRuleBodyStatement>();
            while (!CheckKeyword("END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();
                if (stmt is not null) {
                    exceptionStatements.Add(stmt);
                }
                Match(SqlTokenKind.Semicolon);
            }
        }

        var endTok = ConsumeKeyword("END", "Expected 'END' closing BEGIN ATOMIC block");
        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            beginTok.Span.Offset,
            (endTok.Span.Offset + endTok.Span.Length) - beginTok.Span.Offset,
            beginTok.Span.Line,
            beginTok.Span.Column
        );

        return new SqlAtomicStatement(mainStatements, exceptionStatements, span);
    }

    private SqlIfStatement? ParseIfStatement() {
        var ifTok = Advance(); // IF

        var condition = ParseExpression();
        ConsumeKeyword("THEN", "Expected 'THEN' after IF condition");

        var thenStatements = new List<SqlRuleBodyStatement>();
        while (!CheckKeyword("ELSIF") && !CheckKeyword("ELSE") && !CheckKeyword("END") && !IsAtEnd) {
            var stmt = ParseRuleBodyStatement();
            if (stmt is not null) {
                thenStatements.Add(stmt);
            }
            Match(SqlTokenKind.Semicolon);
        }

        var elsifStatements = new List<(SqlExpression Condition, IReadOnlyList<SqlRuleBodyStatement> Statements)>();

        while (MatchKeyword("ELSIF")) {
            var elsifCond = ParseExpression();
            ConsumeKeyword("THEN", "Expected 'THEN' after ELSIF condition");
            var stmts = new List<SqlRuleBodyStatement>();
            while (!CheckKeyword("ELSIF") && !CheckKeyword("ELSE") && !CheckKeyword("END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();
                if (stmt is not null) {
                    stmts.Add(stmt);
                }
                Match(SqlTokenKind.Semicolon);
            }
            elsifStatements.Add((elsifCond, stmts));
        }

        List<SqlRuleBodyStatement>? elseStatements = null;
        if (MatchKeyword("ELSE")) {
            elseStatements = new List<SqlRuleBodyStatement>();
            while (!CheckKeyword("END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();
                if (stmt is not null) {
                    elseStatements.Add(stmt);
                }
                Match(SqlTokenKind.Semicolon);
            }
        }

        var endTok = ConsumeKeyword("END", "Expected 'END IF'");
        ConsumeKeyword("IF", "Expected 'IF' after 'END'");
        Match(SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            ifTok.Span.Offset,
            (endTok.Span.Offset + endTok.Span.Length) - ifTok.Span.Offset,
            ifTok.Span.Line,
            ifTok.Span.Column
        );

        return new SqlIfStatement(condition, thenStatements, elsifStatements, elseStatements, span);
    }

    // ---- Expression Parsing with Precedence -----------------------------------------------------------------

    public SqlExpression ParseExpression() => ParseOr();

    private SqlExpression ParseOr() {
        var left = ParseAnd();

        while (MatchKeyword("OR")) {
            var right = ParseAnd();
            var span = CombineSpan(left.Span, right.Span);
            left = new SqlBinaryExpression("OR", left, right, span);
        }

        return left;
    }

    private SqlExpression ParseAnd() {
        var left = ParseNot();

        while (MatchKeyword("AND")) {
            var right = ParseNot();
            var span = CombineSpan(left.Span, right.Span);
            left = new SqlBinaryExpression("AND", left, right, span);
        }

        return left;
    }

    private SqlExpression ParseNot() {
        if (MatchKeyword("NOT")) {
            var opTok = m_tokens[m_index - 1];
            var operand = ParseComparison();
            var span = CombineSpan(opTok.Span, operand.Span);
            return new SqlUnaryExpression("NOT", operand, span);
        }

        return ParseComparison();
    }

    private SqlExpression ParseComparison() {
        var left = ParseAdditive();

        // Check for IS NULL / IS NOT NULL
        if (MatchKeyword("IS")) {
            var isSpan = m_tokens[m_index - 1].Span;
            var isNot = MatchKeyword("NOT");
            ConsumeKeyword("NULL", "Expected 'NULL' after 'IS'");
            var clause = isNot ? "IS NOT NULL" : "IS NULL";
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"'{clause}' is unsupported in Puck state SQL dialect — state facts hold absent cells rather than three-valued NULL logic.",
                span: isSpan
            );
            return left;
        }

        if (Check(SqlTokenKind.Equal) || Check(SqlTokenKind.NotEqual) ||
            Check(SqlTokenKind.Less) || Check(SqlTokenKind.LessOrEqual) ||
            Check(SqlTokenKind.Greater) || Check(SqlTokenKind.GreaterOrEqual) ||
            Check(SqlTokenKind.CosineDistance)) {
            var opTok = Advance();
            var op = opTok.Text;
            var right = ParseAdditive();
            var span = CombineSpan(left.Span, right.Span);
            return new SqlBinaryExpression(op, left, right, span);
        }

        return left;
    }

    private SqlExpression ParseAdditive() {
        var left = ParseMultiplicative();

        while (Check(SqlTokenKind.Plus) || Check(SqlTokenKind.Minus)) {
            var opTok = Advance();
            var right = ParseMultiplicative();
            var span = CombineSpan(left.Span, right.Span);
            left = new SqlBinaryExpression(opTok.Text, left, right, span);
        }

        return left;
    }

    private SqlExpression ParseMultiplicative() {
        var left = ParseUnary();

        while (Check(SqlTokenKind.Multiply) || Check(SqlTokenKind.Divide) || Check(SqlTokenKind.Modulo)) {
            var opTok = Advance();
            var right = ParseUnary();
            var span = CombineSpan(left.Span, right.Span);
            left = new SqlBinaryExpression(opTok.Text, left, right, span);
        }

        return left;
    }

    private SqlExpression ParseUnary() {
        if (Check(SqlTokenKind.Minus) || Check(SqlTokenKind.Plus)) {
            var opTok = Advance();
            var operand = ParsePrimary();
            var span = CombineSpan(opTok.Span, operand.Span);
            return new SqlUnaryExpression(opTok.Text, operand, span);
        }

        return ParsePrimary();
    }

    private SqlExpression ParsePrimary() {
        // Literals
        if (Check(SqlTokenKind.NumberLiteral) || Check(SqlTokenKind.StringLiteral) ||
            Check(SqlTokenKind.BooleanLiteral) || Check(SqlTokenKind.NullLiteral)) {
            var tok = Advance();
            return new SqlLiteralExpression(tok.Value, tok.Span);
        }

        // Aggregate functions: COUNT, MIN, MAX, SUM
        if (CheckKeyword("COUNT") || CheckKeyword("MIN") || CheckKeyword("MAX") || CheckKeyword("SUM")) {
            var funcTok = Advance();
            Consume(SqlTokenKind.OpenParen, $"Expected '(' after '{funcTok.Text}'");
            string? tbl = null;
            var col = ConsumeWord("Expected column name in aggregate").Text;
            if (Match(SqlTokenKind.Dot)) {
                tbl = col;
                col = ConsumeWord("Expected column name after '.' in aggregate").Text;
            }
            var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' after aggregate column");
            var aggSpan = CombineSpan(funcTok.Span, closeParen.Span);

            // Refuse row aggregate: Puck.State.ExpressionVocabulary has no row aggregates
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"Row aggregate '{funcTok.Text}(...)' is unsupported in Puck state SQL dialect — state expressions evaluate per-cell scalar operations only.",
                span: aggSpan
            );

            return new SqlAggregateExpression(funcTok.Text, col, tbl, aggSpan);
        }

        // Vector literals: embed('text' [, space: lore]) and vector('base64url')
        if (CheckKeyword("embed") && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();
            Consume(SqlTokenKind.OpenParen, "Expected '(' after 'embed'");
            var textTok = Consume(SqlTokenKind.StringLiteral, "Expected string literal in embed(...)");
            string? space = null;
            if (Match(SqlTokenKind.Comma)) {
                ConsumeKeyword("space", "Expected 'space' named argument in embed(...)");
                if (Match(SqlTokenKind.Colon) || Match(SqlTokenKind.Equal)) {
                    var spaceTok = ConsumeWord("Expected space name in embed(...)");
                    space = spaceTok.Text;
                }
            }
            var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' closing embed(...)");
            return new SqlVectorLiteralExpression("embed", textTok.Text, space, CombineSpan(funcTok.Span, closeParen.Span));
        }

        if (CheckKeyword("vector") && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();
            Consume(SqlTokenKind.OpenParen, "Expected '(' after 'vector'");
            var textTok = Consume(SqlTokenKind.StringLiteral, "Expected base64url string literal in vector(...)");
            var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' closing vector(...)");
            return new SqlVectorLiteralExpression("vector", textTok.Text, null, CombineSpan(funcTok.Span, closeParen.Span));
        }

        // Vector functions: similarity, dot, identical
        if ((CheckKeyword("similarity") || CheckKeyword("dot") || CheckKeyword("identical")) && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();
            Consume(SqlTokenKind.OpenParen, $"Expected '(' after '{funcTok.Text}'");
            var args = new List<SqlExpression>();
            while (!Check(SqlTokenKind.CloseParen) && !IsAtEnd) {
                args.Add(ParseExpression());
                if (!Match(SqlTokenKind.Comma)) {
                    break;
                }
            }
            var closeParen = Consume(SqlTokenKind.CloseParen, $"Expected ')' after '{funcTok.Text}' arguments");
            return new SqlFunctionCallExpression(funcTok.Text.ToLowerInvariant(), args, CombineSpan(funcTok.Span, closeParen.Span));
        }

        // Parentheses: grouping or scalar subquery
        if (Match(SqlTokenKind.OpenParen)) {
            var openParen = m_tokens[m_index - 1];

            // Subquery: (SELECT c FROM other WHERE key = 'k')
            if (MatchKeyword("SELECT")) {
                var colTok = ConsumeWord("Expected column name in scalar subquery");
                ConsumeKeyword("FROM", "Expected 'FROM' in scalar subquery");
                var fromTok = ConsumeWord("Expected table name in scalar subquery");
                ConsumeKeyword("WHERE", "Expected 'WHERE' in scalar subquery");
                var keyColTok = ConsumeWord("Expected key column name in scalar subquery WHERE clause");
                Consume(SqlTokenKind.Equal, "Expected '=' in scalar subquery WHERE clause");
                var valTok = Advance();
                var keyVal = valTok.Text.Trim('\'', '"');
                var closeParen = Consume(SqlTokenKind.CloseParen, "Expected ')' closing scalar subquery");
                var subquerySpan = CombineSpan(openParen.Span, closeParen.Span);
                return new SqlSubqueryExpression(fromTok.Text, colTok.Text, keyColTok.Text, keyVal, subquerySpan);
            }

            var inner = ParseExpression();
            Consume(SqlTokenKind.CloseParen, "Expected ')' after parenthesized expression");
            return inner;
        }

        // Column reference: col or table.col or col[key] or table.col[key]
        if (Check(SqlTokenKind.Identifier) || Check(SqlTokenKind.Keyword)) {
            var firstTok = Advance();
            string? tbl = null;
            var col = firstTok.Text;

            if (Match(SqlTokenKind.Dot)) {
                tbl = firstTok.Text;
                var secondTok = ConsumeWord("Expected column name after '.'");
                col = secondTok.Text;
                if (Match(SqlTokenKind.OpenBracket)) {
                    var keyTok = Advance();
                    var closeBracket = Consume(SqlTokenKind.CloseBracket, "Expected ']' after cell key");
                    var keyVal = keyTok.Text.Trim('\'', '"');
                    var span = CombineSpan(firstTok.Span, closeBracket.Span);
                    return new SqlSubqueryExpression(tbl, col, "id", keyVal, span);
                }
                return new SqlColumnRefExpression(tbl, col, CombineSpan(firstTok.Span, secondTok.Span));
            }

            if (Match(SqlTokenKind.OpenBracket)) {
                var keyTok = Advance();
                var closeBracket = Consume(SqlTokenKind.CloseBracket, "Expected ']' after cell key");
                var keyVal = keyTok.Text.Trim('\'', '"');
                var span = CombineSpan(firstTok.Span, closeBracket.Span);
                return new SqlSubqueryExpression(col, "value", "id", keyVal, span);
            }

            return new SqlColumnRefExpression(null, col, firstTok.Span);
        }

        var errTok = Advance();
        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: $"Unexpected expression token '{errTok.Text}' in SQL dialect",
            span: errTok.Span
        );
        return new SqlLiteralExpression(null, errTok.Span);
    }

    private static SourceSpan CombineSpan(SourceSpan a, SourceSpan b) {
        var start = Math.Min(a.Offset, b.Offset);
        var end = Math.Max(a.Offset + a.Length, b.Offset + b.Length);
        return new SourceSpan(start, end - start, a.Line, a.Column);
    }

    private void Synchronize() {
        while (!IsAtEnd) {
            if (Current.Kind == SqlTokenKind.Semicolon) {
                Advance();
                return;
            }

            if (CheckKeyword("CREATE") || CheckKeyword("INSERT") || CheckKeyword("DECLARE") ||
                CheckKeyword("UPDATE") || CheckKeyword("DELETE") || CheckKeyword("BEGIN") || CheckKeyword("IF")) {
                return;
            }

            Advance();
        }
    }
}
