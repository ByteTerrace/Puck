using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder : IWorldScreenImages {
    // The source a live presentation verb shows over a screen's row, by screen index; a row's next reconcile clears its
    // screen's entry.
    private readonly Dictionary<int, WorldScreenSource> m_live = [];
    // The rows ReconcileScreens last applied.
    private IReadOnlyList<WorldScreen> m_rows = [];

    /// <summary>Gets the mapping every screen publishes for the source it shows, keyed by its source instance's handle and
    /// rebuilt from the rows <see cref="ReconcileScreens"/> last applied and the live binds over them;
    /// <see cref="Publish"/> publishes it each frame. Its <see cref="WorldScreenMappingSet.Sources"/> are the source
    /// instances the render graph runs for the screens.</summary>
    public WorldScreenMappingSet Mappings { get; } = new();

    /// <inheritdoc/>
    /// <remarks>A machine output's extent is its framebuffer's, a producer feed's its descriptor's (the feed its source
    /// instance opened), and a probe output's its provisioned ring's.</remarks>
    public bool TryExtent(int screen, out int width, out int height) {
        (width, height) = (0, 0);

        switch (ShownOf(screen: screen)) {
            case WorldScreenSource.Machine machine:
                if (m_machines.VideoOutput(
                    instance: machine.Instance,
                    output: machine.Output
                ) is { } output) {
                    (width, height) = (output.Width, output.Height);
                }

                break;
            case WorldScreenSource.Probe probe:
                if (
                    m_probeFeeds.TryGetValue(
                        key: probe.Id,
                        value: out var feed
                    ) &&
                    (feed.Output is { } ring)
                ) {
                    (width, height) = (ring.Width, ring.Height);
                }

                break;
            default:
                if (
                    (ReadOf(screen: screen) is { } instance) &&
                    (FeedOf(instance: instance) is { } source)
                ) {
                    (width, height) = (((int)source.Descriptor.Width), ((int)source.Descriptor.Height));
                }

                break;
        }

        return ((width > 0) && (height > 0));
    }

    // Reconciles the mappings, and the source instances they name, with the rows and the live binds over them.
    private void ReconcileMappings() => Mappings.Reconcile(
        cameras: m_cameras,
        live: m_live,
        screens: m_rows
    );
    // Reconciles the mappings with the rows a screen mutation delivered.
    private void ReconcileMappings(IReadOnlyList<WorldScreen> screens) {
        m_rows = screens;
        ReconcileMappings();
    }
    // The source a screen shows: the one a live presentation verb bound over its row, or its row's.
    private WorldScreenSource? ShownOf(int screen) {
        if (m_live.TryGetValue(
            key: screen,
            value: out var live
        )) {
            return live;
        }

        return (m_slots.TryGetValue(
            key: screen,
            value: out var slot
        )
            ? slot.DeclaredSource
            : null
        );
    }
    // Shows a source a live presentation verb bound over a screen's row, which the render graph runs from its next frame.
    private void ShowLive(int index, WorldScreenSource source) {
        m_live[index] = source;
        ReconcileMappings();
    }
    // Gives a screen back its row's source.
    private void ShowRow(int index) {
        if (m_live.Remove(key: index)) {
            ReconcileMappings();
        }
    }
}
