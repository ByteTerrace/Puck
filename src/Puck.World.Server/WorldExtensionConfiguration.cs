using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Server;

/// <summary>Host-approved service composition. This document is deployment authority, never imported world content.</summary>
/// <param name="Schema">Must be puck.world.extensions.v1.</param>
/// <param name="World">The exact document ID this deployment may connect.</param>
/// <param name="Lineage">Stable private history namespace; forks require another lineage and safe bindings.</param>
/// <param name="Providers">Named instances of installed provider types.</param>
/// <param name="Operations">Named operations bound through those instances.</param>
/// <param name="Clients">Authenticated principal policies.</param>
/// <param name="Connections">Connections between ordinary request/status state tables and operations.</param>
/// <param name="Worker">Optional bounded worker policy.</param>
/// <param name="MaximumEntries">Retained operation ceiling.</param>
/// <param name="MaximumBytes">Private journal byte ceiling.</param>
/// <param name="ScanEveryTicks">Minimum completed world ticks between connection scans.</param>
/// <param name="Recovery">checkpoint or recording; recording requires an active closed replay prefix.</param>
public sealed record WorldExtensionConfiguration(string Schema, string World, Guid Lineage,
    IReadOnlyList<WorldExtensionProviderSettings> Providers, IReadOnlyList<WorldExtensionOperationSettings> Operations,
    IReadOnlyList<WorldExtensionClientSettings> Clients, IReadOnlyList<WorldExtensionConnection> Connections,
    WorldExtensionHostOptions? Worker = null, int MaximumEntries = 1024, int MaximumBytes = 16777216,
    int ScanEveryTicks = 240, string Recovery = "checkpoint") {
    /// <summary>Parses bounded, strict JSON; unknown and duplicate members refuse at every depth.</summary>
    /// <param name="utf8">The host-selected configuration bytes.</param>
    /// <returns>The detached configuration.</returns>
    /// <exception cref="JsonException">The document is malformed, ambiguous, oversized, or unsupported.</exception>
    public static WorldExtensionConfiguration Parse(ReadOnlySpan<byte> utf8) {
        if (utf8.Length > 1048576) { throw new JsonException("Extension configuration exceeds 1 MiB."); }
        using var document = JsonDocument.Parse(utf8.ToArray());
        CheckDuplicates(document.RootElement);
        var value = JsonSerializer.Deserialize(utf8, WorldExtensionConfigurationJson.Default.WorldExtensionConfiguration)
            ?? throw new JsonException("Extension configuration must be an object.");
        if (value.Schema != "puck.world.extensions.v1") { throw new JsonException("Unsupported extension configuration schema."); }
        return value;
    }

    private static void CheckDuplicates(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Object) {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject()) {
                if (!names.Add(member.Name)) { throw new JsonException($"Duplicate configuration member '{member.Name}'."); }
                CheckDuplicates(member.Value);
            }
        } else if (value.ValueKind == JsonValueKind.Array) {
            foreach (var item in value.EnumerateArray()) { CheckDuplicates(item); }
        }
    }
}

/// <summary>One named, explicitly installed provider and its private configuration.</summary>
/// <param name="Name">Deployment-local provider instance name.</param>
/// <param name="Type">Installed provider catalog key.</param>
/// <param name="Settings">Private provider settings, validated by that provider.</param>
public sealed record WorldExtensionProviderSettings(string Name, string Type, JsonElement Settings);
/// <summary>One caller-visible operation and the provider-specific binding configuration.</summary>
/// <param name="Name">Stable operation name, included in durable request identity.</param>
/// <param name="Provider">Configured provider instance name.</param>
/// <param name="Description">Caller-visible explanation of the operation.</param>
/// <param name="Settings">Private binding settings, validated by the selected provider.</param>
public sealed record WorldExtensionOperationSettings(string Name, string Provider, string Description, JsonElement Settings);
/// <summary>One authenticated principal's service grants, world manifest, and optional storage byte ceiling.</summary>
/// <param name="Principal">Canonical principal spelling supplied by trusted ingress.</param>
/// <param name="Operations">Configured operations this principal may invoke.</param>
/// <param name="Requests">World capabilities requested from the existing authority.</param>
/// <param name="StorageBytes">Optional positive per-blob byte ceiling for isolated storage.</param>
/// <param name="StorageWritable">Whether the optional storage capability permits writes.</param>
public sealed record WorldExtensionClientSettings(string Principal, IReadOnlyList<string> Operations,
    IReadOnlyList<WorldExtensionWorldRequest> Requests, int? StorageBytes = null, bool StorageWritable = false);
/// <summary>The existing world capability and subject spellings, parsed through the ordinary authority vocabulary.</summary>
/// <param name="Capability">Named world capability, such as observe or mutate.</param>
/// <param name="Subject">Existing grant-subject spelling, such as state:requests.</param>
public sealed record WorldExtensionWorldRequest(string Capability, string Subject);
/// <summary>A text request table keyed by stable incarnation/generation IDs, its integer status table, and optional
/// text result table. Removing a request never cancels or erases an external effect.</summary>
/// <param name="Name">Diagnostic connection name; not part of durable operation identity.</param>
/// <param name="Client">Canonical principal of a configured client.</param>
/// <param name="Operation">Configured operation invoked by each request.</param>
/// <param name="Requests">Observable text table containing immutable request bodies.</param>
/// <param name="Status">Observable integer table receiving operation status values.</param>
/// <param name="Results">Optional observable text table receiving provider result payloads.</param>
public sealed record WorldExtensionConnection(string Name, string Client, string Operation, string Requests,
    string Status, string? Results = null);

/// <summary>An installed provider type. Configuration selects its key; it never supplies executable paths.</summary>
/// <param name="Type">The stable catalog key.</param>
/// <param name="Create">Constructs a trusted provider without making external service calls.</param>
public sealed record WorldExtensionProviderType(string Type, Func<JsonElement, IWorldConfiguredProvider> Create);

/// <summary>An explicitly installed provider's configurable operation factory. Owns its credentials and adapters.</summary>
public interface IWorldConfiguredProvider : IDisposable {
    /// <summary>Validates and binds an operation without making external service calls.</summary>
    /// <param name="name">The host-selected operation name.</param>
    /// <param name="description">Caller-visible documentation.</param>
    /// <param name="settings">The provider-specific configuration.</param>
    /// <returns>The bound operation.</returns>
    WorldExtensionOperation Bind(string name, string description, JsonElement settings);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorldExtensionConfiguration))]
internal sealed partial class WorldExtensionConfigurationJson : JsonSerializerContext;
