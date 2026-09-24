using System.Text.Json;
using Puck.Embeddings;
using Puck.State;
using Puck.World.Server;

namespace Puck.World.Embeddings;

/// <summary>Configured fixture embedding provider producing deterministic offline embeddings using SHA-256.</summary>
public sealed class FixtureConfiguredEmbeddingProvider : IWorldConfiguredEmbeddingProvider {
    private readonly EmbeddingIdentity m_identity;

    /// <summary>Initializes a new instance of <see cref="FixtureConfiguredEmbeddingProvider"/>.</summary>
    /// <param name="identity">The embedding identity.</param>
    public FixtureConfiguredEmbeddingProvider(EmbeddingIdentity identity) {
        m_identity = identity;
    }

    /// <summary>Constructs a fixture embedding provider from JSON settings.</summary>
    /// <param name="settings">Configuration settings.</param>
    /// <returns>A configured embedding provider.</returns>
    public static IWorldConfiguredEmbeddingProvider Create(JsonElement settings) {
        if (settings.ValueKind != JsonValueKind.Object) {
            throw new ArgumentException(message: "Fixture settings must be a JSON object.");
        }

        var model = ((settings.TryGetProperty(propertyName: "model", value: out var modelEl) ? modelEl.GetString() : FixtureEmbeddingGenerator.SupportedModel) ?? FixtureEmbeddingGenerator.SupportedModel);

        if (!string.Equals(a: model, b: FixtureEmbeddingGenerator.SupportedModel, comparisonType: StringComparison.Ordinal)) {
            throw new ArgumentException(message: $"Fixture provider only supports model '{FixtureEmbeddingGenerator.SupportedModel}'.");
        }

        var revision = (settings.TryGetProperty(propertyName: "revision", value: out var revEl) ? revEl.GetString() : "1");

        if (string.IsNullOrWhiteSpace(value: revision)) {
            throw new ArgumentException(message: "Revision must not be empty.");
        }

        if (!settings.TryGetProperty(propertyName: "dimensions", value: out var dimsEl) || !dimsEl.TryGetInt32(value: out var dimensions)) {
            throw new ArgumentException(message: "Dimensions must be specified as an integer.");
        }

        var identity = new EmbeddingIdentity(Dimensions: dimensions, Model: model, Revision: revision);

        if (!identity.IsValid) {
            throw new ArgumentException(message: "Invalid fixture embedding identity.");
        }

        return new FixtureConfiguredEmbeddingProvider(identity: identity);
    }
    /// <inheritdoc />
    public IWorldEmbeddingSource BindEmbedding(JsonElement settings) =>
        new FixtureEmbeddingSource(identity: m_identity);
    /// <inheritdoc />
    public void Dispose() { }

    private sealed class FixtureEmbeddingSource(EmbeddingIdentity identity) : IWorldEmbeddingSource {
        public EmbeddingIdentity Identity => identity;

        public Task<IReadOnlyList<EmbeddingAnswer>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) {
            ArgumentNullException.ThrowIfNull(argument: texts);
            var answers = new List<EmbeddingAnswer>(capacity: texts.Count);
            var buffer = new sbyte[identity.Dimensions];

            foreach (var text in texts) {
                cancellationToken.ThrowIfCancellationRequested();
                FixtureEmbeddingGenerator.ComputeFixtureSbytes(destination: buffer, identity: identity, text: text);
                if (StateVector.TryCreate(components: buffer, error: out var error, vector: out var vector)) {
                    answers.Add(item: new EmbeddingAnswer(Refusal: null, Vector: vector));
                } else {
                    answers.Add(item: new EmbeddingAnswer(Refusal: error, Vector: null));
                }
            }

            return Task.FromResult<IReadOnlyList<EmbeddingAnswer>>(result: answers);
        }
        public void Dispose() { }
    }
}
