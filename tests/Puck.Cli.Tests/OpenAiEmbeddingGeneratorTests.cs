using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Puck.Embeddings;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class OpenAiEmbeddingGeneratorTests {
    [Fact]
    public async Task RequestShapeAndIdentityBearerTokenMatchSpecificationAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var receivedHeader = "";
        JsonObject? receivedBody = null;

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            receivedHeader = context.Request.Headers["Authorization"] ?? "";

            using var reader = new StreamReader(stream: context.Request.InputStream, encoding: Encoding.UTF8);
            var bodyText = await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
            receivedBody = JsonSerializer.Deserialize<JsonObject>(json: bodyText);

            var responseJson = """
            {
              "object": "list",
              "data": [
                {
                  "object": "embedding",
                  "index": 0,
                  "embedding": [0.1, 0.2, 0.3]
                }
              ],
              "model": "test-model",
              "usage": { "prompt_tokens": 5, "total_tokens": 5 }
            }
            """;
            var bytes = Encoding.UTF8.GetBytes(s: responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: "test-identity-token"),
            Dimensions = 3,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
            OmitDimensions = false,
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var result = await generator.GenerateAsync(values: ["hello world"], cancellationToken: TestContext.Current.CancellationToken);

        await serverTask;

        Assert.Single(collection: result);
        Assert.Equal(expected: "Bearer test-identity-token", actual: receivedHeader);
        Assert.NotNull(@object: receivedBody);
        Assert.Equal(expected: 3, actual: receivedBody["dimensions"]?.GetValue<int>());
    }

    [Fact]
    public async Task OmitDimensionsOmitsFieldInPayloadAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        JsonObject? receivedBody = null;

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(stream: context.Request.InputStream, encoding: Encoding.UTF8);
            var bodyText = await reader.ReadToEndAsync(cancellationToken: TestContext.Current.CancellationToken);
            receivedBody = JsonSerializer.Deserialize<JsonObject>(json: bodyText);

            var responseJson = """
            {
              "object": "list",
              "data": [
                { "object": "embedding", "index": 0, "embedding": [0.1, 0.2] }
              ],
              "model": "test-model"
            }
            """;
            var bytes = Encoding.UTF8.GetBytes(s: responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: "mock-token"),
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
            OmitDimensions = true,
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        await generator.GenerateAsync(values: ["hello"], cancellationToken: TestContext.Current.CancellationToken);

        await serverTask;

        Assert.NotNull(@object: receivedBody);
        Assert.False(condition: receivedBody.ContainsKey(propertyName: "dimensions"));
    }

    [Fact]
    public void FactoryCreatesGeneratorWithDefaultAzureCredentialWhenNoneProvided() {
        var options = new OpenAiEmbeddingOptions {
            Dimensions = 128,
            Endpoint = new Uri(uriString: "https://example-resource.openai.azure.com/"),
            Model = "text-embedding-3-small",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        Assert.NotNull(@object: generator);
    }

    [Fact]
    public async Task GeneratorThrowsOnCountMismatchAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            var responseJson = """
            {
              "object": "list",
              "data": [
                { "object": "embedding", "index": 0, "embedding": [0.1, 0.2] }
              ],
              "model": "test-model"
            }
            """;
            var bytes = Encoding.UTF8.GetBytes(s: responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: "test-token"),
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            testCode: () => generator.GenerateAsync(values: ["first", "second"], cancellationToken: TestContext.Current.CancellationToken)
        );
        await serverTask;

        Assert.Contains(expectedSubstring: "Answer count mismatch", actualString: ex.Message);
    }

    [Fact]
    public async Task GeneratorThrowsOnLengthMismatchAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            var responseJson = """
            {
              "object": "list",
              "data": [
                { "object": "embedding", "index": 0, "embedding": [0.1, 0.2, 0.3] }
              ],
              "model": "test-model"
            }
            """;
            var bytes = Encoding.UTF8.GetBytes(s: responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: "test-token"),
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            testCode: () => generator.GenerateAsync(values: ["test"], cancellationToken: TestContext.Current.CancellationToken)
        );
        await serverTask;

        Assert.Contains(expectedSubstring: "Embedding vector length mismatch", actualString: ex.Message);
    }

    [Fact]
    public async Task GeneratorOrdersOutOfOrderIndexesAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            var responseJson = """
            {
              "object": "list",
              "data": [
                { "object": "embedding", "index": 1, "embedding": [0.3, 0.4] },
                { "object": "embedding", "index": 0, "embedding": [0.1, 0.2] }
              ],
              "model": "test-model"
            }
            """;
            var bytes = Encoding.UTF8.GetBytes(s: responseJson);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: "test-token"),
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var result = await generator.GenerateAsync(values: ["first", "second"], cancellationToken: TestContext.Current.CancellationToken);
        await serverTask;

        var list = result.ToList();
        Assert.Equal(expected: 2, actual: list.Count);
        Assert.Equal(expected: 0.1f, actual: list[0].Vector.Span[0], precision: 4);
        Assert.Equal(expected: 0.3f, actual: list[1].Vector.Span[0], precision: 4);
    }

    [Fact]
    public async Task Generator500ResponseHoldsStatusAndBodyWithoutTokenAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var secretToken = "super-secret-azure-token-xyz123";

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            var errorBody = "Internal server error occurred while processing model weights.";
            var bytes = Encoding.UTF8.GetBytes(s: errorBody);
            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/plain";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            Credential = new MockTokenCredential(token: secretToken),
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            MaxRetries = 0,
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            testCode: () => generator.GenerateAsync(values: ["hello"], cancellationToken: TestContext.Current.CancellationToken)
        );
        await serverTask;

        Assert.Contains(expectedSubstring: "500", actualString: ex.Message);
        Assert.Contains(expectedSubstring: "Internal server error", actualString: ex.Message);
        Assert.DoesNotContain(expectedSubstring: secretToken, actualString: ex.Message);
    }

    [Fact]
    public void BatchSplittingProducesCorrectChunks() {
        var items = new[] { "item1", "item2", "item3", "item4", "item5" };
        var batches = EmbeddingBatcher.Batch(batchSize: 2, items: items).ToList();

        Assert.Equal(expected: 3, actual: batches.Count);
        Assert.Equal(expected: 2, actual: batches[0].Count);
        Assert.Equal(expected: 2, actual: batches[1].Count);
        Assert.Single(collection: batches[2]);
    }

    private sealed class MockTokenCredential : TokenCredential {
        private readonly string m_token;

        public MockTokenCredential(string token) {
            m_token = token;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(m_token, DateTimeOffset.UtcNow.AddHours(hours: 1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(result: new AccessToken(m_token, DateTimeOffset.UtcNow.AddHours(hours: 1)));
    }

    private static int GetFreePort() {
        using var tcp = new System.Net.Sockets.TcpListener(localaddr: IPAddress.Loopback, port: 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
