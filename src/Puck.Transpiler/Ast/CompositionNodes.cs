namespace Puck.Transpiler.Ast;

/// <summary>An asset path retained for package-aware lowering.</summary>
public sealed record AssetExpressionNode(string Path, int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : ExpressionNode(Offset, Length, Line, Column);
/// <summary>Instantiates a named world from a module call.</summary>
/// <param name="Name">The world's name.</param>
/// <param name="Module">The module call the world expands.</param>
/// <param name="Entry">Whether the declaration is written <c>entry world</c>: the world a composition boots.</param>
/// <param name="Offset">The declaration's first character.</param>
/// <param name="Length">The declaration's length in characters.</param>
/// <param name="Line">The declaration's 1-based line.</param>
/// <param name="Column">The declaration's 1-based column.</param>
public sealed record WorldDeclarationNode(
    ExpressionNode Name,
    CallExpressionNode Module,
    bool Entry = false,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);
/// <summary>Connects two world endpoints with a border or door.</summary>
public sealed record WorldLinkNode(
    string Kind,
    ExpressionNode Left,
    ExpressionNode Right,
    IReadOnlyList<StatementNode> Properties,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);
