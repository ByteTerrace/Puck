using Azure.Core;
using System.Net.Http.Headers;

namespace Puck.Azure.Functions.HttpDelegatingHandlers;

/// <summary>
/// Stamps every outbound request with an app-only bearer token for one fixed scope.
/// </summary>
public sealed class BearerTokenDelegatingHandler(
    TokenCredential tokenCredential,
    TokenRequestContext tokenRequestContext
) : DelegatingHandler {
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    ) {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            scheme: "Bearer",
            parameter: (await tokenCredential.GetTokenAsync(
                cancellationToken: cancellationToken,
                requestContext: tokenRequestContext
            )).Token
        );

        return await base.SendAsync(cancellationToken: cancellationToken, request: request);
    }
}
