using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A cel style on the wire (<see cref="SdfToon"/>): the world's default style (<c>render.toon</c>, which the
/// live <c>world.toon</c> lever overrides) and a palette row's own (<see cref="PaletteEntryDocument.Toon"/>) are this one
/// shape, so a style authored once (the standard's <c>styles.puck</c>) serves both. Presentation only; no field enters
/// simulation state or the bake.</summary>
/// <param name="Enabled">Whether the style bands; absent is on. False on a palette row keeps that material continuous
/// whatever the world's default is; false on <c>render.toon</c> boots the world's default off until the lever turns it
/// on.</param>
/// <param name="Threshold">Diffuse band threshold in [0, 1].</param>
/// <param name="Softness">Full terminator width in [0.001, 0.5].</param>
/// <param name="ShadowTint">Shadow-band reflectance, three linear RGB values in [0, 1].</param>
/// <param name="OutlineWidth">Close outline radius in output pixels, in [0, 4]; zero disables outlines.</param>
/// <param name="OutlineDistance">Distance in metres at which the outline radius halves, in [0.1, 1000].</param>
/// <param name="OutlineNormal">Crease normal cosine in [0, 0.95].</param>
/// <param name="OutlineTint">Outline multiplier, three linear RGB values in [0, 1].</param>
/// <param name="SpecularSize">Highlight size as one minus the half-vector cosine, in [0.001, 0.25].</param>
/// <param name="Exposure">Reflected-light exposure compensation in stops, in [-4, 4].</param>
/// <param name="Shoulder">Hue-preserving highlight compression in [0, 2]; zero disables it.</param>
/// <param name="Band">How the band treats a light's shadow visibility: <see cref="SdfToonBand.Visibility"/> (the
/// default) keeps the penumbra, <see cref="SdfToonBand.Combined"/> snaps it to the band's edge.</param>
public sealed record ToonStyleDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
    float Threshold = .45f,
    float Softness = .08f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? ShadowTint = null,
    float OutlineWidth = 1.5f,
    float OutlineDistance = 12f,
    float OutlineNormal = .65f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? OutlineTint = null,
    float SpecularSize = .025f,
    float Exposure = .5f,
    float Shoulder = .65f,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfToonBand>))] SdfToonBand Band = SdfToonBand.Visibility
) {
    /// <summary>Gets whether the style bands: <see cref="Enabled"/>, or true when it is absent.</summary>
    [JsonIgnore]
    public bool IsOn => (Enabled ?? true);

    /// <summary>Returns the style's presentation parameters.</summary>
    /// <returns>The style.</returns>
    public SdfToon ToToon() => new() {
        Enabled = IsOn, Threshold = Threshold, Softness = Softness,
        ShadowTint = (ShadowTint ?? SdfToon.Default.ShadowTint),
        OutlineWidth = OutlineWidth, OutlineDistance = OutlineDistance, OutlineNormal = OutlineNormal,
        OutlineTint = (OutlineTint ?? SdfToon.Default.OutlineTint),
        SpecularSize = SpecularSize, Exposure = Exposure, Shoulder = Shoulder, Band = Band,
    };
    /// <summary>Resolves a world's default style and the live lever's override; neither affects simulation state.</summary>
    /// <param name="toon">The document's <c>render.toon</c>, or <see langword="null"/> for the default look.</param>
    /// <param name="enabled">The session override, or <see langword="null"/> to follow the document.</param>
    /// <returns>The world's default style: off when the document authors none, unless the override enables it.</returns>
    public static SdfToon ResolveDefault(ToonStyleDocument? toon, bool? enabled = null) => ((toon?.ToToon() ?? SdfToon.Default) with {
        Enabled = (enabled ?? toon?.IsOn ?? false),
    });
    /// <summary>Lists the style's values a document refuses, each named under <paramref name="path"/>.</summary>
    /// <param name="path">The style's document path, such as <c>render.toon</c>.</param>
    /// <returns>One message per refused value; empty when the style is admissible.</returns>
    public IEnumerable<string> Refusals(string path) => ToToon().Refusals().Select(selector: refusal => $"{path}.{refusal}");
}
