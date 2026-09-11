namespace Puck.Abstractions;

/// <summary>
/// A host-agnostic lifecycle service that can be started, stopped, and asynchronously disposed.
/// Bridges seamlessly to Microsoft.Extensions.Hosting.IHostedService without compile-time coupling.
/// </summary>
public interface IPuckHostedService : IAsyncDisposable, IDisposable {
    /// <summary>Starts the hosted service.</summary>
    /// <param name="cancellationToken">Indicates that the start process has been aborted.</param>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the hosted service.</summary>
    /// <param name="cancellationToken">Indicates that the shutdown process should no longer be graceful.</param>
    Task StopAsync(CancellationToken cancellationToken);
}
