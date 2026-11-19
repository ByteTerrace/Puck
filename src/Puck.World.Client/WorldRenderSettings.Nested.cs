using Puck.SdfVm;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    /// <summary>Gets or sets the share of the display's height a named view reached on the latest measured frame, zero for
    /// one no view showed, or <see langword="null"/> while unmeasured; the presenter answers it from its graph host
    /// (<c>WorldViewGraphHost.DisplayShareOf</c>).</summary>
    public Func<string, double?>? DisplayShare { get; set; }

    /// <summary>Returns what a nested view gives up under the world's nested-view table (<c>render.nested</c>, read from the
    /// definition <see cref="ReadQuality"/> last read) at the share of the display it is presented at: the restrictions to
    /// add to its quality (<see cref="SdfViewQuality.Restrict"/>).</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The restrictions.</returns>
    public SdfViewQuality NestedRestriction(string? view) => NestedRestriction(
        share: (((view is not null) ? DisplayShare?.Invoke(arg: view) : null) ?? 1d),
        table: m_resolutionDefinition?.Render.Nested
    );
    /// <summary>Returns the quality a nested view that renders no frame's views of its own (a session's window or screen)
    /// renders at: the presentation's own quality (<see cref="Client.WorldFramePresenter.ViewQualityOf"/>) with what the
    /// nested-view table gives up at its presented size added.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none.</param>
    /// <returns>The quality.</returns>
    public SdfViewQuality NestedViewQuality(string? view) => Client.WorldFramePresenter.ViewQualityOf(settings: this).Restrict(other: NestedRestriction(view: view));
    /// <summary>Returns what a nested view presented at a share of the display's height gives up under a nested-view table:
    /// the first row whose share it does not exceed, and past every row (or with no table) nothing but temporal
    /// reconstruction. A nested view never reconstructs over time: its reader samples it, and a jittered picture would
    /// shimmer in the reader's own resolve.</summary>
    /// <param name="table">The rows in ascending share, or <see langword="null"/> for none.</param>
    /// <param name="share">The view's share of the display's height.</param>
    /// <returns>The restrictions to add to the view's quality.</returns>
    public static SdfViewQuality NestedRestriction(IReadOnlyList<WorldNestedViewQuality>? table, double share) {
        foreach (var row in (table ?? [])) {
            if (share > row.Share) { continue; }
            return new SdfViewQuality {
                DisableAmbientOcclusion = (row.AmbientOcclusion == false),
                DisableSoftShadows = (row.Shadows == ShadowTier.Off),
                ShadowResolution = ((row.Shadows is { } tier) ? Client.WorldFramePresenter.ShadowResolutionOf(tier: tier) : SdfShadowResolution.Full),
            };
        }
        return default;
    }
}
