namespace Puck.World.Client;

/// <summary>The scene several session screens share: the mirror of the one session whose stream it draws, the world that
/// session observes, and the scene's own name.</summary>
/// <param name="Mirror">The mirror of the session whose stream the scene draws.</param>
/// <param name="Authority">The identity of the world the session observes.</param>
/// <param name="Name">The scene's own name, unique among the presentation's scenes.</param>
/// <param name="Presented">Whether the world the session observes is the one the presentation's own frame draws: the
/// scene is then a second scene of that world, in its coordinates, and plans its meshes at that world's demand as well
/// as its windows' (<see cref="WorldRoutedScene.HostDemand"/>).</param>
public readonly record struct WorldSharedScene(WorldSessionMirror Mirror, string Authority, string Name, bool Presented = false);

/// <summary>
/// A session screen's access to its destination and its route into a scene it shares, both settled once a frame. While
/// the destination's authority proves the session is handed what a whole reader is handed, the window joins the scene
/// the presentation renders that world with (<see cref="WorldFramePresenter.AttachWindow"/>) and renders from the one
/// residency every seat and every such window presenting that world shares, over the endpoint's mirror. While it
/// proves the session is handed the same scene inputs as other sessions' streams, the window joins the scene drawn from
/// one of their mirrors (<see cref="WorldFramePresenter.AttachSharedWindow"/>). Otherwise the route holds no window and
/// the screen renders its own disclosed session: disclosure is never traded for a saving. Once the session no longer
/// shares the whole scene, the narrowing moves <see cref="Access"/>'s epoch, which withdraws every picture the screen
/// published before. A session of the presenting world itself renders as a view of that world's own frame instead.
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
    /// views: joins the endpoint's scene while the session shares its world's whole scene, else the shared scene it is
    /// handed, re-joins when either is another, and leaves it otherwise. It reads the access the frame's
    /// <see cref="SettleAccess"/> found.</summary>
    /// <param name="presenter">The presenter whose scenes the window joins.</param>
    /// <param name="endpoint">The destination's endpoint the window may join, or <see langword="null"/> when it runs none
    /// here or the screen renders its destination another way.</param>
    /// <param name="shared">The scene this session shares with other sessions its authority proved are handed the same
    /// scene inputs, joined when no endpoint is; <see langword="null"/> when it shares none this frame.</param>
    /// <exception cref="ArgumentNullException"><paramref name="presenter"/> is <see langword="null"/>.</exception>
    public void Settle(WorldFramePresenter presenter, WorldAuthorityEndpoint? endpoint, WorldSharedScene? shared = null) {
        ArgumentNullException.ThrowIfNull(argument: presenter);

        if (
            (Access.Level == WorldSessionAccess.Everything) &&
            (endpoint is not null)
        ) {
            if (
                (Window is { } window) &&
                ReferenceEquals(
                    objA: window.Scene.Key,
                    objB: endpoint
                )
            ) {
                return;
            }

            Window?.Dispose();
            Window = presenter.AttachWindow(endpoint: endpoint);

            return;
        }
        if (shared is { } scene) {
            if ((Window is { } held) && ReferenceEquals(objA: held.Scene.Key, objB: scene.Mirror)) { return; }

            Window?.Dispose();
            Window = presenter.AttachSharedWindow(authority: scene.Authority, mirror: scene.Mirror, name: scene.Name, presented: scene.Presented);

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
