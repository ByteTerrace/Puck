namespace Puck.World.Protocol;

/// <summary>The opaque identity of the one World wire contract this build speaks.</summary>
public static class WorldProtocol {
    /// <summary>The current wire identity. This is an opaque re-key, never an ordered version and never shared with
    /// replay-tape or guest-ABI shape identities: a peer offering a stale key refuses cleanly on the Hello door
    /// rather than desynchronizing mid-handshake.</summary>
    public const ulong WireProtocolKey = 0x5055_434B_5034_4C32UL; // "PUCKP4L2"
}
/// <summary>A Hello-door refusal. The name is the stable protocol diagnostic; detail is narration only. This is the
/// version-compatibility door only — checked first, over the wire-protocol key alone, before any identity is asked
/// for. <see cref="WorldAdmissionRefusal"/> (<c>WorldAdmission.cs</c>) is the separate identity door that runs after
/// this one passes; the two refusal spellings are never allowed to collide (a peer offering a stale build must never
/// read the same reason as a peer offering no or a wrong identity).</summary>
public enum WorldHelloRefusal : byte {
    /// <summary>The offered opaque wire identity is not the one this build accepts.</summary>
    WireProtocolKeyMismatch,
}
/// <summary>The Hello door every connection checks before admission — <c>Server.WorldTcpHost</c>'s raw handshake for a
/// remote peer, and the loopback <c>Session.Join</c> path for a local one — both before any frame is admitted. This
/// door is protocol-version compatibility only, and stays that way: the loopback path (the owner's own process,
/// never a socket) calls only this check, by construction, and stops there — see <c>WorldServer.ApplySession</c>'s
/// own remarks on why loopback is and stays credential-free. A remote connection additionally passes through
/// <see cref="WorldAdmissionDoor"/> once this check succeeds; loopback never does, because there is no wire for an
/// identity to travel over and no boundary for a claim to cross — the process boundary is the trust boundary here.</summary>
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
