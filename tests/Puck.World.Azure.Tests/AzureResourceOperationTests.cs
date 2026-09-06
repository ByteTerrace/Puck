using System.Net;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureResourceOperationTests {
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/game/providers/Microsoft.Example/widgets/creature";
    private const string Poll = "https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Example/locations/eastus/operations/42?api-version=2025-01-01";
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("17", 17)]
    [InlineData("Thu, 01 Jan 2026 00:00:20 GMT", 20)]
    [InlineData("Wed, 31 Dec 2025 23:59:59 GMT", 0)]
    [InlineData("not-a-delay", -1)]
    [InlineData("9223372036854775807", 922337203685L)]
    public async Task HostRegistrationValidatesInputsAndSchedulesFromDurableServiceHints(string retryAfter, long expectedSeconds) {
        using var wire = new Wire(Reply(202, headers: [("Azure-AsyncOperation", Poll), ("Retry-After", retryAfter)]));
        using var provider = Provider(wire);
        var registration = provider.Register("Delete the associated resource");
        Assert.DoesNotContain(ResourceId, registration.Description.InputSchema);
        Assert.Throws<System.Text.Json.JsonException>(() => registration.CreateRequest("invalid", "[]"));
        var request = registration.CreateRequest("death/registered", "{}");
        var running = await registration.Provider.ExecuteAsync(request, Cancel);
        var delay = registration.PollingDelay!(running, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        if (expectedSeconds < 0) { Assert.Null(delay); }
        else { Assert.Equal(TimeSpan.FromTicks(expectedSeconds * TimeSpan.TicksPerSecond), delay); }
        Assert.Single(wire.Requests);
    }

    [Fact]
    public async Task ProviderQueryArgumentsAreEscapedCopiedAndIncludedInIdentity() {
        using var wire = new Wire(Reply(204));
        var query = new Dictionary<string, string> { ["filter"] = "a&b=c", ["force"] = "true" };
        var binding = Binding(AzureResourceMethod.Delete) with { QueryParameters = query };
        using var provider = Provider(wire, binding);
        var operation = provider.CreateOperation("death/query");
        query["force"] = "false";
        await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal("?api-version=2025-01-01&filter=a%26b%3Dc&force=true", Assert.Single(wire.Requests).Uri.Query);
        using var changed = Provider(wire, binding);
        Assert.NotEqual(provider.Identity, changed.Identity);
        Assert.Throws<ArgumentException>(() => Provider(wire, binding with { QueryParameters = new Dictionary<string, string> { ["API-VERSION"] = "other" } }));
    }

    [Theory]
    [InlineData("Failed", WorldExternalOperationStatus.Failed)]
    [InlineData("Canceled", WorldExternalOperationStatus.Failed)]
    [InlineData("InProgress", WorldExternalOperationStatus.Running)]
    public async Task StatusEndpointDefinesOutcomeRegardlessOfHttp200(string state, WorldExternalOperationStatus expected) {
        using var wire = new Wire(Reply(202, headers: [("Azure-AsyncOperation", Poll)]), Reply(200, "{\"status\":\"" + state + "\"}"));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/status");
        var running = await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal(expected, (await provider.ReconcileAsync(operation, running, Cancel)).Status);
    }

    [Fact]
    public async Task AcceptedWithoutStatusUrlCannotBeSpeculativelyReconciled() {
        using var wire = new Wire(Reply(202));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/no-receipt");
        var running = await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Running, running.Status);
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await provider.ReconcileAsync(operation, running, Cancel)).Status);
        Assert.Single(wire.Requests);
    }

    [Theory]
    [InlineData(AzureResourceMethod.Post)]
    [InlineData(AzureResourceMethod.Patch)]
    [InlineData(AzureResourceMethod.Delete)]
    [InlineData(AzureResourceMethod.Put)]
    public async Task GenericMethodsPreserveBodyVersionPreconditionAndAuthentication(AzureResourceMethod method) {
        using var wire = new Wire(Reply(204));
        using var provider = Provider(wire, Binding(method) with { Action = method == AzureResourceMethod.Post ? "restart" : null, IfMatch = "\"etag-3\"" });
        var operation = provider.CreateOperation("world/entity/generation", "{\"properties\":{\"arbitrary\":42}}");
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await provider.ExecuteAsync(operation, Cancel)).Status);
        var request = Assert.Single(wire.Requests);
        Assert.Equal(method.ToString().ToUpperInvariant(), request.Method);
        Assert.Equal(operation.Payload, request.Body);
        Assert.Equal("?api-version=2025-01-01", request.Uri.Query);
        Assert.Equal(ResourceId + (method == AzureResourceMethod.Post ? "/restart" : ""), request.Uri.AbsolutePath);
        Assert.Equal("\"etag-3\"", request.IfMatch);
        Assert.Equal("Bearer test-token", request.Authorization);
        Assert.NotEmpty(request.ClientRequestId);
        Assert.Single(wire.Credential.Scopes);
        Assert.Equal(ArmEnvironment.AzurePublicCloud.DefaultScope, wire.Credential.Scopes[0]);
    }

    [Theory]
    [InlineData("Azure-AsyncOperation")]
    [InlineData("Operation-Location")]
    [InlineData("Location")]
    public async Task RestartPollsSavedReceiptWithGetAndNeverRepeatsMutation(string header) {
        using var wire = new Wire(Reply(202, headers: [(header, Poll), ("Retry-After", "17")]),
            Reply(200, header == "Location" ? "{}" : "{\"status\":\"Succeeded\"}"));
        var binding = Binding(AzureResourceMethod.Delete);
        WorldExternalOperation operation;
        WorldExternalOperationResult running;
        using (var provider = Provider(wire, binding)) {
            operation = provider.CreateOperation("death/1");
            running = await provider.ExecuteAsync(operation, Cancel);
        }
        Assert.Equal(WorldExternalOperationStatus.Running, running.Status);
        var receipt = AzureResourceOperationReceipt.Parse(running.Result);
        Assert.Equal(Poll, receipt.PollUri);
        Assert.Equal("17", receipt.RetryAfter);
        using var restarted = Provider(wire, binding);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await restarted.ReconcileAsync(operation, running, Cancel)).Status);
        Assert.Equal(new[] { "DELETE", "GET" }, wire.Requests.Select(r => r.Method));
        Assert.Equal(Poll, wire.Requests[1].Uri.AbsoluteUri);
        Assert.Null(wire.Requests[1].IfMatch);
        Assert.Equal("", wire.Requests[1].Body);
    }

    [Fact]
    public async Task AsyncOperationHeaderTakesPrecedenceAndReadFailuresRetainReceipt() {
        using var wire = new Wire(Reply(202, headers: [("Azure-AsyncOperation", Poll), ("Location", "https://untrusted.example/status")]),
            Reply(503, "{\"error\":{\"message\":\"secret\"}}"), Reply(200, "{\"status\":\"Succeeded\"}"));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/2");
        var running = await provider.ExecuteAsync(operation, Cancel);
        var unknown = await provider.ReconcileAsync(operation, running, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Unknown, unknown.Status);
        Assert.Equal(Poll, AzureResourceOperationReceipt.Parse(unknown.Result).PollUri);
        Assert.DoesNotContain("secret", unknown.Result);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, (await provider.ReconcileAsync(operation, unknown, Cancel)).Status);
        Assert.Equal(3, wire.Requests.Count);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(307)]
    public async Task AmbiguousMutationResponseIsNotRetriedOrReportedAsSuccess(int status) {
        using var wire = new Wire(Reply(status, headers: [("Location", "https://untrusted.example/steal")]));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/3");
        var result = await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Unknown, result.Status);
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await provider.ReconcileAsync(operation, result, Cancel)).Status);
        Assert.Single(wire.Requests);
    }

    [Theory]
    [InlineData("https://untrusted.example/status")]
    [InlineData("http://management.azure.com/status")]
    [InlineData("https://management.azure.com:444/status")]
    [InlineData("https://user@management.azure.com/status")]
    [InlineData("https://management.azure.com/status#fragment")]
    public async Task UntrustedStatusAddressesAreNeverFollowed(string uri) {
        using var wire = new Wire(Reply(202, headers: [("Azure-AsyncOperation", uri)]));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/4");
        var result = await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Unknown, result.Status);
        Assert.Null(AzureResourceOperationReceipt.Parse(result.Result).PollUri);
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await provider.ReconcileAsync(operation, result, Cancel)).Status);
        Assert.Single(wire.Requests);
    }

    [Theory]
    [InlineData("Succeeded", WorldExternalOperationStatus.Succeeded)]
    [InlineData("Failed", WorldExternalOperationStatus.Failed)]
    [InlineData("Canceled", WorldExternalOperationStatus.Failed)]
    [InlineData("Updating", WorldExternalOperationStatus.Running)]
    public async Task ProvisioningStatesAreNotConfusedWithHttpSuccess(string state, WorldExternalOperationStatus expected) {
        using var wire = new Wire(Reply(201, "{\"properties\":{\"provisioningState\":\"" + state + "\"}}"));
        using var provider = Provider(wire, Binding(AzureResourceMethod.Put));
        Assert.Equal(expected, (await provider.ExecuteAsync(provider.CreateOperation("create/1", "{}"), Cancel)).Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"status\":null}")]
    public async Task MissingOrMalformedStatusRemainsUnknownAndRetainsPolling(string body) {
        using var wire = new Wire(Reply(202, headers: [("Azure-AsyncOperation", Poll)]), Reply(200, body));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/5");
        var running = await provider.ExecuteAsync(operation, Cancel);
        var unknown = await provider.ReconcileAsync(operation, running, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Unknown, unknown.Status);
        Assert.Equal(Poll, AzureResourceOperationReceipt.Parse(unknown.Result).PollUri);
    }

    [Fact]
    public async Task NoSavedReceiptMeansNoSpeculativeResourceReadOrRepeatedEffect() {
        using var wire = new Wire();
        using var provider = Provider(wire);
        var result = await provider.ReconcileAsync(provider.CreateOperation("death/6"), new(WorldExternalOperationStatus.Unknown, "IOException"), Cancel);
        Assert.Equal(WorldExternalOperationStatus.Unknown, result.Status);
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task BodiesAreBoundedAndServiceOutputIsNotJournaled() {
        using var wire = new Wire(Reply(200, "{\"keys\":[\"secret\"]}"), Reply(200, new string('x', 100)));
        using var provider = Provider(wire, maximumRequestBytes: 8, maximumResponseBytes: 32);
        Assert.Throws<ArgumentException>(() => provider.CreateOperation("large", "{\"value\":100}"));
        var operation = provider.CreateOperation("action/1");
        var result = await provider.ExecuteAsync(operation, Cancel);
        Assert.Equal(WorldExternalOperationStatus.Succeeded, result.Status);
        Assert.DoesNotContain("secret", result.Result);
        Assert.Equal(WorldExternalOperationStatus.Unknown, (await provider.ExecuteAsync(provider.CreateOperation("action/2"), Cancel)).Status);
    }

    [Theory]
    [InlineData("../delete")]
    [InlineData("%2e%2e/delete")]
    [InlineData("/delete")]
    [InlineData("delete?api-version=evil")]
    [InlineData("delete#fragment")]
    public void ActionCannotEscapeBoundResource(string action) {
        using var wire = new Wire();
        Assert.Throws<ArgumentException>(() => Provider(wire, Binding(AzureResourceMethod.Post) with { Action = action }));
        Assert.Empty(wire.Requests);
    }

    [Fact]
    public async Task BindingIdentityIncludesDestinationIncarnationOperationVersionAndPrecondition() {
        using var wire = new Wire();
        var binding = Binding(AzureResourceMethod.Delete);
        using var original = Provider(wire, binding);
        var operation = original.CreateOperation("death/7");
        foreach (var changed in new[] { binding with { ResourceId = ResourceId + "2" }, binding with { Incarnation = "new" },
            binding with { Method = AzureResourceMethod.Post }, binding with { ApiVersion = "2026-01-01" }, binding with { IfMatch = "etag" } }) {
            using var replacement = Provider(wire, changed);
            Assert.NotEqual(original.Identity, replacement.Identity);
            await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.ExecuteAsync(operation, Cancel).AsTask());
        }
        Assert.Empty(wire.Requests);
    }

    private static AzureResourceBinding Binding(AzureResourceMethod method) => new("creature-resource", ResourceId, "incarnation-3", method, "2025-01-01");
    private static AzureResourceOperationProvider Provider(Wire wire, AzureResourceBinding? binding = null,
        int maximumRequestBytes = 65536, int maximumResponseBytes = 1048576) => new(wire.Credential,
            binding ?? Binding(AzureResourceMethod.Delete), maximumRequestBytes: maximumRequestBytes,
            maximumResponseBytes: maximumResponseBytes, transport: new HttpClientTransport(wire.Client));

    private static HttpResponseMessage Reply(int status, string? body = null, (string Name, string Value)[]? headers = null) {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (body is not null) { response.Content = new StringContent(body, Encoding.UTF8, "application/json"); }
        foreach (var (name, value) in headers ?? []) { response.Headers.TryAddWithoutValidation(name, value); }
        return response;
    }

    private sealed record Sent(string Method, Uri Uri, string Body, string? Authorization, string? IfMatch, string ClientRequestId);

    private sealed class Wire : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> m_replies;
        public List<Sent> Requests { get; } = [];
        public Credential Credential { get; } = new();
        public HttpClient Client { get; }
        public Wire(params HttpResponseMessage[] replies) {
            m_replies = new(replies);
            Client = new HttpClient(this, disposeHandler: false);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(new(request.Method.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString(), request.Headers.IfMatch.FirstOrDefault()?.ToString(),
                request.Headers.TryGetValues("x-ms-client-request-id", out var ids) ? ids.Single() : ""));
            return m_replies.Dequeue();
        }
        protected override void Dispose(bool disposing) {
            if (disposing) { Client.Dispose(); foreach (var reply in m_replies) { reply.Dispose(); } }
            base.Dispose(disposing);
        }
    }

    private sealed class Credential : TokenCredential {
        public List<string> Scopes { get; } = [];
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
            Scopes.AddRange(requestContext.Scopes);
            return new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
