using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Embeddings;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class OpenAiEmbeddingGeneratorTests {
    [Fact]
    public async Task RequestShapeAndAuthorizationHeaderMatchSpecificationAsync() {
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
            ApiKey = "sk-secret-test-key",
            ApiKeyHeader = "Authorization",
            Dimensions = 3,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
            OmitDimensions = false,
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var result = await generator.GenerateAsync(values: ["hello world"], cancellationToken: TestContext.Current.CancellationToken);

        await serverTask;

        Assert.Single(collection: result);
        Assert.Equal(expected: "Bearer sk-secret-test-key", actual: receivedHeader);
        Assert.NotNull(@object: receivedBody);
        Assert.Equal(expected: "test-model", actual: receivedBody["model"]?.ToString());
        Assert.Equal(expected: 3, actual: receivedBody["dimensions"]?.GetValue<int>());
    }

    [Fact]
    public async Task CustomApiKeyHeaderSendsRawKeyAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var receivedCustomHeader = "";
        var authHeaderPresent = true;

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            receivedCustomHeader = context.Request.Headers["api-key"] ?? "";
            authHeaderPresent = (context.Request.Headers["Authorization"] is not null);

            var responseJson = """
            {
              "object": "list",
              "data": [
                {
                  "object": "embedding",
                  "index": 0,
                  "embedding": [0.5, -0.5]
                }
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
            ApiKey = "raw-api-key-xyz",
            ApiKeyHeader = "api-key",
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);
        var result = await generator.GenerateAsync(values: ["query"], cancellationToken: TestContext.Current.CancellationToken);

        await serverTask;

        Assert.Single(collection: result);
        Assert.Equal(expected: "raw-api-key-xyz", actual: receivedCustomHeader);
        Assert.False(condition: authHeaderPresent);
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
            ApiKey = "key",
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
    public async Task ProviderFailureRedactsApiKeyAndTruncatesMessageAsync() {
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(uriPrefix: prefix);
        listener.Start();

        var serverTask = Task.Run(
            cancellationToken: TestContext.Current.CancellationToken,
            function: async () => {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = 400;
            var errorBody = new string(c: 'E', count: 1000);
            var bytes = Encoding.UTF8.GetBytes(s: $"{{\"error\": \"super-secret-key-12345 {errorBody}\"}}");
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(buffer: bytes.AsMemory(), cancellationToken: TestContext.Current.CancellationToken);
            context.Response.Close();
        });

        var options = new OpenAiEmbeddingOptions {
            ApiKey = "super-secret-key-12345",
            Dimensions = 2,
            Endpoint = new Uri(uriString: prefix),
            Model = "test-model",
        };

        using var generator = OpenAiEmbeddingGeneratorFactory.Create(options: options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            testCode: async () => await generator.GenerateAsync(values: ["test"], cancellationToken: TestContext.Current.CancellationToken)
        );

        await serverTask;

        Assert.DoesNotContain(expectedSubstring: "super-secret-key-12345", actualString: ex.Message);
        Assert.True(condition: ex.Message.Length <= 512);
    }

    private static int GetFreePort() {
        using var tcp = new System.Net.Sockets.TcpListener(localaddr: IPAddress.Loopback, port: 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
