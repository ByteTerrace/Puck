using System.Text.Json;

namespace Puck.World.Server;

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
    EmbeddingIdentity Identity,
    long Selected,
    long Cached,
    long Submitted,
    long Failed,
    ulong? LastCallTick,
    bool InFlight,
    string? LastFailure
);
