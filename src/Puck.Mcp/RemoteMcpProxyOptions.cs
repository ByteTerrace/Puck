namespace Puck.Mcp;

/// <summary>The OAuth identity of a reverse proxy that replaces Authorization and preserves the caller in ClientAuthorization.</summary>
public sealed record RemoteMcpProxyOptions {
    /// <summary>The proxy token's exact HTTPS OIDC issuer, independent of the caller's issuer.</summary>
    public required string Issuer { get; init; }
    /// <summary>The API audience of the proxy's origin access token.</summary>
    public required string Audience { get; init; }
    /// <summary>The exact signed subject claim used to identify the proxy: sub or oid.</summary>
    public required string SubjectClaim { get; init; }
    /// <summary>The sole proxy identity allowed to supply a forwarded caller token.</summary>
    public required string Subject { get; init; }
    /// <summary>The exact signed tenant claim; required for oid subjects.</summary>
    public string? TenantId { get; init; }

    internal void Validate() {
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" ||
            issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(Audience) || string.IsNullOrWhiteSpace(Subject) ||
            SubjectClaim is not ("sub" or "oid") || (SubjectClaim == "oid" && !Guid.TryParse(TenantId, out _))) {
            throw new ArgumentException("trustedProxy requires an exact HTTPS issuer, audience, subject claim and subject; oid requires tenantId.");
        }
    }
}
