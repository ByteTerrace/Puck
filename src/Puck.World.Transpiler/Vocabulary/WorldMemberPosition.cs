namespace Puck.World.Transpiler.Vocabulary;

/// <summary>Where a described member is written relative to its construct's keyword.</summary>
public enum WorldMemberPosition {
    /// <summary>A bare word on the keyword's own line: the declared name, a <c>: Kind</c> annotation, a
    /// <c>Type</c> target, or a reference such as <c>pile</c>'s <c>of tokenRow</c>.</summary>
    Header,
    /// <summary>A <c>name(args)</c> call on the keyword's own line, after the header words:
    /// <c>capacity(8)</c>, <c>advance(perSecond: 1)</c>.</summary>
    Modifier,
    /// <summary>A <c>name: value</c> statement inside the construct's body.</summary>
    Property,
    /// <summary>An entry of a declaration's cell body — <c>key = value</c> with its own modifiers — whose
    /// document keys sit on the cell object rather than on the row.</summary>
    Cell,
    /// <summary>The body itself, standing for every entry in it: a rule's effect statements, a pile's tokens, a
    /// section's nested constructs.</summary>
    Body,
}
