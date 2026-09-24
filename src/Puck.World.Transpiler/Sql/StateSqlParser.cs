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
        m_tokens = (tokens ?? throw new ArgumentNullException(paramName: nameof(tokens)));
        m_diagnostics = (diagnostics ?? throw new ArgumentNullException(paramName: nameof(diagnostics)));
        m_index = 0;
    }

    private SqlToken Current => ((m_index < m_tokens.Count) ? m_tokens[m_index] : m_tokens[^1]);
    private bool IsAtEnd => (Current.Kind == SqlTokenKind.Eof);

    private SqlToken Peek(int offset = 1) {
        var pos = (m_index + offset);

        return ((pos < m_tokens.Count) ? m_tokens[pos] : m_tokens[^1]);
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
        string.Equals(a: Current.Text, b: keyword, comparisonType: StringComparison.OrdinalIgnoreCase);
    private bool Match(SqlTokenKind kind) {
        if (Check(kind: kind)) {
            Advance();
            return true;
        }
        return false;
    }
    private bool MatchKeyword(string keyword) {
        if (CheckKeyword(keyword: keyword)) {
            Advance();
            return true;
        }
        return false;
    }
    private SqlToken Consume(SqlTokenKind kind, string message) {
        if (Check(kind: kind)) {
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
        if (CheckKeyword(keyword: keyword)) {
            return Advance();
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlSyntaxError,
            message: message,
            span: Current.Span
        );
        return Current;
    }
    private bool TryConsumeWord(string message, out SqlToken token) {
        var admitted = (Current.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword);

        token = ConsumeWord(message: message);

        return admitted;
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
            if (Match(kind: SqlTokenKind.Semicolon)) {
                continue;
            }

            var startTok = Current;

            if (CheckKeyword(keyword: "CREATE")) {
                var stmt = ParseCreate();

                if (stmt is not null) {
                    statements.Add(item: stmt);
                }
            } else if (CheckKeyword(keyword: "INSERT")) {
                var stmt = ParseInsert();

                if (stmt is not null) {
                    statements.Add(item: stmt);
                }
            } else if (CheckKeyword(keyword: "DECLARE")) {
                var stmt = ParseDeclareSlot();

                if (stmt is not null) {
                    statements.Add(item: stmt);
                }
            } else if (CheckKeyword(keyword: "UPDATE") || CheckKeyword(keyword: "DELETE") || CheckKeyword(keyword: "BEGIN") || CheckKeyword(keyword: "IF")) {
                var stmt = ParseRuleBodyStatement();

                if (stmt is not null) {
                    statements.Add(item: stmt);
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

        if (MatchKeyword(keyword: "TABLE")) {
            return ParseCreateTable(startSpan: createTok.Span);
        }
        if (MatchKeyword(keyword: "POLICY")) {
            return ParseCreatePolicy(startSpan: createTok.Span);
        }
        if (MatchKeyword(keyword: "RULE")) {
            return ParseCreateRule(startSpan: createTok.Span);
        }

        if (CheckKeyword(keyword: "VIEW") || CheckKeyword(keyword: "TRIGGER")) {
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
        var nameTok = ConsumeWord(message: "Expected table name in CREATE TABLE");
        var tableName = nameTok.Text;

        Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' after table name");

        var columns = new List<SqlColumnDefinition>();
        var hasPrimaryKey = false;
        var malformedColumn = false;

        while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
            // Check for table constraint: PRIMARY KEY (col1, col2, ...)
            if (CheckKeyword(keyword: "PRIMARY")) {
                var pkTok = Advance();

                ConsumeKeyword(keyword: "KEY", message: "Expected 'KEY' after 'PRIMARY'");
                var parenTok = Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' after 'PRIMARY KEY'");

                var pkCols = new List<string>();

                while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                    var colTok = ConsumeWord(message: "Expected column name in PRIMARY KEY constraint");

                    pkCols.Add(item: colTok.Text);
                    if (!Match(kind: SqlTokenKind.Comma)) {
                        break;
                    }
                }
                var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing PRIMARY KEY constraint");

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

                    for (var i = 0; (i < columns.Count); i++) {
                        if (string.Equals(a: columns[i].Name, b: pkCols[0], comparisonType: StringComparison.OrdinalIgnoreCase)) {
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

                Match(kind: SqlTokenKind.Comma);
                continue;
            }

            var colDef = ParseColumnDefinition(hasPrimaryKey: ref hasPrimaryKey, tableName: tableName);

            if (colDef is not null) {
                columns.Add(item: colDef);
            } else {
                malformedColumn = true;
            }

            if (!Match(kind: SqlTokenKind.Comma)) {
                break;
            }
        }

        var closeParenTok = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing table column definitions");

        // Table options after ')'
        int? capacity = null;
        var isOrdered = false;
        var isEvicts = false;

        while (!Check(kind: SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword(keyword: "CREATE") && !CheckKeyword(keyword: "INSERT") && !CheckKeyword(keyword: "DECLARE")) {
            if (MatchKeyword(keyword: "CAPACITY")) {
                var capTok = Consume(kind: SqlTokenKind.NumberLiteral, message: "Expected integer capacity value");

                if (capTok.Value is long l) {
                    capacity = ((int)l);
                } else if (int.TryParse(capTok.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCap)) {
                    capacity = parsedCap;
                }
                continue;
            }

            if (MatchKeyword(keyword: "ORDERED")) {
                isOrdered = true;
                continue;
            }

            if (MatchKeyword(keyword: "EVICTS")) {
                isEvicts = true;
                continue;
            }

            break;
        }

        Match(kind: SqlTokenKind.Semicolon);

        var endSpan = closeParenTok.Span;
        var totalLength = ((endSpan.Offset + endSpan.Length) - startSpan.Offset);
        var fullSpan = new SourceSpan(startSpan.Offset, totalLength, startSpan.Line, startSpan.Column);

        // A malformed column has been refused already, and may be the key its author meant.
        if (!hasPrimaryKey && !malformedColumn) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Table '{tableName}' must declare a column as PRIMARY KEY",
                span: fullSpan
            );
        }

        var pkCol = columns.Find(match: c => c.IsPrimaryKey);

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
            Capacity: capacity,
            Columns: columns,
            IsEvicts: isEvicts,
            IsOrdered: isOrdered,
            Span: fullSpan,
            TableName: tableName
        );
    }
    // A column missing its name or its type is refused by that one fault and yields no definition, so nothing read
    // after it — the type table, the table's primary key — is held against a column that was never written.
    private SqlColumnDefinition? ParseColumnDefinition(string tableName, ref bool hasPrimaryKey) {
        if (!TryConsumeWord(message: "Expected column name", token: out var colNameTok)) {
            return null;
        }
        var colName = colNameTok.Text;

        if (!TryConsumeWord(message: $"Column '{colName}' has no type; expected one of INT, FIXED, BOOL, TEXT, VECTOR", token: out var typeTok)) {
            return null;
        }
        var declaredType = typeTok.Text;
        var typeSpan = typeTok.Span;

        string? space = null;

        if (string.Equals(a: declaredType, b: "VECTOR", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (Match(kind: SqlTokenKind.OpenParen)) {
                var spaceTok = ConsumeWord(message: "Expected space name in VECTOR(...)");

                space = spaceTok.Text;
                Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing VECTOR(space)");
            }
        } else if (Match(kind: SqlTokenKind.OpenParen)) {
            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing type parameters");
        }

        ValidateType(type: typeTok);

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

        while (!Check(kind: SqlTokenKind.Comma) && !Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
            if (MatchKeyword(keyword: "PRIMARY")) {
                ConsumeKeyword(keyword: "KEY", message: "Expected 'KEY' after 'PRIMARY'");
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

            if (MatchKeyword(keyword: "NOT")) {
                ConsumeKeyword(keyword: "NULL", message: "Expected 'NULL' after 'NOT'");
                isNotNull = true;
                continue;
            }

            if (MatchKeyword(keyword: "NULL")) {
                continue;
            }

            if (MatchKeyword(keyword: "ROW") || MatchKeyword(keyword: "AS")) {
                var rowNameTok = ConsumeWord(message: "Expected custom row name after 'ROW' or 'AS'");

                explicitRowName = rowNameTok.Text;
                continue;
            }

            if (MatchKeyword(keyword: "DEFAULT")) {
                defaultValue = ParseDefaultValue();
                continue;
            }

            if (MatchKeyword(keyword: "CHECK")) {
                check = ParseCheckConstraint(colType: declaredType, expectedColName: colName);
                continue;
            }

            if (MatchKeyword(keyword: "ON")) {
                ConsumeKeyword(keyword: "OVERFLOW", message: "Expected 'OVERFLOW' after 'ON'");
                var policyTok = ConsumeWord(message: "Expected 'SATURATE' or 'REFUSE' after 'ON OVERFLOW'");

                if (!string.Equals(a: policyTok.Text, b: "SATURATE", comparisonType: StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(a: policyTok.Text, b: "REFUSE", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    m_diagnostics.ReportError(
                        code: PuckDiagnosticCodes.SqlSyntaxError,
                        message: $"ON OVERFLOW policy must be SATURATE or REFUSE, found '{policyTok.Text}'",
                        span: policyTok.Span
                    );
                }
                overflowPolicy = policyTok.Text;
                continue;
            }

            if (MatchKeyword(keyword: "ADVANCE")) {
                advance = ParseAdvanceClause();
                continue;
            }

            if (MatchKeyword(keyword: "DYNAMICS")) {
                var dynTok = ConsumeWord(message: "Expected dynamics row name after 'DYNAMICS'");

                dynamicsName = dynTok.Text;
                continue;
            }

            if (MatchKeyword(keyword: "REFERENCES")) {
                var refTok = ConsumeWord(message: "Expected referenced table name after 'REFERENCES'");

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

            if (MatchKeyword(keyword: "ORDERED")) {
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
            Advance: advance,
            Check: check,
            DeclaredType: declaredType,
            DefaultValue: defaultValue,
            DynamicsName: dynamicsName,
            ExplicitRowName: explicitRowName,
            IsNotNull: isNotNull,
            IsOrdered: isOrdered,
            IsPrimaryKey: isPrimaryKey,
            Name: colName,
            OverflowPolicy: overflowPolicy,
            ReferencesTable: referencesTable,
            Space: space,
            Span: colSpan,
            TypeSpan: typeSpan
        );
    }

    /// <summary>Returns the state cell kind a declared SQL type lowers to: the dialect's one type table, which the
    /// parser refuses a declaration by and the lowering reads a declaration's kind from.</summary>
    /// <param name="typeName">The declared type, in any case.</param>
    /// <returns><c>Int</c>, <c>Fixed</c>, <c>Bool</c>, <c>Text</c> or <c>Vector</c>, or <see langword="null"/> for a
    /// type the dialect does not admit.</returns>
    public static string? KindOf(string typeName) {
        ArgumentNullException.ThrowIfNull(argument: typeName);

        return typeName.ToUpperInvariant() switch {
            "INT" or "INTEGER" or "SMALLINT" or "BIGINT" or "TINYINT" => "Int",
            "FIXED" or "DECIMAL" or "NUMERIC" => "Fixed",
            "BOOL" or "BOOLEAN" => "Bool",
            "TEXT" or "VARCHAR" or "CHAR" or "STRING" => "Text",
            "VECTOR" => "Vector",
            _ => null,
        };
    }

    private void ValidateType(SqlToken type) {
        if (KindOf(typeName: type.Text) is not null) {
            return;
        }

        m_diagnostics.ReportError(
            code: PuckDiagnosticCodes.SqlUnsupportedType,
            message: ((type.Text.ToUpperInvariant() is "REAL" or "FLOAT" or "DOUBLE")
                ? $"Type '{type.Text}' is unsupported; state holds no floats — use 'FIXED' instead."
                : $"Type '{type.Text}' is unrecognized or unsupported; admitted types are INT, FIXED, BOOL, TEXT, VECTOR."),
            span: type.Span
        );
    }
    private object? ParseDefaultValue() {
        if (Check(kind: SqlTokenKind.NumberLiteral)) {
            return Advance().Value;
        }
        if (Check(kind: SqlTokenKind.StringLiteral)) {
            return Advance().Value;
        }
        if (Check(kind: SqlTokenKind.BooleanLiteral)) {
            return Advance().Value;
        }
        if (Match(kind: SqlTokenKind.NullLiteral)) {
            return null;
        }

        // Unary minus for negative number
        if (Match(kind: SqlTokenKind.Minus)) {
            var numTok = Consume(kind: SqlTokenKind.NumberLiteral, message: "Expected number after '-'");

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
        var openParen = Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' starting CHECK constraint");

        var colTok = ConsumeWord(message: "Expected column name in CHECK constraint");
        var colName = colTok.Text;

        SqlCheckKind kind;
        decimal? min = null;
        decimal? max = null;

        var isInt = (string.Equals(a: colType, b: "INT", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a: colType, b: "INTEGER", comparisonType: StringComparison.OrdinalIgnoreCase));

        if (MatchKeyword(keyword: "IS")) {
            var isNot = MatchKeyword(keyword: "NOT");
            var nullTok = ConsumeKeyword(keyword: "NULL", message: "Expected 'NULL' after 'IS'");
            var isSpan = CombineSpan(a: colTok.Span, b: nullTok.Span);
            var clause = (isNot ? "IS NOT NULL" : "IS NULL");

            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"'{clause}' is unsupported in Puck state SQL dialect — state facts hold absent cells rather than three-valued NULL logic.",
                span: isSpan
            );
            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing CHECK constraint");
            return null;
        }

        if (MatchKeyword(keyword: "BETWEEN")) {
            kind = SqlCheckKind.Between;
            min = ParseDecimalValue(errorMessage: "Expected minimum value in BETWEEN clause");
            ConsumeKeyword(keyword: "AND", message: "Expected 'AND' in BETWEEN clause");
            max = ParseDecimalValue(errorMessage: "Expected maximum value in BETWEEN clause");
        } else if (Match(kind: SqlTokenKind.GreaterOrEqual)) {
            min = ParseDecimalValue(errorMessage: "Expected number after '>=' in CHECK constraint");
            if (MatchKeyword(keyword: "AND")) {
                var col2 = ConsumeWord(message: "Expected column name after 'AND' in CHECK constraint");

                Consume(kind: SqlTokenKind.LessOrEqual, message: "Expected '<=' after column in CHECK constraint");
                max = ParseDecimalValue(errorMessage: "Expected maximum value after '<=' in CHECK constraint");
                kind = SqlCheckKind.Between;
            } else {
                kind = SqlCheckKind.GreaterOrEqual;
            }
        } else if (Match(kind: SqlTokenKind.LessOrEqual)) {
            max = ParseDecimalValue(errorMessage: "Expected number after '<=' in CHECK constraint");
            kind = SqlCheckKind.LessOrEqual;
        } else if (Match(kind: SqlTokenKind.Greater)) {
            var val = ParseDecimalValue(errorMessage: "Expected number after '>' in CHECK constraint");

            if (isInt) {
                kind = SqlCheckKind.Greater;
                min = (val.HasValue ? (val.Value + 1) : null);
            } else {
                m_diagnostics.ReportError(
                    code: PuckDiagnosticCodes.SqlInvalidCheckShape,
                    message: "CHECK constraint with '>' is only admitted for INT columns; use '>=' instead.",
                    span: Current.Span
                );
                kind = SqlCheckKind.GreaterOrEqual;
                min = val;
            }
        } else if (Match(kind: SqlTokenKind.Less)) {
            var val = ParseDecimalValue(errorMessage: "Expected number after '<' in CHECK constraint");

            if (isInt) {
                kind = SqlCheckKind.Less;
                max = (val.HasValue ? (val.Value - 1) : null);
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
            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing CHECK constraint");
            return null;
        }

        var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing CHECK constraint");

        var span = new SourceSpan(
            openParen.Span.Offset,
            ((closeParen.Span.Offset + closeParen.Span.Length) - openParen.Span.Offset),
            openParen.Span.Line,
            openParen.Span.Column
        );

        return new SqlCheckConstraint(ColumnName: colName, Kind: kind, Max: max, Min: min, Span: span);
    }
    private decimal? ParseDecimalValue(string errorMessage) {
        var negative = false;

        if (Match(kind: SqlTokenKind.Minus)) {
            negative = true;
        } else if (Match(kind: SqlTokenKind.Plus)) {
            negative = false;
        }

        if (Check(kind: SqlTokenKind.NumberLiteral)) {
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
            return (negative ? -val : val);
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
        var rateVal = ParseDecimalValue(errorMessage: "Expected advance rate number");

        if (!rateVal.HasValue) {
            return null;
        }

        ConsumeKeyword(keyword: "PER", message: "Expected 'PER' in ADVANCE clause");
        var unitTok = ConsumeWord(message: "Expected unit (e.g. 'SECOND') in ADVANCE clause");

        if (!string.Equals(a: unitTok.Text, b: "SECOND", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"ADVANCE unit '{unitTok.Text}' is not supported; only 'SECOND' is admitted",
                span: unitTok.Span
            );
        }

        var span = new SourceSpan(
            advanceTok.Span.Offset,
            ((unitTok.Span.Offset + unitTok.Span.Length) - advanceTok.Span.Offset),
            advanceTok.Span.Line,
            advanceTok.Span.Column
        );

        return new SqlAdvanceClause(rateVal.Value, unitTok.Text, span);
    }
    private SqlRuleBodyStatement? ParseInsert() {
        var insertTok = Advance(); // INSERT

        ConsumeKeyword(keyword: "INTO", message: "Expected 'INTO' after 'INSERT'");

        var tableTok = ConsumeWord(message: "Expected table name in INSERT statement");
        var tableName = tableTok.Text;

        List<string>? columns = null;

        if (Match(kind: SqlTokenKind.OpenParen)) {
            columns = new List<string>();
            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                var colTok = ConsumeWord(message: "Expected column name in column list");

                columns.Add(item: colTok.Text);
                if (!Match(kind: SqlTokenKind.Comma)) {
                    break;
                }
            }
            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing column list");
        }

        if (MatchKeyword(keyword: "SELECT")) {
            var selectList = new List<SqlExpression>();

            while (!CheckKeyword(keyword: "FROM") && !IsAtEnd) {
                selectList.Add(item: ParseExpression());
                if (!Match(kind: SqlTokenKind.Comma)) {
                    break;
                }
            }

            ConsumeKeyword(keyword: "FROM", message: "Expected 'FROM' in nearest query");
            var fromTok = ConsumeWord(message: "Expected source table name after 'FROM'");
            var fromTable = fromTok.Text;

            SqlExpression? whereExpr = null;

            if (MatchKeyword(keyword: "WHERE")) {
                whereExpr = ParseExpression();
            }

            ConsumeKeyword(keyword: "ORDER", message: "Expected 'ORDER BY' in nearest query");
            ConsumeKeyword(keyword: "BY", message: "Expected 'BY' after 'ORDER'");
            var orderExpr = ParseExpression();

            var isDescending = false;

            if (MatchKeyword(keyword: "DESC")) {
                isDescending = true;
            } else {
                MatchKeyword(keyword: "ASC");
            }

            ConsumeKeyword(keyword: "LIMIT", message: "Expected 'LIMIT' in nearest query");
            var limitTok = Consume(kind: SqlTokenKind.NumberLiteral, message: "Expected integer limit in nearest query");
            var limit = Convert.ToInt32((limitTok.Value ?? int.Parse(limitTok.Text, CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

            Match(kind: SqlTokenKind.Semicolon);

            var fullSpan = new SourceSpan(
                insertTok.Span.Offset,
                (Current.Span.Offset - insertTok.Span.Offset),
                insertTok.Span.Line,
                insertTok.Span.Column
            );

            return new SqlInsertSelectStatement(
                FromTable: fromTable,
                IsDescending: isDescending,
                Limit: limit,
                OrderBy: orderExpr,
                SelectList: selectList,
                Span: fullSpan,
                TargetColumns: columns,
                TargetTable: tableName,
                Where: whereExpr
            );
        }

        ConsumeKeyword(keyword: "VALUES", message: "Expected 'VALUES' in INSERT statement");

        var valuesRows = new List<IReadOnlyList<SqlExpression>>();

        while (!Check(kind: SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword(keyword: "CREATE") && !CheckKeyword(keyword: "INSERT") && !CheckKeyword(keyword: "DECLARE")) {
            var openParen = Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' starting values row");
            var rowVals = new List<SqlExpression>();

            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                var expr = ParseExpression();

                rowVals.Add(item: expr);
                if (!Match(kind: SqlTokenKind.Comma)) {
                    break;
                }
            }

            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing values row");
            valuesRows.Add(item: rowVals);

            if (!Match(kind: SqlTokenKind.Comma)) {
                break;
            }
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            insertTok.Span.Offset,
            (Current.Span.Offset - insertTok.Span.Offset),
            insertTok.Span.Line,
            insertTok.Span.Column
        );

        return new SqlInsertStatement(Columns: columns, Span: span, TableName: tableName, ValuesRows: valuesRows);
    }
    private SqlDeclareSlotStatement? ParseDeclareSlot() {
        var declareTok = Advance(); // DECLARE

        var nameTok = ConsumeWord(message: "Expected slot name after DECLARE");
        var slotName = nameTok.Text;

        var typeTok = ConsumeWord(message: "Expected slot type (INT, FIXED, BOOL, TEXT, etc.)");
        var declaredType = typeTok.Text;
        var typeSpan = typeTok.Span;

        string? space = null;

        if (string.Equals(a: declaredType, b: "VECTOR", comparisonType: StringComparison.OrdinalIgnoreCase) && Match(kind: SqlTokenKind.OpenParen)) {
            if (!Check(kind: SqlTokenKind.CloseParen)) {
                var spaceTok = ConsumeWord(message: "Expected space name in VECTOR(...)");

                space = spaceTok.Text;
                Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing VECTOR(space)");
            }
        } else if (Match(kind: SqlTokenKind.OpenParen)) {
            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                Advance();
            }
            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing type parameters");
        }

        ValidateType(type: typeTok);

        SqlExpression? defaultValue = null;
        SqlCheckConstraint? check = null;
        SqlAdvanceClause? advance = null;

        while (!Check(kind: SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword(keyword: "CREATE") && !CheckKeyword(keyword: "INSERT") && !CheckKeyword(keyword: "DECLARE")) {
            if (MatchKeyword(keyword: "DEFAULT")) {
                defaultValue = ParseExpression();
                continue;
            }

            if (MatchKeyword(keyword: "CHECK")) {
                check = ParseCheckConstraint(colType: declaredType, expectedColName: slotName);
                continue;
            }

            if (MatchKeyword(keyword: "ADVANCE")) {
                advance = ParseAdvanceClause();
                continue;
            }

            break;
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            declareTok.Span.Offset,
            (Current.Span.Offset - declareTok.Span.Offset),
            declareTok.Span.Line,
            declareTok.Span.Column
        );

        return new SqlDeclareSlotStatement(
            Advance: advance,
            Check: check,
            DeclaredType: declaredType,
            DefaultValue: defaultValue,
            Name: slotName,
            Space: space,
            Span: span,
            TypeSpan: typeSpan
        );
    }
    private SqlCreatePolicyStatement? ParseCreatePolicy(SourceSpan startSpan) {
        // Optional policy name: CREATE POLICY [name] ON target ...
        string? policyName = null;

        if ((Current.Kind is SqlTokenKind.Identifier or SqlTokenKind.Keyword) &&
            !CheckKeyword(keyword: "ON")) {
            var nameTok = Advance();

            policyName = nameTok.Text;
        }

        ConsumeKeyword(keyword: "ON", message: "Expected 'ON' in CREATE POLICY");
        var targetTok = ConsumeWord(message: "Expected target table or row name in CREATE POLICY");
        var target = targetTok.Text;

        ConsumeKeyword(keyword: "FOR", message: "Expected 'FOR SELECT' in CREATE POLICY");
        ConsumeKeyword(keyword: "SELECT", message: "Expected 'SELECT' in CREATE POLICY");
        ConsumeKeyword(keyword: "TO", message: "Expected 'TO' in CREATE POLICY");

        List<string>? readers = null;
        string? readersFrom = null;

        if (MatchKeyword(keyword: "READERS")) {
            ConsumeKeyword(keyword: "FROM", message: "Expected 'FROM' after 'READERS'");
            var fromTok = ConsumeWord(message: "Expected readersFrom row name");

            readersFrom = fromTok.Text;
        } else {
            readers = new List<string>();
            while (!Check(kind: SqlTokenKind.Semicolon) && !IsAtEnd) {
                var rTok = ConsumeWord(message: "Expected reader name");

                readers.Add(item: rTok.Text);
                if (!Match(kind: SqlTokenKind.Comma)) {
                    break;
                }
            }
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            startSpan.Offset,
            (Current.Span.Offset - startSpan.Offset),
            startSpan.Line,
            startSpan.Column
        );

        return new SqlCreatePolicyStatement(
            Readers: readers,
            ReadersFrom: readersFrom,
            Role: "SELECT",
            Span: span,
            Target: target
        );
    }
    private SqlCreateRuleStatement? ParseCreateRule(SourceSpan startSpan) {
        var nameTok = ConsumeWord(message: "Expected rule name after CREATE RULE");
        var ruleName = nameTok.Text;

        string firingMode;

        if (MatchKeyword(keyword: "EVERY")) {
            ConsumeKeyword(keyword: "TICK", message: "Expected 'TICK' after 'EVERY'");
            firingMode = "Level";
        } else if (MatchKeyword(keyword: "ON")) {
            ConsumeKeyword(keyword: "ENTER", message: "Expected 'ENTER' after 'ON'");
            firingMode = "Edge";
        } else {
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: "Expected 'EVERY TICK' or 'ON ENTER' firing mode in CREATE RULE",
                span: Current.Span
            );
            firingMode = "Level";
        }

        ConsumeKeyword(keyword: "AS", message: "Expected 'AS' starting rule body");

        var statements = new List<SqlRuleBodyStatement>();

        if (CheckKeyword(keyword: "BEGIN")) {
            var atomicStmt = ParseAtomicStatement();

            if (atomicStmt is not null) {
                statements.Add(item: atomicStmt);
            }
        } else {
            var bodyStmt = ParseRuleBodyStatement();

            if (bodyStmt is not null) {
                statements.Add(item: bodyStmt);
            }
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            startSpan.Offset,
            (Current.Span.Offset - startSpan.Offset),
            startSpan.Line,
            startSpan.Column
        );

        return new SqlCreateRuleStatement(
            FiringMode: firingMode,
            Name: ruleName,
            Span: span,
            Statements: statements
        );
    }

    public SqlRuleBodyStatement? ParseRuleBodyStatement() {
        if (CheckKeyword(keyword: "UPDATE")) {
            return ParseUpdate();
        }
        if (CheckKeyword(keyword: "DELETE")) {
            return ParseDelete();
        }
        if (CheckKeyword(keyword: "INSERT")) {
            return ParseInsert();
        }
        if (CheckKeyword(keyword: "BEGIN")) {
            return ParseAtomicStatement();
        }
        if (CheckKeyword(keyword: "IF")) {
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

        var tableTok = ConsumeWord(message: "Expected table name in UPDATE statement");
        var tableName = tableTok.Text;

        ConsumeKeyword(keyword: "SET", message: "Expected 'SET' in UPDATE statement");

        var assignments = new List<SqlAssignment>();

        while (!Check(kind: SqlTokenKind.Semicolon) && !IsAtEnd && !CheckKeyword(keyword: "WHERE")) {
            var colTok = ConsumeWord(message: "Expected column name in SET clause");
            var colName = colTok.Text;

            Consume(kind: SqlTokenKind.Equal, message: "Expected '=' after column name in SET clause");

            var valExpr = ParseExpression();

            // Detect `col = col + expr` or `col = col - expr`
            var op = SqlAssignmentOp.Assign;

            if (valExpr is SqlBinaryExpression { Operator: "+" or "-" } binExpr) {
                if ((binExpr.Left is SqlColumnRefExpression colRef) &&
                    string.Equals(a: colRef.ColumnName, b: colName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
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
                ((valExpr.Span.Offset + valExpr.Span.Length) - colTok.Span.Offset),
                colTok.Span.Line,
                colTok.Span.Column
            );

            assignments.Add(item: new SqlAssignment(Column: colName, Op: op, Span: assignSpan, Value: valExpr));

            if (!Match(kind: SqlTokenKind.Comma)) {
                break;
            }
        }

        SqlExpression? whereExpr = null;

        if (MatchKeyword(keyword: "WHERE")) {
            whereExpr = ParseExpression();
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            updateTok.Span.Offset,
            (Current.Span.Offset - updateTok.Span.Offset),
            updateTok.Span.Line,
            updateTok.Span.Column
        );

        return new SqlUpdateStatement(Assignments: assignments, Span: span, TableName: tableName, Where: whereExpr);
    }
    private SqlDeleteStatement? ParseDelete() {
        var deleteTok = Advance(); // DELETE

        ConsumeKeyword(keyword: "FROM", message: "Expected 'FROM' in DELETE statement");

        var tableTok = ConsumeWord(message: "Expected table name in DELETE statement");
        var tableName = tableTok.Text;

        SqlExpression? whereExpr = null;

        if (MatchKeyword(keyword: "WHERE")) {
            whereExpr = ParseExpression();
        }

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            deleteTok.Span.Offset,
            (Current.Span.Offset - deleteTok.Span.Offset),
            deleteTok.Span.Line,
            deleteTok.Span.Column
        );

        return new SqlDeleteStatement(Span: span, TableName: tableName, Where: whereExpr);
    }
    private SqlAtomicStatement? ParseAtomicStatement() {
        var beginTok = Advance(); // BEGIN

        ConsumeKeyword(keyword: "ATOMIC", message: "Expected 'ATOMIC' after 'BEGIN'");

        var mainStatements = new List<SqlRuleBodyStatement>();

        while (!CheckKeyword(keyword: "EXCEPTION") && !CheckKeyword(keyword: "END") && !IsAtEnd) {
            var stmt = ParseRuleBodyStatement();

            if (stmt is not null) {
                mainStatements.Add(item: stmt);
            }
            Match(kind: SqlTokenKind.Semicolon);
        }

        List<SqlRuleBodyStatement>? exceptionStatements = null;

        if (MatchKeyword(keyword: "EXCEPTION")) {
            exceptionStatements = new List<SqlRuleBodyStatement>();
            while (!CheckKeyword(keyword: "END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();

                if (stmt is not null) {
                    exceptionStatements.Add(item: stmt);
                }
                Match(kind: SqlTokenKind.Semicolon);
            }
        }

        var endTok = ConsumeKeyword(keyword: "END", message: "Expected 'END' closing BEGIN ATOMIC block");

        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            beginTok.Span.Offset,
            ((endTok.Span.Offset + endTok.Span.Length) - beginTok.Span.Offset),
            beginTok.Span.Line,
            beginTok.Span.Column
        );

        return new SqlAtomicStatement(ExceptionStatements: exceptionStatements, Span: span, Statements: mainStatements);
    }
    private SqlIfStatement? ParseIfStatement() {
        var ifTok = Advance(); // IF

        var condition = ParseExpression();

        ConsumeKeyword(keyword: "THEN", message: "Expected 'THEN' after IF condition");

        var thenStatements = new List<SqlRuleBodyStatement>();

        while (!CheckKeyword(keyword: "ELSIF") && !CheckKeyword(keyword: "ELSE") && !CheckKeyword(keyword: "END") && !IsAtEnd) {
            var stmt = ParseRuleBodyStatement();

            if (stmt is not null) {
                thenStatements.Add(item: stmt);
            }
            Match(kind: SqlTokenKind.Semicolon);
        }

        var elsifStatements = new List<(SqlExpression Condition, IReadOnlyList<SqlRuleBodyStatement> Statements)>();

        while (MatchKeyword(keyword: "ELSIF")) {
            var elsifCond = ParseExpression();

            ConsumeKeyword(keyword: "THEN", message: "Expected 'THEN' after ELSIF condition");
            var stmts = new List<SqlRuleBodyStatement>();

            while (!CheckKeyword(keyword: "ELSIF") && !CheckKeyword(keyword: "ELSE") && !CheckKeyword(keyword: "END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();

                if (stmt is not null) {
                    stmts.Add(item: stmt);
                }
                Match(kind: SqlTokenKind.Semicolon);
            }
            elsifStatements.Add(item: (elsifCond, stmts));
        }

        List<SqlRuleBodyStatement>? elseStatements = null;

        if (MatchKeyword(keyword: "ELSE")) {
            elseStatements = new List<SqlRuleBodyStatement>();
            while (!CheckKeyword(keyword: "END") && !IsAtEnd) {
                var stmt = ParseRuleBodyStatement();

                if (stmt is not null) {
                    elseStatements.Add(item: stmt);
                }
                Match(kind: SqlTokenKind.Semicolon);
            }
        }

        var endTok = ConsumeKeyword(keyword: "END", message: "Expected 'END IF'");

        ConsumeKeyword(keyword: "IF", message: "Expected 'IF' after 'END'");
        Match(kind: SqlTokenKind.Semicolon);

        var span = new SourceSpan(
            ifTok.Span.Offset,
            ((endTok.Span.Offset + endTok.Span.Length) - ifTok.Span.Offset),
            ifTok.Span.Line,
            ifTok.Span.Column
        );

        return new SqlIfStatement(Condition: condition, ElseStatements: elseStatements, ElsifStatements: elsifStatements, Span: span, ThenStatements: thenStatements);
    }

    // SQL's own precedence, deliberately not the expression table's (ExpressionOperators' Binding column): its logic
    // is the keywords OR, AND and NOT, with NOT looser than a comparison; every comparison, `<=>` included, shares
    // one level and does not chain; equality is `=`; and it spells no bitwise operator. Its arithmetic keeps C's
    // order, which is the table's.
    public SqlExpression ParseExpression() => ParseOr();

    private SqlExpression ParseOr() {
        var left = ParseAnd();

        while (MatchKeyword(keyword: "OR")) {
            var right = ParseAnd();
            var span = CombineSpan(a: left.Span, b: right.Span);

            left = new SqlBinaryExpression(Left: left, Operator: "OR", Right: right, Span: span);
        }

        return left;
    }
    private SqlExpression ParseAnd() {
        var left = ParseNot();

        while (MatchKeyword(keyword: "AND")) {
            var right = ParseNot();
            var span = CombineSpan(a: left.Span, b: right.Span);

            left = new SqlBinaryExpression(Left: left, Operator: "AND", Right: right, Span: span);
        }

        return left;
    }
    private SqlExpression ParseNot() {
        if (MatchKeyword(keyword: "NOT")) {
            var opTok = m_tokens[(m_index - 1)];
            var operand = ParseComparison();
            var span = CombineSpan(a: opTok.Span, b: operand.Span);

            return new SqlUnaryExpression(Operand: operand, Operator: "NOT", Span: span);
        }

        return ParseComparison();
    }
    private SqlExpression ParseComparison() {
        var left = ParseAdditive();

        // Check for IS NULL / IS NOT NULL
        if (MatchKeyword(keyword: "IS")) {
            var isSpan = m_tokens[(m_index - 1)].Span;
            var isNot = MatchKeyword(keyword: "NOT");

            ConsumeKeyword(keyword: "NULL", message: "Expected 'NULL' after 'IS'");
            var clause = (isNot ? "IS NOT NULL" : "IS NULL");

            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"'{clause}' is unsupported in Puck state SQL dialect — state facts hold absent cells rather than three-valued NULL logic.",
                span: isSpan
            );
            return left;
        }

        if (Check(kind: SqlTokenKind.Equal) || Check(kind: SqlTokenKind.NotEqual) ||
            Check(kind: SqlTokenKind.Less) || Check(kind: SqlTokenKind.LessOrEqual) ||
            Check(kind: SqlTokenKind.Greater) || Check(kind: SqlTokenKind.GreaterOrEqual) ||
            Check(kind: SqlTokenKind.CosineDistance)) {
            var opTok = Advance();
            var op = opTok.Text;
            var right = ParseAdditive();
            var span = CombineSpan(a: left.Span, b: right.Span);

            return new SqlBinaryExpression(Left: left, Operator: op, Right: right, Span: span);
        }

        return left;
    }
    private SqlExpression ParseAdditive() {
        var left = ParseMultiplicative();

        while (Check(kind: SqlTokenKind.Plus) || Check(kind: SqlTokenKind.Minus)) {
            var opTok = Advance();
            var right = ParseMultiplicative();
            var span = CombineSpan(a: left.Span, b: right.Span);

            left = new SqlBinaryExpression(opTok.Text, left, right, span);
        }

        return left;
    }
    private SqlExpression ParseMultiplicative() {
        var left = ParseUnary();

        while (Check(kind: SqlTokenKind.Multiply) || Check(kind: SqlTokenKind.Divide) || Check(kind: SqlTokenKind.Modulo)) {
            var opTok = Advance();
            var right = ParseUnary();
            var span = CombineSpan(a: left.Span, b: right.Span);

            left = new SqlBinaryExpression(opTok.Text, left, right, span);
        }

        return left;
    }
    private SqlExpression ParseUnary() {
        if (Check(kind: SqlTokenKind.Minus) || Check(kind: SqlTokenKind.Plus)) {
            var opTok = Advance();
            var operand = ParsePrimary();
            var span = CombineSpan(a: opTok.Span, b: operand.Span);

            return new SqlUnaryExpression(opTok.Text, operand, span);
        }

        return ParsePrimary();
    }
    private SqlExpression ParsePrimary() {
        // Literals
        if (Check(kind: SqlTokenKind.NumberLiteral) || Check(kind: SqlTokenKind.StringLiteral) ||
            Check(kind: SqlTokenKind.BooleanLiteral) || Check(kind: SqlTokenKind.NullLiteral)) {
            var tok = Advance();

            return new SqlLiteralExpression(tok.Value, tok.Span);
        }

        // Aggregate functions: COUNT, MIN, MAX, SUM
        if (CheckKeyword(keyword: "COUNT") || CheckKeyword(keyword: "MIN") || CheckKeyword(keyword: "MAX") || CheckKeyword(keyword: "SUM")) {
            var funcTok = Advance();

            Consume(kind: SqlTokenKind.OpenParen, message: $"Expected '(' after '{funcTok.Text}'");
            string? tbl = null;
            var col = ConsumeWord(message: "Expected column name in aggregate").Text;

            if (Match(kind: SqlTokenKind.Dot)) {
                tbl = col;
                col = ConsumeWord(message: "Expected column name after '.' in aggregate").Text;
            }
            var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' after aggregate column");
            var aggSpan = CombineSpan(a: funcTok.Span, b: closeParen.Span);

            // Refuse row aggregate: Puck.State.ExpressionVocabulary has no row aggregates
            m_diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlUnsupportedClause,
                message: $"Row aggregate '{funcTok.Text}(...)' is unsupported in Puck state SQL dialect — state expressions evaluate per-cell scalar operations only.",
                span: aggSpan
            );

            return new SqlAggregateExpression(funcTok.Text, col, tbl, aggSpan);
        }

        // Vector literals: embed('text' [, space: lore]) and vector('base64url')
        if (CheckKeyword(keyword: "embed") && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();

            Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' after 'embed'");
            var textTok = Consume(kind: SqlTokenKind.StringLiteral, message: "Expected string literal in embed(...)");
            string? space = null;

            if (Match(kind: SqlTokenKind.Comma)) {
                ConsumeKeyword(keyword: "space", message: "Expected 'space' named argument in embed(...)");
                if (Match(kind: SqlTokenKind.Colon) || Match(kind: SqlTokenKind.Equal)) {
                    var spaceTok = ConsumeWord(message: "Expected space name in embed(...)");

                    space = spaceTok.Text;
                }
            }
            var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing embed(...)");

            return new SqlVectorLiteralExpression(Kind: "embed", Payload: textTok.Text, Space: space, Span: CombineSpan(a: funcTok.Span, b: closeParen.Span));
        }

        if (CheckKeyword(keyword: "vector") && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();

            Consume(kind: SqlTokenKind.OpenParen, message: "Expected '(' after 'vector'");
            var textTok = Consume(kind: SqlTokenKind.StringLiteral, message: "Expected base64url string literal in vector(...)");
            var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing vector(...)");

            return new SqlVectorLiteralExpression(Kind: "vector", Payload: textTok.Text, Space: null, Span: CombineSpan(a: funcTok.Span, b: closeParen.Span));
        }

        // Vector functions: similarity, dot, identical
        if ((CheckKeyword(keyword: "similarity") || CheckKeyword(keyword: "dot") || CheckKeyword(keyword: "identical")) && (Peek().Kind == SqlTokenKind.OpenParen)) {
            var funcTok = Advance();

            Consume(kind: SqlTokenKind.OpenParen, message: $"Expected '(' after '{funcTok.Text}'");
            var args = new List<SqlExpression>();

            while (!Check(kind: SqlTokenKind.CloseParen) && !IsAtEnd) {
                args.Add(item: ParseExpression());
                if (!Match(kind: SqlTokenKind.Comma)) {
                    break;
                }
            }
            var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: $"Expected ')' after '{funcTok.Text}' arguments");

            return new SqlFunctionCallExpression(funcTok.Text.ToLowerInvariant(), args, CombineSpan(a: funcTok.Span, b: closeParen.Span));
        }

        // Parentheses: grouping or scalar subquery
        if (Match(kind: SqlTokenKind.OpenParen)) {
            var openParen = m_tokens[(m_index - 1)];

            // Subquery: (SELECT c FROM other WHERE key = 'k')
            if (MatchKeyword(keyword: "SELECT")) {
                var colTok = ConsumeWord(message: "Expected column name in scalar subquery");

                ConsumeKeyword(keyword: "FROM", message: "Expected 'FROM' in scalar subquery");
                var fromTok = ConsumeWord(message: "Expected table name in scalar subquery");

                ConsumeKeyword(keyword: "WHERE", message: "Expected 'WHERE' in scalar subquery");
                var keyColTok = ConsumeWord(message: "Expected key column name in scalar subquery WHERE clause");

                Consume(kind: SqlTokenKind.Equal, message: "Expected '=' in scalar subquery WHERE clause");
                var valTok = Advance();
                var keyVal = valTok.Text.Trim('\'', '"');
                var closeParen = Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' closing scalar subquery");
                var subquerySpan = CombineSpan(a: openParen.Span, b: closeParen.Span);

                return new SqlSubqueryExpression(fromTok.Text, colTok.Text, keyColTok.Text, keyVal, subquerySpan);
            }

            var inner = ParseExpression();

            Consume(kind: SqlTokenKind.CloseParen, message: "Expected ')' after parenthesized expression");
            return inner;
        }

        // Column reference: col or table.col or col[key] or table.col[key]
        if (Check(kind: SqlTokenKind.Identifier) || Check(kind: SqlTokenKind.Keyword)) {
            var firstTok = Advance();
            string? tbl = null;
            var col = firstTok.Text;

            if (Match(kind: SqlTokenKind.Dot)) {
                tbl = firstTok.Text;
                var secondTok = ConsumeWord(message: "Expected column name after '.'");

                col = secondTok.Text;
                if (Match(kind: SqlTokenKind.OpenBracket)) {
                    var keyTok = Advance();
                    var closeBracket = Consume(kind: SqlTokenKind.CloseBracket, message: "Expected ']' after cell key");
                    var keyVal = keyTok.Text.Trim('\'', '"');
                    var span = CombineSpan(a: firstTok.Span, b: closeBracket.Span);

                    return new SqlSubqueryExpression(ColumnName: col, KeyColumn: "id", KeyValue: keyVal, Span: span, TableName: tbl);
                }
                return new SqlColumnRefExpression(tbl, col, CombineSpan(a: firstTok.Span, b: secondTok.Span));
            }

            if (Match(kind: SqlTokenKind.OpenBracket)) {
                var keyTok = Advance();
                var closeBracket = Consume(kind: SqlTokenKind.CloseBracket, message: "Expected ']' after cell key");
                var keyVal = keyTok.Text.Trim('\'', '"');
                var span = CombineSpan(a: firstTok.Span, b: closeBracket.Span);

                return new SqlSubqueryExpression(ColumnName: "value", KeyColumn: "id", KeyValue: keyVal, Span: span, TableName: col);
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
        var start = Math.Min(val1: a.Offset, val2: b.Offset);
        var end = Math.Max(val1: (a.Offset + a.Length), val2: (b.Offset + b.Length));

        return new SourceSpan(start, (end - start), a.Line, a.Column);
    }
    private void Synchronize() {
        while (!IsAtEnd) {
            if (Current.Kind == SqlTokenKind.Semicolon) {
                Advance();
                return;
            }

            if (CheckKeyword(keyword: "CREATE") || CheckKeyword(keyword: "INSERT") || CheckKeyword(keyword: "DECLARE") ||
                CheckKeyword(keyword: "UPDATE") || CheckKeyword(keyword: "DELETE") || CheckKeyword(keyword: "BEGIN") || CheckKeyword(keyword: "IF")) {
                return;
            }

            Advance();
        }
    }
}
