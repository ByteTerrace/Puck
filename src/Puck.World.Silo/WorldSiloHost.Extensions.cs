using System.Text.Json;
using Puck.Abstractions;
using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost : IAsyncDisposable {
    // A row's extension configuration refused at activation; the activation cleans up and reports it by name.
    private sealed class ExtensionsRefusedException(string message) : Exception(message: message);

    private readonly PuckExtensionSet m_extensions;

    private static bool TryLoadRowExtensions(WorldSiloWorldRow worldRow, out WorldExtensionConfiguration? configuration, out string refusal) {
        configuration = null;
        refusal = string.Empty;
        if (worldRow.Extensions is not { } path) { return true; }
        try {
            configuration = WorldExtensionConfiguration.Load(path: path);
            return true;
        } catch (Exception error) when ((error is IOException or UnauthorizedAccessException or ArgumentException or JsonException)) {
            refusal = $"extensions '{path}': {error.Message}";
            return false;
        }
    }
    // Attaches the row's configuration on the tick thread through the one path every host shares. A refusal surfaces as
    // ExtensionsRefusedException so the activation's cleanup retires the row before reporting it.
    private async Task AttachExtensionsAsync(WorldInstance row, WorldExtensionConfiguration configuration) {
        var attached = new TaskCompletionSource<string?>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            try {
                _ = WorldConfiguredExtensions.Attach(
                    configuration: configuration,
                    extensions: m_extensions,
                    instances: Instances,
                    row: row,
                    store: m_blobStore,
                    target: m_storageTarget,
                    timeProvider: m_clock
                );
                attached.TrySetResult(result: null);
            } catch (Exception error) when ((error is ArgumentException or InvalidOperationException or JsonException)) {
                attached.TrySetResult(result: error.Message);
            } catch (Exception error) { attached.TrySetException(exception: error); }
        });
        if (await attached.Task.ConfigureAwait(continueOnCapturedContext: false) is { } refusal) {
            throw new ExtensionsRefusedException(message: $"extensions: {refusal}");
        }
    }
    // Takes the runtime off a row; its disposal revokes clients through the row's authority, so a retiring row
    // retires its runtime before it freezes.
    private static WorldConfiguredExtensions? DetachExtensions(WorldInstance row) {
        var runtime = row.Extensions;

        row.Extensions = null;
        return runtime;
    }
    private static async Task DisposeExtensionsAsync(IEnumerable<WorldConfiguredExtensions?> runtimes) {
        foreach (var runtime in runtimes) {
            if (runtime is not null) { await runtime.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false); }
        }
    }
    // Detaches the named rows' runtimes on the tick thread, then stops and disposes them off it, before the rows
    // freeze for retirement.
    private async Task RetireExtensionsAsync(IReadOnlyList<string> worldIds) {
        var detached = new TaskCompletionSource<List<WorldConfiguredExtensions?>>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        Post(action: () => {
            var runtimes = new List<WorldConfiguredExtensions?>();

            foreach (var worldId in worldIds) {
                if (Instances.TryGet(
                    instance: out var row,
                    name: worldId
                ) && (row is not null)) {
                    runtimes.Add(item: DetachExtensions(row: row));
                }
            }
            detached.TrySetResult(result: runtimes);
        });
        await DisposeExtensionsAsync(runtimes: await detached.Task.ConfigureAwait(continueOnCapturedContext: false)).ConfigureAwait(continueOnCapturedContext: false);
    }

    /// <summary>Starts and pumps every admitted row's extension runtime at the master boundary, after the rows step —
    /// the silo's counterpart of a local World's per-step pump. Call only from the tick thread, and only while release
    /// admission is open: configured operations and participants are external effects.</summary>
    public void PumpExtensions() {
        foreach (var (name, bookkeeping) in m_rows) {
            if (
                bookkeeping.Initializing ||
                bookkeeping.Released ||
                !Instances.TryGet(
                    instance: out var row,
                    name: name
                ) ||
                (row?.Extensions is not { } runtime)
            ) {
                continue;
            }
            runtime.Start();
            runtime.Pump(completedTick: row.CompletedTicks);
        }
    }
    /// <summary>Stops and disposes every extension runtime still attached to an admitted row. Call after the tick
    /// thread has stopped; later calls do nothing.</summary>
    /// <returns>A task that completes once every runtime has stopped.</returns>
    public async ValueTask DisposeAsync() {
        var runtimes = new List<WorldConfiguredExtensions?>();

        foreach (var name in m_rows.Keys) {
            if (Instances.TryGet(
                instance: out var row,
                name: name
            ) && (row is not null)) {
                runtimes.Add(item: DetachExtensions(row: row));
            }
        }
        await DisposeExtensionsAsync(runtimes: runtimes).ConfigureAwait(continueOnCapturedContext: false);
    }
}
