using System.Text;
using System.Text.Json;

namespace Puck.Mcp;

/// <summary>A trusted service's request for interactive user authorization, translated to an HTTP bearer challenge.</summary>
public sealed class RemoteMcpAuthorizationException : Exception {
    /// <summary>Creates a bounded authorization challenge. The resource server retains its configured issuer and scope.</summary>
    /// <param name="claims">Optional JSON claims request; never a raw header or token.</param>
    public RemoteMcpAuthorizationException(string? claims = null) : base("User authorization is required before this operation can proceed.") {
        if (claims is not null) {
            if (Encoding.UTF8.GetByteCount(s: claims) > 4096) { throw new ArgumentException(
                message: "Claims challenge exceeds its byte budget.",
                paramName: nameof(claims)
            ); }
            using var document = JsonDocument.Parse(claims);

            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new ArgumentException(
                message: "Claims challenge must be a JSON object.",
                paramName: nameof(claims)
            ); }
            EncodedClaims = Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: document.RootElement.GetRawText()));
        }
    }

    internal string? EncodedClaims { get; }
}
