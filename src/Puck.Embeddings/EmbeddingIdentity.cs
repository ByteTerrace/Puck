namespace Puck.Embeddings;

/// <summary>Identifies an embedding space by its model, revision, and dimensions.</summary>
/// <param name="Model">The model identifier, non-empty and at most 128 characters.</param>
/// <param name="Revision">The model revision, non-empty and at most 128 characters.</param>
/// <param name="Dimensions">The component dimension count, in [8, 1024].</param>
public readonly record struct EmbeddingIdentity(
    string Model,
    string Revision,
    int Dimensions
) {
    /// <summary>The maximum character length of a model identifier.</summary>
    public const int MaxModelLength = 128;
    /// <summary>The maximum character length of a revision identifier.</summary>
    public const int MaxRevisionLength = 128;
    /// <summary>The minimum supported dimension count.</summary>
    public const int MinDimensions = 8;
    /// <summary>The maximum supported dimension count.</summary>
    public const int MaxDimensions = 1024;

    /// <summary>Validates the embedding identity fields.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(value: Model) &&
        (Model.Length <= MaxModelLength) &&
        !string.IsNullOrWhiteSpace(value: Revision) &&
        (Revision.Length <= MaxRevisionLength) &&
        (Dimensions is >= MinDimensions and <= MaxDimensions);

    /// <summary>Checks whether two identities share model, revision, and dimensions.</summary>
    public bool HasSameIdentity(EmbeddingIdentity other) =>
        (Dimensions == other.Dimensions) &&
        string.Equals(a: Model, b: other.Model, comparisonType: StringComparison.Ordinal) &&
        string.Equals(a: Revision, b: other.Revision, comparisonType: StringComparison.Ordinal);
}
