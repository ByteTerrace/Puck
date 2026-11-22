namespace Puck.World;

/// <summary>
/// What the diagnostic overlay draws, copied at a host step's collect while a diagnostic view is on and published whole
/// (<see cref="WorldPresentationFeed.Diagnostics"/>): the view, how it is presented, and the report the collect last
/// started or completed over the authority's values. The overlay projects this report and never starts one, so no
/// frame reads the population, the definition or the solid field the analysis copies.
/// </summary>
/// <param name="Mode">The diagnostic view: <c>colliders</c>, <c>contacts</c> or <c>off</c>.</param>
/// <param name="Presentation">How the view is drawn: <c>default</c>, <c>focus</c> or <c>pins-only</c>.</param>
/// <param name="Amber">Whether known approximations are pinned beside contradictions.</param>
/// <param name="Report">The latest report of the view, unfinished while its analysis runs, or <see langword="null"/>
/// while the view is off.</param>
public sealed record WorldDiagnosticCopy(string Mode, string Presentation, bool Amber, WorldDiagnosticReport? Report) {
    /// <summary>Gets the copy published while no diagnostic view is on.</summary>
    public static WorldDiagnosticCopy Off { get; } = new(Amber: false, Mode: "off", Presentation: "default", Report: null);

    /// <summary>Gets a value indicating whether the copy is of a view the overlay draws.</summary>
    public bool Drawn => (Mode is "colliders" or "contacts");

    /// <summary>Finds a finding of the copied report.</summary>
    /// <param name="id">The finding's stable id.</param>
    /// <returns>The finding, or <see langword="null"/> when the report holds none of that id.</returns>
    public WorldDiagnosticFinding? Find(string id) {
        if (Report is not { } report) {
            return null;
        }
        foreach (var finding in report.Findings) {
            if (finding.Id == id) {
                return finding;
            }
        }

        return null;
    }
}
