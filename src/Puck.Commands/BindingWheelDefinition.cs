using System.Text.Json.Serialization;

namespace Puck.Commands;

/// <summary>
/// One page group's radial action menu — the wheel a seat presents while it HOLDS the group's
/// <paramref name="HoldPage"/> (the chord row that selects that page is the wheel's whole open/close gesture). The
/// rings are PAGES (<see cref="BindingPageDefinition"/> — the same record a chord row carries), presented as
/// concentric shells rather than selected by chord; each ring's entries are the wheel's SECTORS, and every sector is
/// an ordinary binding row whose destination is a console-dispatchable command — committing a sector dispatches that
/// command through the console door, so nothing a wheel offers is reachable only by wheel.
/// </summary>
/// <remarks>A sector row deliberately narrows the page-entry shape: it carries a <see cref="BindingPageEntryDefinition.Command"/>
/// destination plus display metadata and NOTHING else — no <c>Source</c>/<c>Activator</c> (the radial gesture is the
/// trigger), no <c>Channel</c> (a channel is a folded intent vector, not a console line), no <c>Value</c>/<c>Scale</c>/
/// <c>ActivateOn</c>/<c>Mode</c> (a console line carries no constant and no edge). <see cref="BindingProfile.Compile"/>
/// refuses each of those by name. Ring page ids share the document-wide page-id namespace.</remarks>
/// <param name="Group">The page group this wheel belongs to — the seat's ACTIVE group decides which wheel presents,
/// so a group without a wheel simply presents nothing. Exactly one wheel per group.</param>
/// <param name="HoldPage">The page id whose selection presents the wheel — a page a chord row of the SAME group
/// declares (holding that chord IS holding the wheel open, and the page's own entries are what stays live while it
/// is: the ring-cycle rows, the commit row, whatever else the author deliberately keeps bound).</param>
/// <param name="Rings">The concentric ring pages, innermost first — <see cref="MinRings"/>..<see cref="MaxRings"/>
/// of them, each carrying <see cref="MinSectorsPerRing"/>..<see cref="MaxSectorsPerRing"/> sector rows.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BindingWheelDefinition(
    string Group,
    string HoldPage,
    IReadOnlyList<BindingPageDefinition> Rings
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
