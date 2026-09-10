using Azure.Core;
using Azure.Identity;

namespace Puck.Azure;

public sealed class OnBehalfOfOptions {
    public string? ClientId { get; set; }
    public string? TenantId { get; set; }

    public void Deconstruct(out string clientId, out string tenantId) {
        clientId = (ClientId ?? "");
        tenantId = (TenantId ?? "");
    }
}
public static class IdentityUtilities {
    private static readonly string[] Scopes = ["api://AzureADTokenExchange"];

    /// <summary>
    /// Keyed-service key for the credential that mints the client assertion an on-behalf-of exchange
    /// signs with. It is a separate registration from the ambient credential because a host may run
    /// the exchange under a user-assigned identity federated to the shared app registration.
    /// </summary>
    public const string ClientAssertionCredentialKey = "ClientAssertionCredential";

    public static OnBehalfOfCredential ToOnBehalfOfCredential(
        this TokenCredential tokenCredential,
        OnBehalfOfOptions? options,
        string? userAssertion
    ) {
        ArgumentNullException.ThrowIfNull(argument: options);

        var (onBehalfOfClientId, onBehalfOfTenantId) = options;

        ArgumentException.ThrowIfNullOrWhiteSpace(argument: onBehalfOfClientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: onBehalfOfTenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: userAssertion);

        return new OnBehalfOfCredential(
            clientAssertionCallback: async (cancellationToken) => (await tokenCredential
                .GetTokenAsync(
                    cancellationToken: cancellationToken,
                    requestContext: new(scopes: Scopes)
                ))
                .Token,
            clientId: onBehalfOfClientId,
            options: default,
            tenantId: onBehalfOfTenantId,
            userAssertion: userAssertion
        );
    }
}
