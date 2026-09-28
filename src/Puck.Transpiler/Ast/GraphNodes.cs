namespace Puck.Transpiler.Ast;

/// <summary><c>parameter pass.member = value</c> inside a <c>graph</c> block: one bound parameter of the instance,
/// lowering to <c>parameters.&lt;pass&gt;.&lt;member&gt;</c> of the row the block writes.</summary>
/// <param name="Pass">The pass the parameter belongs to.</param>
/// <param name="Member">The pass's config member the value binds.</param>
/// <param name="Value">The bound value: a number, or a <c>state.&lt;row&gt;</c> binding token.</param>
/// <param name="PassQuoted">Whether the pass name was written as a quoted string rather than a bare word.</param>
/// <param name="MemberQuoted">Whether the member name was written as a quoted string rather than a bare word.</param>
/// <param name="Offset">The statement's first character.</param>
/// <param name="Length">The statement's length in characters.</param>
/// <param name="Line">The statement's 1-based line.</param>
/// <param name="Column">The statement's 1-based column.</param>
public sealed record GraphParameterNode(
    string Pass,
    string Member,
    ExpressionNode Value,
    bool PassQuoted = false,
    bool MemberQuoted = false,
    int Offset = 0,
    int Length = 0,
    int Line = 1,
    int Column = 1
) : StatementNode(Offset, Length, Line, Column);
