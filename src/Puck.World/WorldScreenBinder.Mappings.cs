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
    public WorldScreenMappingSet Mappings { get; } = new(world: WorldInstanceHost.BootInstanceName);

    /// <inheritdoc/>
    /// <remarks>A machine output's extent is its framebuffer's, and a producer's or a probe output's its feed's descriptor's
    /// (the feed its source instance opened: a probe's states its provisioned ring's).</remarks>
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
            default:
                if (
                    (InstanceOf(screen: screen) is { } instance) &&
                    (FeedOf(instance: instance) is { } source)
                ) {
                    (width, height) = (((int)source.Descriptor.Width), ((int)source.Descriptor.Height));
                }

                break;
        }

        return ((width > 0) && (height > 0));
    }

    // Reconciles the mappings, and the source and view instances they name, with the rows and the live binds over them.
    private void ReconcileMappings() {
        Mappings.Reconcile(
            cameras: m_cameras,
            live: m_live,
            screens: m_rows,
            sessionsPastDepth: (m_nestingDepth == 0)
        );
        ReconcileViews();
    }
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
    /// <summary>Gives a screen back the source its row authors, dropping the live bind a presentation verb made over it:
    /// the <c>screen.source &lt;index&gt; row</c> path. A camera view the live bind registered is released; a row that
    /// authors a camera view binds it again, as the view self-heal does. A machine the row's screen displays is not a
    /// live bind and is left alone. Fails for an undeclared screen, or one already showing its row's source.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>Whether the screen returned to its row's source, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryShowRow(int index) {
        if (!m_slots.TryGetValue(
            key: index,
            value: out var slot
        )) {
            return (Ok: false, Message: $"no screen {index} declared");
        }
        if (!m_live.ContainsKey(key: index)) {
            return (Ok: false, Message: $"screen {index} already shows its row's source");
        }

        Rebind(live: null, slot: slot);
        if (
            (slot.DeclaredSource is WorldScreenSource.View declared) &&
            (ResolveCamera(name: declared.CameraName) is not null) &&
            (m_viewPipelines is not null)
        ) {
            _ = TryView(
                cameraName: declared.CameraName,
                index: index
            );
        }
        ShowRow(index: index);

        return (Ok: true, Message: $"screen {index} showing its row's source");
    }
    // The one path a live retarget takes: the slot stops filming the camera view it held, releasing the registration
    // when nothing else shows it, and the screen then shows the live source, or its row's when there is none.
    private void Rebind(ScreenSlot slot, WorldScreenSource? live) {
        ReleaseSlotView(slot: slot);
        if (live is null) {
            ShowRow(index: slot.Index);
        } else {
            ShowLive(
                index: slot.Index,
                source: live
            );
        }
    }
    // Gives a screen back its row's source.
    private void ShowRow(int index) {
        if (m_live.Remove(key: index)) {
            ReconcileMappings();
        }
    }
}
