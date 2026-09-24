namespace Puck.State;

/// <summary>
/// Identifies an embedding space by the model that produces its vectors, that model's revision, and the component
/// count every vector in the space carries. It is the one identity a world's declared <see cref="StateSpace"/>, an
/// embedding lock, an embedding provider, and a host's embedding connection all compare, and two identities are the
/// same space exactly when they are equal: ordinal on both strings and equal on dimensions.
/// </summary>
/// <remarks>The field descriptions state the limits as numbers because a world document's schema shows them as its
/// <c>model</c>, <c>revision</c> and <c>dimensions</c> hover text (<see cref="StateSpace.ExtendJson"/>); the limits
/// themselves are <see cref="MaxModelLength"/>, <see cref="MaxRevisionLength"/>,
/// <see cref="StateCapacity.MinVectorDimensions"/> and <see cref="StateCapacity.MaxVectorDimensions"/>.</remarks>
/// <param name="Model">The model name, non-empty and at most 128 characters.</param>
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

    /// <summary>Gets whether every field lies within its limit.</summary>
    public bool IsValid => (IsModelValid(model: Model) && IsRevisionValid(revision: Revision) && IsDimensionsValid(dimensions: Dimensions));

    /// <summary>Adds one refusal for each field outside its limit, naming the field as <c>{path}.model</c>,
    /// <c>{path}.revision</c> or <c>{path}.dimensions</c>.</summary>
    /// <param name="path">The document path of the declaration that carries this identity.</param>
    /// <param name="refusals">The collection each refusal is added to.</param>
    /// <returns><see langword="true"/> when no refusal was added; otherwise <see langword="false"/>.</returns>
    public bool TryValidate(string path, ICollection<string> refusals) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: refusals);

        var valid = true;

        if (!IsModelValid(model: Model)) {
            refusals.Add(item: $"{path}.model must be non-empty and at most {MaxModelLength} characters.");
            valid = false;
        }

        if (!IsRevisionValid(revision: Revision)) {
            refusals.Add(item: $"{path}.revision must be non-empty and at most {MaxRevisionLength} characters.");
            valid = false;
        }

        if (!IsDimensionsValid(dimensions: Dimensions)) {
            refusals.Add(item: $"{path}.dimensions {Dimensions} must be between {StateCapacity.MinVectorDimensions} and {StateCapacity.MaxVectorDimensions}.");
            valid = false;
        }

        return valid;
    }

    private static bool IsModelValid(string? model) => (!string.IsNullOrWhiteSpace(value: model) && (model.Length <= MaxModelLength));
    private static bool IsRevisionValid(string? revision) => (!string.IsNullOrWhiteSpace(value: revision) && (revision.Length <= MaxRevisionLength));
    private static bool IsDimensionsValid(int dimensions) => (dimensions is >= StateCapacity.MinVectorDimensions and <= StateCapacity.MaxVectorDimensions);
}
