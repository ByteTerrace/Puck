using System.Buffers.Text;
using System.Runtime.InteropServices;
using Microsoft.Extensions.AI;
using Puck.Maths;

namespace Puck.Embeddings;

/// <summary>Quantization helpers to convert floating point embedding vectors into signed byte unit vectors.</summary>
public static class VectorQuantizer {
    /// <summary>Quantizes a unit float span into a unit signed byte span.</summary>
    /// <param name="source">Floating-point component values.</param>
    /// <param name="destination">Target buffer for signed byte components.</param>
    /// <returns>True if quantization and admission checks succeeded, false otherwise.</returns>
    public static bool TryQuantizeUnit(ReadOnlySpan<float> source, Span<sbyte> destination) {
        if (source.Length == 0 || destination.Length != source.Length) {
            return false;
        }

        var doubles = (source.Length <= 1024 ? (Span<double>)stackalloc double[source.Length] : new double[source.Length]);

        for (var i = 0; i < source.Length; i++) {
            doubles[i] = source[i];
        }

        return SignedByteVectorFunctions.TryQuantizeUnit(source: doubles, destination: destination);
    }

    /// <summary>Quantizes an <see cref="Embedding{Single}"/> into a base64url-encoded signed-byte vector string.</summary>
    /// <param name="embedding">The source embedding.</param>
    /// <param name="base64UrlVector">The resulting base64url string on success.</param>
    /// <returns>True if successfully quantized and encoded, false otherwise.</returns>
    public static bool TryQuantizeToBase64Url(Embedding<float> embedding, out string base64UrlVector) {
        ArgumentNullException.ThrowIfNull(argument: embedding);

        var floatSpan = embedding.Vector.Span;
        var sbyteBuffer = new sbyte[floatSpan.Length];

        if (!TryQuantizeUnit(source: floatSpan, destination: sbyteBuffer)) {
            base64UrlVector = string.Empty;
            return false;
        }

        var byteSpan = MemoryMarshal.AsBytes(span: sbyteBuffer.AsSpan());
        base64UrlVector = Base64Url.EncodeToString(source: byteSpan);
        return true;
    }
}
