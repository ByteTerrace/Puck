using Puck.Abstractions;
using Puck.Storage;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldConfiguredExtensions {
    /// <summary>Composes a host-approved configuration onto one hosted world row: the one way every host — a local
    /// World's boot row, each silo row — selects installed providers and participants and binds them to that row's
    /// authority, recovery cause, and link. Sets <see cref="WorldInstance.Extensions"/>.</summary>
    /// <param name="configuration">The host-approved configuration; its <c>world</c> must be the row's document ID.</param>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <param name="instances">The host that admitted <paramref name="row"/>, which captures its recovery row.</param>
    /// <param name="row">The admitted row the configuration acts on.</param>
    /// <param name="store">The host's routed store.</param>
    /// <param name="target">The host-selected private persistence target for the operation journal.</param>
    /// <param name="timeProvider">The host clock; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
    /// <returns>The attached runtime. The caller pumps it at closed boundaries on the simulation thread, starts it
    /// with the host, and disposes it when the row or host stops.</returns>
    /// <exception cref="ArgumentNullException">An argument other than <paramref name="timeProvider"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configuration names another world, an invalid name or reference, or an
    /// uninstalled provider or participant type (naming the installed ones).</exception>
    /// <exception cref="InvalidOperationException">The row already has a runtime, <c>recording</c> recovery is selected
    /// and the row keeps no replay tape or the tape refuses to record, or a required table is unavailable.</exception>
    public static WorldConfiguredExtensions Attach(WorldExtensionConfiguration configuration, PuckExtensionSet extensions,
        WorldInstanceHost instances, WorldInstance row, IObjectBlobStore store, ObjectStorageTarget target, TimeProvider? timeProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: row);
        if (row.Extensions is not null) { throw new InvalidOperationException(message: $"World '{row.Name}' already has an extension runtime."); }
        var recording = (configuration.Recovery == "recording");
        var tape = row.Tape;

        if (recording && (tape is null)) {
            throw new InvalidOperationException(message: $"Extension recovery 'recording' needs a replay tape, and world '{row.Name}' keeps none; select 'checkpoint'.");
        }
        var runtime = Create(
            captureCause: (recording
                ? tape!.CaptureExternalOperationCause
                : () => row.Server.CaptureExternalOperationCause(hostRow: instances.CaptureRow(row: row))),
            configuration: configuration,
            extensions: extensions,
            link: (row.Link as IPrincipalServerLink),
            server: row.Server,
            store: store,
            target: target,
            timeProvider: timeProvider
        );

        if (
            recording &&
            !tape!.TryBeginRecording(
                name: GeneratedName.JoinFile("extensions", runtime.m_configuration.Lineage.ToString(format: "N")),
                refusal: out var reason
            )
        ) {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException(message: $"Extension recovery recording refused: {reason}");
        }
        row.Extensions = runtime;
        return runtime;
    }
}
