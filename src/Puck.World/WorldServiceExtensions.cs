using Microsoft.Extensions.Hosting;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Explicit deployment input; absent means external services are disabled.</summary>
internal sealed record WorldServiceExtensionOptions(WorldExtensionConfiguration? Configuration);
/// <summary>The every-boot-shape service-extension composition. Only an operator-selected configuration enables it.</summary>
internal sealed class WorldServiceExtensions(WorldServiceExtensionOptions options, WorldServer server,
    WorldInstanceHost instances, WorldReplayTape tape, IObjectBlobStore store) : IHostedService {
    private static readonly Dictionary<string, WorldExtensionProviderType> TypesValue = new(comparer: StringComparer.Ordinal);
    private static readonly Dictionary<string, WorldExtensionEmbeddingProviderType> EmbeddingTypesValue = new(comparer: StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    internal void Initialize() {
        if (options.Configuration is not { } configuration) { return; }
        if (
            !instances.TryGet(
            instance: out var boot,
            name: WorldInstanceHost.BootInstanceName
        ) ||
            (boot is null)
        ) {
            throw new InvalidOperationException(message: "Extension authority is unavailable.");
        }
        Runtime = WorldConfiguredExtensions.Create(
            configuration,
            Types,
            server,
            store,
            new DirectoryObjectStorageTarget(
                Path.Combine(
                    path1: WorldStateRoot.Resolve(),
                    path2: "extensions"
                ),
                maximumBlobBytes: configuration.MaximumBytes
            ),
            () => ((configuration.Recovery == "recording")
            ? tape.CaptureExternalOperationCause()
            : server.CaptureExternalOperationCause(hostRow: instances.CaptureRow(row: boot))),
            EmbeddingTypes
        );
        if (
            (configuration.Recovery == "recording") &&
            !tape.TryBeginRecording(
            name: ("extensions-" + configuration.Lineage.ToString(format: "N")),
            refusal: out var reason
        )
        ) {
            throw new InvalidOperationException(message: $"Extension recovery recording refused: {reason}");
        }
    }
    internal void Pump(ulong tick) => Runtime?.Pump(completedTick: tick);
    internal static void Register(WorldExtensionProviderType providerType) {
        ArgumentNullException.ThrowIfNull(argument: providerType);
        lock (Gate) {
            TypesValue[providerType.Type] = providerType;
        }
    }
    internal static void Register(WorldExtensionEmbeddingProviderType embeddingType) {
        ArgumentNullException.ThrowIfNull(argument: embeddingType);
        lock (Gate) {
            EmbeddingTypesValue[embeddingType.Type] = embeddingType;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (Runtime?.OperationNames.Count > 0) { Runtime.Host.Start(); }
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken) {
        if (Runtime is not null) { await Runtime.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
    }

    internal WorldConfiguredExtensions? Runtime { get; private set; }
    internal static WorldExtensionRegistry<WorldExtensionProviderType> Types {
        get {
            lock (Gate) {
                return new WorldExtensionRegistry<WorldExtensionProviderType>(
                    extensions: TypesValue.Values.ToArray(),
                    keyOf: static type => type.Type
                );
            }
        }
    }
    internal static WorldExtensionRegistry<WorldExtensionEmbeddingProviderType> EmbeddingTypes {
        get {
            lock (Gate) {
                return new WorldExtensionRegistry<WorldExtensionEmbeddingProviderType>(
                    extensions: EmbeddingTypesValue.Values.ToArray(),
                    keyOf: static type => type.Type
                );
            }
        }
    }
}
