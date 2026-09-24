using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Puck.Testing;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureDelegatedObservationTests {
    private const string Application = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Claims = """{"access_token":{"acrs":{"essential":true,"value":"c1"}}}""";
    private const string Subject = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static HttpResponseMessage ChallengeResponse() {
        var response = new HttpResponseMessage(statusCode: HttpStatusCode.Unauthorized) { Content = new StringContent(content: "") };

        response.Headers.TryAddWithoutValidation(
            name: "WWW-Authenticate",
            value: $"Bearer error=\"insufficient_claims\", claims=\"{Convert.ToBase64String(inArray: Encoding.UTF8.GetBytes(s: Claims))}\""
        );
        return response;
    }

    [InlineData("exchange")]
    [InlineData("onboarding")]
    [InlineData("arm")]
    [Theory]
    public async Task ChallengesPreserveClaimsAndNeverCompleteProtectedWork(string source) {
        using var exchange = new ExchangeHandler {
            ArmChallenge = (source == "arm"),
            Challenge = (source == "exchange"),
            ExpectedScope = ((source == "arm")
            ? "https://management.azure.com//.default"
            : $"{Application}/.default"),
        };
        using var http = new HttpClient(handler: exchange);
        using var platform = new OnboardingHandler(state: "Ready") { Challenge = (source == "onboarding") };
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            ((source == "arm")
            ? Settings
            : OnboardingSettings),
            new AssertionCredential(),
            new HttpClientTransport(client: http),
            platform,
            clock: new VirtualClock(start: Now)
        );
        // An Entra interaction requirement surfaces from the exchange itself, before any protected request; a
        // downstream refusal of the exchanged token surfaces from the operation.
        var error = await Assert.ThrowsAsync<AzureDelegatedAuthenticationException>(testCode: async () => {
            if (source == "arm") {
                var grant = await service.ExchangeObservationAsync(
                    "inventory",
                    Subject,
                    "validated-user-assertion",
                    Now.AddMinutes(minutes: 1),
                    Token
                );

                await service.ReadAsync(
                    "inventory",
                    grant,
                    Token
                );
            } else if (source == "exchange") {
                await service.ExchangeOnboardingAsync(
                    "validated-user-assertion",
                    Now.AddMinutes(minutes: 1),
                    Token
                );
            } else {
                var grant = await service.ExchangeOnboardingAsync(
                    "validated-user-assertion",
                    Now.AddMinutes(minutes: 1),
                    Token
                );

                await service.EnsureOnboardedAsync(
                    grant,
                    Token
                );
            }
        });

        Assert.Equal(
            Claims,
            error.Claims
        );
        Assert.DoesNotContain(
            "validated-user-assertion",
            error.ToString()
        );
        if (source == "exchange") {
            Assert.Equal(
            actual: (platform.Calls + exchange.Reads),
            expected: 0
        );
        }
    }
    [Fact]
    public async Task ExchangesUserAndFederatedClientAssertionsInsteadOfForwardingEitherToArm() {
        using var handler = new ExchangeHandler();
        using var http = new HttpClient(handler: handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            Settings,
            assertion,
            new HttpClientTransport(client: http),
            clock: new VirtualClock(start: Now)
        );

        Assert.Equal(
            actual: assertion.Calls,
            expected: 0
        );
        var grant = await service.ExchangeObservationAsync(
            "inventory",
            Subject,
            "validated-user-assertion",
            Now.AddMinutes(minutes: 1),
            Token
        );

        Assert.Equal(
            actual: handler.Reads,
            expected: 0
        );
        var result = await service.ReadAsync(
            "inventory",
            grant,
            Token
        );

        Assert.Equal(
            "sample",
            Assert.Single(collection: result).Fields["name"]
        );
        Assert.Equal(
            actual: assertion.Calls,
            expected: 1
        );
        Assert.Equal(
            actual: handler.Exchanges,
            expected: 1
        );
        Assert.Equal(
            actual: handler.Reads,
            expected: 1
        );
        await Assert.ThrowsAsync<UnauthorizedAccessException>(testCode: async () => await service.ReadAsync(
            "another",
            grant,
            Token
        ));
    }
    [Fact]
    public async Task FailedConsentNeverFallsBackToTheHostCredential() {
        using var handler = new ExchangeHandler { Deny = true };
        using var http = new HttpClient(handler: handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            Settings,
            assertion,
            new HttpClientTransport(client: http),
            clock: new VirtualClock(start: Now)
        );

        await Assert.ThrowsAsync<AzureDelegatedAuthenticationException>(testCode: async () => await service.ExchangeObservationAsync(
            "inventory",
            Subject,
            "validated-user-assertion",
            Now.AddMinutes(minutes: 1),
            Token
        ));
        Assert.Equal(
            actual: assertion.Calls,
            expected: 1
        );
        Assert.Equal(
            actual: handler.Reads,
            expected: 0
        );
    }
    [Fact]
    public async Task MissingGrantAndExpiredAssertionNeverContactIdentityOrArm() {
        using var handler = new ExchangeHandler();
        using var http = new HttpClient(handler: handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            Settings,
            assertion,
            new HttpClientTransport(client: http),
            clock: new VirtualClock(start: Now)
        );

        await Assert.ThrowsAsync<UnauthorizedAccessException>(testCode: async () => await service.ExchangeObservationAsync(
            "inventory",
            "mallory",
            "user",
            Now.AddMinutes(minutes: 1),
            Token
        ));
        await Assert.ThrowsAsync<AuthenticationFailedException>(testCode: async () => await service.ExchangeObservationAsync(
            "inventory",
            Subject,
            "user",
            Now.AddSeconds(seconds: -1),
            Token
        ));
        Assert.Equal(
            actual: assertion.Calls,
            expected: 0
        );
        Assert.Equal(
            actual: (handler.Exchanges + handler.Reads),
            expected: 0
        );
    }
    // The assertion's remaining lifetime, capped at two minutes, is the deadline an in-flight exchange is cancelled at.
    [InlineData(1, 60)]
    [InlineData(10, 120)]
    [Theory]
    public async Task TheAssertionsRemainingLifetimeBoundsAnInFlightExchange(int lifetimeMinutes, int deadlineSeconds) {
        var clock = new VirtualClock(start: Now);
        using var handler = new ExchangeHandler { Stalled = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously) };
        using var http = new HttpClient(handler: handler);
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            Settings,
            new AssertionCredential(),
            new HttpClientTransport(client: http),
            clock: clock
        );
        var exchange = service.ExchangeObservationAsync(
            "inventory",
            Subject,
            "validated-user-assertion",
            Now.AddMinutes(minutes: lifetimeMinutes),
            Token
        ).AsTask();

        await Task.WhenAny(
            task1: handler.Stalled.Task,
            task2: exchange
        ).WaitAsync(cancellationToken: Token);
        Assert.False(
            condition: exchange.IsCompleted,
            userMessage: $"the exchange ended before its deadline fired: {exchange.Exception?.GetBaseException().Message}"
        );
        await clock.WhenArmedAsync(
            count: 1,
            ct: Token,
            dueTime: TimeSpan.FromSeconds(seconds: deadlineSeconds)
        );
        clock.Advance(by: TimeSpan.FromSeconds(seconds: deadlineSeconds));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => exchange);
        Assert.False(condition: Token.IsCancellationRequested);
        Assert.Equal(
            actual: handler.Reads,
            expected: 0
        );
    }
    [Fact]
    public void ObservationDiscoveryDisclosesOnlyGrantedNames() {
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            Settings,
            new AssertionCredential(),
            clock: new VirtualClock(start: Now)
        );

        Assert.Equal(
            ["inventory"],
            service.NamesFor(subject: Subject)
        );
        Assert.Empty(collection: service.NamesFor(subject: "another-subject"));
    }
    [Fact]
    public async Task OnboardingRefusedConsentAndExpiredAssertionsNeverReachPlatform() {
        using var exchange = new ExchangeHandler { Deny = true, ExpectedScope = $"{Application}/.default" };
        using var http = new HttpClient(handler: exchange);
        using var platform = new OnboardingHandler(state: "Ready");
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            OnboardingSettings,
            new AssertionCredential(),
            new HttpClientTransport(client: http),
            platform,
            clock: new VirtualClock(start: Now)
        );

        await Assert.ThrowsAsync<AzureDelegatedAuthenticationException>(testCode: async () => await service.ExchangeOnboardingAsync(
            "validated-user-assertion",
            Now.AddMinutes(minutes: 1),
            Token
        ));
        await Assert.ThrowsAsync<AuthenticationFailedException>(testCode: async () => await service.ExchangeOnboardingAsync(
            "validated-user-assertion",
            Now.AddSeconds(seconds: -1),
            Token
        ));
        Assert.Equal(
            actual: exchange.Exchanges,
            expected: 1
        );
        Assert.Equal(
            actual: platform.Calls,
            expected: 0
        );
    }
    [InlineData("Ready")]
    [InlineData("Migrating")]
    [InlineData("Onboarding")]
    [Theory]
    public async Task OnboardingUsesExistingApiWithAnExchangedUserToken(string state) {
        using var exchange = new ExchangeHandler { ExpectedScope = $"{Application}/.default" };
        using var http = new HttpClient(handler: exchange);
        using var platform = new OnboardingHandler(state: state);
        using var service = new AzureDelegatedServices(
            Tenant,
            Application,
            OnboardingSettings,
            new AssertionCredential(),
            new HttpClientTransport(client: http),
            platform,
            clock: new VirtualClock(start: Now)
        );

        var grant = await service.ExchangeOnboardingAsync(
            "validated-user-assertion",
            Now.AddMinutes(minutes: 1),
            Token
        );

        Assert.Equal(
            actual: platform.Calls,
            expected: 0
        );
        Assert.Equal(
            state,
            await service.EnsureOnboardedAsync(
                grant,
                Token
            )
        );
        Assert.Equal(
            actual: platform.Calls,
            expected: 1
        );
        Assert.Equal(
            actual: exchange.Exchanges,
            expected: 1
        );
    }

    private static JsonElement OnboardingSettings => JsonElement.Parse("""{"managedIdentityClientId":"dddddddd-dddd-dddd-dddd-dddddddddddd","observations":[],"onboardingUrl":"https://api.example.test/api/self-onboard"}""");
    private static JsonElement Settings => JsonElement.Parse("""
        {"managedIdentityClientId":"dddddddd-dddd-dddd-dddd-dddddddddddd","observations":[{
          "name":"inventory","kind":"inventory","subjects":["cccccccc-cccc-cccc-cccc-cccccccccccc"],
          "settings":{"resourceGroup":"/subscriptions/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/resourceGroups/approved",
          "apiVersion":"2021-04-01","resourceType":"Microsoft.Storage/storageAccounts","fields":{"name":"/name"}}
        }]}
        """);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Every service's clock reads this instant until a law advances it, so an assertion's remaining lifetime is exactly
    // what a law authors.
    private static readonly DateTimeOffset Now = new(
        day: 1,
        hour: 0,
        minute: 0,
        month: 1,
        offset: TimeSpan.Zero,
        second: 0,
        year: 2026
    );

    private sealed class OnboardingHandler(string state) : HttpMessageHandler {
        internal int Calls;
        internal bool Challenge;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal(
                HttpMethod.Post,
                request.Method
            );
            Assert.Equal(
                "https://api.example.test/api/self-onboard",
                request.RequestUri!.AbsoluteUri
            );
            Assert.Equal(
                "Bearer delegated-arm-token",
                request.Headers.Authorization!.ToString()
            );
            Calls++;
            if (Challenge) { return Task.FromResult(result: ChallengeResponse()); }
            return Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) { Content = new StringContent(content: $"{{\"State\":\"{state}\"}}") });
        }
    }
    private sealed class AssertionCredential : TokenCredential {
        internal int Calls;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
            Assert.Equal(
                ["api://AzureADTokenExchange"],
                requestContext.Scopes
            );
            Calls++;
            return new(
                accessToken: "federated-client-assertion",
                expiresOn: DateTimeOffset.UtcNow.AddMinutes(minutes: 5)
            );
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result: GetToken(
                cancellationToken: cancellationToken,
                requestContext: requestContext
            ));
    }
    private sealed class ExchangeHandler : HttpMessageHandler {
        internal bool ArmChallenge;
        internal bool Challenge;
        internal bool Deny;
        internal int Exchanges;
        internal string ExpectedScope = "https://management.azure.com//.default";
        internal int Reads;
        // When set, the token exchange signals it and then waits for its request to be cancelled.
        internal TaskCompletionSource? Stalled;

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(statusCode: status) {
            Content = new StringContent(
            content: body,
            encoding: Encoding.UTF8,
            mediaType: "application/json"
        ),
        };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.RequestUri!.Host == "login.microsoftonline.com") {
                Assert.Equal(
                    $"/{Tenant}/oauth2/v2.0/token",
                    request.RequestUri.AbsolutePath
                );
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken: cancellationToken)).Split('&')
                    .Select(selector: pair => pair.Split(
                    '=',
                    2
                )).ToDictionary(
                    pair => pair[0],
                    pair => Uri.UnescapeDataString(stringToUnescape: pair[1].Replace(
                        newChar: ' ',
                        oldChar: '+'
                    ))
                );

                Assert.Equal(
                    Application,
                    form["client_id"]
                );
                Assert.Equal(
                    "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    form["grant_type"]
                );
                Assert.Equal(
                    "on_behalf_of",
                    form["requested_token_use"]
                );
                Assert.Equal(
                    "validated-user-assertion",
                    form["assertion"]
                );
                Assert.Equal(
                    "federated-client-assertion",
                    form["client_assertion"]
                );
                Assert.Contains(
                    ExpectedScope,
                    form["scope"].Split(
                        options: StringSplitOptions.RemoveEmptyEntries,
                        separator: ' '
                    )
                );
                Exchanges++;
                if (Stalled is { } stalled) {
                    stalled.TrySetResult();
                    await Task.Delay(
                        cancellationToken: cancellationToken,
                        delay: Timeout.InfiniteTimeSpan
                    );
                }
                if (Challenge) {
                    return Response(
                        HttpStatusCode.BadRequest,
                        JsonSerializer.Serialize(new { error = "invalid_grant", error_description = "MFA required", error_codes = new[] { 50076 }, claims = Claims })
                    );
                }
                return (Deny
                    ? Response(
                        body: """{"error":"invalid_grant","error_description":"Consent required","error_codes":[65001]}""",
                        status: HttpStatusCode.BadRequest
                    )
                    : Response(
                        body: """{"access_token":"delegated-arm-token","expires_in":3600,"token_type":"Bearer"}""",
                        status: HttpStatusCode.OK
                    )
                );
            }
            Assert.Equal(
                "management.azure.com",
                request.RequestUri.Host
            );
            Assert.Equal(
                "Bearer delegated-arm-token",
                request.Headers.Authorization?.ToString()
            );
            Reads++;
            if (ArmChallenge) { return ChallengeResponse(); }
            return Response(
                body: """{"value":[{"id":"/subscriptions/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/resourceGroups/approved/providers/Microsoft.Storage/storageAccounts/sample","name":"sample","type":"Microsoft.Storage/storageAccounts"}]}""",
                status: HttpStatusCode.OK
            );
        }
    }
}
