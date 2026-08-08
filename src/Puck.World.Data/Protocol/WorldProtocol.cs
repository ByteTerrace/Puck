namespace Puck.World.Protocol;

/// <summary>The opaque identity of the one World wire contract this build speaks.</summary>
public static class WorldProtocol {
    /// <summary>The current wire identity. This is an opaque re-key, never an ordered version and never shared with
    /// replay-tape or guest-ABI shape identities.</summary>
    public const ulong WireProtocolKey = 0x5055_434B_5034_4C31UL; // "PUCKP4L1"
}

/// <summary>A Hello-door refusal. The name is the stable protocol diagnostic; detail is narration only.</summary>
public enum WorldHelloRefusal : byte {
    /// <summary>The offered opaque wire identity is not the one this build accepts.</summary>
    WireProtocolKeyMismatch,
}

/// <summary>The Hello door every connection checks before admission — <c>Server.WorldTcpHost</c>'s raw handshake for a
/// remote peer, and the loopback <c>Session.Join</c> path for a local one — both before any frame is admitted.</summary>
public static class WorldHelloDoor {
    /// <summary>Checks one offered wire identity.</summary>
    /// <param name="offeredKey">The peer's opaque wire identity.</param>
    /// <param name="refusal">The named refusal on mismatch; default on success.</param>
    /// <returns><see langword="true"/> only for this build's exact key.</returns>
    public static bool TryAccept(ulong offeredKey, out WorldHelloRefusal refusal) {
        if (offeredKey != WorldProtocol.WireProtocolKey) {
            refusal = WorldHelloRefusal.WireProtocolKeyMismatch;

            return false;
        }

        refusal = default;

        return true;
    }
}
