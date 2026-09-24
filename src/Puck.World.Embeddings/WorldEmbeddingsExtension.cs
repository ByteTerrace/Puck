using Puck.Abstractions;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.World.Embeddings.WorldEmbeddingsExtension))]

namespace Puck.World.Embeddings;

/// <summary>Contributes the <c>embedding.fixture</c> and <c>embedding.azure-openai</c> embedding provider types.</summary>
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
        registry.AddEmbedding(provider: new WorldExtensionEmbeddingProviderType(
            Create: AzureOpenAiConfiguredEmbeddingProvider.Create,
            Type: "embedding.azure-openai"
        ));
    }
}
