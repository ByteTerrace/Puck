using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Lowering;

/// <summary>Resolves an <see cref="IDocumentVocabulary"/> for a document source by matching its declared schema.</summary>
public sealed class DocumentVocabularyResolver {
    private readonly IReadOnlyDictionary<string, IDocumentVocabulary> m_vocabularies;
    private readonly IDocumentVocabulary m_fallback;

    /// <summary>Initializes a new instance of the <see cref="DocumentVocabularyResolver"/> class.</summary>
    /// <param name="vocabularies">Vocabularies keyed by schema identifier.</param>
    /// <param name="fallback">Fallback vocabulary when schema is omitted or not matched.</param>
    public DocumentVocabularyResolver(IReadOnlyDictionary<string, IDocumentVocabulary> vocabularies, IDocumentVocabulary fallback) {
        ArgumentNullException.ThrowIfNull(argument: vocabularies);
        ArgumentNullException.ThrowIfNull(argument: fallback);
        m_vocabularies = new Dictionary<string, IDocumentVocabulary>(collection: vocabularies, comparer: StringComparer.Ordinal);
        m_fallback = fallback;
    }

    /// <summary>Resolves the vocabulary for the given document source text.</summary>
    /// <param name="source">The document source text.</param>
    /// <returns>The resolved vocabulary.</returns>
    public IDocumentVocabulary Resolve(string source) {
        if (!string.IsNullOrEmpty(value: source) &&
            PuckParser.TryReadDocumentSchema(source: source, schema: out var schema) &&
            !string.IsNullOrEmpty(value: schema) &&
            m_vocabularies.TryGetValue(key: schema, value: out var vocabulary)) {
            return vocabulary;
        }

        return m_fallback;
    }
}
