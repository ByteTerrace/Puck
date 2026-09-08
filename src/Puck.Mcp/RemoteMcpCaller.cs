namespace Puck.Mcp;

/// <summary>One validated HTTP caller, supplied only to trusted host integrations. Never persist or return this object.</summary>
public sealed class RemoteMcpCaller {
    /// <summary>The subject authorized by the resource server.</summary>
    public string Subject { get; }
    /// <summary>The exact validated issuer.</summary>
    public string Issuer { get; }
    /// <summary>The validated tenant constraint, when configured.</summary>
    public string? TenantId { get; }
    /// <summary>Expiry of this request's validated API access token.</summary>
    public DateTimeOffset ExpiresAt { get; }
    /// <summary>The validated API token, solely for a trusted downstream OAuth exchange. Never forward it to the downstream API.</summary>
    public string UserAssertion { get; }
    internal RemoteMcpCaller(string subject, string issuer, string? tenantId, DateTimeOffset expiresAt, string userAssertion) {
        Subject = subject; Issuer = issuer; TenantId = tenantId; ExpiresAt = expiresAt; UserAssertion = userAssertion;
    }
}
