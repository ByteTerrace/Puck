using Puck.Networking;

namespace Puck.World.Protocol;

/// <summary>The per-<see cref="WorldSubmissionKind"/> cap table over <see cref="Puck.Networking.FrameCodec"/>'s
/// transport-neutral grammar. The caps are per kind so an untrusted definition may be large without handing that
/// same allocation budget to a command or query.</summary>
public static class WorldFrameCodec {
    /// <summary>The fixed prefix size.</summary>
    public const int PrefixBytes = Puck.Networking.FrameCodec.PrefixBytes;

    /// <summary>Returns the hard payload cap for a declared kind.</summary>
    /// <param name="kind">The declared kind.</param>
    /// <returns>The maximum leaf bytes accepted.</returns>
    public static int MaxPayloadBytes(WorldSubmissionKind kind) => kind switch {
        WorldSubmissionKind.Command => (4 * 1024),
        WorldSubmissionKind.Grant => (4 * 1024),
        WorldSubmissionKind.Revoke => (4 * 1024),
        WorldSubmissionKind.Session => (256 * 1024),
        WorldSubmissionKind.Rebuild => ((16 * 1024) * 1024),
        WorldSubmissionKind.Mutation => ((4 * 1024) * 1024),
        WorldSubmissionKind.Undo => sizeof(int),
        WorldSubmissionKind.Composition => (16 * 1024),
        WorldSubmissionKind.Lever => 64,
        WorldSubmissionKind.Query => (4 * 1024),
        WorldSubmissionKind.AddonLifecycle => (4 * 1024),
        // A screen.insert content path is a filesystem path, never file bytes — 4 KiB matches the other small
        // structural leaves (Command/Grant/Query) rather than Rebuild's document-embedding cap.
        WorldSubmissionKind.ScreenOp => (4 * 1024),
        WorldSubmissionKind.Designation => (4 * 1024),
        _ => 0,
    };
    /// <summary>Decodes exactly one complete frame through the canonical leaf codec.</summary>
    /// <param name="frame">The complete frame bytes.</param>
    /// <param name="payload">The decoded payload on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out WorldSubmissionPayload? payload, out WorldCodecFailure failure) {
        payload = null;

        // The kind byte gates the cap, but the grammar cannot admit an unbounded cap while it reads the kind —
        // decode with the widest cap any kind declares, then re-check the leaf against the kind's own cap below.
        var wideCap = int.MaxValue;

        if (!Puck.Networking.FrameCodec.TrySplit(
            failure: out var wireFailure,
            frame: frame,
            kind: out var rawKind,
            maxPayloadBytes: wideCap,
            payload: out var leaf
        )) {
            failure = new WorldCodecFailure(
                Detail: wireFailure.Detail,
                Refusal: ((wireFailure.Refusal == WireRefusal.PayloadTooLarge)
                ? WorldCodecRefusal.PayloadTooLarge
                : WorldCodecRefusal.FrameLengthInvalid)
            );

            return false;
        }

        var kind = ((WorldSubmissionKind)rawKind);

        if (!Enum.IsDefined(value: kind)) {
            failure = new WorldCodecFailure(
                Detail: $"frame kind {rawKind} is not declared",
                Refusal: WorldCodecRefusal.FrameKindUnknown
            );

            return false;
        }

        var cap = MaxPayloadBytes(kind: kind);

        if (leaf.Length > cap) {
            failure = new WorldCodecFailure(
                Detail: $"{kind} payload is {leaf.Length} bytes; cap is {cap}",
                Refusal: WorldCodecRefusal.PayloadTooLarge
            );

            return false;
        }

        return WorldSubmissionCodec.TryDecode(
            bytes: leaf,
            failure: out failure,
            kind: kind,
            payload: out payload
        );
    }
    /// <summary>Encodes one complete frame through the canonical leaf codec.</summary>
    /// <param name="payload">The submission payload.</param>
    /// <param name="frame">The complete frame on success.</param>
    /// <param name="failure">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryEncode(WorldSubmissionPayload payload, out byte[] frame, out WorldCodecFailure failure) {
        frame = [];

        if (!WorldSubmissionCodec.TryEncode(
            bytes: out var leaf,
            failure: out failure,
            kind: out var kind,
            payload: payload
        )) {
            return false;
        }

        var cap = MaxPayloadBytes(kind: kind);

        if (leaf.Length > cap) {
            failure = new WorldCodecFailure(
                Detail: $"{kind} payload is {leaf.Length} bytes; cap is {cap}",
                Refusal: WorldCodecRefusal.PayloadTooLarge
            );

            return false;
        }

        frame = Puck.Networking.FrameCodec.Join(
            kind: ((byte)kind),
            payload: leaf
        );
        failure = default;

        return true;
    }
}
