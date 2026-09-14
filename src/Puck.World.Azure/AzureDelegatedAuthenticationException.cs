using Azure.Identity;
using Microsoft.Identity.Client;
using System.Text;
using System.Text.Json;

namespace Puck.World.Azure;

/// <summary>A downstream user authentication challenge. It contains no access token, authority override, or downstream scope.</summary>
public sealed class AzureDelegatedAuthenticationException : AuthenticationFailedException {
    /// <summary>Creates a challenge requiring user interaction.</summary>
    /// <param name="claims">Optional JSON claims request from the trusted downstream service or Entra.</param>
    public AzureDelegatedAuthenticationException(string? claims = null) : base("Delegated access requires user interaction.") {
        if (claims is not null) {
            if (Encoding.UTF8.GetByteCount(s: claims) > 4096) { throw new InvalidDataException(message: "Claims challenge exceeds its byte budget."); }
            using var document = JsonDocument.Parse(claims);

            if (document.RootElement.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(message: "Claims challenge must be a JSON object."); }
            Claims = document.RootElement.GetRawText();
        }
    }

    /// <summary>The validated JSON claims request, or null when consent/sign-in alone is required.</summary>
    public string? Claims { get; }

    internal static AzureDelegatedAuthenticationException? From(AuthenticationFailedException error) {
        if (error is AzureDelegatedAuthenticationException challenge) { return challenge; }
        for (var current = error.InnerException; (current is not null); current = current.InnerException) {
            if (current is MsalUiRequiredException ui) { return new(claims: ui.Claims); }
        }
        return null;
    }
    internal static void ThrowIfChallenge(int status, string? header) {
        if (status is not (401 or 403)) { return; }
        if (header is { Length: > 8192 }) { throw new InvalidDataException(message: "Authentication challenge exceeds its header budget."); }
        if (!string.IsNullOrEmpty(value: header)) {
            if (header.Any(predicate: c => (c is '\r' or '\n'))) { throw new InvalidDataException(message: "Invalid authentication header."); }
            using var response = new HttpResponseMessage();

            response.Headers.TryAddWithoutValidation(
                name: "WWW-Authenticate",
                value: header
            );
            var claims = WwwAuthenticateParameters.GetClaimChallengeFromResponseHeaders(
                httpResponseHeaders: response.Headers,
                scheme: "Bearer"
            );

            if (!string.IsNullOrEmpty(value: claims)) {
                throw new AzureDelegatedAuthenticationException(claims: (claims.AsSpan().TrimStart().StartsWith(value: "{")
                    ? claims
                    : Encoding.UTF8.GetString(bytes: Convert.FromBase64String(s: claims))));
            }
        }
        if (status == 401) { throw new AzureDelegatedAuthenticationException(); }
    }
}
