using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.Commands;

/// <summary>
/// One named radial presentation — the wheel a seat presents while it holds ANY of the group's
/// <paramref name="HoldPages"/>. Several physical sources may select different hold pages that point at the same
/// radial (Tab and LT, for example), and a group may declare several radials. The
/// rings are PAGES (<see cref="BindingPageDefinition"/> — the same record a chord row carries), presented as
/// concentric shells rather than selected by chord; each ring's entries are the wheel's SECTORS, and every sector is
/// an ordinary command binding activated through the originating seat's input-router lane.
/// </summary>
/// <remarks>A sector row deliberately narrows the page-entry shape: it carries a <see cref="BindingPageEntryDefinition.Command"/>
/// destination plus display metadata and NOTHING else — no <c>Sources</c>/<c>Activator</c> (the radial gesture is the
/// trigger), no <c>Channel</c>/<c>Scale</c> (a radial choice is a one-shot command activation), and no
/// <c>Mode</c> (it has no held state). Command <c>Value</c> and <c>ActivateOn</c> remain meaningful and compile into
/// the same activation shape an ordinary binding uses. Ring page ids share the document-wide page-id namespace.</remarks>
/// <param name="Id">The profile-unique radial id. Composition and runtime continuity key on this identity.</param>
/// <param name="Group">The page group this wheel belongs to — the seat's ACTIVE group decides which wheel presents,
/// so a group without a wheel simply presents nothing. A containing world may bind the name to a Text state cell
/// with <c>state.&lt;row&gt;[.&lt;key&gt;]</c>.</param>
/// <param name="HoldPages">The page ids whose selection presents this wheel. Each is a chord-row page of the same
/// group. Their ordinary entries author selection, ring navigation, commit, and cancel sources.</param>
/// <param name="Rings">The concentric ring pages, innermost first — <see cref="MinRings"/>..<see cref="MaxRings"/>
/// of them, each carrying <see cref="MinSectorsPerRing"/>..<see cref="MaxSectorsPerRing"/> sector rows.</param>
/// <param name="Style">Author-controlled presentation and pointer-selection policy, or the documented defaults.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingWheelDefinition(
    string Id,
    DocumentIdentifier Group,
    IReadOnlyList<string> HoldPages,
    IReadOnlyList<BindingPageDefinition> Rings,
    BindingWheelStyleDefinition? Style = null
) {
    /// <summary>The fewest rings a wheel may declare.</summary>
    public const int MinRings = 1;
    /// <summary>The most rings a wheel may declare.</summary>
    public const int MaxRings = 3;
    /// <summary>The fewest sector rows a ring may declare.</summary>
    public const int MinSectorsPerRing = 2;
    /// <summary>The most sector rows a ring may declare.</summary>
    public const int MaxSectorsPerRing = 8;
}
/// <summary>A spatial input's sector-selection geometry. Pointer input authors this today; a future touch binding
/// can reuse the same policy without pretending a touch location is an analog stick.</summary>
public enum BindingWheelSpatialSelectionMode {
    /// <summary>The spatial input does not participate in radial selection.</summary>
    Disabled,

    /// <summary>Once outside the dead zone, direction from the input device's captured neutral selects a sector.
    /// Distance is unbounded and independent from the displayed hub.</summary>
    Angle,

    /// <summary>The input must lie inside the wheel's authored annulus. This is the direct-targeting policy suited
    /// to pointer input and, eventually, a touch location. When ring selection uses Excursion, ring magnitude
    /// remains device-neutral-relative while this sector target remains hub-relative.</summary>
    HitTarget,
}
/// <summary>Where the radial hub is anchored for the lifetime of one open gesture.</summary>
public enum BindingWheelPlacement {
    /// <summary>At the pointer's opening position when one is available; otherwise at viewport center.</summary>
    Pointer,

    /// <summary>At the center of the owning seat's viewport, regardless of pointer position.</summary>
    ViewportCenter,
}
/// <summary>How a wheel chooses among its authored rings.</summary>
public enum BindingWheelRingSelectionMode {
    /// <summary>The active ring is selected explicitly by <c>player.wheel.ring</c> bindings or pointer-wheel input.</summary>
    Explicit,

    /// <summary>The active ring follows the selector's normalized distance from that input device's neutral.</summary>
    Excursion,
}
/// <summary>Author-controlled neutral-relative ring ranges. Magnitudes are device-neutral: an Axis2D selector uses
/// its native magnitude, while pointer/touch displacement is divided by <paramref name="SpatialTravelFraction"/>
/// of the seat viewport's smaller extent.</summary>
/// <param name="DeadZone">The inclusive neutral magnitude that selects no ring and makes release a no-op.</param>
/// <param name="Thresholds">The ascending boundary between adjacent rings. A wheel with N rings declares exactly
/// N-1 thresholds: above the final threshold selects the final ring without an outer limit.</param>
/// <param name="SpatialTravelFraction">The pointer/touch displacement that represents magnitude 1, as a fraction
/// of the seat viewport's smaller extent.</param>
/// <param name="Hysteresis">The magnitude retained on each side of a ring boundary before changing rings.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingWheelExcursionDefinition(
    float DeadZone,
    IReadOnlyList<float> Thresholds,
    float SpatialTravelFraction = 0.25f,
    float Hysteresis = 0.02f
);
/// <summary>Author-controlled radial presentation policy. Fractions are relative to the seat viewport's smaller
/// extent; rotation is degrees clockwise from twelve o'clock.</summary>
/// <param name="PointerSelection">How pointer location selects a sector: disabled, angle-only, or direct target.</param>
/// <param name="Placement">Where the wheel hub is anchored when the radial opens.</param>
/// <param name="DeadZoneFraction">The visual hub radius and spatial-input dead zone as a fraction of the seat
/// viewport's smaller extent.</param>
/// <param name="RingWidthFraction">One ring's radial width as a viewport fraction.</param>
/// <param name="OuterGraceRingFraction">Additional direct-target selecting distance beyond the last visual ring,
/// in ring widths.</param>
/// <param name="RotationDegrees">Sector-zero rotation clockwise from twelve o'clock.</param>
/// <param name="Clockwise">Whether sector indices advance clockwise.</param>
/// <param name="InitialRing">The initially active ring, zero-based.</param>
/// <param name="RingSelection">Whether bindings select rings explicitly or selector excursion selects them.</param>
/// <param name="Excursion">The required neutral-relative ranges when <paramref name="RingSelection"/> is
/// <see cref="BindingWheelRingSelectionMode.Excursion"/>; otherwise null.</param>
/// <param name="AxisDeadZone">The inclusive normalized Axis2D magnitude that selects neutral on an explicit-ring
/// wheel. Excursion-controlled wheels use <see cref="BindingWheelExcursionDefinition.DeadZone"/>.</param>
/// <param name="SelectionGraceSeconds">How long a highlighted sector stays selected after the selector returns to the
/// dead zone. A quick throw may return through neutral during this interval without losing its sector; remaining
/// neutral beyond the interval arms an empty commit as a deliberate cancel. 0 disables.</param>
/// <param name="SwitchFraction">The normalized selector magnitude a different sector needs to replace a sector held by
/// <paramref name="SelectionGraceSeconds"/>, so a return swing that clips the far side of the dead zone cannot steal the
/// selection.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingWheelStyleDefinition(
    BindingWheelSpatialSelectionMode PointerSelection = BindingWheelSpatialSelectionMode.Angle,
    BindingWheelPlacement Placement = BindingWheelPlacement.Pointer,
    float DeadZoneFraction = 0.10f,
    float RingWidthFraction = 0.07f,
    float OuterGraceRingFraction = 0.5f,
    float RotationDegrees = 0f,
    bool Clockwise = true,
    int InitialRing = 0,
    BindingWheelRingSelectionMode RingSelection = BindingWheelRingSelectionMode.Explicit,
    BindingWheelExcursionDefinition? Excursion = null,
    float AxisDeadZone = 0.08f,
    float SelectionGraceSeconds = 0.50f,
    float SwitchFraction = 0.40f
);
