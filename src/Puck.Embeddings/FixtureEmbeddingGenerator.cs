using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Puck.Embeddings;

/// <summary>Offline fixture generator that produces deterministic pseudo-random embedding vectors using SHA-256.</summary>
public sealed class FixtureEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>> {
    /// <summary>The model name supported by this generator.</summary>
    public const string SupportedModel = "puck-fixture";

    private readonly EmbeddingIdentity m_identity;

    /// <inheritdoc />
    public EmbeddingGeneratorMetadata Metadata { get; }

    /// <summary>Initializes a new instance of <see cref="FixtureEmbeddingGenerator"/>.</summary>
    /// <param name="identity">The embedding space identity.</param>
    public FixtureEmbeddingGenerator(EmbeddingIdentity identity) {
        if (!string.Equals(a: identity.Model, b: SupportedModel, comparisonType: StringComparison.Ordinal)) {
            throw new ArgumentException(
                message: $"Fixture provider only supports model '{SupportedModel}', received '{identity.Model}'.",
                paramName: nameof(identity)
            );
        }

        m_identity = identity;
        Metadata = new EmbeddingGeneratorMetadata(
            providerName: "Puck.Fixture",
            defaultModelId: identity.Model,
            defaultModelDimensions: identity.Dimensions
        );
    }

    /// <inheritdoc />
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(argument: values);

        var list = (values as IReadOnlyList<string> ?? values.ToList());
        var embeddings = new List<Embedding<float>>(capacity: list.Count);

        for (var t = 0; t < list.Count; t++) {
            cancellationToken.ThrowIfCancellationRequested();
            var vector = ComputeFixtureVector(identity: m_identity, text: list[t]);
            embeddings.Add(item: new Embedding<float>(vector: vector));
        }

        var result = new GeneratedEmbeddings<Embedding<float>>(embeddings: embeddings);
        return Task.FromResult(result: result);
    }

    /// <summary>Computes the floating-point unit vector for a single text using the fixture algorithm.</summary>
    /// <param name="identity">The embedding identity.</param>
    /// <param name="text">The text to embed.</param>
    /// <returns>A normalized unit vector of floats.</returns>
    public static float[] ComputeFixtureVector(EmbeddingIdentity identity, string text) {
        var dims = identity.Dimensions;
        var vector = new float[dims];
        var modelBytes = Encoding.UTF8.GetBytes(s: identity.Model);
        var revisionBytes = Encoding.UTF8.GetBytes(s: identity.Revision);
        var dimsBytes = Encoding.UTF8.GetBytes(s: dims.ToString(provider: CultureInfo.InvariantCulture));
        var textBytes = Encoding.UTF8.GetBytes(s: text);

        var prefixLength = (modelBytes.Length + 1 + revisionBytes.Length + 1 + dimsBytes.Length + 1 + textBytes.Length + 1);
        var blockCount = ((dims + 31) / 32);
        var sumSquares = 0.0;

        for (var b = 0; b < blockCount; b++) {
            var blockStrBytes = Encoding.UTF8.GetBytes(s: b.ToString(provider: CultureInfo.InvariantCulture));
            var totalLength = (prefixLength + blockStrBytes.Length);
            var buffer = new byte[totalLength];
            var offset = 0;

            Array.Copy(sourceArray: modelBytes, sourceIndex: 0, destinationArray: buffer, destinationIndex: offset, length: modelBytes.Length);
            offset += modelBytes.Length;
            buffer[offset++] = 0;

            Array.Copy(sourceArray: revisionBytes, sourceIndex: 0, destinationArray: buffer, destinationIndex: offset, length: revisionBytes.Length);
            offset += revisionBytes.Length;
            buffer[offset++] = 0;

            Array.Copy(sourceArray: dimsBytes, sourceIndex: 0, destinationArray: buffer, destinationIndex: offset, length: dimsBytes.Length);
            offset += dimsBytes.Length;
            buffer[offset++] = 0;

            Array.Copy(sourceArray: textBytes, sourceIndex: 0, destinationArray: buffer, destinationIndex: offset, length: textBytes.Length);
            offset += textBytes.Length;
            buffer[offset++] = 0;

            Array.Copy(sourceArray: blockStrBytes, sourceIndex: 0, destinationArray: buffer, destinationIndex: offset, length: blockStrBytes.Length);

            var hash = SHA256.HashData(source: buffer);

            for (var byteIdx = 0; byteIdx < 32; byteIdx++) {
                var componentIndex = (b * 32 + byteIdx);

                if (componentIndex >= dims) {
                    break;
                }

                var sbyteVal = (sbyte)hash[byteIdx];

                if (sbyteVal == -128) {
                    sbyteVal = -127;
                }

                var valDouble = (double)sbyteVal;

                vector[componentIndex] = (float)valDouble;
                sumSquares += (valDouble * valDouble);
            }
        }

        if (sumSquares > 0.0) {
            var norm = Math.Sqrt(d: sumSquares);

            for (var i = 0; i < dims; i++) {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc />
    public void Dispose() { }
}
