using Puck.Abstractions.Presentation;
using Puck.SdfVm;

namespace Puck.World;

public sealed partial class WorldRenderSettings {
    /// <summary>The share of a row's bound a view held in a row past it must fall below before it takes the row again
    /// (<see cref="NestedRowIndex"/>).</summary>
    public const double NestedHysteresis = 0.9;

    // The row each named nested view holds, by its index in m_heldTable (its count past every row). A view's row moves its
    // graph's shadow resolution, which rebuilds the graph and redraws every cascade cold, so a view near a bound holds its
    // row until its share leaves the bound by the hysteresis.
    private readonly Dictionary<string, int> m_nestedRows = new(comparer: StringComparer.Ordinal);
    private IReadOnlyList<WorldNestedViewQuality>? m_heldTable;
    private string? m_nestedTable;

    /// <summary>Gets or sets the share of the display's height a named view reached on the latest measured frame, zero for
    /// one no view showed, or <see langword="null"/> while unmeasured; the presenter answers it from its graph host
    /// (<c>WorldViewGraphHost.DisplayShareOf</c>).</summary>
    public Func<string, double?>? DisplayShare { get; set; }
    /// <summary>Gets or sets the share of the display's pixels a named view reached on the latest measured frame, zero for
    /// one no view showed, or <see langword="null"/> while unmeasured; the presenter answers it from its graph host
    /// (<c>WorldViewGraphHost.DisplayAreaOf</c>).</summary>
    public Func<string, double?>? DisplayArea { get; set; }
    /// <summary>Gets or sets the name of the quality table whose nested-view table the presentation runs: a device
    /// profile's name (its own table's <see cref="WorldQualityPreset.Nested"/>) or a tier's (that preset's), the world's
    /// <c>render.nested</c> where the table authors none, and <see langword="null"/> for the world's always. A quality
    /// preset or device profile writes it (<c>world.quality</c>, <c>world.device-profile</c>).</summary>
    public string? NestedTable {
        get => m_nestedTable;
        set {
            if (string.Equals(a: m_nestedTable, b: value, comparisonType: StringComparison.Ordinal)) { return; }
            m_nestedTable = value;
            m_revision++;
        }
    }

    /// <summary>Gets the nested-view table the presentation runs: <see cref="NestedTable"/>'s, else the world's
    /// <c>render.nested</c> (from the definition <see cref="ReadQuality"/> last read).</summary>
    public IReadOnlyList<WorldNestedViewQuality>? NestedRows {
        get {
            if (m_resolutionDefinition?.Render is not { } render) { return null; }
            if (m_nestedTable is { } name) {
                if (WorldDeviceProfiles.Find(render: render, name: name) is { Table: { } table }) { return (table.Nested ?? render.Nested); }
                if ((QualityTiers.Parse(name: name) is { } tier) && (render.Preset(tier: tier) is { } preset)) { return (preset.Nested ?? render.Nested); }
            }
            return render.Nested;
        }
    }

    /// <summary>Returns what a nested view gives up under the presentation's nested-view table (<see cref="NestedRows"/>)
    /// at the share of the display it is presented at: the restrictions to add to its quality
    /// (<see cref="SdfViewQuality.Restrict"/>).</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The restrictions.</returns>
    public SdfViewQuality NestedRestriction(string? view) => RestrictionOf(row: HeldRowOf(view: view));
    /// <summary>Returns the quality a nested view that renders no frame's views of its own (a session's window or screen)
    /// renders at: the presentation's own quality (<see cref="Client.WorldFramePresenter.ViewQualityOf"/>) with what the
    /// nested-view table gives up at its presented size added.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none.</param>
    /// <returns>The quality.</returns>
    public SdfViewQuality NestedViewQuality(string? view) => Client.WorldFramePresenter.ViewQualityOf(settings: this).Restrict(other: NestedRestriction(view: view));
    /// <summary>Returns how many of the irradiance volume's levels, counted from the coarsest, a nested view's world
    /// measures under the presentation's nested-view table at the share of the display's height the view is presented at
    /// (<see cref="SdfFrame.IndirectLevels"/>): its row's, and every level past every row, with no table, or for a row
    /// that names none.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The levels, zero for every level.</returns>
    public int NestedIndirectLevels(string? view) => (HeldRowOf(view: view)?.IndirectLevels ?? 0);
    /// <summary>Returns the share of the display's pixels a nested view is presented over
    /// (<see cref="SdfFrame.DisplayShare"/>), which weighs its world's draw on the presentation's background budget.</summary>
    /// <param name="view">The view's instance name, or <see langword="null"/> for one with none; a view whose share is
    /// unmeasured counts as filling the display.</param>
    /// <returns>The share, in [0, 1].</returns>
    public float NestedDisplayShare(string? view) => (float)Math.Clamp(max: 1d, min: 0d, value: ShareOf(measure: DisplayArea, view: view));
    /// <summary>Returns what a nested view presented at a share of the display's height gives up under a nested-view table:
    /// its row's terms (<see cref="NestedRowIndex"/>, holding no earlier row), and past every row (or with no table)
    /// nothing but temporal reconstruction. A nested view never reconstructs over time: its reader samples it, and a
    /// jittered picture would shimmer in the reader's own resolve.</summary>
    /// <param name="table">The rows in ascending share, or <see langword="null"/> for none.</param>
    /// <param name="share">The view's share of the display's height.</param>
    /// <returns>The restrictions to add to the view's quality.</returns>
    public static SdfViewQuality NestedRestriction(IReadOnlyList<WorldNestedViewQuality>? table, double share) =>
        RestrictionOf(row: RowAt(table: table, index: NestedRowIndex(held: -1, share: share, table: table)));
    /// <summary>Returns the index of the row of a nested-view table a view presented at a share of the display's height
    /// takes, given the row it held: the first row whose share it does not exceed when that row lies past the held one
    /// (the view grew), else the held row until the share falls to <see cref="NestedHysteresis"/> of a lower row's bound,
    /// and then the first such row. A view near a bound therefore keeps one row as its share wavers.</summary>
    /// <param name="table">The rows in ascending share, or <see langword="null"/> for none.</param>
    /// <param name="share">The view's share of the display's height.</param>
    /// <param name="held">The index the view held, the table's row count past every row, or negative for none.</param>
    /// <returns>The index, the table's row count past every row (zero with no table).</returns>
    public static int NestedRowIndex(IReadOnlyList<WorldNestedViewQuality>? table, double share, int held) {
        var fresh = FirstRowOf(table: table, share: share, bound: 1d);

        if ((held < 0) || (fresh >= held)) { return fresh; }
        return Math.Min(val1: held, val2: FirstRowOf(table: table, share: share, bound: NestedHysteresis));
    }

    // The row a view takes this frame, held across frames by its name under the current table.
    private WorldNestedViewQuality? HeldRowOf(string? view) {
        var table = NestedRows;
        var share = HeightShareOf(view: view);

        if (view is null) { return RowAt(table: table, index: NestedRowIndex(held: -1, share: share, table: table)); }
        if (!ReferenceEquals(objA: table, objB: m_heldTable)) {
            m_nestedRows.Clear();
            m_heldTable = table;
        }
        var index = NestedRowIndex(held: (m_nestedRows.TryGetValue(key: view, value: out var held) ? held : -1), share: share, table: table);

        m_nestedRows[view] = index;
        return RowAt(table: table, index: index);
    }
    // The first row whose bound, scaled, the share does not exceed: the table's count past every row.
    private static int FirstRowOf(IReadOnlyList<WorldNestedViewQuality>? table, double share, double bound) {
        var count = (table?.Count ?? 0);

        for (var index = 0; (index < count); index++) {
            if (share <= (table![index].Share * bound)) { return index; }
        }
        return count;
    }
    private static WorldNestedViewQuality? RowAt(IReadOnlyList<WorldNestedViewQuality>? table, int index) =>
        ((index < (table?.Count ?? 0)) ? table![index] : null);
    private static SdfViewQuality RestrictionOf(WorldNestedViewQuality? row) => (row is { } taken)
        ? new SdfViewQuality {
            DisableAmbientOcclusion = (taken.AmbientOcclusion == false),
            DisableSoftShadows = (taken.Shadows == ShadowTier.Off),
            ShadowResolution = ((taken.Shadows is { } tier) ? Client.WorldFramePresenter.ShadowResolutionOf(tier: tier) : SdfShadowResolution.Full),
        }
        : default;
    // The render-scale ceiling a nested view keeps: the presentation's own, and below it its row's.
    private float NestedCeiling(string view) => Math.Min(
        val1: (Quality(view: "*")?.RenderScale ?? RenderScale),
        val2: (HeldRowOf(view: view)?.RenderScale ?? 1f));
    // A view's share of the display's height, the whole display for one unnamed or unmeasured.
    private double HeightShareOf(string? view) => ShareOf(measure: DisplayShare, view: view);
    // A view's share by a measure of the display, the whole display for one unnamed, unmeasured or with no measure.
    private static double ShareOf(Func<string, double?>? measure, string? view) => (((view is not null) ? measure?.Invoke(arg: view) : null) ?? 1d);
}
