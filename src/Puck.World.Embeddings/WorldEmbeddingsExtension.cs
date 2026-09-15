using Puck.Abstractions;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.World.Embeddings.WorldEmbeddingsExtension))]

namespace Puck.World.Embeddings;

/// <summary>First-class world extension registering embedding.fixture and embedding.azure-openai provider types.</summary>
public sealed class WorldEmbeddingsExtension : IWorldExtension {
    /// <inheritdoc />
    public string Name => "Embeddings";

    /// <inheritdoc />
    public void Register(IWorldExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterEmbedding(provider: new WorldExtensionEmbeddingProviderType(
            Type: "embedding.fixture",
            Create: FixtureConfiguredEmbeddingProvider.Create
        ));

        registry.RegisterEmbedding(provider: new WorldExtensionEmbeddingProviderType(
            Type: "embedding.azure-openai",
            Create: AzureOpenAiConfiguredEmbeddingProvider.Create
        ));
    }
}
