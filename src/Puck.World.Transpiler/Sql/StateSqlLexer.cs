using System.Globalization;
using System.Text;
using Puck.State;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Sql;

public enum SqlTokenKind {
    Eof,
    Identifier,
    Keyword,
    StringLiteral,
    NumberLiteral,
    BooleanLiteral,
    NullLiteral,

    // Punctuation & Operators
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Comma,
    Semicolon,
    Colon,
    Dot,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    CosineDistance,
    Plus,
    Minus,
    Multiply,
    Divide,
    Modulo
}
public sealed record SqlToken(
    SqlTokenKind Kind,
    string Text,
    object? Value,
    SourceSpan Span
);
public sealed class StateSqlLexer {
    private readonly string m_source;
    private readonly int m_baseOffset;
    private readonly int m_baseLine;
    private readonly int m_baseColumn;

    private int m_cursor;

    public StateSqlLexer(string source, int baseOffset = 0, int baseLine = 1, int baseColumn = 1) {
        m_source = (source ?? string.Empty);
        m_baseOffset = baseOffset;
        m_baseLine = baseLine;
        m_baseColumn = baseColumn;
        m_cursor = 0;
    }

    public IReadOnlyList<SqlToken> Tokenize(DiagnosticBag diagnostics) {
        var tokens = new List<SqlToken>();

        while (true) {
            SkipWhiteSpaceAndComments();

            if (m_cursor >= m_source.Length) {
                var eofSpan = ((m_source.Length > 0)
                    ? CreateSpan((m_source.Length - 1), 1)
                    : CreateSpan(length: 1, start: 0));

                tokens.Add(item: new SqlToken(Kind: SqlTokenKind.Eof, Span: eofSpan, Text: string.Empty, Value: null));
                break;
            }

            var start = m_cursor;
            var c = m_source[m_cursor];

            // String literal: '...' or "..."
            if (c is '\'' or '"') {
                tokens.Add(item: ReadStringLiteral(diagnostics: diagnostics, quoteChar: c, start: start));
                continue;
            }

            // Numeric literal
            var prevToken = ((tokens.Count > 0) ? tokens[^1] : null);
            var canBeBinaryOp = ((prevToken is not null) && (prevToken.Kind is SqlTokenKind.Identifier or SqlTokenKind.NumberLiteral or SqlTokenKind.StringLiteral or SqlTokenKind.BooleanLiteral or SqlTokenKind.CloseParen));

            if (char.IsAsciiDigit(c: c) || (!canBeBinaryOp && (c is '+' or '-') && ((m_cursor + 1) < m_source.Length) && char.IsAsciiDigit(c: m_source[(m_cursor + 1)]))) {
                tokens.Add(item: ReadNumberLiteral(start: start));
                continue;
            }

            // Operators & Punctuation
            switch (c) {
                case '(':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.OpenParen, "(", null, CreateSpan(length: 1, start: start)));
                    continue;
                case ')':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.CloseParen, ")", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '[':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.OpenBracket, "[", null, CreateSpan(length: 1, start: start)));
                    continue;
                case ']':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.CloseBracket, "]", null, CreateSpan(length: 1, start: start)));
                    continue;
                case ',':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Comma, ",", null, CreateSpan(length: 1, start: start)));
                    continue;
                case ';':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Semicolon, ";", null, CreateSpan(length: 1, start: start)));
                    continue;
                case ':':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Colon, ":", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '.':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Dot, ".", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '+':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Plus, "+", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '-':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Minus, "-", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '*':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Multiply, "*", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '/':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Divide, "/", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '%':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Modulo, "%", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '=':
                    m_cursor++;
                    tokens.Add(item: new SqlToken(SqlTokenKind.Equal, "=", null, CreateSpan(length: 1, start: start)));
                    continue;
                case '<':
                    m_cursor++;
                    if ((m_cursor < m_source.Length) && (m_source[m_cursor] == '>')) {
                        m_cursor++;
                        tokens.Add(item: new SqlToken(SqlTokenKind.NotEqual, "<>", null, CreateSpan(length: 2, start: start)));
                    } else if ((m_cursor < m_source.Length) && (m_source[m_cursor] == '=')) {
                        m_cursor++;
                        if ((m_cursor < m_source.Length) && (m_source[m_cursor] == '>')) {
                            m_cursor++;
                            tokens.Add(item: new SqlToken(SqlTokenKind.CosineDistance, "<=>", null, CreateSpan(length: 3, start: start)));
                        } else {
                            tokens.Add(item: new SqlToken(SqlTokenKind.LessOrEqual, "<=", null, CreateSpan(length: 2, start: start)));
                        }
                    } else {
                        tokens.Add(item: new SqlToken(SqlTokenKind.Less, "<", null, CreateSpan(length: 1, start: start)));
                    }
                    continue;
                case '>':
                    m_cursor++;
                    if ((m_cursor < m_source.Length) && (m_source[m_cursor] == '=')) {
                        m_cursor++;
                        tokens.Add(item: new SqlToken(SqlTokenKind.GreaterOrEqual, ">=", null, CreateSpan(length: 2, start: start)));
                    } else {
                        tokens.Add(item: new SqlToken(SqlTokenKind.Greater, ">", null, CreateSpan(length: 1, start: start)));
                    }
                    continue;
                case '!':
                    if (((m_cursor + 1) < m_source.Length) && (m_source[(m_cursor + 1)] == '=')) {
                        m_cursor += 2;
                        tokens.Add(item: new SqlToken(SqlTokenKind.NotEqual, "!=", null, CreateSpan(length: 2, start: start)));
                        continue;
                    }
                    break;
            }

            // Word: identifier or keyword
            if (IdentifierSpelling.IsStart(character: c) || (c == '@')) {
                tokens.Add(item: ReadWord(start: start));
                continue;
            }

            // Unexpected character
            m_cursor++;
            var errSpan = CreateSpan(length: 1, start: start);

            diagnostics.ReportError(
                code: PuckDiagnosticCodes.SqlSyntaxError,
                message: $"Unexpected character '{c}' in SQL dialect",
                span: errSpan
            );
        }

        return tokens;
    }

    private void SkipWhiteSpaceAndComments() {
        while (m_cursor < m_source.Length) {
            var c = m_source[m_cursor];

            if (char.IsWhiteSpace(c: c)) {
                m_cursor++;
                continue;
            }

            // Line comments: -- or //
            if (((c == '-') && ((m_cursor + 1) < m_source.Length) && (m_source[(m_cursor + 1)] == '-')) ||
                ((c == '/') && ((m_cursor + 1) < m_source.Length) && (m_source[(m_cursor + 1)] == '/'))) {
                m_cursor += 2;
                while ((m_cursor < m_source.Length) && (m_source[m_cursor] is not '\n' and not '\r')) {
                    m_cursor++;
                }
                continue;
            }

            // Block comments: /* ... */
            if ((c == '/') && ((m_cursor + 1) < m_source.Length) && (m_source[(m_cursor + 1)] == '*')) {
                m_cursor += 2;
                while (m_cursor < m_source.Length) {
                    if ((m_source[m_cursor] == '*') && ((m_cursor + 1) < m_source.Length) && (m_source[(m_cursor + 1)] == '/')) {
                        m_cursor += 2;
                        break;
                    }
                    m_cursor++;
                }
                continue;
            }

            break;
        }
    }
    private SqlToken ReadStringLiteral(char quoteChar, int start, DiagnosticBag diagnostics) {
        m_cursor++; // Skip opening quote
        var sb = new StringBuilder();

        while (m_cursor < m_source.Length) {
            var c = m_source[m_cursor];

            if ((c == '\\') && (quoteChar == '"')) {
                m_cursor++;
                if (m_cursor < m_source.Length) {
                    sb.Append(value: m_source[m_cursor]);
                    m_cursor++;
                }
                continue;
            }

            if (c == quoteChar) {
                m_cursor++;
                // Check for '' escape in SQL single quotes
                if ((quoteChar == '\'') && (m_cursor < m_source.Length) && (m_source[m_cursor] == '\'')) {
                    sb.Append(value: '\'');
                    m_cursor++;
                    continue;
                }
                break;
            }

            sb.Append(value: c);
            m_cursor++;
        }

        var text = sb.ToString();
        var len = (m_cursor - start);

        return new SqlToken(SqlTokenKind.StringLiteral, text, text, CreateSpan(length: len, start: start));
    }
    private SqlToken ReadNumberLiteral(int start) {
        if (m_source[m_cursor] is '+' or '-') {
            m_cursor++;
        }

        var isDecimal = false;

        while (m_cursor < m_source.Length) {
            var c = m_source[m_cursor];

            if (char.IsAsciiDigit(c: c)) {
                m_cursor++;
            } else if ((c == '.') && !isDecimal && ((m_cursor + 1) < m_source.Length) && char.IsAsciiDigit(c: m_source[(m_cursor + 1)])) {
                isDecimal = true;
                m_cursor++;
            } else {
                break;
            }
        }

        var text = m_source[start..m_cursor];
        var len = (m_cursor - start);

        if (isDecimal) {
            decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decVal);
            return new SqlToken(SqlTokenKind.NumberLiteral, text, decVal, CreateSpan(length: len, start: start));
        }

        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal);
        return new SqlToken(SqlTokenKind.NumberLiteral, text, longVal, CreateSpan(length: len, start: start));
    }
    private SqlToken ReadWord(int start) {
        while (m_cursor < m_source.Length) {
            var c = m_source[m_cursor];

            if (IdentifierSpelling.IsPart(character: c)) {
                m_cursor++;
            } else {
                break;
            }
        }

        var text = m_source[start..m_cursor];
        var len = (m_cursor - start);
        var span = CreateSpan(length: len, start: start);

        if (string.Equals(a: text, b: "TRUE", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return new SqlToken(Kind: SqlTokenKind.BooleanLiteral, Span: span, Text: text, Value: true);
        }
        if (string.Equals(a: text, b: "FALSE", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return new SqlToken(Kind: SqlTokenKind.BooleanLiteral, Span: span, Text: text, Value: false);
        }
        if (string.Equals(a: text, b: "NULL", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return new SqlToken(Kind: SqlTokenKind.NullLiteral, Span: span, Text: text, Value: null);
        }

        if (IsKeyword(word: text)) {
            return new SqlToken(Kind: SqlTokenKind.Keyword, Span: span, Text: text, Value: null);
        }

        return new SqlToken(Kind: SqlTokenKind.Identifier, Span: span, Text: text, Value: text);
    }

    /// <summary>Whether the dialect reads <paramref name="word"/>, in any case, as something other than an
    /// identifier: a keyword, a boolean literal, or <c>NULL</c>.</summary>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> when the word never reads as an identifier.</returns>
    public static bool IsReservedWord(string word) => (
        IsKeyword(word: word) ||
        (word.ToUpperInvariant() is "TRUE" or "FALSE" or "NULL")
    );

    private static bool IsKeyword(string word) {
        return word.ToUpperInvariant() switch {
            "CREATE" or "TABLE" or "PRIMARY" or "KEY" or "NOT" or "NULL" or "DEFAULT" or
            "CHECK" or "BETWEEN" or "AND" or "OR" or "ON" or "OVERFLOW" or "SATURATE" or
            "ADVANCE" or "PER" or "SECOND" or "DYNAMICS" or "CAPACITY" or "ORDERED" or
            "REFERENCES" or "DECLARE" or "ROW" or "INSERT" or "INTO" or "VALUES" or
            "POLICY" or "FOR" or "SELECT" or "TO" or "READERS" or "FROM" or "RULE" or
            "EVERY" or "TICK" or "ENTER" or "AS" or "UPDATE" or "SET" or "WHERE" or
            "DELETE" or "BEGIN" or "ATOMIC" or "EXCEPTION" or "END" or "IF" or "THEN" or
            "ELSIF" or "ELSE" or "CASE" or "WHEN" or "IS" or "INT" or "INTEGER" or
            "FIXED" or "DECIMAL" or "NUMERIC" or "BOOL" or "BOOLEAN" or "TEXT" or
            "VARCHAR" or "CHAR" or "REAL" or "FLOAT" or "DOUBLE" or "GROUP" or "BY" or
            "HAVING" or "LIMIT" or "WINDOW" or "UNION" or "TRIGGER" or "VIEW" or
            "ALTER" or "DROP" or "COUNT" or "MIN" or "MAX" or "SUM" or
            "JOIN" or "CROSS" or "APPLY" or "ORDER" or "DISTINCT" or "IN" or "EXISTS" or
            "EVICTS" or "VECTOR" or "ASC" or "DESC" => true,
            _ => false
        };
    }
    private SourceSpan CreateSpan(int start, int length) {
        // Compute line and column relative to m_baseOffset, m_baseLine, m_baseColumn
        var line = m_baseLine;
        var col = m_baseColumn;

        for (var i = 0; (i < start); i++) {
            if (m_source[i] == '\n') {
                line++;
                col = 1;
            } else if (m_source[i] != '\r') {
                col++;
            }
        }

        return new SourceSpan(Column: col, Length: length, Line: line, Offset: (m_baseOffset + start));
    }
}
