using System.Text.Json;

namespace Puck.World.Server;

/// <summary>Identifies an embedding space by its model, revision, and dimensions.</summary>
/// <param name="Model">The model identifier.</param>
/// <param name="Revision">The model revision.</param>
/// <param name="Dimensions">The component dimension count.</param>
public readonly record struct EmbeddingIdentity(string Model, string Revision, int Dimensions) {
    /// <summary>Checks whether this identity matches the declared space's identity.</summary>
    public bool HasSameIdentity(StateSpace space) {
        ArgumentNullException.ThrowIfNull(argument: space);
        return (Dimensions == space.Dimensions) &&
            string.Equals(a: Model, b: space.Model, comparisonType: StringComparison.Ordinal) &&
            string.Equals(a: Revision, b: space.Revision, comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Checks whether two identities share model, revision, and dimensions.</summary>
    public bool HasSameIdentity(EmbeddingIdentity other) =>
        (Dimensions == other.Dimensions) &&
        string.Equals(a: Model, b: other.Model, comparisonType: StringComparison.Ordinal) &&
        string.Equals(a: Revision, b: other.Revision, comparisonType: StringComparison.Ordinal);
}

/// <summary>One text embedding result: an admitted StateVector on success, or a bounded refusal message on failure.</summary>
/// <param name="Vector">The normalized StateVector, or null on refusal.</param>
/// <param name="Refusal">The bounded refusal message, or null on success.</param>
public readonly record struct EmbeddingAnswer(StateVector? Vector, string? Refusal);

/// <summary>An active, host-bound embedding generator producing StateVector values outside the simulation tick.</summary>
public interface IWorldEmbeddingSource : IDisposable {
    /// <summary>Gets this source's embedding identity.</summary>
    EmbeddingIdentity Identity { get; }

    /// <summary>Embeds a batch of texts asynchronously.</summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One EmbeddingAnswer per text.</returns>
    Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <summary>An explicitly installed provider's configurable embedding source factory.</summary>
public interface IWorldConfiguredEmbeddingProvider : IDisposable {
    /// <summary>Binds and returns an owned embedding source.</summary>
    /// <param name="settings">Provider-specific connection settings.</param>
    /// <returns>The bound embedding source.</returns>
    IWorldEmbeddingSource BindEmbedding(JsonElement settings);
}

/// <summary>An installed embedding provider type registration.</summary>
/// <param name="Type">The catalog key (e.g. embedding.fixture, embedding.azure-openai).</param>
/// <param name="Create">Factory constructing the configured provider from configuration settings.</param>
public sealed record WorldExtensionEmbeddingProviderType(string Type, Func<JsonElement, IWorldConfiguredEmbeddingProvider> Create);

/// <summary>Diagnostic snapshot of an active embedding connection for world.extensions read-back.</summary>
public sealed record WorldExtensionEmbeddingStatus(
    string Name,
    string Space,
    string Model,
    string Revision,
    int Dimensions,
    long Selected,
    long Cached,
    long Submitted,
    long Failed,
    ulong? LastCallTick,
    bool InFlight,
    string? LastFailure
);
