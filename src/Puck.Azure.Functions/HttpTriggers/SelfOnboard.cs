using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Json;
using Puck.Azure.Functions.Middleware;

namespace Puck.Azure.Functions.HttpTriggers;

public sealed class SelfOnboard(
    IConfiguration configuration,
    IDataProtectionProvider dataProtectionProvider,
    IHttpClientFactory httpClientFactory
)
{
    private const string ActorsBaseUrlKey = "Onboarding:ActorsBaseUrl";
    private const string MigratingState = "Migrating";
    private const string NotOnboardedState = "NotOnboarded";
    private const string OnboardingState = "Onboarding";
    private const string ReadyState = "Ready";

    private sealed record ActorProvisioningState(
        string? Status,
        string[]? CompletedSteps,
        DateTimeOffset? UpdatedAt,
        string? FaultReason
    );

    // App-only tokens (e.g. the Front Door origin identity, which the worker's JwtBearer
    // fallback accepts on anonymous edge requests) carry no scope claim; onboarding must
    // only ever run for a delegated user principal.
    private static string? GetDelegatedUserObjectId(FunctionContext functionContext) {
        var user = functionContext
            .GetHttpContext()!
            .User;
        var hasScopes = user
            .Claims
            .Any(predicate: static claim =>
                ("scp" == claim.Type) ||
                ("http://schemas.microsoft.com/identity/claims/scope" == claim.Type)
            );

        return hasScopes
            ? user
                .Identity
                ?.Name
                ?.ToLowerInvariant()
            : null;
    }
    private static async Task<HttpResponseData> CreateStateResponseAsync(
        HttpRequestData httpRequestData,
        string state,
        HttpStatusCode statusCode,
        CancellationToken cancellationToken
    ) {
        var response = httpRequestData.CreateResponse();

        response.StatusCode = statusCode;

        await response.WriteAsJsonAsync(
            cancellationToken: cancellationToken,
            instance: new { State = state, }
        );

        return response;
    }

    private HttpClient CreateActorsClient() =>
        httpClientFactory.CreateClient(name: "Actors");
    private string GetActorsBaseUrl() =>
        configuration
            .GetValue<string>(key: ActorsBaseUrlKey)
            ?.TrimEnd('/')
            ?? throw new InvalidOperationException(message: $"The \"{ActorsBaseUrlKey}\" configuration value is required.");

    [FeatureGate(features: nameof(SelfOnboard))]
    [Function(name: nameof(SelfOnboard))]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "post",
            Route = "self-onboard"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var httpContext = functionContext.GetHttpContext()!;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var escrow = ProtectedToken.FromJwt(
            accessToken: await httpContext.GetTokenAsync(tokenName: "access_token"),
            dataProtectionProvider: dataProtectionProvider
        );
        var actorResponse = await CreateActorsClient().PostAsJsonAsync(
            cancellationToken: cancellationToken,
            requestUri: $"{GetActorsBaseUrl()}/users/{userObjectId}/ensure-provisioned",
            value: new {
                assertionExpiresAt = DateTimeOffset.UtcNow.Add(timeSpan: escrow.TokenDiscriminator.TimeToLive),
                protectedAssertion = escrow.Value,
                tokenDiscriminator = escrow.TokenDiscriminator.ToBase64String(),
            }
        );

        actorResponse.EnsureSuccessStatusCode();

        var actorState = await actorResponse.Content.ReadFromJsonAsync<ActorProvisioningState>(cancellationToken: cancellationToken);

        // Migrating is a READY-equivalent for the portal: the user's data is reachable at its
        // recorded home for the whole migration (reads throughout, writes again after the flip),
        // so it must never fall into the Onboarding/NotOnboarded poll loop.
        return actorState?.Status switch {
            ReadyState => await CreateStateResponseAsync(
                cancellationToken: cancellationToken,
                httpRequestData: httpRequestData,
                state: ReadyState,
                statusCode: HttpStatusCode.OK
            ),
            MigratingState => await CreateStateResponseAsync(
                cancellationToken: cancellationToken,
                httpRequestData: httpRequestData,
                state: MigratingState,
                statusCode: HttpStatusCode.OK
            ),
            _ => await CreateStateResponseAsync(
                cancellationToken: cancellationToken,
                httpRequestData: httpRequestData,
                state: OnboardingState,
                statusCode: HttpStatusCode.Accepted
            ),
        };
    }

    [FeatureGate(features: nameof(SelfOnboard))]
    [Function(name: nameof(SelfOnboardStatus))]
    public async Task<HttpResponseData> SelfOnboardStatus(
        [HttpTrigger(
            authLevel: AuthorizationLevel.Anonymous,
            methods: "get",
            Route = "self-onboard"
        )] HttpRequestData httpRequestData,
        FunctionContext functionContext
    ) {
        var cancellationToken = functionContext.CancellationToken;
        var userObjectId = GetDelegatedUserObjectId(functionContext: functionContext);

        if (userObjectId is null) {
            return httpRequestData.CreateResponse(statusCode: HttpStatusCode.Forbidden);
        }

        var actorState = await CreateActorsClient().GetFromJsonAsync<ActorProvisioningState>(
            cancellationToken: cancellationToken,
            requestUri: $"{GetActorsBaseUrl()}/users/{userObjectId}/provisioning-state"
        );

        return await CreateStateResponseAsync(
            cancellationToken: cancellationToken,
            httpRequestData: httpRequestData,
            // Faulted maps to NotOnboarded so the portal's poll loop re-POSTs with a
            // fresh escrow, which resumes the grain from its last completed step.
            state: actorState?.Status switch {
                ReadyState => ReadyState,
                MigratingState => MigratingState,
                OnboardingState => OnboardingState,
                _ => NotOnboardedState,
            },
            statusCode: HttpStatusCode.OK
        );
    }
}

