namespace Puck.World.Client;

/// <summary>
/// A session screen's access to its destination and its route into that destination's endpoint scene, both settled
/// once a frame. While the session is delivered everything its destination holds (a live <c>Replica</c> admission under
/// an observer disclosure that redacts nothing), the window joins the scene the presentation renders that world with
/// (<see cref="WorldFramePresenter.AttachWindow"/>) and renders from the one residency every seat and every such window
/// presenting that world shares, over the endpoint's mirror. Any other admission holds no window, and the screen renders
/// its own disclosed session instead: disclosure is never traded for a saving. The session stays the gate: once it no
/// longer discloses everything, the window leaves the scene, and the narrowing moves <see cref="Access"/>'s epoch, which
/// withdraws every picture the screen published before. A session of the presenting world itself renders as a view of
/// that world's own frame instead and holds no route.
/// </summary>
public sealed class WorldSessionRoute : IDisposable {
    /// <summary>Gets the window the route holds in its endpoint's scene, or <see langword="null"/> while the screen renders
    /// its own session.</summary>
    public WorldRoutedWindow? Window { get; private set; }
    /// <summary>Gets the session's access to its destination, whose epoch names the pictures the screen may still
    /// show.</summary>
    public WorldViewerAccess Access { get; } = new();

    /// <summary>Settles the session's access for the frame being prepared, on the frame thread before any view of it
    /// resolves.</summary>
    /// <param name="access">The access the session has now.</param>
    /// <param name="admission">The observation that access was admitted through, compared by reference, or
    /// <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when the access narrowed (<see cref="WorldViewerAccess.Settle"/>).</returns>
    public bool SettleAccess(WorldSessionAccess access, object? admission) => Access.Settle(admission: admission, level: access);
    /// <summary>Settles the route for the frame being prepared, on the frame thread before the presenter latches its
    /// views: joins the endpoint's scene while the session discloses everything, re-joins when the destination runs under
    /// another endpoint, and leaves it otherwise. It reads the access the frame's <see cref="SettleAccess"/> found.</summary>
    /// <param name="presenter">The presenter whose scenes the window joins.</param>
    /// <param name="endpoint">The destination's endpoint the window may join, or <see langword="null"/> when it runs none
    /// here or the screen renders its destination another way.</param>
    /// <exception cref="ArgumentNullException"><paramref name="presenter"/> is <see langword="null"/>.</exception>
    public void Settle(WorldFramePresenter presenter, WorldAuthorityEndpoint? endpoint) {
        ArgumentNullException.ThrowIfNull(argument: presenter);

        if (
            (Access.Level == WorldSessionAccess.Everything) &&
            (endpoint is not null)
        ) {
            if (
                (Window is { } window) &&
                ReferenceEquals(
                    objA: window.Scene.Endpoint,
                    objB: endpoint
                )
            ) {
                return;
            }

            Window?.Dispose();
            Window = presenter.AttachWindow(endpoint: endpoint);

            return;
        }

        Dispose();
    }
    /// <summary>Leaves the endpoint's scene.</summary>
    public void Dispose() {
        Window?.Dispose();
        Window = null;
    }
}
