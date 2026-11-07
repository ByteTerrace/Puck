using Puck.Abstractions;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.World.Embeddings.WorldEmbeddingsExtension))]

namespace Puck.World.Embeddings;

/// <summary>Contributes the <c>embedding.fixture</c> embedding provider type. A hosted model provider's type comes from
/// that provider's own extension (<c>embedding.azure-openai</c> from <c>Puck.World.Azure</c>).</summary>
public sealed class WorldEmbeddingsExtension : IPuckExtension {
    /// <inheritdoc />
    public string Name => "Puck.World.Embeddings";

    /// <inheritdoc />
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddEmbedding(provider: new WorldExtensionEmbeddingProviderType(
            Create: FixtureConfiguredEmbeddingProvider.Create,
            Type: "embedding.fixture"
        ));
    }
}
