using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A cel style on the wire (<see cref="SdfToon"/>): the world's default style (<c>render.toon</c>) and a palette
/// row's own (<see cref="PaletteEntryDocument.Toon"/>) are this one shape, so a style authored once (the standard's
/// <c>styles.puck</c>) serves both. A style marks its material styled and carries its ink; the banding is the material's
/// shade program (the standard's <c>celMaterial</c> writes both), and the live <c>world.toon off</c> shades every styled
/// material continuously and uninked. The engine holds no look of its own: a style that is on authors every value, and
/// one that says <c>enabled: false</c> needs none. Presentation only; no field enters simulation state or the
/// bake.</summary>
/// <param name="Enabled">Whether the style is on; absent is on. False on a palette row keeps that material unstyled
/// whatever the world's default is; false on <c>render.toon</c> leaves the world's default off.</param>
/// <param name="OutlineWidth">The close outline radius in output pixels, in [0, 4]; zero disables outlines.</param>
/// <param name="OutlineDistance">The distance in metres at which the outline radius halves, in [0.1, 1000].</param>
/// <param name="OutlineNormal">The crease normal cosine, in [0, 0.95].</param>
/// <param name="OutlineTint">The outline multiplier, three linear RGB values in [0, 1].</param>
public sealed record ToonStyleDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineDistance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineNormal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? OutlineTint = null
) {
    /// <summary>Gets whether the style is on: <see cref="Enabled"/>, or true when it is absent.</summary>
    [JsonIgnore]
    public bool IsOn => (Enabled ?? true);
    /// <summary>Gets whether the style authors every value an on style inks with.</summary>
    [JsonIgnore]
    public bool IsComplete => ((OutlineWidth is not null) && (OutlineDistance is not null) && (OutlineNormal is not null) && (OutlineTint is not null));

    /// <summary>Returns the style's presentation parameters, each value it leaves out zero.</summary>
    /// <returns>The style.</returns>
    public SdfToon ToToon() => new() {
        Enabled = IsOn, OutlineWidth = (OutlineWidth ?? 0f), OutlineDistance = (OutlineDistance ?? 0f),
        OutlineNormal = (OutlineNormal ?? 0f), OutlineTint = (OutlineTint ?? Vector3.Zero),
    };
    /// <summary>Resolves a world's default style and the live lever; neither affects simulation state.</summary>
    /// <param name="toon">The document's <c>render.toon</c>, or <see langword="null"/> when the world authors none.</param>
    /// <param name="shown">The live lever: false suppresses every style, a material's own included; true or
    /// <see langword="null"/> shows every style as authored.</param>
    /// <returns>The world's default style: on when the document turns it on and authors every value, and otherwise off,
    /// the default <see cref="SdfToon"/> when the world authors none; <see cref="SdfToon.Suppressed"/> when
    /// <paramref name="shown"/> is false.</returns>
    public static SdfToon ResolveDefault(ToonStyleDocument? toon, bool? shown = null) => ((toon is null)
        ? new SdfToon { Suppressed = (shown == false) }
        : (toon.ToToon() with { Enabled = (toon.IsOn && toon.IsComplete), Suppressed = (shown == false) }));
    /// <summary>Lists the style's values a document refuses, each named under <paramref name="path"/>: a value outside its
    /// range, and, for a style that is on, a value it leaves out.</summary>
    /// <param name="path">The style's document path, such as <c>render.toon</c>.</param>
    /// <returns>One message per refused value; empty when the style is admissible.</returns>
    public IEnumerable<string> Refusals(string path) {
        var missing = Missing().ToHashSet(comparer: StringComparer.Ordinal);
        var ranges = ToToon().Refusals().Where(predicate: refusal => !missing.Contains(item: FieldOf(refusal: refusal)));
        var required = (IsOn ? missing.Select(selector: field => $"{field} is required by a style that is on") : []);

        return required.Concat(second: ranges).Select(selector: refusal => $"{path}.{refusal}");
    }

    // The document fields the style leaves out, by the names SdfToon.Refusals gives them; IsComplete tests the same four.
    private IEnumerable<string> Missing() {
        if (OutlineWidth is null) { yield return "outlineWidth"; }
        if (OutlineDistance is null) { yield return "outlineDistance"; }
        if (OutlineNormal is null) { yield return "outlineNormal"; }
        if (OutlineTint is null) { yield return "outlineTint"; }
    }
    // The field a refusal of SdfToon.Refusals names: its first word, less a component index.
    private static string FieldOf(string refusal) {
        var end = refusal.IndexOfAny(anyOf: [' ', '[']);

        return ((end < 0) ? refusal : refusal[..end]);
    }
}
