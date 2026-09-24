using Puck.Transpiler.Lowering;
using Puck.Transpiler.Units;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document vocabulary that dimensions no field, names no positional argument and embeds no language: the
/// stand-in for a schema that is not the world's, so what a world document alone offers can be told apart.</summary>
internal sealed class InertVocabulary : IDocumentVocabulary {
    /// <summary>Gets the one instance.</summary>
    public static InertVocabulary Instance { get; } = new();

    /// <inheritdoc/>
    public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
    /// <inheritdoc/>
    public bool IsEmbeddedLanguage(string identifier) => false;
    /// <inheritdoc/>
    public string? NameCallArgument(string callName, int positionalIndex) => null;
    /// <summary>Returns a resolver that reads <paramref name="schema"/> with this vocabulary and everything else,
    /// including a document that declares no schema, with the world's.</summary>
    /// <param name="schema">The schema this vocabulary answers for.</param>
    /// <returns>The resolver.</returns>
    public static DocumentVocabularyResolver ResolverFor(string schema) => new(
        fallback: WorldDocumentVocabulary.Instance,
        vocabularies: new Dictionary<string, IDocumentVocabulary>(comparer: StringComparer.Ordinal) {
            [schema] = Instance,
            [WorldDocumentVocabulary.Schema] = WorldDocumentVocabulary.Instance,
        }
    );
}
