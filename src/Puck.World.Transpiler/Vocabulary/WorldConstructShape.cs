namespace Puck.World.Transpiler.Vocabulary;

/// <summary>The grammatical shape a described construct is written in: how it is spelled, how it is completed, and
/// where its members sit.</summary>
/// <remarks>The shape classifies the source spelling, not the document member. Two constructs lowering to the same
/// member (<c>table</c> and <c>row</c>) carry different shapes, and one shape covers constructs lowering to
/// unrelated members.</remarks>
public enum WorldConstructShape {
    /// <summary>A block naming one document member of its parent: <c>host { }</c>, <c>state { }</c>.</summary>
    Section,
    /// <summary>A block appending one element to a document array: <c>shape Box "a" { }</c>,
    /// <c>placement "id" { }</c>.</summary>
    Row,
    /// <summary>A keyword-led declaration — a name, an optional kind or reference, zero or more
    /// <c>name(args)</c> modifiers, and an optional body: <c>table hp : Int capacity(8) { }</c>.</summary>
    Declaration,
    /// <summary>A keyword-led block whose body is further statements rather than plain properties:
    /// <c>rule "r" { }</c>, <c>decision { }</c>, <c>transaction { }</c>.</summary>
    Block,
    /// <summary>A one-line keyword statement: <c>set s: all</c>, <c>local n : Int = 1</c>,
    /// <c>schedule due in 5s</c>.</summary>
    Statement,
    /// <summary>A block whose body is another language's source text, handed to that language's own parser:
    /// <c>sql { }</c>.</summary>
    EmbeddedLanguage,
}
