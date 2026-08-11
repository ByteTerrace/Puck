namespace Puck.World.Protocol;

/// <summary>One peer identity affected by an ordered admission/disconnect event.</summary>
/// <param name="BodyIndex">The population body index.</param>
/// <param name="Generation">The admission generation at the point of effect.</param>
/// <param name="Source">The peer's gap-filling intent source.</param>
/// <param name="Identity">The exact generated peer principal. Carried explicitly so replay never re-invents identity
/// from an index.</param>
/// <param name="IdentityDomain">The cryptographically verified admission domain for a remote human, or empty for a
/// document-authored/simulated peer. Replay needs this independently of <paramref name="Identity"/>, which is the
/// generated runtime <c>peer:index:generation</c> principal rather than the identity that admission verified.</param>
/// <param name="IdentitySubject">The verified admission subject, or empty when no remote identity was admitted.</param>
/// <param name="AuthorityTransferred">Whether this occupant arrived from another authority and must therefore be
/// excluded from destination-authored census reconciliation.</param>
/// <param name="PlacementId">The source-local inhabited placement claim, carried only so an aborted detach can restore
/// the exact admission row; a committed destination admission starts without it.</param>
/// <param name="CatalogRig">The occupant-owned procedural appearance rig, recorded so replay restores the same shape
/// even when authority admission selected another population slot.</param>
public readonly record struct WorldPeerEventEntry(int BodyIndex, int Generation, IntentSource Source, WorldPrincipal Identity, string IdentityDomain, string IdentitySubject, bool AuthorityTransferred, string? PlacementId, byte CatalogRig);

/// <summary>Server-authored entries in the same ordered domain as submissions. They are not submission payloads and
/// can never arrive from a client.</summary>
public abstract record WorldServerEvent {
    private WorldServerEvent() {
    }

    /// <summary>Peer bodies became active and their new-generation grants were minted.</summary>
    /// <param name="Entries">The admitted peer identities, in point-of-effect order.</param>
    /// <param name="MintedGrants">The grants minted through the ordinary grant door.</param>
    public sealed record PeerAdmitted(IReadOnlyList<WorldPeerEventEntry> Entries, IReadOnlyList<WorldGrant> MintedGrants) : WorldServerEvent;

    /// <summary>Peer bodies disconnected and every grant/route held by those generations was revoked.</summary>
    /// <param name="Entries">The disconnected peer identities, in point-of-effect order.</param>
    /// <param name="RevokedGrants">The rows revoked through the ordinary revoke door.</param>
    public sealed record PeerDisconnected(IReadOnlyList<WorldPeerEventEntry> Entries, IReadOnlyList<WorldGrant> RevokedGrants) : WorldServerEvent;
}
