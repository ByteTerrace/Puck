using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Puck.Embeddings;

/// <summary>Options for configuring an OpenAI-compatible embedding generator.</summary>
public sealed class OpenAiEmbeddingOptions {
    /// <summary>The base endpoint URI (e.g. https://api.openai.com/v1).</summary>
    public required Uri Endpoint { get; init; }
    /// <summary>The model name.</summary>
    public required string Model { get; init; }
    /// <summary>The embedding dimensions (if supported).</summary>
    public int? Dimensions { get; init; }
    /// <summary>The raw API key string.</summary>
    public string? ApiKey { get; init; }
    /// <summary>Environment variable name containing the API key.</summary>
    public string? ApiKeyEnvironment { get; init; }
    /// <summary>HTTP header name for the API key (defaults to "Authorization").</summary>
    public string ApiKeyHeader { get; init; } = "Authorization";
    /// <summary>When true, omits the dimensions field from requests.</summary>
    public bool OmitDimensions { get; init; }
    /// <summary>HTTP request timeout. Defaults to 60 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(value: 60);
    /// <summary>Optional custom HttpClient for testing or custom transports.</summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>Factory to create configured OpenAI-compatible embedding generators.</summary>
public static class OpenAiEmbeddingGeneratorFactory {
    /// <summary>Creates a configured <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> for OpenAI-compatible endpoints.</summary>
    /// <param name="options">The generator configuration options.</param>
    /// <returns>A configured embedding generator.</returns>
    public static IEmbeddingGenerator<string, Embedding<float>> Create(OpenAiEmbeddingOptions options) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentNullException.ThrowIfNull(argument: options.Endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: options.Model);

        string? apiKey = null;

        if (!string.IsNullOrEmpty(value: options.ApiKey)) {
            apiKey = options.ApiKey;
        } else if (!string.IsNullOrEmpty(value: options.ApiKeyEnvironment)) {
            apiKey = Environment.GetEnvironmentVariable(variable: options.ApiKeyEnvironment);
        }

        var effectiveKey = (string.IsNullOrEmpty(value: apiKey) ? "no-key" : apiKey);
        var credential = new ApiKeyCredential(key: effectiveKey);

        var clientOptions = new OpenAIClientOptions {
            Endpoint = options.Endpoint,
            NetworkTimeout = options.Timeout,
        };

        HttpMessageHandler httpHandler = (options.HttpClient is not null
            ? new ForwardingHandler(options.HttpClient)
            : new HttpClientHandler());

        var customHeaderHandler = new HeaderOverrideHandler(
            inner: httpHandler,
            headerName: options.ApiKeyHeader,
            apiKey: effectiveKey
        );

        var customHttpClient = new HttpClient(handler: customHeaderHandler) {
            Timeout = options.Timeout,
        };

        clientOptions.Transport = new HttpClientPipelineTransport(client: customHttpClient);

        var client = new OpenAIClient(credential: credential, options: clientOptions);
        var embeddingClient = client.GetEmbeddingClient(model: options.Model);

        var dimensions = (options.OmitDimensions ? (int?)null : options.Dimensions);
        var innerGenerator = embeddingClient.AsIEmbeddingGenerator(defaultModelDimensions: dimensions);

        return new SanitizingEmbeddingGenerator(
            inner: innerGenerator,
            apiKey: (string.IsNullOrEmpty(value: apiKey) ? null : apiKey),
            expectedDimensions: dimensions
        );
    }

    private sealed class ForwardingHandler : HttpMessageHandler {
        private readonly HttpClient m_client;

        public ForwardingHandler(HttpClient client) {
            m_client = client;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            m_client.SendAsync(request: request, cancellationToken: cancellationToken);
    }

    private sealed class HeaderOverrideHandler : DelegatingHandler {
        private readonly string m_headerName;
        private readonly string m_apiKey;

        public HeaderOverrideHandler(HttpMessageHandler inner, string headerName, string apiKey) : base(innerHandler: inner) {
            m_headerName = headerName;
            m_apiKey = apiKey;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (!string.Equals(a: m_headerName, b: "Authorization", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                request.Headers.Remove(name: "Authorization");
                request.Headers.TryAddWithoutValidation(name: m_headerName, value: m_apiKey);
            }

            return base.SendAsync(request: request, cancellationToken: cancellationToken);
        }
    }

    private sealed class SanitizingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>> {
        private readonly IEmbeddingGenerator<string, Embedding<float>> m_inner;
        private readonly string? m_apiKey;
        private readonly int? m_expectedDimensions;

        public SanitizingEmbeddingGenerator(
            IEmbeddingGenerator<string, Embedding<float>> inner,
            string? apiKey,
            int? expectedDimensions
        ) {
            m_inner = inner;
            m_apiKey = apiKey;
            m_expectedDimensions = expectedDimensions;
        }


        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default
        ) {
            try {
                var result = await m_inner.GenerateAsync(
                    values: values,
                    options: options,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);

                foreach (var emb in result) {
                    var span = emb.Vector.Span;

                    if (m_expectedDimensions.HasValue && span.Length != m_expectedDimensions.Value) {
                        throw new InvalidOperationException(
                            message: $"Dimension mismatch: expected {m_expectedDimensions.Value}, received {span.Length}."
                        );
                    }

                    for (var i = 0; i < span.Length; i++) {
                        if (!float.IsFinite(span[i])) {
                            throw new InvalidOperationException(message: "Received non-finite float value in embedding vector.");
                        }
                    }
                }

                return result;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                var message = ex.Message;

                if (message.Length > 512) {
                    message = string.Concat(message.AsSpan(start: 0, length: 509), "...");
                }

                if (!string.IsNullOrEmpty(value: m_apiKey)) {
                    message = message.Replace(oldValue: m_apiKey, newValue: "***");
                }

                throw new InvalidOperationException(message: message, innerException: ex);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            m_inner.GetService(serviceType: serviceType, serviceKey: serviceKey);

        public void Dispose() => m_inner.Dispose();
    }
}
