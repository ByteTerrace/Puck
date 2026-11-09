using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A cel style on the wire (<see cref="SdfToon"/>): the world's default style (<c>render.toon</c>, which the
/// live <c>world.toon</c> lever overrides) and a palette row's own (<see cref="PaletteEntryDocument.Toon"/>) are this one
/// shape, so a style authored once (the standard's <c>styles.puck</c>) serves both. The engine holds no look of its own:
/// a style that bands authors every value, and one that says <c>enabled: false</c> needs none. Presentation only; no
/// field enters simulation state or the bake.</summary>
/// <param name="Enabled">Whether the style bands; absent is on. False on a palette row keeps that material continuous
/// whatever the world's default is; false on <c>render.toon</c> boots the world's default off until the lever turns it
/// on, which a style with every value then bands with.</param>
/// <param name="Threshold">Diffuse band threshold in [0, 1].</param>
/// <param name="Softness">Full terminator width in [0.001, 0.5].</param>
/// <param name="ShadowTint">The share of each direct light its shade side keeps, the painted shadow colour, three
/// linear RGB values in [0, 1].</param>
/// <param name="OutlineWidth">Close outline radius in output pixels, in [0, 4]; zero disables outlines.</param>
/// <param name="OutlineDistance">Distance in metres at which the outline radius halves, in [0.1, 1000].</param>
/// <param name="OutlineNormal">Crease normal cosine in [0, 0.95].</param>
/// <param name="OutlineTint">Outline multiplier, three linear RGB values in [0, 1].</param>
/// <param name="SpecularSize">Highlight size as one minus the half-vector cosine, in [0.001, 0.25].</param>
/// <param name="Exposure">Reflected-light exposure compensation in stops, in [-4, 4].</param>
/// <param name="Shoulder">Hue-preserving highlight compression in [0, 2]; zero disables it.</param>
/// <param name="Band">How the band treats a light's shadow visibility: <see cref="SdfToonBand.Visibility"/> (the
/// default) keeps the penumbra, <see cref="SdfToonBand.Combined"/> steps it into a held shape.</param>
public sealed record ToonStyleDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Threshold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Softness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? ShadowTint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineWidth = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineDistance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? OutlineNormal = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? OutlineTint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? SpecularSize = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Exposure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Shoulder = null,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfToonBand>))] SdfToonBand Band = SdfToonBand.Visibility
) {
    /// <summary>Gets whether the style bands: <see cref="Enabled"/>, or true when it is absent.</summary>
    [JsonIgnore]
    public bool IsOn => (Enabled ?? true);
    /// <summary>Gets whether the style authors every value a band shades with, so it can band.</summary>
    [JsonIgnore]
    public bool IsComplete => !Missing().Any();

    /// <summary>Returns the style's presentation parameters, each value it leaves out zero.</summary>
    /// <returns>The style.</returns>
    public SdfToon ToToon() => new() {
        Enabled = IsOn, Threshold = (Threshold ?? 0f), Softness = (Softness ?? 0f),
        ShadowTint = (ShadowTint ?? Vector3.Zero), OutlineWidth = (OutlineWidth ?? 0f),
        OutlineDistance = (OutlineDistance ?? 0f), OutlineNormal = (OutlineNormal ?? 0f),
        OutlineTint = (OutlineTint ?? Vector3.Zero), SpecularSize = (SpecularSize ?? 0f), Exposure = (Exposure ?? 0f),
        Shoulder = (Shoulder ?? 0f), Band = Band,
    };
    /// <summary>Resolves a world's default style and the live lever's override; neither affects simulation state.</summary>
    /// <param name="toon">The document's <c>render.toon</c>, or <see langword="null"/> when the world authors none.</param>
    /// <param name="enabled">The session override, or <see langword="null"/> to follow the document.</param>
    /// <returns>The world's default style: on when the override or the document turns it on and the document authors
    /// every value, and otherwise off, the default <see cref="SdfToon"/> when the world authors none.</returns>
    public static SdfToon ResolveDefault(ToonStyleDocument? toon, bool? enabled = null) => ((toon is null)
        ? default
        : (toon.ToToon() with { Enabled = ((enabled ?? toon.IsOn) && toon.IsComplete) }));
    /// <summary>Lists the style's values a document refuses, each named under <paramref name="path"/>: a value outside its
    /// range, and, for a style that bands, a value it leaves out.</summary>
    /// <param name="path">The style's document path, such as <c>render.toon</c>.</param>
    /// <returns>One message per refused value; empty when the style is admissible.</returns>
    public IEnumerable<string> Refusals(string path) {
        var missing = Missing().ToHashSet(comparer: StringComparer.Ordinal);
        var ranges = ToToon().Refusals().Where(predicate: refusal => !missing.Contains(item: FieldOf(refusal: refusal)));
        var required = (IsOn ? missing.Select(selector: field => $"{field} is required by a style that bands") : []);

        return required.Concat(second: ranges).Select(selector: refusal => $"{path}.{refusal}");
    }

    // The document fields the style leaves out, by the names SdfToon.Refusals gives them.
    private IEnumerable<string> Missing() {
        if (Threshold is null) { yield return "threshold"; }
        if (Softness is null) { yield return "softness"; }
        if (ShadowTint is null) { yield return "shadowTint"; }
        if (OutlineWidth is null) { yield return "outlineWidth"; }
        if (OutlineDistance is null) { yield return "outlineDistance"; }
        if (OutlineNormal is null) { yield return "outlineNormal"; }
        if (OutlineTint is null) { yield return "outlineTint"; }
        if (SpecularSize is null) { yield return "specularSize"; }
        if (Exposure is null) { yield return "exposure"; }
        if (Shoulder is null) { yield return "shoulder"; }
    }
    // The field a refusal of SdfToon.Refusals names: its first word, less a component index.
    private static string FieldOf(string refusal) {
        var end = refusal.IndexOfAny(anyOf: [' ', '[']);

        return ((end < 0) ? refusal : refusal[..end]);
    }
}
