namespace Puck.Commands;

/// <summary>
/// The immutable, UI-facing snapshot of one group's compiled wheel (<see cref="BindingWheelDefinition"/>) — what a
/// radial presenter draws and what a commit dispatches. Precomputed by <see cref="BindingProfile.Compile"/> exactly
/// as every <see cref="BindingPageView"/> is, so reading the active wheel
/// (<see cref="PagedInputBindings.WheelFor"/>) is a single reference read — zero allocation per frame.
/// </summary>
/// <param name="Id">The profile-unique radial id.</param>
/// <param name="Group">The page group the wheel belongs to.</param>
/// <param name="HoldPageIds">Every page id whose selection presents the wheel.</param>
/// <param name="Rings">The ring views, innermost first.</param>
/// <param name="Style">The validated presentation policy.</param>
/// <param name="Excursion">Compiler-precomputed neutral-relative thresholds, or null for explicit ring selection.</param>
public sealed record BindingWheelView(
    string Id,
    string Group,
    IReadOnlyList<string> HoldPageIds,
    IReadOnlyList<BindingWheelRingView> Rings,
    BindingWheelStyleDefinition Style,
    BindingWheelExcursionView? Excursion
);
/// <summary>Squared neutral-relative range boundaries compiled once for allocation-free selector resolution.</summary>
/// <param name="DeadZoneSquared">The authored inclusive dead zone, squared.</param>
/// <param name="ThresholdsSquared">The ordinary squared boundary between adjacent rings.</param>
/// <param name="OutwardThresholdsSquared">Each boundary plus authored hysteresis, squared.</param>
/// <param name="InwardThresholdsSquared">Each boundary minus authored hysteresis, squared.</param>
/// <param name="SpatialTravelFraction">The viewport fraction that normalizes pointer/touch displacement.</param>
public sealed record BindingWheelExcursionView(
    float DeadZoneSquared,
    IReadOnlyList<float> ThresholdsSquared,
    IReadOnlyList<float> OutwardThresholdsSquared,
    IReadOnlyList<float> InwardThresholdsSquared,
    float SpatialTravelFraction
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
/// <param name="Activation">The compiled binding activation the sector commits through the input router.</param>
/// <param name="Label">The sector's display label, if any; opaque to the engine.</param>
/// <param name="Icon">The sector's display icon id, if any; opaque to the engine.</param>
public sealed record BindingWheelSectorView(
    BindingActivation Activation,
    string? Label,
    string? Icon
) {
    /// <summary>Gets the command name, exposed for presentation/read-back only.</summary>
    public string Command => Activation.Command;
}
