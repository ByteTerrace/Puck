namespace Puck.World.Client;

/// <summary>
/// A window session screen's route into its destination's endpoint scene. While the session is delivered everything its
/// destination holds (a live <c>Replica</c> admission under an observer disclosure that redacts nothing), the window joins
/// the scene the presentation renders that world with (<see cref="WorldFramePresenter.AttachWindow"/>) and renders from
/// the one residency every seat and every such window presenting that world shares, over the endpoint's mirror. Any other
/// admission holds no window, and the screen renders its own disclosed session instead: disclosure is never traded for a
/// saving. The session stays the gate: once it no longer discloses everything, the window leaves the scene.
/// </summary>
public sealed class WorldSessionWindowRoute : IDisposable {
    /// <summary>Gets the window the route holds in its endpoint's scene, or <see langword="null"/> while the screen renders
    /// its own session.</summary>
    public WorldRoutedWindow? Window { get; private set; }

    /// <summary>Settles the route for the frame being prepared, on the frame thread before the presenter latches its
    /// views: joins the endpoint's scene while the session discloses everything, re-joins when the destination runs under
    /// another endpoint, and leaves it otherwise.</summary>
    /// <param name="presenter">The presenter whose scenes the window joins.</param>
    /// <param name="endpoint">The destination's endpoint, or <see langword="null"/> when it runs none here.</param>
    /// <param name="disclosesEverything">Whether the screen's session is delivered everything its destination holds right
    /// now.</param>
    /// <exception cref="ArgumentNullException"><paramref name="presenter"/> is <see langword="null"/>.</exception>
    public void Settle(WorldFramePresenter presenter, WorldAuthorityEndpoint? endpoint, bool disclosesEverything) {
        ArgumentNullException.ThrowIfNull(argument: presenter);

        if (
            disclosesEverything &&
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
