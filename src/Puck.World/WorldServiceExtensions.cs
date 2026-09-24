using Microsoft.Extensions.Hosting;
using Puck.Abstractions;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Explicit deployment input; absent means external services are disabled.</summary>
internal sealed record WorldServiceExtensionOptions(WorldExtensionConfiguration? Configuration);
/// <summary>The every-boot-shape service-extension composition. Only an operator-selected configuration enables it; it
/// attaches to the boot row through <see cref="WorldConfiguredExtensions.Attach"/>, the path every host shares.</summary>
internal sealed class WorldServiceExtensions(WorldServiceExtensionOptions options, WorldInstanceHost instances,
    IObjectBlobStore store, PuckExtensionSet extensions) : IHostedService {
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
        Runtime = WorldConfiguredExtensions.Attach(
            configuration: configuration,
            extensions: extensions,
            instances: instances,
            row: boot,
            store: store,
            target: new DirectoryObjectStorageTarget(
                Path.Combine(
                    path1: WorldStateRoot.Resolve(),
                    path2: "extensions"
                ),
                maximumBlobBytes: configuration.MaximumBytes
            )
        );
    }
    internal void Pump(ulong tick) => Runtime?.Pump(completedTick: tick);

    public Task StartAsync(CancellationToken cancellationToken) {
        Runtime?.Start();
        return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken) {
        if (Runtime is not null) { await Runtime.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
    }

    internal WorldConfiguredExtensions? Runtime { get; private set; }
}
