using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureDelegatedObservationTests {
    [Theory]
    [InlineData("Ready")]
    [InlineData("Migrating")]
    [InlineData("Onboarding")]
    public async Task OnboardingUsesExistingApiWithAnExchangedUserToken(string state) {
        using var exchange = new ExchangeHandler { ExpectedScope = $"api://{Application}/.default" };
        using var http = new HttpClient(exchange);
        using var platform = new OnboardingHandler(state);
        using var service = new AzureDelegatedServices(Tenant, Application, OnboardingSettings, new AssertionCredential(), new HttpClientTransport(http), platform);
        Assert.Equal(state, await service.EnsureOnboardedAsync("validated-user-assertion", DateTimeOffset.UtcNow.AddMinutes(1), Token));
        Assert.Equal(1, platform.Calls);
        Assert.Equal(1, exchange.Exchanges);
    }

    [Fact]
    public async Task OnboardingRefusedConsentAndExpiredAssertionsNeverReachPlatform() {
        using var exchange = new ExchangeHandler { ExpectedScope = $"api://{Application}/.default", Deny = true };
        using var http = new HttpClient(exchange);
        using var platform = new OnboardingHandler("Ready");
        using var service = new AzureDelegatedServices(Tenant, Application, OnboardingSettings, new AssertionCredential(), new HttpClientTransport(http), platform);
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () => await service.EnsureOnboardedAsync("validated-user-assertion", DateTimeOffset.UtcNow.AddMinutes(1), Token));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () => await service.EnsureOnboardedAsync("validated-user-assertion", DateTimeOffset.UtcNow.AddSeconds(-1), Token));
        Assert.Equal(1, exchange.Exchanges);
        Assert.Equal(0, platform.Calls);
    }

    private static JsonElement OnboardingSettings => JsonElement.Parse("""{"managedIdentityClientId":"dddddddd-dddd-dddd-dddd-dddddddddddd","observations":[],"onboardingUrl":"https://api.example.test/api/self-onboard"}""");
    private sealed class OnboardingHandler(string state) : HttpMessageHandler {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.example.test/api/self-onboard", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer delegated-arm-token", request.Headers.Authorization!.ToString());
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"State\":\"{state}\"}}") });
        }
    }
    private const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Application = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string Subject = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Settings => JsonElement.Parse("""
        {"managedIdentityClientId":"dddddddd-dddd-dddd-dddd-dddddddddddd","observations":[{
          "name":"inventory","kind":"inventory","subjects":["cccccccc-cccc-cccc-cccc-cccccccccccc"],
          "settings":{"resourceGroup":"/subscriptions/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/resourceGroups/approved",
          "apiVersion":"2021-04-01","resourceType":"Microsoft.Storage/storageAccounts","fields":{"name":"/name"}}
        }]}
        """);

    [Fact]
    public async Task ExchangesUserAndFederatedClientAssertionsInsteadOfForwardingEitherToArm() {
        using var handler = new ExchangeHandler();
        using var http = new HttpClient(handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(Tenant, Application, Settings, assertion, new HttpClientTransport(http));
        Assert.Equal(0, assertion.Calls);
        var result = await service.ReadAsync("inventory", Subject, "validated-user-assertion", DateTimeOffset.UtcNow.AddMinutes(1), Token);
        Assert.Equal("sample", Assert.Single(result).Fields["name"]);
        Assert.Equal(1, assertion.Calls);
        Assert.Equal(1, handler.Exchanges);
        Assert.Equal(1, handler.Reads);
    }

    [Fact]
    public async Task MissingGrantAndExpiredAssertionNeverContactIdentityOrArm() {
        using var handler = new ExchangeHandler();
        using var http = new HttpClient(handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(Tenant, Application, Settings, assertion, new HttpClientTransport(http));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await service.ReadAsync("inventory", "mallory", "user", DateTimeOffset.UtcNow.AddMinutes(1), Token));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () => await service.ReadAsync("inventory", Subject, "user", DateTimeOffset.UtcNow.AddSeconds(-1), Token));
        Assert.Equal(0, assertion.Calls);
        Assert.Equal(0, handler.Exchanges + handler.Reads);
    }

    [Fact]
    public async Task FailedConsentNeverFallsBackToTheHostCredential() {
        using var handler = new ExchangeHandler { Deny = true };
        using var http = new HttpClient(handler);
        var assertion = new AssertionCredential();
        using var service = new AzureDelegatedServices(Tenant, Application, Settings, assertion, new HttpClientTransport(http));
        await Assert.ThrowsAsync<AuthenticationFailedException>(async () => await service.ReadAsync("inventory", Subject, "validated-user-assertion", DateTimeOffset.UtcNow.AddMinutes(1), Token));
        Assert.Equal(1, assertion.Calls);
        Assert.Equal(0, handler.Reads);
    }

    private sealed class AssertionCredential : TokenCredential {
        internal int Calls;
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
            Assert.Equal(["api://AzureADTokenExchange"], requestContext.Scopes);
            Calls++;
            return new("federated-client-assertion", DateTimeOffset.UtcNow.AddMinutes(5));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class ExchangeHandler : HttpMessageHandler {
        internal string ExpectedScope = "https://management.azure.com//.default";
        internal bool Deny;
        internal int Exchanges;
        internal int Reads;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.RequestUri!.Host == "login.microsoftonline.com") {
                Assert.Equal($"/{Tenant}/oauth2/v2.0/token", request.RequestUri.AbsolutePath);
                var form = (await request.Content!.ReadAsStringAsync(cancellationToken)).Split('&')
                    .Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1].Replace('+', ' ')));
                Assert.Equal(Application, form["client_id"]);
                Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
                Assert.Equal("on_behalf_of", form["requested_token_use"]);
                Assert.Equal("validated-user-assertion", form["assertion"]);
                Assert.Equal("federated-client-assertion", form["client_assertion"]);
                Assert.Contains(ExpectedScope, form["scope"]);
                Exchanges++;
                return Deny ? Response(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Consent required","error_codes":[65001]}""") :
                    Response(HttpStatusCode.OK, """{"access_token":"delegated-arm-token","expires_in":3600,"token_type":"Bearer"}""");
            }
            Assert.Equal("management.azure.com", request.RequestUri.Host);
            Assert.Equal("Bearer delegated-arm-token", request.Headers.Authorization?.ToString());
            Reads++;
            return Response(HttpStatusCode.OK, """{"value":[{"id":"/subscriptions/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee/resourceGroups/approved/providers/Microsoft.Storage/storageAccounts/sample","name":"sample","type":"Microsoft.Storage/storageAccounts"}]}""");
        }
        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
