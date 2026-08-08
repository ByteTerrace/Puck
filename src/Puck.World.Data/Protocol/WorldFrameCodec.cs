using System.Buffers.Binary;

namespace Puck.World.Protocol;

/// <summary>The socketless World frame grammar: <c>[u32 length][u8 kind][payload]</c>, little-endian. Length counts
/// the kind byte plus payload bytes, never its own four-byte prefix. The caps are per kind so an untrusted definition
/// may be large without handing that same allocation budget to a command or query.</summary>
public static class WorldFrameCodec {
    /// <summary>The fixed prefix size.</summary>
    public const int PrefixBytes = sizeof(uint) + sizeof(byte);

    /// <summary>Encodes one complete frame through the canonical leaf codec.</summary>
    /// <param name="payload">The submission payload.</param>
    /// <param name="frame">The complete frame on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryEncode(WorldSubmissionPayload payload, out byte[] frame, out WorldCodecFailure failure) {
        frame = [];
        if (!WorldSubmissionCodec.TryEncode(payload, out var kind, out var leaf, out failure)) {
            return false;
        }
        var cap = MaxPayloadBytes(kind);
        if (leaf.Length > cap) {
            failure = new WorldCodecFailure(WorldCodecRefusal.PayloadTooLarge, $"{kind} payload is {leaf.Length} bytes; cap is {cap}");
            return false;
        }
        var following = checked(leaf.Length + sizeof(byte));
        frame = new byte[checked(sizeof(uint) + following)];
        BinaryPrimitives.WriteUInt32LittleEndian(destination: frame, value: checked((uint)following));
        frame[sizeof(uint)] = (byte)kind;
        leaf.CopyTo(array: frame, index: PrefixBytes);
        failure = default;
        return true;
    }

    /// <summary>Decodes exactly one complete frame through the canonical leaf codec.</summary>
    /// <param name="frame">The complete frame bytes.</param>
    /// <param name="payload">The decoded payload on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out WorldSubmissionPayload? payload, out WorldCodecFailure failure) {
        payload = null;
        if (frame.Length < PrefixBytes) {
            failure = new WorldCodecFailure(WorldCodecRefusal.FrameLengthInvalid, $"frame is {frame.Length} bytes; at least {PrefixBytes} are required");
            return false;
        }
        var following = BinaryPrimitives.ReadUInt32LittleEndian(source: frame);
        if ((following < sizeof(byte)) || (following != (uint)(frame.Length - sizeof(uint)))) {
            failure = new WorldCodecFailure(WorldCodecRefusal.FrameLengthInvalid, $"prefix declares {following} following bytes; buffer carries {frame.Length - sizeof(uint)}");
            return false;
        }
        var kind = (WorldSubmissionKind)frame[sizeof(uint)];
        if (!Enum.IsDefined(value: kind)) {
            failure = new WorldCodecFailure(WorldCodecRefusal.FrameKindUnknown, $"frame kind {(byte)kind} is not declared");
            return false;
        }
        var leaf = frame[PrefixBytes..];
        var cap = MaxPayloadBytes(kind);
        if (leaf.Length > cap) {
            failure = new WorldCodecFailure(WorldCodecRefusal.PayloadTooLarge, $"{kind} payload is {leaf.Length} bytes; cap is {cap}");
            return false;
        }
        return WorldSubmissionCodec.TryDecode(kind, leaf, out payload, out failure);
    }

    /// <summary>Returns the hard payload cap for a declared kind.</summary>
    /// <param name="kind">The declared kind.</param>
    /// <returns>The maximum leaf bytes accepted.</returns>
    public static int MaxPayloadBytes(WorldSubmissionKind kind) => kind switch {
        WorldSubmissionKind.Command => 4 * 1024,
        WorldSubmissionKind.Grant => 4 * 1024,
        WorldSubmissionKind.Revoke => 4 * 1024,
        WorldSubmissionKind.Session => 256 * 1024,
        WorldSubmissionKind.Rebuild => 16 * 1024 * 1024,
        WorldSubmissionKind.Mutation => 4 * 1024 * 1024,
        WorldSubmissionKind.Undo => sizeof(int),
        WorldSubmissionKind.Composition => 16 * 1024,
        WorldSubmissionKind.Lever => 64,
        WorldSubmissionKind.Query => 4 * 1024,
        WorldSubmissionKind.AddonLifecycle => 4 * 1024,
        // A screen.insert content path is a filesystem path, never file bytes — 4 KiB matches the other small
        // structural leaves (Command/Grant/Query) rather than Rebuild's document-embedding cap.
        WorldSubmissionKind.ScreenOp => 4 * 1024,
        WorldSubmissionKind.Designation => 4 * 1024,
        _ => 0,
    };
}
