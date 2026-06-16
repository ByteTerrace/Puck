namespace Puck.Hosting;

/// <summary>
/// The grantor's handle for force-reclaiming a held capability it passed on. It exists only when the
/// capability was granted <em>revocably</em>; a grant made "no take backsies" yields no take-back, so the
/// capability can never be reclaimed. Revoking is permanent: the grantee stops holding the capability (its
/// <see cref="IHostContext.HoldsCapability{TCapability}"/> returns <see langword="false"/>), and any sub-grant
/// the grantee derived from it dies with it.
/// </summary>
public interface ICapabilityTakeBack {
    /// <summary>Gets whether the grant has been reclaimed.</summary>
    bool IsRevoked { get; }

    /// <summary>Force-reclaims the granted capability from the grantee.</summary>
    void Revoke();
}
