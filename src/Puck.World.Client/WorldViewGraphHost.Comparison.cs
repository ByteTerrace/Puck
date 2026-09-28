using Puck.Shaders;

namespace Puck.World.Client;

public sealed partial class WorldViewGraphHost {
    private ulong m_lastComparisonRevision;
    private string? m_comparisonLiveRoot;

    /// <summary>Gets or sets the session's held-frame state, or null for a host with no comparison controls.</summary>
    public WorldFrameComparison? Comparison { get; set; }
    /// <summary>Gets or sets the viewports published for the frame this host composes.</summary>
    public WorldSeatViewports? ComparisonViewports { get; set; }
    /// <summary>Gets the live root before the comparison wrapper, which a hold or measurement captures.</summary>
    public string? ComparisonLiveRoot => (m_comparisonLiveRoot ?? m_runtime?.Root);

    private bool TryComparisonPlacement(string instance, string pass, out RenderGraphPlacement placement) {
        placement = default;
        if (instance != WorldComparisonGraph.Root) { return false; }
        var slot = WorldComparisonGraph.SeatOf(source: pass);

        if (slot < 0) { return false; }
        if ((Comparison?.Seat(slot: slot) is { Mode: not WorldCompareMode.Off }) &&
            (ComparisonViewports?.Seat(slot: slot) is { Present: true } view)) {
            var rect = view.Region;

            placement = new RenderGraphPlacement(Shown: true, Left: rect.X, Top: rect.Y, Width: rect.Width, Height: rect.Height, Sharpness: 0);
        }
        return true;
    }

    /// <summary>Writes live comparison controls through the ordinary scalar parameter seam after this frame's
    /// viewport preparation. Changing a wipe or mode allocates no graph or resource.</summary>
    public void PresentComparison() {
        if ((Comparison is not { Active: true } comparison) || (m_runtime?.NodeOf(instance: WorldComparisonGraph.Root) is not { } node)) { return; }
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (comparison.Seat(slot: slot) is not { Mode: not WorldCompareMode.Off } held) { continue; }
            var pass = WorldComparisonGraph.Source(slot: slot);

            _ = node.TryWriteParameter(passName: pass, field: RenderGraphPackageCatalog.PlaceCompareMode, value: ((uint)held.Mode));
            _ = node.TryWriteParameter(passName: pass, field: RenderGraphPackageCatalog.PlaceWipe, value: held.Wipe);
        }
    }
}
