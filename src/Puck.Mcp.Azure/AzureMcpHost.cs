using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Azure.Identity;
using ModelContextProtocol.Protocol;
using Puck.Hosting;
using Puck.World.Azure;
using Puck.World.Server;

namespace Puck.Mcp.Azure;

/// <summary>The Azure services host: <c>puck_onboard</c> and <c>puck_service_observe</c> over
/// <see cref="AzureDelegatedServices"/>, with every Entra exchange performed before dispatch. Without a target it is a
/// service-only host and offers no Console attachment.</summary>
internal sealed class AzureMcpHost : RemoteMcpHost, IDisposable {
    private readonly IControlSessionHost m_host;
    private readonly AzureDelegatedServices m_services;
    private readonly string? m_target;

    private static readonly Tool OnboardTool = new() {
        Name = "puck_onboard",
        Description = "Ensure your existing Puck account is provisioned, depositing fresh delegated user escrow through the platform onboarding API. Returns Ready, Migrating, or Onboarding. Retry explicitly while Onboarding; no duplicate user provisioning is performed here.",
        InputSchema = JsonElement.Parse("""{"type":"object","additionalProperties":false}"""),
        OutputSchema = JsonElement.Parse("""{"type":"object","properties":{"state":{"enum":["Ready","Migrating","Onboarding"]}},"required":["state"],"additionalProperties":false}"""),
        Annotations = new() { DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true, ReadOnlyHint = false },
    };
    private static readonly Tool ObserveTool = new() {
        Name = "puck_service_observe",
        Description = "Read a host-configured Azure inventory or metrics observation on behalf of the signed-in caller. Requires a separate per-observation grant and downstream consent. Only approved fields are returned. No World mutation or cloud write occurs.",
        InputSchema = JsonElement.Parse("""{"type":"object","properties":{"observation":{"type":"string","minLength":1,"maxLength":128}},"required":["observation"],"additionalProperties":false}"""),
        OutputSchema = JsonElement.Parse("""{"type":"object","properties":{"observation":{"type":"string"},"items":{"type":"array","items":{"type":"object","properties":{"key":{"type":"string"},"fields":{"type":"object","additionalProperties":{"type":"string"}}},"required":["key","fields"],"additionalProperties":false}}},"required":["observation","items"],"additionalProperties":false}"""),
        Annotations = new() { DestructiveHint = false, IdempotentHint = true, OpenWorldHint = true, ReadOnlyHint = true },
    };

    public AzureMcpHost(IControlSessionHost host, RemoteMcpOptions options) {
        if (
            (options.SubjectClaim != "oid") ||
            (options.TenantId is null) ||
            (options.Issuer != $"https://login.microsoftonline.com/{options.TenantId}/v2.0")
        ) {
            throw new ArgumentException(message: "Azure delegated observations require a tenant-specific Entra public-cloud issuer and oid subjects.");
        }
        m_host = host;
        m_target = options.Target;
        m_services = new(
            options.TenantId,
            options.Audience,
            options.Services!.Value
        );
    }

    public override bool IsReady => ((m_target is null) || m_host.IsReady(target: m_target));
    public override IReadOnlyList<Tool> ServiceTools => [.. (m_services.SupportsOnboarding
        ? new[] { OnboardTool }
        : []), .. ((m_services.Names.Count > 0)
        ? new[] { ObserveTool }
        : [])];
    public override bool SupportsAttachments => (m_target is not null);

    // The authorization state of a call whose exchange failed without a user-interaction challenge.
    private static readonly object ExchangeFailed = new();

    // The single observation name a well-formed observe call carries, or null.
    private static string? Observation(CallToolRequestParams request) => (((request.Arguments is { Count: 1 } arguments) &&
        arguments.TryGetValue(
        key: "observation",
        value: out var name
    ) &&
        (name.ValueKind == JsonValueKind.String) &&
        (name.GetString() is { Length: > 0 and <= 128 } observation))
        ? observation
        : null
    );
    private static CallToolResult Failure(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

    public override async ValueTask<IControlSession> AttachAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) {
        if (m_target is not { } target) { throw new InvalidOperationException(message: "This services host has no Console target."); }
        if (m_services.SupportsOnboarding) {
            if (caller.Authorization is not AzureDelegatedGrant grant) { throw new UnauthorizedAccessException(message: "Platform onboarding failed; sign in and verify consent before attaching."); }
            try {
                var state = await m_services.EnsureOnboardedAsync(
                    cancellationToken: cancellationToken,
                    grant: grant
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (state == "Onboarding") { throw new UnauthorizedAccessException(message: "Account provisioning is still running. Use puck_onboard to check it."); }
            } catch (AzureDelegatedAuthenticationException challenge) {
                throw new RemoteMcpAuthorizationException(claims: challenge.Claims);
            } catch (Exception error) when ((error is AuthenticationFailedException or HttpRequestException or JsonException or InvalidDataException)) {
                throw new UnauthorizedAccessException(message: "Platform onboarding failed; sign in and verify consent before attaching.");
            }
        }
        return await m_host.AttachAsync(
            target,
            new(
                Issuer: caller.Issuer,
                Subject: caller.Subject
            ),
            cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false);
    }
    // Every Entra exchange happens here, before dispatch, so an interaction requirement is always an HTTP challenge.
    // A downstream API that refuses the exchanged token after dispatch, as continuous access evaluation does on
    // revocation, fails that call and challenges the caller's next request.
    public override async ValueTask<object?> AuthorizeAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
        try {
            if (
                ((request.Name is "puck_attach") || (request.Name == OnboardTool.Name)) &&
                m_services.SupportsOnboarding
            ) {
                return await m_services.ExchangeOnboardingAsync(
                    caller.UserAssertion,
                    caller.ExpiresAt,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
            if (
                (request.Name == ObserveTool.Name) &&
                IsReady &&
                (Observation(request: request) is { } observation) &&
                m_services.NamesFor(subject: caller.Subject).Contains(
                observation,
                StringComparer.Ordinal
            )
            ) {
                return await m_services.ExchangeObservationAsync(
                    observation,
                    caller.Subject,
                    caller.UserAssertion,
                    caller.ExpiresAt,
                    cancellationToken
                ).ConfigureAwait(continueOnCapturedContext: false);
            }
        } catch (AzureDelegatedAuthenticationException challenge) {
            throw new RemoteMcpAuthorizationException(claims: challenge.Claims);
        } catch (AuthenticationFailedException) {
            return ExchangeFailed;
        }
        return null;
    }
    public override async ValueTask<CallToolResult> CallServiceAsync(RemoteMcpCaller caller, CallToolRequestParams request, CancellationToken cancellationToken) {
        if (
            (request.Name == OnboardTool.Name) &&
            m_services.SupportsOnboarding
        ) {
            if (request.Arguments is { Count: > 0 }) { return Failure(message: "Onboarding takes no arguments; identity comes from your validated access token."); }
            if (caller.Authorization is not AzureDelegatedGrant onboarding) { return Failure(message: "Platform onboarding failed. Sign in again and verify downstream consent; host credentials are never substituted."); }
            try {
                var state = await m_services.EnsureOnboardedAsync(
                    cancellationToken: cancellationToken,
                    grant: onboarding
                ).ConfigureAwait(continueOnCapturedContext: false);
                var content = JsonSerializer.SerializeToElement(
                    new AzureMcpOnboardingResult(State: state),
                    AzureMcpJson.Default.AzureMcpOnboardingResult
                );

                return new() { StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
            } catch (AzureDelegatedAuthenticationException challenge) {
                throw new RemoteMcpAuthorizationException(claims: challenge.Claims);
            } catch (Exception error) when ((error is AuthenticationFailedException or HttpRequestException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)) {
                return Failure(message: "Platform onboarding failed. Sign in again and verify downstream consent; host credentials are never substituted.");
            }
        }
        if (
            (request.Name != ObserveTool.Name) ||
            (Observation(request: request) is not { } observation)
        ) {
            return Failure(message: "One configured observation name is required.");
        }
        if (!IsReady) { return Failure(message: "The configured host target is unavailable."); }
        if (caller.Authorization is not AzureDelegatedGrant grant) {
            return Failure(message: ((caller.Authorization == ExchangeFailed)
                ? "Delegated authentication failed. Sign in again and verify downstream consent; host credentials are never substituted."
                : "This caller has no grant for that observation."));
        }
        try {
            var items = await m_services.ReadAsync(
                cancellationToken: cancellationToken,
                grant: grant,
                name: observation
            ).ConfigureAwait(continueOnCapturedContext: false);
            var content = JsonSerializer.SerializeToElement(
                new AzureMcpObservationResult(
                    Items: items,
                    Observation: observation
                ),
                AzureMcpJson.Default.AzureMcpObservationResult
            );

            return new() { StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
        } catch (AzureDelegatedAuthenticationException challenge) {
            throw new RemoteMcpAuthorizationException(claims: challenge.Claims);
        } catch (AuthenticationFailedException) {
            return Failure(message: "Delegated authentication failed. Sign in again and verify downstream consent; host credentials are never substituted.");
        } catch (UnauthorizedAccessException) {
            return Failure(message: "This caller has no grant for that observation.");
        } catch (Exception error) when ((error is ArgumentException or InvalidOperationException or IOException or JsonException or global::Azure.RequestFailedException)) {
            return Failure(message: "The delegated observation failed or exceeded its disclosure budget. No partial snapshot is returned.");
        }
    }
    public override ValueTask<ControlCapabilities> DescribeControlAsync(RemoteMcpCaller caller, CancellationToken cancellationToken) => ((m_target is { } target)
        ? m_host.DescribeAsync(
            target,
            new(
                Issuer: caller.Issuer,
                Subject: caller.Subject
            ),
            cancellationToken
        )
        : base.DescribeControlAsync(
            caller: caller,
            cancellationToken: cancellationToken
        )
    );
    public void Dispose() => m_services.Dispose();
    public override IReadOnlyList<Tool> GetServiceTools(RemoteMcpCaller caller) {
        var tools = new List<Tool>();

        if (m_services.SupportsOnboarding) { tools.Add(item: OnboardTool); }
        var names = m_services.NamesFor(subject: caller.Subject);

        if (names.Length > 0) {
            var input = JsonNode.Parse(ObserveTool.InputSchema.GetRawText())!.AsObject();

            input["properties"]!["observation"]!["enum"] = new JsonArray(names.Select(selector: name => ((JsonNode?)JsonValue.Create(name))).ToArray());
            tools.Add(item: new() { Name = ObserveTool.Name, Description = ObserveTool.Description, InputSchema = JsonElement.Parse(input.ToJsonString()), OutputSchema = ObserveTool.OutputSchema, Annotations = ObserveTool.Annotations });
        }
        return tools;
    }
}
internal sealed record AzureMcpObservationResult(string Observation, IReadOnlyList<WorldExtensionObservationItem> Items);
internal sealed record AzureMcpOnboardingResult(string State);
[JsonSerializable(typeof(AzureMcpObservationResult))]
[JsonSerializable(typeof(AzureMcpOnboardingResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AzureMcpJson : JsonSerializerContext;
