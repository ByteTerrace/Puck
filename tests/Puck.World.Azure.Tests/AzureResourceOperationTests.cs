using System.Net;
using System.Text;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureResourceOperationTests {
    private const string Poll = "https://management.azure.com/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Example/locations/eastus/operations/42?api-version=2025-01-01";
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/game/providers/Microsoft.Example/widgets/creature";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static AzureResourceBinding Binding(AzureResourceMethod method) => new(
        "creature-resource",
        ResourceId,
        "incarnation-3",
        method,
        "2025-01-01"
    );
    private static AzureResourceOperationProvider Provider(Wire wire, AzureResourceBinding? binding = null,
        int maximumRequestBytes = 65536, int maximumResponseBytes = 1048576) => new(
            wire.Credential,
            (binding ?? Binding(method: AzureResourceMethod.Delete)),
            maximumRequestBytes: maximumRequestBytes,
            maximumResponseBytes: maximumResponseBytes,
            transport: new HttpClientTransport(client: wire.Client)
        );
    private static HttpResponseMessage Reply(int status, string? body = null, (string Name, string Value)[]? headers = null) {
        var response = new HttpResponseMessage(statusCode: ((HttpStatusCode)status));

        if (body is not null) { response.Content = new StringContent(
            content: body,
            encoding: Encoding.UTF8,
            mediaType: "application/json"
        ); }
        foreach (var (name, value) in (headers ?? [])) { response.Headers.TryAddWithoutValidation(
            name: name,
            value: value
        ); }
        return response;
    }

    [Fact]
    public async Task AcceptedWithoutStatusUrlCannotBeSpeculativelyReconciled() {
        using var wire = new Wire(Reply(202));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/no-receipt");
        var running = await provider.ExecuteAsync(
            operation,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Running,
            running.Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await provider.ReconcileAsync(
                operation,
                running,
                Cancel
            )).Status
        );
        Assert.Single(collection: wire.Requests);
    }
    [InlineData("../delete")]
    [InlineData("%2e%2e/delete")]
    [InlineData("/delete")]
    [InlineData("delete?api-version=evil")]
    [InlineData("delete#fragment")]
    [Theory]
    public void ActionCannotEscapeBoundResource(string action) {
        using var wire = new Wire();

        Assert.Throws<ArgumentException>(testCode: () => Provider(
            wire,
            Binding(method: AzureResourceMethod.Post) with { Action = action }
        ));
        Assert.Empty(collection: wire.Requests);
    }
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(307)]
    [Theory]
    public async Task AmbiguousMutationResponseIsNotRetriedOrReportedAsSuccess(int status) {
        using var wire = new Wire(Reply(
            status,
            headers: [("Location", "https://untrusted.example/steal")]
        ));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/3");
        var result = await provider.ExecuteAsync(
            operation,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            result.Status
        );
        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await provider.ReconcileAsync(
                operation,
                result,
                Cancel
            )).Status
        );
        Assert.Single(collection: wire.Requests);
    }
    [Fact]
    public async Task AsyncOperationHeaderTakesPrecedenceAndReadFailuresRetainReceipt() {
        using var wire = new Wire(
            Reply(
                202,
                headers: [("Azure-AsyncOperation", Poll), ("Location", "https://untrusted.example/status")]
            ),
            Reply(
                503,
                "{\"error\":{\"message\":\"secret\"}}"
            ),
            Reply(
                200,
                "{\"status\":\"Succeeded\"}"
            )
        );
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/2");
        var running = await provider.ExecuteAsync(
            operation,
            Cancel
        );
        var unknown = await provider.ReconcileAsync(
            operation,
            running,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            unknown.Status
        );
        Assert.Equal(
            Poll,
            AzureResourceOperationReceipt.Parse(result: unknown.Result).PollUri
        );
        Assert.DoesNotContain(
            "secret",
            unknown.Result
        );
        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await provider.ReconcileAsync(
                operation,
                unknown,
                Cancel
            )).Status
        );
        Assert.Equal(
            3,
            wire.Requests.Count
        );
    }
    [Fact]
    public async Task BindingIdentityIncludesDestinationIncarnationOperationVersionAndPrecondition() {
        using var wire = new Wire();
        var binding = Binding(method: AzureResourceMethod.Delete);
        using var original = Provider(
            wire,
            binding
        );
        var operation = original.CreateOperation("death/7");

        foreach (var changed in new[] { binding with { ResourceId = (ResourceId + "2") }, binding with { Incarnation = "new" },
            binding with { Method = AzureResourceMethod.Post }, binding with { ApiVersion = "2026-01-01" }, binding with { IfMatch = "etag" } }) {
            using var replacement = Provider(
                wire,
                changed
            );

            Assert.NotEqual(
                original.Identity,
                replacement.Identity
            );
            await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => replacement.ExecuteAsync(
                operation,
                Cancel
            ).AsTask());
        }
        Assert.Empty(collection: wire.Requests);
    }
    [Fact]
    public async Task BodiesAreBoundedAndServiceOutputIsNotJournaled() {
        using var wire = new Wire(
            Reply(
                200,
                "{\"keys\":[\"secret\"]}"
            ),
            Reply(
                200,
                new string(
                    c: 'x',
                    count: 100
                )
            )
        );
        using var provider = Provider(
            wire,
            maximumRequestBytes: 8,
            maximumResponseBytes: 32
        );

        Assert.Throws<ArgumentException>(testCode: () => provider.CreateOperation(
            id: "large",
            payload: "{\"value\":100}"
        ));
        var operation = provider.CreateOperation("action/1");
        var result = await provider.ExecuteAsync(
            operation,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            result.Status
        );
        Assert.DoesNotContain(
            "secret",
            result.Result
        );
        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await provider.ExecuteAsync(
                provider.CreateOperation("action/2"),
                Cancel
            )).Status
        );
    }
    [InlineData("managedIdentity")]
    [InlineData("azureCli")]
    [Theory]
    public void DeclarativeProviderSelectsExplicitAuthenticationAndBindsGenericOperationsWithoutCalls(string authentication) {
        using var settings = System.Text.Json.JsonDocument.Parse((("{\"authentication\":\"" + authentication) + "\"}"));
        using var provider = AzureConfiguredProvider.Registration.Create(settings.RootElement);
        using var operationSettings = System.Text.Json.JsonDocument.Parse((("{\"resourceId\":\"" + ResourceId) + "\",\"incarnation\":\"one\",\"method\":\"delete\",\"apiVersion\":\"2025-01-01\"}"));
        var operation = provider.Bind(
            "creature.delete",
            "Delete the associated resource",
            operationSettings.RootElement
        );

        Assert.Equal(
            "creature.delete",
            operation.CreateRequest(
                "request/1",
                "{}"
            ).Binding
        );
        using var invalid = System.Text.Json.JsonDocument.Parse("{\"authentication\":\"azureCli\",\"password\":\"must-not-be-accepted\"}");

        Assert.Throws<System.Text.Json.JsonException>(testCode: () => AzureConfiguredProvider.Registration.Create(invalid.RootElement));
        using var implicitCredential = System.Text.Json.JsonDocument.Parse("{\"authentication\":\"default\"}");

        Assert.Throws<ArgumentException>(testCode: () => AzureConfiguredProvider.Registration.Create(implicitCredential.RootElement));
    }
    [InlineData(AzureResourceMethod.Post)]
    [InlineData(AzureResourceMethod.Patch)]
    [InlineData(AzureResourceMethod.Delete)]
    [InlineData(AzureResourceMethod.Put)]
    [Theory]
    public async Task GenericMethodsPreserveBodyVersionPreconditionAndAuthentication(AzureResourceMethod method) {
        using var wire = new Wire(Reply(204));
        using var provider = Provider(
            wire,
            Binding(method: method) with { Action = ((method == AzureResourceMethod.Post)
            ? "restart"
            : null), IfMatch = "\"etag-3\"" }
        );
        var operation = provider.CreateOperation(
            id: "world/entity/generation",
            payload: "{\"properties\":{\"arbitrary\":42}}"
        );

        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await provider.ExecuteAsync(
                operation,
                Cancel
            )).Status
        );
        var request = Assert.Single(collection: wire.Requests);

        Assert.Equal(
            method.ToString().ToUpperInvariant(),
            request.Method
        );
        Assert.Equal(
            operation.Payload,
            request.Body
        );
        Assert.Equal(
            "?api-version=2025-01-01",
            request.Uri.Query
        );
        Assert.Equal(
            (ResourceId + ((method == AzureResourceMethod.Post)
            ? "/restart"
            : "")),
            request.Uri.AbsolutePath
        );
        Assert.Equal(
            "\"etag-3\"",
            request.IfMatch
        );
        Assert.Equal(
            "Bearer test-token",
            request.Authorization
        );
        Assert.NotEmpty(collection: request.ClientRequestId);
        Assert.Single(collection: wire.Credential.Scopes);
        Assert.Equal(
            ArmEnvironment.AzurePublicCloud.DefaultScope,
            wire.Credential.Scopes[0]
        );
    }
    [InlineData("17", 17)]
    [InlineData("Thu, 01 Jan 2026 00:00:20 GMT", 20)]
    [InlineData("Wed, 31 Dec 2025 23:59:59 GMT", 0)]
    [InlineData("not-a-delay", -1)]
    [InlineData("9223372036854775807", 922337203685L)]
    [Theory]
    public async Task HostRegistrationValidatesInputsAndSchedulesFromDurableServiceHints(string retryAfter, long expectedSeconds) {
        using var wire = new Wire(Reply(
            202,
            headers: [("Azure-AsyncOperation", Poll), ("Retry-After", retryAfter)]
        ));
        using var provider = Provider(wire);
        var registration = provider.Register(description: "Delete the associated resource");

        Assert.DoesNotContain(
            ResourceId,
            registration.Description.InputSchema
        );
        Assert.Throws<System.Text.Json.JsonException>(testCode: () => registration.CreateRequest(
            "invalid",
            "[]"
        ));
        var request = registration.CreateRequest(
            "death/registered",
            "{}"
        );
        var running = await registration.Provider.ExecuteAsync(
            request,
            Cancel
        );
        var delay = registration.PollingDelay!(
            running,
            new(
                day: 1,
                hour: 0,
                minute: 0,
                month: 1,
                offset: TimeSpan.Zero,
                second: 0,
                year: 2026
            )
        );

        if (expectedSeconds < 0) { Assert.Null(value: delay); } else { Assert.Equal(
            TimeSpan.FromTicks(value: (expectedSeconds * TimeSpan.TicksPerSecond)),
            delay
        ); }
        Assert.Single(collection: wire.Requests);
    }
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"status\":null}")]
    [Theory]
    public async Task MissingOrMalformedStatusRemainsUnknownAndRetainsPolling(string body) {
        using var wire = new Wire(
            Reply(
                202,
                headers: [("Azure-AsyncOperation", Poll)]
            ),
            Reply(
                200,
                body
            )
        );
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/5");
        var running = await provider.ExecuteAsync(
            operation,
            Cancel
        );
        var unknown = await provider.ReconcileAsync(
            operation,
            running,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            unknown.Status
        );
        Assert.Equal(
            Poll,
            AzureResourceOperationReceipt.Parse(result: unknown.Result).PollUri
        );
    }
    [Fact]
    public async Task NoSavedReceiptMeansNoSpeculativeResourceReadOrRepeatedEffect() {
        using var wire = new Wire();
        using var provider = Provider(wire);
        var result = await provider.ReconcileAsync(
            provider.CreateOperation("death/6"),
            new(
                Result: "IOException",
                Status: WorldExternalOperationStatus.Unknown
            ),
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            result.Status
        );
        Assert.Empty(collection: wire.Requests);
    }
    [Fact]
    public async Task ProviderQueryArgumentsAreEscapedCopiedAndIncludedInIdentity() {
        using var wire = new Wire(Reply(204));
        var query = new Dictionary<string, string> { ["filter"] = "a&b=c", ["force"] = "true" };
        var binding = Binding(method: AzureResourceMethod.Delete) with { QueryParameters = query };
        using var provider = Provider(
            wire,
            binding
        );
        var operation = provider.CreateOperation("death/query");

        query["force"] = "false";
        await provider.ExecuteAsync(
            operation,
            Cancel
        );
        Assert.Equal(
            "?api-version=2025-01-01&filter=a%26b%3Dc&force=true",
            Assert.Single(collection: wire.Requests).Uri.Query
        );
        using var changed = Provider(
            wire,
            binding
        );

        Assert.NotEqual(
            provider.Identity,
            changed.Identity
        );
        Assert.Throws<ArgumentException>(testCode: () => Provider(
            wire,
            binding with { QueryParameters = new Dictionary<string, string> { ["API-VERSION"] = "other" } }
        ));
    }
    [InlineData("Succeeded", WorldExternalOperationStatus.Succeeded)]
    [InlineData("Failed", WorldExternalOperationStatus.Failed)]
    [InlineData("Canceled", WorldExternalOperationStatus.Failed)]
    [InlineData("Updating", WorldExternalOperationStatus.Running)]
    [Theory]
    public async Task ProvisioningStatesAreNotConfusedWithHttpSuccess(string state, WorldExternalOperationStatus expected) {
        using var wire = new Wire(Reply(
            201,
            (("{\"properties\":{\"provisioningState\":\"" + state) + "\"}}")
        ));
        using var provider = Provider(
            wire,
            Binding(method: AzureResourceMethod.Put)
        );

        Assert.Equal(
            expected,
            (await provider.ExecuteAsync(
                provider.CreateOperation(
                    id: "create/1",
                    payload: "{}"
                ),
                Cancel
            )).Status
        );
    }
    [InlineData("Azure-AsyncOperation")]
    [InlineData("Operation-Location")]
    [InlineData("Location")]
    [Theory]
    public async Task RestartPollsSavedReceiptWithGetAndNeverRepeatsMutation(string header) {
        using var wire = new Wire(
            Reply(
                202,
                headers: [(header, Poll), ("Retry-After", "17")]
            ),
            Reply(
                200,
                ((header == "Location")
            ? "{}"
            : "{\"status\":\"Succeeded\"}")
            )
        );
        var binding = Binding(method: AzureResourceMethod.Delete);
        WorldExternalOperation operation;
        WorldExternalOperationResult running;

        using (var provider = Provider(
            wire,
            binding
        )) {
            operation = provider.CreateOperation("death/1");
            running = await provider.ExecuteAsync(
                operation,
                Cancel
            );
        }
        Assert.Equal(
            WorldExternalOperationStatus.Running,
            running.Status
        );
        var receipt = AzureResourceOperationReceipt.Parse(result: running.Result);

        Assert.Equal(
            Poll,
            receipt.PollUri
        );
        Assert.Equal(
            "17",
            receipt.RetryAfter
        );
        using var restarted = Provider(
            wire,
            binding
        );

        Assert.Equal(
            WorldExternalOperationStatus.Succeeded,
            (await restarted.ReconcileAsync(
                operation,
                running,
                Cancel
            )).Status
        );
        Assert.Equal(
            new[] { "DELETE", "GET" },
            wire.Requests.Select(selector: r => r.Method)
        );
        Assert.Equal(
            Poll,
            wire.Requests[1].Uri.AbsoluteUri
        );
        Assert.Null(@object: wire.Requests[1].IfMatch);
        Assert.Equal(
            "",
            wire.Requests[1].Body
        );
    }
    [InlineData("Failed", WorldExternalOperationStatus.Failed)]
    [InlineData("Canceled", WorldExternalOperationStatus.Failed)]
    [InlineData("InProgress", WorldExternalOperationStatus.Running)]
    [Theory]
    public async Task StatusEndpointDefinesOutcomeRegardlessOfHttp200(string state, WorldExternalOperationStatus expected) {
        using var wire = new Wire(
            Reply(
                202,
                headers: [("Azure-AsyncOperation", Poll)]
            ),
            Reply(
                200,
                (("{\"status\":\"" + state) + "\"}")
            )
        );
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/status");
        var running = await provider.ExecuteAsync(
            operation,
            Cancel
        );

        Assert.Equal(
            expected,
            (await provider.ReconcileAsync(
                operation,
                running,
                Cancel
            )).Status
        );
    }
    [InlineData("https://untrusted.example/status")]
    [InlineData("http://management.azure.com/status")]
    [InlineData("https://management.azure.com:444/status")]
    [InlineData("https://user@management.azure.com/status")]
    [InlineData("https://management.azure.com/status#fragment")]
    [Theory]
    public async Task UntrustedStatusAddressesAreNeverFollowed(string uri) {
        using var wire = new Wire(Reply(
            202,
            headers: [("Azure-AsyncOperation", uri)]
        ));
        using var provider = Provider(wire);
        var operation = provider.CreateOperation("death/4");
        var result = await provider.ExecuteAsync(
            operation,
            Cancel
        );

        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            result.Status
        );
        Assert.Null(@object: AzureResourceOperationReceipt.Parse(result: result.Result).PollUri);
        Assert.Equal(
            WorldExternalOperationStatus.Unknown,
            (await provider.ReconcileAsync(
                operation,
                result,
                Cancel
            )).Status
        );
        Assert.Single(collection: wire.Requests);
    }

    private sealed record Sent(string Method, Uri Uri, string Body, string? Authorization, string? IfMatch, string ClientRequestId);
    private sealed class Wire : HttpMessageHandler {
        private readonly Queue<HttpResponseMessage> m_replies;

        public List<Sent> Requests { get; } = [];
        public Credential Credential { get; } = new();

        public HttpClient Client { get; }

        public Wire(params HttpResponseMessage[] replies) {
            m_replies = new(collection: replies);
            Client = new HttpClient(
                this,
                disposeHandler: false
            );
        }

        protected override void Dispose(bool disposing) {
            if (disposing) { Client.Dispose(); foreach (var reply in m_replies) { reply.Dispose(); } }
            base.Dispose(disposing: disposing);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(item: new(
                request.Method.Method,
                request.RequestUri!,
                ((request.Content is null)
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken: cancellationToken)),
                request.Headers.Authorization?.ToString(),
                request.Headers.IfMatch.FirstOrDefault()?.ToString(),
                (request.Headers.TryGetValues(
                    name: "x-ms-client-request-id",
                    values: out var ids
                )
                ? ids.Single()
                : "")
            ));
            return m_replies.Dequeue();
        }
    }
    private sealed class Credential : TokenCredential {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) {
            Scopes.AddRange(collection: requestContext.Scopes);
            return new(
                accessToken: "test-token",
                expiresOn: DateTimeOffset.UtcNow.AddHours(hours: 1)
            );
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result: GetToken(
                cancellationToken: cancellationToken,
                requestContext: requestContext
            ));
    }
}
