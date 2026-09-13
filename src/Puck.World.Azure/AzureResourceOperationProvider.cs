using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>Executes a host-bound generic ARM operation through the Azure SDK's authenticated resource pipeline.</summary>
/// <remarks>Use through WorldExternalOperationDispatcher. The adapter makes no automatic transient retries,
/// does not follow redirects, and reconciles only a saved operation-status URL. It never infers that a lost
/// mutation succeeded from the current resource's existence or provisioning state. Credentials are host-owned.</remarks>
public sealed partial class AzureResourceOperationProvider : IWorldExternalOperationProvider, IDisposable {
    private readonly AzureResourceBinding m_binding;
    private readonly HttpPipeline m_pipeline;
    private readonly Uri m_endpoint;
    private readonly Uri m_requestUri;
    private readonly HttpClient? m_httpClient;
    private readonly int m_maximumRequestBytes;
    private readonly int m_maximumResponseBytes;

    /// <summary>Creates an opt-in resource binding. No credential lookup or network call occurs at construction.</summary>
    /// <param name="credential">The host's scoped Azure credential.</param>
    /// <param name="binding">The immutable resource, operation, and incarnation selection.</param>
    /// <param name="environment">The host's cloud; null selects Azure public cloud.</param>
    /// <param name="maximumRequestBytes">Maximum UTF-8 JSON request size.</param>
    /// <param name="maximumResponseBytes">Maximum response body inspected for operation status.</param>
    /// <param name="transport">Optional trusted transport for hosting or tests. It must not retry or redirect;
    /// its lifetime belongs to the caller. The default transport disables automatic redirects.</param>
    /// <exception cref="ArgumentException">The binding or cloud endpoint is invalid.</exception>
    /// <exception cref="ArgumentNullException">The credential or binding is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A byte limit is not positive.</exception>
    public AzureResourceOperationProvider(TokenCredential credential, AzureResourceBinding binding,
        ArmEnvironment? environment = null, int maximumRequestBytes = 65536, int maximumResponseBytes = 1048576,
        HttpPipelineTransport? transport = null) {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRequestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        m_binding = binding;
        m_maximumRequestBytes = maximumRequestBytes;
        m_maximumResponseBytes = maximumResponseBytes;
        var cloud = (environment ?? ArmEnvironment.AzurePublicCloud);

        m_endpoint = (cloud.Endpoint ?? throw new ArgumentException(
            message: "An ARM cloud endpoint is required.",
            paramName: nameof(environment)
        ));
        if (
            (m_endpoint.Scheme != Uri.UriSchemeHttps) ||
            (m_endpoint.UserInfo.Length != 0) ||
            (m_endpoint.AbsolutePath != "/") ||
            (m_endpoint.Query.Length != 0) ||
            (m_endpoint.Fragment.Length != 0)
        ) {
            throw new ArgumentException(
                message: "The ARM endpoint must be an HTTPS origin.",
                paramName: nameof(environment)
            );
        }
        m_requestUri = new Uri(
            baseUri: m_endpoint,
            relativeUri: (binding.ValidateAndGetPath() + binding.GetQuery())
        );
        if (transport is null) {
            m_httpClient = new HttpClient(handler: new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(client: m_httpClient);
        }
        var options = new ArmClientOptions { Environment = cloud, Transport = transport };

        options.Retry.MaxRetries = 0;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        var client = new ArmClient(
            credential,
            defaultSubscriptionId: null,
            options
        );

        m_pipeline = new BoundResource(
            client: client,
            id: new ResourceIdentifier(resourceId: binding.ResourceId)
        ).RequestPipeline;
        // Length-prefixed fields prevent identities with embedded separators from colliding.
        using var bytes = new MemoryStream();

        using (var writer = new BinaryWriter(
            bytes,
            Encoding.UTF8,
            leaveOpen: true
        )) {
            foreach (var field in new[] { "puck.azure.resource.v1", binding.Name, m_requestUri.AbsoluteUri,
                cloud.DefaultScope, binding.Incarnation, binding.Method.ToString(), (binding.IfMatch ?? "") }) { writer.Write(value: field); }
        }
        Identity = Convert.ToHexString(inArray: SHA256.HashData(source: bytes.ToArray()));
    }

    /// <inheritdoc/>
    public string Identity { get; }

    /// <summary>Creates a validated request for durable commitment. It performs no service call.</summary>
    /// <param name="id">Stable authority-lineage, entity-incarnation, and request-generation id.</param>
    /// <param name="payload">A JSON object in the selected provider API schema; empty is allowed for POST or DELETE.</param>
    /// <returns>The request with this binding's name and identity pinned.</returns>
    /// <exception cref="ArgumentException">The id or payload is invalid or oversized.</exception>
    /// <exception cref="JsonException">The body is not a JSON object.</exception>
    public WorldExternalOperation CreateOperation(string id, string payload = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ValidatePayload(payload: payload);
        return new(
            id,
            m_binding.Name,
            Identity,
            payload
        );
    }
    /// <inheritdoc/>
    public async ValueTask<WorldExternalOperationResult> ExecuteAsync(WorldExternalOperation operation, CancellationToken cancellationToken) {
        CheckOperation(operation: operation);
        using var message = CreateMessage(
            m_requestUri,
            new RequestMethod(method: m_binding.Method.ToString().ToUpperInvariant())
        );
        // This is a correlation header, not Azure's promise of deduplicated execution.
        message.Request.Headers.SetValue(
            name: "x-ms-client-request-id",
            value: Convert.ToHexString(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: operation.Id)))
        );
        if (m_binding.IfMatch is { } etag) { message.Request.Headers.SetValue(
            name: "If-Match",
            value: etag
        ); }
        if (operation.Payload.Length != 0) {
            message.Request.Headers.SetValue(
                name: "Content-Type",
                value: "application/json"
            );
            message.Request.Content = RequestContent.Create(bytes: Encoding.UTF8.GetBytes(s: operation.Payload));
        }
        await m_pipeline.SendAsync(
            cancellationToken: cancellationToken,
            message: message
        ).ConfigureAwait(continueOnCapturedContext: false);
        return await InterpretAsync(
            message.Response,
            previous: null,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <inheritdoc/>
    public async ValueTask<WorldExternalOperationResult> ReconcileAsync(WorldExternalOperation operation,
        WorldExternalOperationResult previous, CancellationToken cancellationToken) {
        CheckOperation(operation: operation);
        ArgumentNullException.ThrowIfNull(previous);
        AzureResourceOperationReceipt receipt;

        try { receipt = AzureResourceOperationReceipt.Parse(result: previous.Result); } catch (JsonException) { return previous with { Status = WorldExternalOperationStatus.Unknown }; }
        if (
            (receipt.PollUri is null) ||
            (receipt.PollKind is not ("Azure-AsyncOperation" or "Operation-Location" or "Location")) ||
            !TryPollUri(
            receipt.PollUri,
            out var uri
        )
        ) {
            return previous with { Status = WorldExternalOperationStatus.Unknown };
        }
        using var message = CreateMessage(
            uri,
            RequestMethod.Get
        );

        await m_pipeline.SendAsync(
            cancellationToken: cancellationToken,
            message: message
        ).ConfigureAwait(continueOnCapturedContext: false);
        return await InterpretAsync(
            message.Response,
            receipt,
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }

    private HttpMessage CreateMessage(Uri uri, RequestMethod method) {
        var message = m_pipeline.CreateMessage();

        message.BufferResponse = false;
        message.Request.Method = method;
        message.Request.Uri.Reset(value: uri);
        message.Request.Headers.SetValue(
            name: "Accept",
            value: "application/json"
        );
        return message;
    }
    private void CheckOperation(WorldExternalOperation operation) {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Id);
        if (
            (operation.Binding != m_binding.Name) ||
            (operation.BindingIdentity != Identity)
        ) {
            throw new InvalidOperationException(message: "The Azure operation does not match this resource binding.");
        }
        ValidatePayload(payload: operation.Payload);
    }
    private void ValidatePayload(string payload) {
        ArgumentNullException.ThrowIfNull(payload);
        if (Encoding.UTF8.GetByteCount(s: payload) > m_maximumRequestBytes) { throw new ArgumentException(
            message: "Azure request exceeds its byte budget.",
            paramName: nameof(payload)
        ); }
        if (
            (payload.Length == 0) &&
            (m_binding.Method is AzureResourceMethod.Post or AzureResourceMethod.Delete)
        ) { return; }
        using var body = JsonDocument.Parse(payload);

        if (body.RootElement.ValueKind != JsonValueKind.Object) { throw new JsonException(message: "ARM request body must be a JSON object."); }
    }
    private bool TryPollUri(string value, out Uri uri) {
        if (
            Uri.TryCreate(
            baseUri: m_requestUri,
            relativeUri: value,
            result: out var candidate
        ) &&
            (candidate.Scheme == Uri.UriSchemeHttps) &&
            candidate.Authority.Equals(
            m_endpoint.Authority,
            StringComparison.OrdinalIgnoreCase
        ) &&
            (candidate.UserInfo.Length == 0) &&
            (candidate.Fragment.Length == 0)
        ) {
            uri = candidate;
            return true;
        }
        uri = m_endpoint;
        return false;
    }

    /// <summary>Disposes the owned default HTTP client. The host retires its recorded runtime separately.</summary>
    public void Dispose() => m_httpClient?.Dispose();

    // ArmResource exposes the authenticated pipeline to resource implementations. The typed GenericResource
    // CRUD methods cannot express arbitrary POST actions or preserve every provider-specific JSON field.
    private sealed class BoundResource(ArmClient client, ResourceIdentifier id) : ArmResource(
        client,
        id
    ) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}
