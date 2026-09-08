namespace Puck.World.Server;

/// <summary>Caller-visible operation metadata, containing no credentials, provider objects, or resource bindings.</summary>
/// <param name="Name">The registered operation name.</param>
/// <param name="Description">A human/tool description.</param>
/// <param name="InputSchema">The input's JSON schema for discovery. The request factory enforces input validity.</param>
public sealed record WorldExtensionOperationDescription(string Name, string Description, string InputSchema);

/// <summary>A trusted host's explicit operation registration. Never deserialize or discover executable factories
/// from an imported world or a writable plugin directory.</summary>
/// <param name="Description">The operation's caller-visible description.</param>
/// <param name="Provider">The host-owned execution adapter and credentials.</param>
/// <param name="CreateRequest">Validates input and creates a request with the supplied id and payload.</param>
/// <param name="PollingDelay">Optional service-specific delay derived from the previous durable result and UTC now.
/// Null uses the host interval. This callback runs on workers, outside the world authority lock.</param>
public sealed record WorldExtensionOperation(WorldExtensionOperationDescription Description,
    IWorldExternalOperationProvider Provider, Func<string, string, WorldExternalOperation> CreateRequest,
    Func<WorldExternalOperationResult, DateTimeOffset, TimeSpan?>? PollingDelay = null);

/// <summary>A caller's operation read-back. Authority-private recovery evidence is never included.</summary>
/// <param name="Id">The host-derived operation id.</param>
/// <param name="Name">The registered operation name.</param>
/// <param name="Status">The durable delivery state, not a gameplay-application verdict.</param>
/// <param name="Result">The provider's bounded result or progress payload.</param>
public sealed record WorldExtensionOperationSnapshot(string Id, string Name, WorldExternalOperationStatus Status, string Result);

/// <summary>Host policy for bounded extension work. These limits do not change simulation timing.</summary>
/// <param name="MaximumClients">Maximum admitted client capabilities.</param>
/// <param name="MaximumConcurrentOperations">Maximum external calls in one worker pass.</param>
/// <param name="MaximumConcurrentSubmissions">Maximum concurrent durable request submissions.</param>
/// <param name="MaximumInputBytes">Maximum UTF-8 input size before invoking a request factory.</param>
/// <param name="PollInterval">Positive minimum spacing between worker passes.</param>
/// <param name="OperationTimeout">Positive cancellation deadline for one external operation.</param>
public sealed record WorldExtensionHostOptions(int MaximumClients, int MaximumConcurrentOperations,
    int MaximumConcurrentSubmissions, int MaximumInputBytes, TimeSpan PollInterval, TimeSpan OperationTimeout) {
    /// <summary>Gets bounded defaults for a small local or hosted world.</summary>
    public static WorldExtensionHostOptions Default { get; } = new(32, 4, 8, 65536, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}
