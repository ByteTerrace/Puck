namespace Puck.World.Protocol;

public static partial class WorldSubmissionCodec {
    // A mutation payload's header: one flag byte, then, when the flag is set, the 16 little-endian bytes of the
    // activation the mutation expects. The mutation leaf follows.
    private const int ExpectedActivationBytes = 16;

    // Encodes a mutation payload: its expected activation, when it carries one, then the mutation leaf.
    private static bool TryEncodeMutationPayload(WorldSubmissionPayload.Mutation payload, out byte[] bytes, out WorldCodecFailure failure) {
        if (!TryEncodeMutation(
            bytes: out var leaf,
            failure: out failure,
            mutation: payload.Value
        )) {
            bytes = [];

            return false;
        }

        var header = ((payload.ExpectedActivation is null) ? 1 : (1 + ExpectedActivationBytes));

        bytes = new byte[(header + leaf.Length)];

        if (payload.ExpectedActivation is { } expected) {
            bytes[0] = 1;
            _ = expected.TryWriteBytes(
                bigEndian: false,
                bytesWritten: out _,
                destination: bytes.AsSpan(
                    length: ExpectedActivationBytes,
                    start: 1
                )
            );
        }

        leaf.CopyTo(array: bytes, index: header);

        return true;
    }
    // Decodes a mutation payload, refusing a malformed flag and an expectation of the empty activation.
    private static bool TryDecodeMutationPayload(ReadOnlySpan<byte> bytes, out WorldSubmissionPayload.Mutation? payload, out WorldCodecFailure failure) {
        payload = null;

        if ((bytes.Length < 1) || ((bytes[0] == 1) && (bytes.Length < (1 + ExpectedActivationBytes)))) {
            failure = new WorldCodecFailure(Detail: "a mutation payload is shorter than its expectation header", Refusal: WorldCodecRefusal.PayloadTruncated);

            return false;
        }

        if (bytes[0] > 1) {
            failure = new WorldCodecFailure(Detail: $"a mutation payload's expectation flag is {bytes[0]}, neither 0 nor 1", Refusal: WorldCodecRefusal.PayloadMalformed);

            return false;
        }

        Guid? expected = null;

        if (bytes[0] == 1) {
            expected = new Guid(
                b: bytes.Slice(
                    length: ExpectedActivationBytes,
                    start: 1
                ),
                bigEndian: false
            );

            if (expected == Guid.Empty) {
                failure = new WorldCodecFailure(Detail: "a mutation payload expects the empty activation", Refusal: WorldCodecRefusal.PayloadMalformed);

                return false;
            }
        }

        if (!TryDecodeMutation(
            bytes: bytes[((expected is null) ? 1 : (1 + ExpectedActivationBytes))..],
            failure: out failure,
            mutation: out var mutation
        )) {
            return false;
        }

        payload = new WorldSubmissionPayload.Mutation(ExpectedActivation: expected, Value: mutation!);

        return true;
    }
}
