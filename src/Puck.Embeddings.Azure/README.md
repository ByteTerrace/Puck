# Puck.Embeddings.Azure

The Azure OpenAI embedding provider for [Puck.Embeddings](../Puck.Embeddings/README.md):
`AzureOpenAiEmbeddingGeneratorFactory` builds an
`IEmbeddingGenerator<string, Embedding<float>>` over an Azure OpenAI deployment,
authenticated by an Azure `TokenCredential` (`DefaultAzureCredential` when none
is supplied), and refuses a non-finite component or a length or count mismatch.
World hosts reach it through `Puck.World.Azure`'s `embedding.azure-openai`
provider type; `puck embed` references it as a tool.

## Documentation

📚 [Engine overview](https://github.com/ByteTerrace/Puck/blob/main/docs/overview.md) · 🛠️ [Contributing to Puck](https://github.com/ByteTerrace/Puck/blob/main/docs/development/contributing.md)
