using Puck.SdfVm;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    /// <summary>Gets or sets the share of the display's height a named view reached on the latest measured frame, zero for
    /// one no view showed, or <see langword="null"/> while unmeasured; the presenter answers it from its graph host
    /// (<c>WorldViewGraphHost.DisplayShareOf</c>).</summary>
    public Func<string, double?>? DisplayShare { get; set; }
    /// <summary>Gets or sets the share of the display's pixels a named view reached on the latest measured frame, zero for
    /// one no view showed, or <see langword="null"/> while unmeasured; the presenter answers it from its graph host
    /// (<c>WorldViewGraphHost.DisplayAreaOf</c>).</summary>
    public Func<string, double?>? DisplayArea { get; set; }

    /// <summary>Returns what a nested view gives up under the world's nested-view table (<c>render.nested</c>, read from the
    /// definition <see cref="ReadQuality"/> last read) at the share of the display it is presented at: the restrictions to
    /// add to its quality (<see cref="SdfViewQuality.Restrict"/>).</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The restrictions.</returns>
    public SdfViewQuality NestedRestriction(string? view) => NestedRestriction(
        share: HeightShareOf(view: view),
        table: m_resolutionDefinition?.Render.Nested
    );
    /// <summary>Returns the quality a nested view that renders no frame's views of its own (a session's window or screen)
    /// renders at: the presentation's own quality (<see cref="Client.WorldFramePresenter.ViewQualityOf"/>) with what the
    /// nested-view table gives up at its presented size added.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none.</param>
    /// <returns>The quality.</returns>
    public SdfViewQuality NestedViewQuality(string? view) => Client.WorldFramePresenter.ViewQualityOf(settings: this).Restrict(other: NestedRestriction(view: view));
    /// <summary>Returns how many of the irradiance volume's levels, counted from the coarsest, a nested view's world
    /// measures under the world's nested-view table at the share of the display's height the view is presented at
    /// (<see cref="SdfFrame.IndirectLevels"/>): its row's (<see cref="NestedRowOf"/>), and every level past every row,
    /// with no table, or for a row that names none.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The levels, zero for every level.</returns>
    public int NestedIndirectLevels(string? view) => (NestedRowOf(share: HeightShareOf(view: view), table: m_resolutionDefinition?.Render.Nested)?.IndirectLevels ?? 0);
    /// <summary>Returns the share of the display's pixels a nested view is presented over
    /// (<see cref="SdfFrame.DisplayShare"/>), which weighs its world's draw on the presentation's background budget.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The share, in [0, 1].</returns>
    public float NestedDisplayShare(string? view) => (float)Math.Clamp(max: 1d, min: 0d, value: ShareOf(measure: DisplayArea, view: view));
    /// <summary>Returns what a nested view presented at a share of the display's height gives up under a nested-view table:
    /// its row's terms (<see cref="NestedRowOf"/>), and past every row (or with no table) nothing but temporal
    /// reconstruction. A nested view never reconstructs over time: its reader samples it, and a jittered picture would
    /// shimmer in the reader's own resolve.</summary>
    /// <param name="table">The rows in ascending share, or <see langword="null"/> for none.</param>
    /// <param name="share">The view's share of the display's height.</param>
    /// <returns>The restrictions to add to the view's quality.</returns>
    public static SdfViewQuality NestedRestriction(IReadOnlyList<WorldNestedViewQuality>? table, double share) =>
        (NestedRowOf(share: share, table: table) is { } row)
            ? new SdfViewQuality {
                DisableAmbientOcclusion = (row.AmbientOcclusion == false),
                DisableSoftShadows = (row.Shadows == ShadowTier.Off),
                ShadowResolution = ((row.Shadows is { } tier) ? Client.WorldFramePresenter.ShadowResolutionOf(tier: tier) : SdfShadowResolution.Full),
            }
            : default;
    /// <summary>Returns the row of a nested-view table a view presented at a share of the display's height takes: the first
    /// whose share it does not exceed.</summary>
    /// <param name="table">The rows in ascending share, or <see langword="null"/> for none.</param>
    /// <param name="share">The view's share of the display's height.</param>
    /// <returns>The row, or <see langword="null"/> past every row and with no table.</returns>
    public static WorldNestedViewQuality? NestedRowOf(IReadOnlyList<WorldNestedViewQuality>? table, double share) {
        foreach (var row in (table ?? [])) {
            if (share <= row.Share) { return row; }
        }
        return null;
    }

    // A view's share of the display's height, the whole display for one unnamed or unmeasured.
    private double HeightShareOf(string? view) => ShareOf(measure: DisplayShare, view: view);
    // A view's share by a measure of the display, the whole display for one unnamed, unmeasured or with no measure.
    private static double ShareOf(Func<string, double?>? measure, string? view) => (((view is not null) ? measure?.Invoke(arg: view) : null) ?? 1d);
}
