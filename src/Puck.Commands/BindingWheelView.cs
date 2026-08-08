namespace Puck.Commands;

/// <summary>
/// The immutable, UI-facing snapshot of one group's compiled wheel (<see cref="BindingWheelDefinition"/>) — what a
/// radial presenter draws and what a commit dispatches. Precomputed by <see cref="BindingProfile.Compile"/> exactly
/// as every <see cref="BindingPageView"/> is, so reading the active wheel
/// (<see cref="PagedInputBindings.WheelFor"/>) is a single reference read — zero allocation per frame.
/// </summary>
/// <param name="Group">The page group the wheel belongs to.</param>
/// <param name="HoldPageId">The page id whose selection presents the wheel.</param>
/// <param name="Rings">The ring views, innermost first.</param>
public sealed record BindingWheelView(
    string Group,
    string HoldPageId,
    IReadOnlyList<BindingWheelRingView> Rings
);

/// <summary>One wheel ring as the UI presents it — a page worn as a concentric shell.</summary>
/// <param name="PageId">The ring page's profile-unique id.</param>
/// <param name="Label">The ring page's display label, if any; opaque to the engine.</param>
/// <param name="Sectors">The ring's sector rows, in page order — sector 0 sits at twelve o'clock and the rest
/// follow clockwise.</param>
public sealed record BindingWheelRingView(
    string PageId,
    string? Label,
    IReadOnlyList<BindingWheelSectorView> Sectors
);

/// <summary>One wheel sector as the UI presents it and a commit dispatches it.</summary>
/// <param name="Command">The console-dispatchable command the sector commits.</param>
/// <param name="Label">The sector's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The sector's display icon id, if any; opaque to the engine.</param>
public sealed record BindingWheelSectorView(
    string Command,
    string? Label,
    string? Icon
);
