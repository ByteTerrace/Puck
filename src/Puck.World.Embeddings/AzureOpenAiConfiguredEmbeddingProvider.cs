using System.Text.Json;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Puck.Embeddings;
using Puck.State;
using Puck.World.Server;

namespace Puck.World.Embeddings;

/// <summary>Configured Azure OpenAI embedding provider authenticating strictly via passwordless TokenCredential (DefaultAzureCredential).</summary>
public sealed class AzureOpenAiConfiguredEmbeddingProvider : IWorldConfiguredEmbeddingProvider {
    private readonly OpenAiEmbeddingOptions m_options;
    private readonly EmbeddingIdentity m_identity;

    /// <summary>Initializes a new instance of <see cref="AzureOpenAiConfiguredEmbeddingProvider"/>.</summary>
    /// <param name="options">Options for Azure OpenAI.</param>
    /// <param name="identity">Embedding space identity.</param>
    public AzureOpenAiConfiguredEmbeddingProvider(OpenAiEmbeddingOptions options, EmbeddingIdentity identity) {
        m_options = options;
        m_identity = identity;
    }

    /// <summary>Constructs an Azure OpenAI embedding provider from JSON settings.</summary>
    /// <param name="settings">Configuration settings.</param>
    /// <returns>A configured embedding provider.</returns>
    public static IWorldConfiguredEmbeddingProvider Create(JsonElement settings) {
        if (settings.ValueKind != JsonValueKind.Object) {
            throw new ArgumentException(message: "Azure OpenAI settings must be a JSON object.");
        }

        if (
            !settings.TryGetProperty(propertyName: "endpoint", value: out var endpointEl) ||
            string.IsNullOrWhiteSpace(value: endpointEl.GetString()) ||
            !Uri.TryCreate(uriString: endpointEl.GetString(), uriKind: UriKind.Absolute, result: out var endpoint)
        ) {
            throw new ArgumentException(message: "Azure OpenAI settings require a valid absolute 'endpoint' URI.");
        }

        var deployment = (settings.TryGetProperty(propertyName: "deployment", value: out var depEl) ? depEl.GetString() : null);
        var model = (settings.TryGetProperty(propertyName: "model", value: out var modelEl) ? modelEl.GetString() : deployment);

        if (string.IsNullOrWhiteSpace(value: model)) {
            throw new ArgumentException(message: "Azure OpenAI settings require 'model' or 'deployment'.");
        }

        var revision = (settings.TryGetProperty(propertyName: "revision", value: out var revEl) ? revEl.GetString() : "1");

        if (string.IsNullOrWhiteSpace(value: revision)) {
            throw new ArgumentException(message: "Revision must not be empty.");
        }

        if (
            !settings.TryGetProperty(propertyName: "dimensions", value: out var dimsEl) ||
            !dimsEl.TryGetInt32(value: out var dimensions) ||
            (dimensions is < StateCapacity.MinVectorDimensions or > StateCapacity.MaxVectorDimensions)
        ) {
            throw new ArgumentException(message: $"Dimensions must be an integer in [{StateCapacity.MinVectorDimensions}, {StateCapacity.MaxVectorDimensions}].");
        }

        var omitDimensions = (settings.TryGetProperty(propertyName: "omitDimensions", value: out var omitEl) && omitEl.GetBoolean());

        var options = new OpenAiEmbeddingOptions {
            Endpoint = endpoint,
            Model = (deployment ?? model),
            Dimensions = dimensions,
            OmitDimensions = omitDimensions,
            Credential = new DefaultAzureCredential(), // Strict passwordless authentication only
        };

        var identity = new EmbeddingIdentity(Dimensions: dimensions, Model: model, Revision: revision);

        return new AzureOpenAiConfiguredEmbeddingProvider(identity: identity, options: options);
    }
    /// <inheritdoc />
    public IWorldEmbeddingSource BindEmbedding(JsonElement settings) {
        var generator = OpenAiEmbeddingGeneratorFactory.Create(options: m_options);

        return new AzureOpenAiEmbeddingSource(generator: generator, identity: m_identity);
    }
    /// <inheritdoc />
    public void Dispose() { }

    private sealed class AzureOpenAiEmbeddingSource(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingIdentity identity
    ) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => identity;

        public async Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) {
            ArgumentNullException.ThrowIfNull(argument: texts);
            if (texts.Count == 0) { return []; }

            GeneratedEmbeddings<Embedding<float>> response;

            try {
                response = await generator.GenerateAsync(values: texts, cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            } catch (Exception ex) {
                var message = ex.Message;

                if (message.Length > 512) { message = message[..512]; }
                return texts.Select(selector: _ => new EmbeddingAnswer(Refusal: message, Vector: null)).ToArray();
            }

            if (response.Count != texts.Count) {
                var error = $"Answer count mismatch: expected {texts.Count}, received {response.Count}.";

                return texts.Select(selector: _ => new EmbeddingAnswer(Refusal: error, Vector: null)).ToArray();
            }

            var answers = new List<EmbeddingAnswer>(capacity: texts.Count);
            var buffer = new sbyte[identity.Dimensions];

            for (var i = 0; (i < response.Count); i++) {
                var emb = response[i];
                var span = emb.Vector.Span;

                if (span.Length != identity.Dimensions) {
                    answers.Add(item: new EmbeddingAnswer(Vector: null, Refusal: $"Vector length mismatch: expected {identity.Dimensions}, got {span.Length}."));
                    continue;
                }

                var hasNonFinite = false;

                for (var j = 0; (j < span.Length); j++) {
                    if (!float.IsFinite(f: span[j])) {
                        hasNonFinite = true;
                        break;
                    }
                }
                if (hasNonFinite) {
                    answers.Add(item: new EmbeddingAnswer(Refusal: "Non-finite vector component encountered.", Vector: null));
                    continue;
                }

                string? reason = null;

                if (!VectorQuantizer.TryQuantizeUnit(destination: buffer, source: span) || !StateVector.TryCreate(components: buffer, error: out reason, vector: out var stateVector)) {
                    answers.Add(item: new EmbeddingAnswer(Refusal: (reason ?? "Vector quantization failed."), Vector: null));
                    continue;
                }

                answers.Add(item: new EmbeddingAnswer(Refusal: null, Vector: stateVector));
            }

            return answers;
        }
        public void Dispose() {
            generator.Dispose();
        }
    }
}
