using Puck.World.Client;

namespace Puck.World;

internal sealed partial class WorldScreenBinder : IWorldScreenImages {
    // The screens a live presentation verb bound a source over, which no row names; a row's next reconcile clears its
    // screen's entry.
    private readonly HashSet<int> m_liveBinds = [];

    /// <summary>Gets the mapping every screen publishes, keyed by its source instance's handle and rebuilt from the rows
    /// <see cref="ReconcileScreens"/> last applied; <see cref="Publish"/> publishes it each frame.</summary>
    public WorldScreenMappingSet Mappings { get; } = new();

    /// <inheritdoc/>
    public bool ShowsRow(int screen) => (
        m_slots.ContainsKey(key: screen) &&
        !m_liveBinds.Contains(item: screen)
    );
    /// <inheritdoc/>
    /// <remarks>A machine output's extent is its framebuffer's, a producer feed's its descriptor's, and a probe
    /// output's its provisioned ring's.</remarks>
    public bool TryExtent(int screen, out int width, out int height) {
        (width, height) = (0, 0);

        if (!m_slots.TryGetValue(
            key: screen,
            value: out var slot
        )) {
            return false;
        }

        if (slot.MachineSource is { } machine) {
            if (m_machines.VideoOutput(
                instance: machine.Instance,
                output: machine.Output
            ) is { } output) {
                (width, height) = (output.Width, output.Height);
            }
        } else if ((slot.LiveFeed ?? slot.DeclaredFeed) is { } feed) {
            (width, height) = (((int)feed.Descriptor.Width), ((int)feed.Descriptor.Height));
        } else if (slot.Probe?.Output is { } probe) {
            (width, height) = (probe.Width, probe.Height);
        }

        return ((width > 0) && (height > 0));
    }

    // Records that a live presentation verb bound a source over a screen's row.
    private void ShowLive(int index) => _ = m_liveBinds.Add(item: index);
    // Reconciles the mappings with the rows the slots now show and the cameras a view names.
    private void ReconcileMappings(IReadOnlyList<WorldScreen> screens) => Mappings.Reconcile(
        cameras: m_cameras,
        screens: screens
    );
}
