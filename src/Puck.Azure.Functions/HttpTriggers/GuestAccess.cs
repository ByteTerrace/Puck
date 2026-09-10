using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using Puck.Azure.Functions.Utilities;

namespace Puck.Azure.Functions.HttpTriggers;

// The tenant's lever for how much of their public/ prefix other authenticated users get:
// None | Read | ReadWrite (the substrate default is Read). Owner-scoped by construction — the
// target is always the caller's own oid from their delegated token — and proxied to the grain,
// which stamps the container metadata that ABAC actually enforces. The system CanRead/CanWrite
// suspension levers are deliberately NOT exposed here; they remain platform-internal.
public sealed class GuestAccess(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory
) {
    private sealed record ActorProvisioningState(
        string? Status,
        string? GuestAccess
    );

    public sealed class SetGuestAccessRequest {
        public string? GuestAccess { get; set; }
    }

    [Function(name: nameof(SetGuestAccess))]
    public async Task<HttpResponseData> SetGuestAccess(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "guest-access"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = functionContext.GetDelegatedUserObjectId();

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var request = await httpRequestData.ReadFromJsonAsync<SetGuestAccessRequest>(cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(value: request?.GuestAccess)) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.BadRequest);
        }

        var actorResponse = await httpClientFactory
            .CreateClient(name: "Actors")
            .PutAsJsonAsync(
                cancellationToken: cancellationToken,
                requestUri: $"{configuration.GetRequiredActorsBaseUrl()}/users/{userObjectId}/guest-access",
                value: new { guestAccess = request.GuestAccess, }
            );

        if (HttpStatusCode.BadRequest == actorResponse.StatusCode) {
            var response = httpRequestData.CreateResponse();

            response.StatusCode = HttpStatusCode.BadRequest;

            await response.WriteAsJsonAsync(
                cancellationToken: cancellationToken,
                instance: new { Detail = "Guest access must be one of: None, Read, ReadWrite.", }
            );

            return response;
        }

        actorResponse.EnsureSuccessStatusCode();

        var actorState = await actorResponse.Content.ReadFromJsonAsync<ActorProvisioningState>(cancellationToken: cancellationToken);
        var successResponse = httpRequestData.CreateResponse();

        await successResponse.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new { GuestAccess = actorState?.GuestAccess, }
        );

        return successResponse;
    }
}

