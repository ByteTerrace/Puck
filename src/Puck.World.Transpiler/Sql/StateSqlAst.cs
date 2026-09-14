using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Sql;

/// <summary>Base node for all SQL dialect AST elements.</summary>
public abstract record SqlSyntaxNode(SourceSpan Span);

/// <summary>Base node for all top-level SQL statements inside a <c>sql { ... }</c> block.</summary>
public abstract record SqlStatement(SourceSpan Span) : SqlSyntaxNode(Span);

/// <summary><c>CREATE TABLE tableName (...) [CAPACITY n] [EVICTS];</c></summary>
public sealed record SqlCreateTableStatement(
    string TableName,
    IReadOnlyList<SqlColumnDefinition> Columns,
    int? Capacity,
    bool IsOrdered,
    SourceSpan Span,
    bool IsEvicts = false
) : SqlStatement(Span);

/// <summary>One column definition inside a <c>CREATE TABLE</c> statement.</summary>
public sealed record SqlColumnDefinition(
    string Name,
    string DeclaredType,
    bool IsPrimaryKey,
    bool IsNotNull,
    string? ExplicitRowName,
    object? DefaultValue,
    SqlCheckConstraint? Check,
    SqlAdvanceClause? Advance,
    string? DynamicsName,
    string? OverflowPolicy,
    string? ReferencesTable,
    bool IsOrdered,
    SourceSpan Span,
    SourceSpan TypeSpan,
    string? Space = null
) : SqlSyntaxNode(Span);

/// <summary>Kind of check constraint admitted by Puck state.</summary>
public enum SqlCheckKind {
    Between,
    GreaterOrEqual,
    LessOrEqual,
    Greater,
    Less,
    Range
}

/// <summary><c>CHECK (col BETWEEN min AND max)</c> or comparison constraint.</summary>
public sealed record SqlCheckConstraint(
    string ColumnName,
    SqlCheckKind Kind,
    decimal? Min,
    decimal? Max,
    SourceSpan Span
) : SqlSyntaxNode(Span);

/// <summary><c>ADVANCE rate PER SECOND</c></summary>
public sealed record SqlAdvanceClause(
    decimal Rate,
    string Unit,
    SourceSpan Span
) : SqlSyntaxNode(Span);

/// <summary><c>INSERT INTO table (cols) VALUES (vals), ...;</c></summary>
public sealed record SqlInsertStatement(
    string TableName,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyList<SqlExpression>> ValuesRows,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary><c>INSERT INTO target (cols) SELECT ... FROM source [WHERE ...] ORDER BY ... [ASC|DESC] LIMIT n;</c></summary>
public sealed record SqlInsertSelectStatement(
    string TargetTable,
    IReadOnlyList<string>? TargetColumns,
    IReadOnlyList<SqlExpression> SelectList,
    string FromTable,
    SqlExpression? Where,
    SqlExpression OrderBy,
    bool IsDescending,
    int Limit,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary><c>DECLARE name TYPE [DEFAULT v] [CHECK ...] [ADVANCE ...];</c></summary>
public sealed record SqlDeclareSlotStatement(
    string Name,
    string DeclaredType,
    SqlExpression? DefaultValue,
    SqlCheckConstraint? Check,
    SqlAdvanceClause? Advance,
    SourceSpan Span,
    SourceSpan TypeSpan,
    string? Space = null
) : SqlStatement(Span);

/// <summary><c>CREATE POLICY ON target FOR SELECT TO readers | TO READERS FROM readersFrom;</c></summary>
public sealed record SqlCreatePolicyStatement(
    string Target,
    string Role,
    IReadOnlyList<string>? Readers,
    string? ReadersFrom,
    SourceSpan Span
) : SqlStatement(Span);

/// <summary><c>CREATE RULE name EVERY TICK | ON ENTER AS ...;</c></summary>
public sealed record SqlCreateRuleStatement(
    string Name,
    string FiringMode,
    IReadOnlyList<SqlRuleBodyStatement> Statements,
    SourceSpan Span
) : SqlStatement(Span);

/// <summary>Base for statements inside a rule body.</summary>
public abstract record SqlRuleBodyStatement(SourceSpan Span) : SqlStatement(Span);

/// <summary>Assignment operation in an <c>UPDATE</c> statement.</summary>
public enum SqlAssignmentOp {
    Assign,
    Add
}

/// <summary>One assignment inside an <c>UPDATE ... SET</c> clause.</summary>
public sealed record SqlAssignment(
    string Column,
    SqlAssignmentOp Op,
    SqlExpression Value,
    SourceSpan Span
) : SqlSyntaxNode(Span);

/// <summary><c>UPDATE table SET col = expr, ... [WHERE predicate];</c></summary>
public sealed record SqlUpdateStatement(
    string TableName,
    IReadOnlyList<SqlAssignment> Assignments,
    SqlExpression? Where,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary><c>DELETE FROM table WHERE key = 'k';</c></summary>
public sealed record SqlDeleteStatement(
    string TableName,
    SqlExpression? Where,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary><c>BEGIN ATOMIC ... [EXCEPTION ...] END;</c></summary>
public sealed record SqlAtomicStatement(
    IReadOnlyList<SqlRuleBodyStatement> Statements,
    IReadOnlyList<SqlRuleBodyStatement>? ExceptionStatements,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary><c>IF cond THEN ... [ELSIF cond THEN ...] [ELSE ...] END IF;</c></summary>
public sealed record SqlIfStatement(
    SqlExpression Condition,
    IReadOnlyList<SqlRuleBodyStatement> ThenStatements,
    IReadOnlyList<(SqlExpression Condition, IReadOnlyList<SqlRuleBodyStatement> Statements)> ElsifStatements,
    IReadOnlyList<SqlRuleBodyStatement>? ElseStatements,
    SourceSpan Span
) : SqlRuleBodyStatement(Span);

/// <summary>Base for all SQL expressions inside predicates, assignments, and values.</summary>
public abstract record SqlExpression(SourceSpan Span) : SqlSyntaxNode(Span);

/// <summary>Literal expression (integer, decimal, string, boolean, or null).</summary>
public sealed record SqlLiteralExpression(
    object? Value,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Column reference: <c>col</c> or <c>table.col</c>.</summary>
public sealed record SqlColumnRefExpression(
    string? TableName,
    string ColumnName,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Unary expression: <c>NOT expr</c> or <c>-expr</c>.</summary>
public sealed record SqlUnaryExpression(
    string Operator,
    SqlExpression Operand,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Binary expression: comparisons, boolean logic, and arithmetic.</summary>
public sealed record SqlBinaryExpression(
    string Operator,
    SqlExpression Left,
    SqlExpression Right,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Scalar subquery: <c>(SELECT col FROM table WHERE keyCol = 'keyVal')</c>.</summary>
public sealed record SqlSubqueryExpression(
    string TableName,
    string ColumnName,
    string KeyColumn,
    string KeyValue,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Row aggregate expression: <c>COUNT(col)</c>, <c>MIN(col)</c>, etc.</summary>
public sealed record SqlAggregateExpression(
    string Function,
    string ColumnName,
    string? TableName,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Vector literal: <c>vector('base64url')</c> or <c>embed('text' [, space: lore])</c>.</summary>
public sealed record SqlVectorLiteralExpression(
    string Kind,
    string Payload,
    string? Space,
    SourceSpan Span
) : SqlExpression(Span);

/// <summary>Function call expression: <c>cosine(a, b)</c>, <c>dot(a, b)</c>, etc.</summary>
public sealed record SqlFunctionCallExpression(
    string FunctionName,
    IReadOnlyList<SqlExpression> Arguments,
    SourceSpan Span
) : SqlExpression(Span);
