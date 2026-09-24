using Azure.Core;

namespace Puck.World.Azure;

/// <summary>One downstream token exchanged for a validated caller ahead of a single operation. It is request-confined:
/// never persist it, and never return it or its token to a caller.</summary>
public sealed class AzureDelegatedGrant {
    internal AzureDelegatedGrant(AccessToken access, string scope, string? observation, DateTimeOffset expiresAt) {
        Access = access;
        ExpiresAt = expiresAt;
        Observation = observation;
        Scope = scope;
    }

    internal AccessToken Access { get; }
    // The expiry of the user assertion the token was exchanged from; it bounds the operation.
    internal DateTimeOffset ExpiresAt { get; }
    // The observation the grant reads, or null for platform onboarding.
    internal string? Observation { get; }
    internal string Scope { get; }
}
