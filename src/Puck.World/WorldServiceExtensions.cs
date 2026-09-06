using Microsoft.Extensions.Hosting;
using Puck.Storage;
using Puck.World.Azure;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Explicit deployment input; absent means external services are disabled.</summary>
internal sealed record WorldServiceExtensionOptions(WorldExtensionConfiguration? Configuration);

/// <summary>The every-boot-shape service-extension composition. Only an operator-selected configuration enables it.</summary>
internal sealed class WorldServiceExtensions(WorldServiceExtensionOptions options, WorldServer server,
    WorldInstanceHost instances, WorldReplayTape tape, IObjectBlobStore store) : IHostedService {
    internal static WorldExtensionRegistry<WorldExtensionProviderType> Types { get; } = new(
        [AzureConfiguredProvider.Registration], type => type.Type);
    internal WorldConfiguredExtensions? Runtime { get; private set; }

    internal void Initialize() {
        if (options.Configuration is not { } configuration) { return; }
        if (!instances.TryGet(WorldInstanceHost.BootInstanceName, out var boot) || boot is null) {
            throw new InvalidOperationException("Extension authority is unavailable.");
        }
        Runtime = WorldConfiguredExtensions.Create(configuration, Types, server, store,
            new DirectoryObjectStorageTarget(Path.Combine(WorldStateRoot.Resolve(), "extensions"), maximumBlobBytes: configuration.MaximumBytes),
            () => configuration.Recovery == "recording" ? tape.CaptureExternalOperationCause()
                : server.CaptureExternalOperationCause(instances.CaptureRow(boot)));
        if (configuration.Recovery == "recording" && !tape.TryBeginRecording("extensions-" + configuration.Lineage.ToString("N"), out var reason)) {
            throw new InvalidOperationException($"Extension recovery recording refused: {reason}");
        }
    }

    internal void Pump(ulong tick) => Runtime?.Pump(tick);

    public Task StartAsync(CancellationToken cancellationToken) {
        if (Runtime?.OperationNames.Count > 0) { Runtime.Host.Start(); }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        if (Runtime is not null) { await Runtime.DisposeAsync().ConfigureAwait(false); }
    }
}
