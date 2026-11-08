using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;

namespace Puck.World.Azure;

/// <summary>The one authenticated ARM pipeline and bounded response gate every Azure resource adapter reads and writes
/// through: an HTTPS ARM origin, a default transport that never follows redirects, SDK logging and tracing off, an
/// unbuffered JSON request, and a response body read under a byte budget before it is parsed.</summary>
/// <remarks>A remote body never reaches the JSON parser past its budget: <see cref="ReadJsonAsync"/> refuses as soon
/// as the bytes read exceed it, and it is the only reader the adapters hold.</remarks>
internal sealed class AzureArmPipeline : IDisposable {
    private readonly HttpClient? m_httpClient;

    /// <summary>Initializes a new instance of the <see cref="AzureArmPipeline"/> class. No credential lookup or
    /// network call occurs.</summary>
    /// <param name="credential">The host-owned credential.</param>
    /// <param name="environment">The ARM cloud whose endpoint is the origin.</param>
    /// <param name="scope">The resource the pipeline is authorized against.</param>
    /// <param name="maximumRetries">The SDK's automatic retry count; zero for a mutation that must never resend.</param>
    /// <param name="transport">The trusted transport, which must not follow redirects and whose lifetime belongs to
    /// the caller; <see langword="null"/> creates an owned transport that does not follow redirects.</param>
    /// <exception cref="ArgumentException">The cloud's endpoint is absent or not an HTTPS origin.</exception>
    internal AzureArmPipeline(TokenCredential credential, ArmEnvironment environment, ResourceIdentifier scope,
        int maximumRetries, HttpPipelineTransport? transport) {
        var origin = environment.Endpoint;

        if (
            (origin is null) ||
            (origin.Scheme != Uri.UriSchemeHttps) ||
            (origin.UserInfo.Length != 0) ||
            (origin.AbsolutePath != "/") ||
            (origin.Query.Length != 0) ||
            (origin.Fragment.Length != 0)
        ) {
            throw new ArgumentException(
                message: "The ARM endpoint must be an HTTPS origin.",
                paramName: nameof(environment)
            );
        }
        Origin = origin;
        if (transport is null) {
            m_httpClient = new HttpClient(handler: new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(client: m_httpClient);
        }
        var options = new ArmClientOptions { Environment = environment, Transport = transport };

        options.Retry.MaxRetries = maximumRetries;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        Pipeline = new ScopedResource(
            client: new ArmClient(
                credential,
                defaultSubscriptionId: null,
                options
            ),
            scope: scope
        ).RequestPipeline;
    }

    /// <summary>Gets the validated HTTPS ARM origin.</summary>
    internal Uri Origin { get; }
    /// <summary>Gets the authenticated request pipeline.</summary>
    internal HttpPipeline Pipeline { get; }

    /// <summary>Creates an unbuffered request that accepts JSON.</summary>
    /// <param name="uri">The absolute request URI.</param>
    /// <param name="method">The HTTP method.</param>
    /// <returns>The message, owned by the caller.</returns>
    internal HttpMessage CreateJsonRequest(Uri uri, RequestMethod method) {
        var message = Pipeline.CreateMessage();

        message.BufferResponse = false;
        message.Request.Method = method;
        message.Request.Uri.Reset(value: uri);
        message.Request.Headers.SetValue(
            name: "Accept",
            value: "application/json"
        );
        return message;
    }
    /// <summary>Reads a JSON document with one GET, refusing any status but 200 and any body past its budget.</summary>
    /// <param name="uri">The absolute request URI.</param>
    /// <param name="maximumBytes">The largest body, in bytes, that is parsed.</param>
    /// <param name="subject">The adapter's name in refusal messages.</param>
    /// <param name="cancellationToken">The token that cancels the request and the read.</param>
    /// <returns>The parsed document, owned by the caller.</returns>
    /// <exception cref="InvalidOperationException">The response status is not 200.</exception>
    /// <exception cref="IOException">The body exceeds <paramref name="maximumBytes"/>.</exception>
    /// <exception cref="JsonException">The body is absent, empty or not JSON.</exception>
    internal async ValueTask<JsonDocument> GetJsonAsync(Uri uri, int maximumBytes, string subject, CancellationToken cancellationToken) {
        using var message = CreateJsonRequest(
            method: RequestMethod.Get,
            uri: uri
        );

        await Pipeline.SendAsync(
            cancellationToken: cancellationToken,
            message: message
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (message.Response.Status != 200) { throw new InvalidOperationException(message: (subject + " HTTP " + message.Response.Status)); }
        return ((await ReadJsonAsync(
            cancellationToken: cancellationToken,
            maximumBytes: maximumBytes,
            response: message.Response
        ).ConfigureAwait(continueOnCapturedContext: false)) ?? throw new JsonException(message: (subject + " response has no body.")));
    }
    /// <summary>Reads a response body under a byte budget and parses it as JSON.</summary>
    /// <param name="response">The response whose content stream is read to its end.</param>
    /// <param name="maximumBytes">The largest body, in bytes, that is parsed.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The parsed document, owned by the caller; <see langword="null"/> when the response has no body or
    /// an empty one.</returns>
    /// <exception cref="IOException">The body exceeds <paramref name="maximumBytes"/>.</exception>
    /// <exception cref="JsonException">The body is not JSON.</exception>
    internal static async ValueTask<JsonDocument?> ReadJsonAsync(Response response, int maximumBytes, CancellationToken cancellationToken) {
        if (response.ContentStream is not { } stream) { return null; }
        using var bytes = new MemoryStream();
        var buffer = new byte[Math.Min(
            val1: 8192,
            val2: maximumBytes
        )];
        int count;

        while ((count = await stream.ReadAsync(
            buffer: buffer,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false)) != 0) {
            if ((bytes.Length + count) > maximumBytes) { throw new IOException(message: "Azure response exceeds its byte budget."); }
            bytes.Write(
                buffer: buffer,
                count: count,
                offset: 0
            );
        }
        // The document keeps the stream's backing array alive after the stream is disposed.
        return ((bytes.Length == 0)
            ? null
            : JsonDocument.Parse(bytes.GetBuffer().AsMemory(
                0,
                checked((int)bytes.Length)
            )));
    }
    /// <summary>Disposes the owned default transport; a supplied transport and the credential remain the host's.</summary>
    public void Dispose() => m_httpClient?.Dispose();

    // ArmResource exposes the authenticated pipeline to resource implementations. The typed GenericResource methods
    // cannot express arbitrary POST actions, bounded unbuffered reads or every provider-specific JSON field.
    private sealed class ScopedResource(ArmClient client, ResourceIdentifier scope) : ArmResource(
        client,
        scope
    ) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}
