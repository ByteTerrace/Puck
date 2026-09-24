using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI.Embeddings;

namespace Puck.Embeddings;

/// <summary>Options for configuring an Azure OpenAI embedding generator using identity credentials.</summary>
public sealed class OpenAiEmbeddingOptions {
    /// <summary>The base endpoint URI (e.g. https://account.openai.azure.com/).</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>The model or deployment name.</summary>
    public required string Model { get; init; }
    /// <summary>The embedding dimensions (if supported).</summary>
    public int? Dimensions { get; init; }
    /// <summary>The token credential for identity authentication. Defaults to <see cref="DefaultAzureCredential"/>.</summary>
    public TokenCredential? Credential { get; init; }
    /// <summary>When true, omits the dimensions field from requests.</summary>
    public bool OmitDimensions { get; init; }

    /// <summary>Maximum retries on failure. Defaults to 3.</summary>
    public int MaxRetries { get; init; } = 3;
    /// <summary>HTTP pipeline timeout. Defaults to 60 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(value: 60);

    /// <summary>Optional custom HttpClient for testing or custom transports.</summary>
    public HttpClient? HttpClient { get; init; }
    /// <summary>Optional custom client options for testing.</summary>
    public AzureOpenAIClientOptions? ClientOptions { get; init; }
}
/// <summary>Factory to create configured Azure OpenAI embedding generators using identity authentication.</summary>
public static class OpenAiEmbeddingGeneratorFactory {
    /// <summary>Creates a configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for Azure OpenAI endpoints.</summary>
    /// <param name="options">The generator configuration options.</param>
    /// <returns>A configured embedding generator.</returns>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(OpenAiEmbeddingOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentNullException.ThrowIfNull(argument: options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: options.Model);

        var credential = (options.Credential ?? new DefaultAzureCredential());
        var clientOptions = (options.ClientOptions ?? new AzureOpenAIClientOptions {
            NetworkTimeout = options.Timeout,
            RetryPolicy = new ClientRetryPolicy(maxRetries: Math.Max(val1: 0, val2: options.MaxRetries)),
        });

        if (options.HttpClient is not null) {
            clientOptions.Transport = new HttpClientPipelineTransport(client: options.HttpClient);
        }

        var client = new AzureOpenAIClient(
            endpoint: options.Endpoint,
            credential: credential,
            options: clientOptions
        );

        var dimensions = (options.OmitDimensions ? (int?)null : options.Dimensions);
        var embeddingClient = client.GetEmbeddingClient(deploymentName: options.Model);

        return new AzureOpenAiEmbeddingGenerator(
            client: embeddingClient,
            expectedDimensions: dimensions,
            model: options.Model
        );
    }

    private sealed class AzureOpenAiEmbeddingGenerator(
        EmbeddingClient client,
        int? expectedDimensions,
        string model
    ) : IEmbeddingGenerator<string, Embedding<float>> {
        public EmbeddingGeneratorMetadata Metadata { get; } = new(
            providerName: "Azure.AI.OpenAI",
            defaultModelId: model,
            defaultModelDimensions: expectedDimensions
        );

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        ) {
            ArgumentNullException.ThrowIfNull(argument: values);
            var inputList = ((values as IReadOnlyList<string>) ?? values.ToList());

            var genOptions = new OpenAI.Embeddings.EmbeddingGenerationOptions();

            if (expectedDimensions.HasValue) {
                genOptions.Dimensions = expectedDimensions.Value;
            }

            OpenAIEmbeddingCollection rawCollection;

            try {
                var response = await client.GenerateEmbeddingsAsync(
                    cancellationToken: cancellationToken,
                    inputs: inputList,
                    options: genOptions
                ).ConfigureAwait(continueOnCapturedContext: false);

                rawCollection = response.Value;
            } catch (ClientResultException crex) {
                var body = (crex.GetRawResponse()?.Content?.ToString() ?? "");

                if (body.Length > 512) {
                    body = body[..512];
                }
                body = SanitizeToken(text: body);
                throw new InvalidOperationException(
                    message: $"Azure OpenAI embedding request failed with HTTP {crex.Status}: {body}",
                    innerException: null
                );
            } catch (Exception ex) {
                throw new InvalidOperationException(
                    message: SanitizeToken(text: ex.Message),
                    innerException: null
                );
            }

            var orderedEmbeddings = rawCollection.OrderBy(keySelector: static e => e.Index).ToList();

            if (orderedEmbeddings.Count != inputList.Count) {
                throw new InvalidOperationException(
                    message: $"Answer count mismatch: expected {inputList.Count}, got {orderedEmbeddings.Count}."
                );
            }

            var result = new List<Embedding<float>>(capacity: orderedEmbeddings.Count);

            for (var i = 0; (i < orderedEmbeddings.Count); i++) {
                var item = orderedEmbeddings[i];
                var floats = item.ToFloats();
                var span = floats.Span;

                if (expectedDimensions.HasValue && (span.Length != expectedDimensions.Value)) {
                    throw new InvalidOperationException(
                        message: $"Embedding vector length mismatch: expected {expectedDimensions.Value}, got {span.Length}."
                    );
                }

                for (var j = 0; (j < span.Length); j++) {
                    if (!float.IsFinite(f: span[j])) {
                        throw new InvalidOperationException(
                            message: "Non-finite embedding component encountered."
                        );
                    }
                }

                result.Add(item: new Embedding<float>(vector: floats));
            }

            return new GeneratedEmbeddings<Embedding<float>>(embeddings: result);
        }

        private static string SanitizeToken(string text) {
            if (string.IsNullOrEmpty(value: text)) {
                return text;
            }
            return System.Text.RegularExpressions.Regex.Replace(
                input: text,
                pattern: @"(?i)bearer\s+[A-Za-z0-9_\-\.]+",
                replacement: "Bearer [REDACTED]"
            );
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            ((serviceType == typeof(EmbeddingGeneratorMetadata)) ? Metadata : null);
        public void Dispose() { }
    }
}

