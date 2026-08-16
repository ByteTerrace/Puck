using System.Formats.Cbor;

namespace Puck.Attestation;

/// <summary>
/// A small transport wrapper around one claim and its supporting chain — a definite-length CBOR array
/// <c>[claim: bstr, chain: [bstr*]]</c> — for the situations where a claim rarely travels alone (a counterpart
/// border claim's root-issuing-subject chain, a federation-identity proof's SignsDirectly/Vouches chain). This is
/// NOT an attestation envelope itself: each element is independently encoded/decoded by an
/// <see cref="IAttestationCodec"/>; this only bundles already-encoded bytes so they cross one wire leaf together.
/// </summary>
public static class AttestationChainEnvelope {
    /// <summary>The chain's maximum entry count — attestation's own two-hop chain-depth rule (a root binding, an
    /// issuing binding).</summary>
    public const int MaxChainEntries = 2;

    /// <summary>Encodes a claim and its chain as one envelope.</summary>
    /// <param name="claim">The claim's already-encoded attestation bytes.</param>
    /// <param name="chain">The chain's already-encoded attestation bytes, root-to-subject order; at most
    /// <see cref="MaxChainEntries"/> entries.</param>
    /// <returns>The encoded envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chain"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="chain"/> carries more than <see cref="MaxChainEntries"/> entries.</exception>
    public static byte[] Encode(ReadOnlySpan<byte> claim, IReadOnlyList<byte[]> chain) {
        ArgumentNullException.ThrowIfNull(argument: chain);

        if (chain.Count > MaxChainEntries) {
            throw new ArgumentException(
                message: $"an attestation chain carries at most {MaxChainEntries} bindings; {chain.Count} were supplied",
                paramName: nameof(chain)
            );
        }

        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 2);
        writer.WriteByteString(value: claim);
        writer.WriteStartArray(definiteLength: chain.Count);

        foreach (var binding in chain) {
            writer.WriteByteString(value: binding);
        }

        writer.WriteEndArray();
        writer.WriteEndArray();

        return writer.Encode();
    }
    /// <summary>Decodes an envelope produced by <see cref="Encode"/>.</summary>
    /// <param name="wire">The encoded bytes.</param>
    /// <param name="claim">The claim's encoded bytes on success.</param>
    /// <param name="chain">The chain's encoded bytes, root-to-subject order, on success.</param>
    /// <param name="reason">The named reason on failure.</param>
    /// <returns><see langword="true"/> when the envelope decodes.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> wire, out byte[]? claim, out byte[][]? chain, out string reason) {
        claim = null;
        chain = null;

        try {
            var reader = new CborReader(
                data: wire.ToArray(),
                conformanceMode: CborConformanceMode.Strict
            );
            var outerLength = reader.ReadStartArray();

            if (outerLength != 2) {
                reason = $"an attestation chain envelope carries exactly 2 elements; {(outerLength?.ToString() ?? "an indefinite length")} arrived";

                return false;
            }

            var claimBytes = reader.ReadByteString();
            var chainLength = reader.ReadStartArray();

            if (
                (chainLength is null) ||
                (chainLength.Value > MaxChainEntries)
            ) {
                reason = $"an attestation chain carries at most {MaxChainEntries} bindings; {(chainLength?.ToString() ?? "an indefinite length")} arrived";

                return false;
            }

            var entries = new byte[chainLength.Value][];

            for (var index = 0; (index < entries.Length); index++) {
                entries[index] = reader.ReadByteString();
            }

            reader.ReadEndArray();
            reader.ReadEndArray();

            if (reader.BytesRemaining != 0) {
                reason = $"an attestation chain envelope carries {reader.BytesRemaining} trailing byte(s)";

                return false;
            }

            claim = claimBytes;
            chain = entries;
            reason = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is CborContentException or InvalidOperationException)) {
            reason = $"the attestation chain envelope does not decode — {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }
    }
}
