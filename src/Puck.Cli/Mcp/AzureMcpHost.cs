using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using ModelContextProtocol.Protocol;
using Puck.Hosting;
using Puck.Mcp;
using Puck.World.Azure;
using Puck.World.Server;

namespace Puck.Cli.Mcp;

// Optional distribution glue: neither Puck.Mcp nor the silo references this Azure integration.
internal sealed class AzureMcpHost : RemoteMcpHost, IDisposable {
    private readonly IControlSessionHost m_host;
    private readonly string m_target;
    private readonly AzureDelegatedObservations m_services;
    private static readonly Tool ObserveTool = new() {
        Name = "puck_service_observe",
        Description = "Read a host-configured Azure inventory or metrics observation on behalf of the signed-in caller. Requires a separate per-observation grant and downstream consent. Only approved fields are returned. No World mutation or cloud write occurs.",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"observation":{"type":"string","minLength":1,"maxLength":128}},"required":["observation"],"additionalProperties":false}"""),
        OutputSchema = JsonElement.Parse("""{"type":"object","properties":{"observation":{"type":"string"},"items":{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"fields":{"type":"object","additionalProperties":{"type":"string"}}},"required":["key","fields"],"additionalProperties":false}}},"required":["observation","items"],"additionalProperties":false}"""),
        Annotations = new() { ReadOnlyHint = true, DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true },
    };
    public AzureMcpHost(IControlSessionHost host, RemoteMcpOptions options) {
        if (options.SubjectClaim != "oid" || options.TenantId is null ||
            options.Issuer != $"https://login.microsoftonline.com/{options.TenantId}/v2.0") {
            throw new ArgumentException("Azure delegated observations require a tenant-specific Entra public-cloud issuer and oid subjects.");
        }
        m_host = host;
        m_target = options.Target!;
        m_services = new(options.TenantId, options.Audience, options.Services!.Value);
    }
    public override bool IsReady => m_host.IsReady(m_target);
    public void Dispose() => m_services.Dispose();
    public override ValueTask<IControlSession> AttachAsync(string subject, CancellationToken cancellationToken) => m_host.AttachAsync(m_target, cancellationToken);
    public override IReadOnlyList<Tool> ServiceTools => [ObserveTool];
    public override async ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
        if (request.Name != ObserveTool.Name || request.Arguments is not { Count: 1 } arguments ||
            !arguments.TryGetValue("observation", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() is not { Length: > 0 and <= 128 } observation) {
            return Failure("One configured observation name is required.");
        }
        if (!IsReady) { return Failure("The configured host target is unavailable."); }
        try {
            var items = await m_services.ReadAsync(observation, caller.Subject, caller.UserAssertion, caller.ExpiresAt, cancellationToken).ConfigureAwait(false);
            var content = JsonSerializer.SerializeToElement(new AzureMcpObservationResult(observation, items), AzureMcpJson.Default.AzureMcpObservationResult);
            return new() { StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
        } catch (AuthenticationFailedException) {
            return Failure("Delegated authentication failed. Sign in again and verify downstream consent; host credentials are never substituted.");
        } catch (UnauthorizedAccessException) {
            return Failure("This caller has no grant for that observation.");
        } catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or JsonException or global::Azure.RequestFailedException) {
            return Failure("The delegated observation failed or exceeded its disclosure budget. No partial snapshot is returned.");
        }
    }
    private static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}

internal sealed record AzureMcpObservationResult(string Observation, IReadOnlyList<WorldExtensionObservationItem> Items);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AzureMcpObservationResult))]
internal sealed partial class AzureMcpJson : JsonSerializerContext;
