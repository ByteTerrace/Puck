using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Ast;

/// <summary>Base class for all Abstract Syntax Tree nodes in the Puck authoring language.</summary>
/// <param name="Offset">The 0-based character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record SyntaxNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1) {
    /// <summary>Gets the source text span of this node.</summary>
    public SourceSpan Span => new(Offset, Length, Line, Column);
}

/// <summary>Represents a top-level authoring document.</summary>
/// <param name="Schema">The schema identifier (e.g., 'puck.world.def.v1').</param>
/// <param name="Basis">The optional base document path inherited by this document.</param>
/// <param name="Statements">The top-level statements composing the document.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record DocumentNode(
    string? Schema,
    string? Basis,
    IReadOnlyList<StatementNode> Statements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(Offset, Length, Line, Column) {
    /// <summary>Gets the span of the <c>basis:</c> header itself, so a composition failure points at the line that
    /// named the basis rather than at the document. Defaults to the whole document when none was authored.</summary>
    public SourceSpan BasisSpan { get; init; }
}

/// <summary>Base class for all statement nodes.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record StatementNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(Offset, Length, Line, Column);

/// <summary>An expression used as a statement: e.g., a template invocation or call expression.</summary>
/// <param name="Expression">The evaluated expression.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ExpressionStatementNode(
    ExpressionNode Expression,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A module import declaration: <c>import "path" [as alias]</c>.</summary>
/// <param name="Path">The relative path to the imported fragment document.</param>
/// <param name="Alias">The optional alias prefix for declared symbols.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ImportNode(
    string Path,
    string? Alias,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>An export declaration exposing named symbols under a specific facet.</summary>
/// <param name="Facet">The export facet ('read', 'action', or 'binding').</param>
/// <param name="Names">The declared export names.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ExportNode(
    string Facet,
    IReadOnlyList<string> Names,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A compile-time variable declaration: <c>let name = expression</c>.</summary>
/// <param name="Name">The identifier name.</param>
/// <param name="Value">The initializer expression.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record LetNode(
    string Name,
    ExpressionNode Value,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A compile-time parametric template: <c>template name(p1 = v1, ...) { ... }</c>.</summary>
/// <param name="Name">The template identifier.</param>
/// <param name="Parameters">The formal parameters with optional default expressions.</param>
/// <param name="Body">The block containing the template definition.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TemplateNode(
    string Name,
    IReadOnlyList<TemplateParameterNode> Parameters,
    BlockNode Body,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>One formal parameter of a template declaration.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="DefaultValue">The optional default expression when omitted by caller.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record TemplateParameterNode(
    string Name,
    ExpressionNode? DefaultValue,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(Offset, Length, Line, Column);

/// <summary>A structural block statement: <c>identifier ["name"] { ... }</c>.</summary>
/// <param name="Identifier">The block section identifier (e.g., 'host', 'views', 'layout', 'seatRig').</param>
/// <param name="Name">An optional name identifying the instance (e.g. 'overview', 'moth-study').</param>
/// <param name="Target">An optional second identifier (e.g., 'solid Prism').</param>
/// <param name="Statements">The statements and properties enclosed in the block.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record BlockNode(
    string Identifier,
    string? Name,
    string? Target,
    IReadOnlyList<StatementNode> Statements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A key-value property assignment: <c>name: expression</c> or <c>name = expression</c>.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Value">The assigned expression value.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record PropertyNode(
    string Name,
    ExpressionNode Value,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>Base class for all expression nodes.</summary>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public abstract record ExpressionNode(int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : SyntaxNode(Offset, Length, Line, Column);

/// <summary>A literal scalar value (string, integer, float, boolean, or null) with optional unit suffix.</summary>
/// <param name="Value">The parsed raw value object.</param>
/// <param name="Unit">The optional unit symbol (e.g. 's', 'hz', 'deg').</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record LiteralExpressionNode(
    object? Value,
    string? Unit = null,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>A hex color literal: <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
/// <param name="Hex">The raw hex string including '#'.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ColorExpressionNode(
    string Hex,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>A bare identifier reference: <c>foo</c>.</summary>
/// <param name="Name">The identifier name.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record IdentifierExpressionNode(
    string Name,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>A member access expression: <c>target.member</c>.</summary>
/// <param name="Target">The object or namespace expression.</param>
/// <param name="Member">The member identifier.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record MemberAccessExpressionNode(
    ExpressionNode Target,
    string Member,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>A function or operation invocation: <c>name(arg1, name2: arg2)</c>.</summary>
/// <param name="Name">The function or operation name.</param>
/// <param name="Arguments">The positional and named argument expressions.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record CallExpressionNode(
    string Name,
    IReadOnlyList<ArgumentNode> Arguments,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>An argument to a call expression, optionally named.</summary>
/// <param name="Name">The parameter name when explicitly named.</param>
/// <param name="Value">The argument expression.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ArgumentNode(
    string? Name,
    ExpressionNode Value,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : SyntaxNode(Offset, Length, Line, Column);

/// <summary>A binary operator expression: <c>left op right</c>.</summary>
/// <param name="Left">The left-hand expression.</param>
/// <param name="Operator">The operator token ('+', '-', '*', '/', '==', '!=', '&gt;=', etc.).</param>
/// <param name="Right">The right-hand expression.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record BinaryExpressionNode(
    ExpressionNode Left,
    string Operator,
    ExpressionNode Right,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>An array or vector expression: <c>[elem1, elem2, ...]</c>.</summary>
/// <param name="Elements">The enclosed expressions.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ArrayExpressionNode(
    IReadOnlyList<ExpressionNode> Elements,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>A range expression: <c>start..end</c>.</summary>
/// <param name="Start">The start bound expression.</param>
/// <param name="End">The end bound expression.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record RangeExpressionNode(
    ExpressionNode Start,
    ExpressionNode End,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>An anonymous inline object: <c>{ key: value, ... }</c>.</summary>
/// <param name="Properties">The properties declared within the object.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ObjectExpressionNode(
    IReadOnlyList<PropertyNode> Properties,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : ExpressionNode(Offset, Length, Line, Column);

/// <summary>An addon capability request statement: <c>request Capability "subject"</c>.</summary>
/// <param name="Capability">The requested capability name (e.g. Mutate, Observe, Emit, Control).</param>
/// <param name="Subject">The subject string (e.g. "section:state", "screen:0", "channel:events").</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record AddonRequestNode(
    string Capability,
    string Subject,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>An addon machine-memory watch declaration: <c>watchMemory screen: 0, address: 0x02000000, length: 4</c>.</summary>
/// <param name="Screen">The screen-surface index hosting the watched machine.</param>
/// <param name="Address">The bus address to watch.</param>
/// <param name="WatchLength">The byte-range length (1..8).</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record AddonMemoryWatchNode(
    int Screen,
    long Address,
    int WatchLength,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

/// <summary>A synthetic statement emitted during parser error recovery representing a failed statement span.</summary>
/// <param name="Message">The error message describing the failure.</param>
/// <param name="Offset">The character offset within the source text.</param>
/// <param name="Length">The character length of the node span.</param>
/// <param name="Line">The 1-based line number in source text.</param>
/// <param name="Column">The 1-based column number in source text.</param>
public sealed record ErrorStatementNode(
    string Message,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);

