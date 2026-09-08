using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureResourceInventoryTests {
    private const string Group = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck";
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/..")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/%2e%2e")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck?next=other")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck/providers/other/type")]
    public void ScopeRejectsUriSyntaxAndTraversalBeforeServiceAccess(string group) {
        using var wire = new Wire();
        Assert.Throws<ArgumentException>(() => Source(wire, group: group));
        Assert.Empty(wire.Uris);
    }
    [Fact]
    public async Task PagesAreCompleteFilteredAndDisclosedThroughAnAllowlist() {
        using var wire = new Wire(Page(["bytrcstp000", "bytrcstp001"], "https://management.azure.com" + Group + "/resources?skip=2"), Page(["other", "bytrcstp002"]));
        using var source = Source(wire);
        var items = await source.ReadAsync(Cancel);
        Assert.Equal(new[] { "bytrcstp001", "bytrcstp002" }, items.Select(item => item.Key));
        Assert.All(items, item => Assert.Equal(new[] { "name", "region" }, item.Fields.Keys));
        Assert.All(wire.Methods, method => Assert.Equal("GET", method));
        Assert.Equal(2, wire.Uris.Count);
        Assert.Contains("Microsoft.Storage%2FstorageAccounts", wire.Uris[0].Query);
    }

    [Theory]
    [InlineData("https://other.example/resources")]
    [InlineData("https://management.azure.com/another/resources")]
    [InlineData("http://management.azure.com/resources")]
    [InlineData("https://user@management.azure.com/resources")]
    public async Task ContinuationsCannotLeaveApprovedOriginOrCollection(string next) {
        using var wire = new Wire(Page(["bytrcstp001"], next));
        using var source = Source(wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Cancel).AsTask());
        Assert.Single(wire.Uris);
    }

    [Fact]
    public async Task ItemAndPageLimitsRefuseInsteadOfPublishingTruncatedCollections() {
        using var wire = new Wire(Page(["bytrcstp001", "bytrcstp002"]));
        using var source = Source(wire, maximumItems: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Cancel).AsTask());
        using var pageWire = new Wire(Page(["bytrcstp001"], "https://management.azure.com" + Group + "/resources?skip=2"));
        using var pageSource = Source(pageWire, maximumPages: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pageSource.ReadAsync(Cancel).AsTask());
        Assert.Single(pageWire.Uris);
    }

    [Fact]
    public async Task DuplicatesAndOutOfScopeResourcesRefuseTheWholeRead() {
        using var wire = new Wire(Page(["bytrcstp001", "bytrcstp001"]));
        using var source = Source(wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Cancel).AsTask());
        using var wrongGroup = new Wire(Page(["bytrcstp001"]).Replace("resourceGroups/puck", "resourceGroups/other", StringComparison.Ordinal));
        using var wrongSource = Source(wrongGroup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongSource.ReadAsync(Cancel).AsTask());
    }

    private static AzureResourceInventory Source(Wire wire, int maximumItems = 8, int maximumPages = 16, string group = Group) {
        using var settings = JsonDocument.Parse($$"""
            {"resourceGroup":"{{group}}","resourceType":"Microsoft.Storage/storageAccounts","apiVersion":"2021-04-01",
             "fields":{"name":"/name","region":"/location"},"namePrefix":"bytrcstp","excludeNames":["bytrcstp000"],"maximumPages":{{maximumPages}}}
            """);
        return new(new Credential(), ArmEnvironment.AzurePublicCloud, settings.RootElement, maximumItems, new HttpClientTransport(wire.Client));
    }
    private static string Page(string[] names, string? next = null) {
        var values = names.Select(name => "{\"id\":\"" + Group + "/providers/Microsoft.Storage/storageAccounts/" + name +
            "\",\"name\":\"" + name + "\",\"type\":\"Microsoft.Storage/storageAccounts\",\"location\":\"southcentralus\",\"secret\":\"never-project\"}");
        return "{\"value\":[" + string.Join(',', values) + "]" + (next is null ? "}" : ",\"nextLink\":\"" + next + "\"}");
    }
    private sealed class Wire(params string[] replies) : HttpMessageHandler {
        private readonly Queue<string> m_replies = new(replies);
        public List<string> Methods { get; } = [];
        public List<Uri> Uris { get; } = [];
        public HttpClient Client => m_client ??= new(this, disposeHandler: false);
        private HttpClient? m_client;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Methods.Add(request.Method.Method); Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(m_replies.Dequeue(), Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing) { if (disposing) { m_client?.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class Credential : TokenCredential {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
