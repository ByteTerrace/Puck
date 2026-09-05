using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>
/// WHEN a <see cref="Draw"/> site's value is decided, and whether it may ever be decided again.
/// </summary>
/// <remarks>
/// <para>A <see cref="Boot"/> site draws exactly once, at first fill (process load or a fresh
/// <c>world.instance.start</c>), and refuses a later <c>generate</c> by name. <see cref="TickPeriod"/> and
/// <see cref="Event"/> sites also draw at first fill but stay redrawable through the same <c>generate</c>
/// effect/mutation; the engine draws no operational distinction between the two — cadence or event gating is
/// authored separately with the ordinary <c>rules</c> vocabulary (a <c>$tick</c>-scheduled Edge rule, or an
/// event-flag-gated one). The split is authored intent made legible at the site.</para>
/// </remarks>
[JsonConverter(typeof(StrictEnumConverter<DrawTiming>))]
public enum DrawTiming : byte {
    /// <summary>Drawn once at first fill; a later <c>generate</c> against this site refuses by name.</summary>
    Boot,

    /// <summary>Drawn at first fill and redrawable via <c>generate</c>; the author gates cadence with an ordinary
    /// <c>$tick</c>-scheduled rule.</summary>
    TickPeriod,

    /// <summary>Drawn at first fill and redrawable via <c>generate</c>; the author gates redraw with an ordinary
    /// event-flag rule.</summary>
    Event,
}
/// <summary>
/// The AUTHORED-RANDOMNESS facet: the declaration that a SITE's value is DRAWN rather than literal. One facet, one
/// source family, one engine — an NPC-bark text site, a loot cell, a random census, and a drawn host backend are the
/// SAME mechanism pointed at different sites.
/// </summary>
/// <remarks>
/// <para>Exactly one of <see cref="Source"/> (naming a row of the document's <c>generators</c> section) and
/// <see cref="StateGenerator"/> (an inline source) is declared. The inline form compiles to an anonymous source of the
/// identical <see cref="StateGenerator"/> family, so nothing is expressible one way and not the other.</para>
/// <para>A referenced source draws on the site's own stream: two sites naming one source draw independent
/// sequences, since the seed ladder folds the site descriptor and the position
/// (<see cref="StateRow.DrawCursor"/>) and drawn masks (<see cref="StateRow.DrawnMasks"/>) live on the
/// site. Sharing a source shares its shape and never its position, so pointing a second site at an existing table
/// can never perturb the first site's sequence.</para>
/// <para>A reference refuses at validate, before any draw runs, when it names a source that does not exist, names
/// one whose emission kind the site cannot hold, or names one the site's own timing cannot drive (a dealing source
/// at a settle-and-clear boot site has no second draw to deal into).</para>
/// </remarks>
/// <param name="Source">The declared source to draw from, by name — or <see langword="null"/> when
/// <see cref="StateGenerator"/> inlines one.</param>
/// <param name="Generator">An inline anonymous source — or <see langword="null"/> when <see cref="Source"/> names a
/// declared one.</param>
/// <param name="Timing">When this site draws and whether it may be redrawn (see <see cref="DrawTiming"/>).</param>
/// <param name="Secret">An authority-provisioned 256-bit secret for an independently keyed streamDraw sample at each cursor. Never sent in observations.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Draw(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CellName? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StateGenerator? Generator = null,
    DrawTiming Timing = DrawTiming.Boot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClosedBitset256? Secret = null
);
