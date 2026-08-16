using System.Buffers.Binary;

namespace Puck.Networking;

/// <summary>The socketless frame grammar every wire shares: <c>[u32 length][u8 kind][payload]</c>,
/// little-endian. Length counts the kind byte plus payload bytes, never its own four-byte prefix. The payload
/// cap is supplied by the caller, so an untrusted definition may be admitted a larger budget than a command or
/// query without widening every caller's cap at once.</summary>
public static class FrameCodec {
    /// <summary>The fixed prefix size (length prefix plus kind byte).</summary>
    public const int PrefixBytes = (sizeof(uint) + sizeof(byte));

    /// <summary>Joins one kind byte and payload span into a complete frame.</summary>
    /// <param name="kind">The frame kind.</param>
    /// <param name="payload">The payload bytes.</param>
    /// <returns>The complete frame.</returns>
    public static byte[] Join(byte kind, ReadOnlySpan<byte> payload) {
        var following = checked((payload.Length + sizeof(byte)));
        var frame = new byte[checked((sizeof(uint) + following))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: frame,
            value: checked((uint)following)
        );
        frame[sizeof(uint)] = kind;
        payload.CopyTo(destination: frame.AsSpan(start: PrefixBytes));

        return frame;
    }
    /// <summary>Splits one complete frame into its kind byte and payload span, checked against a caller-supplied
    /// payload cap. The kind byte is returned unvalidated — a closed kind vocabulary belongs to the caller,
    /// never to this transport-neutral grammar.</summary>
    /// <param name="frame">The complete frame bytes.</param>
    /// <param name="maxPayloadBytes">The hard cap the caller admits on the payload span.</param>
    /// <param name="kind">The frame's kind byte on success.</param>
    /// <param name="payload">The payload span on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TrySplit(ReadOnlySpan<byte> frame, int maxPayloadBytes, out byte kind, out ReadOnlySpan<byte> payload, out WireFailure failure) {
        kind = 0;
        payload = default;

        if (frame.Length < PrefixBytes) {
            failure = new WireFailure(
                Detail: $"frame is {frame.Length} bytes; at least {PrefixBytes} are required",
                Refusal: WireRefusal.FrameLengthInvalid
            );

            return false;
        }

        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: frame);

        if (
            (following < sizeof(byte)) ||
            (following != ((uint)(frame.Length - sizeof(uint))))
        ) {
            failure = new WireFailure(
                Detail: $"prefix declares {following} following bytes; buffer carries {(frame.Length - sizeof(uint))}",
                Refusal: WireRefusal.FrameLengthInvalid
            );

            return false;
        }

        var leaf = frame[PrefixBytes..];

        if (leaf.Length > maxPayloadBytes) {
            failure = new WireFailure(
                Detail: $"payload is {leaf.Length} bytes; cap is {maxPayloadBytes}",
                Refusal: WireRefusal.PayloadTooLarge
            );

            return false;
        }

        kind = frame[sizeof(uint)];
        payload = leaf;
        failure = default;

        return true;
    }
}
