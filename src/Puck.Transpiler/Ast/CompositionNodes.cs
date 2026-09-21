namespace Puck.Transpiler.Ast;

/// <summary>An asset path retained for package-aware lowering.</summary>
public sealed record AssetExpressionNode(string Path, int Offset = 0, int Length = 0, int Line = 1, int Column = 1)
    : ExpressionNode(Offset, Length, Line, Column);
/// <summary>Instantiates a named world from a module call.</summary>
public sealed record WorldDeclarationNode(
    ExpressionNode Name,
    CallExpressionNode Module,
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
