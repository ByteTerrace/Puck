namespace Puck.World.Transpiler.Vocabulary;

/// <summary>What a described member's value is written as. The kind decides the completion placeholder, the hover
/// card's type line, and the literal a generated source writes for the member.</summary>
/// <remarks>This is the authored spelling's kind, not the document field's JSON type — <c>puck schema</c> owns
/// that. <c>Rate</c> and <c>Seconds</c> differ from <c>Number</c> because the author writes a unit.</remarks>
public enum WorldMemberKind {
    /// <summary>A declared name, spelled bare or quoted.</summary>
    Name,
    /// <summary>A reference to another declared name.</summary>
    Reference,
    /// <summary>A <c>Puck.State.CellKind</c> word: <c>Int</c>, <c>Fixed</c>, <c>Bool</c>, <c>Text</c>,
    /// <c>Vector</c>.</summary>
    CellKind,
    /// <summary>A member of a named enumeration; <see cref="WorldConstructMember.Choices"/> lists the words.</summary>
    Enumeration,
    /// <summary>A whole number.</summary>
    Number,
    /// <summary>A decimal the row's kind reads as fixed point.</summary>
    Fixed,
    /// <summary>A quoted string.</summary>
    Text,
    /// <summary>A duration written with a time unit.</summary>
    Seconds,
    /// <summary>A length written with a distance unit.</summary>
    Length,
    /// <summary>A per-second rate written as <c>perSecond: n</c>.</summary>
    Rate,
    /// <summary>A three-element coordinate array.</summary>
    Point,
    /// <summary>A predicate in the <c>when</c> grammar.</summary>
    Gate,
    /// <summary>Operand text handed whole to <c>Puck.State.ExpressionSpelling</c>.</summary>
    Operand,
    /// <summary>A keyword with no value, standing for a fixed node.</summary>
    Flag,
    /// <summary>A body of statements the construct's own grammar admits.</summary>
    Statements,
    /// <summary>A body of <c>key = value</c> cell entries.</summary>
    Cells,
    /// <summary>A body of bare token names.</summary>
    Tokens,
    /// <summary>A value whose shape the document member decides rather than the surface.</summary>
    Value,
    /// <summary>A parenthesized parameter list, each parameter a name and the type word its argument spells;
    /// <see cref="WorldConstructMember.Parameters"/> pairs them and <see cref="WorldConstructMember.Choices"/>
    /// lists the type words the list admits.</summary>
    Parameters,
}
