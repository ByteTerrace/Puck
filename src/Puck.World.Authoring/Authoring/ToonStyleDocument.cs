using System.Text.Json.Serialization;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A style mark on the wire (<see cref="SdfToon"/>): the world's default (<c>render.toon</c>) and a palette
/// row's own (<see cref="PaletteEntryDocument.Toon"/>) are this one shape. A style marks its material styled and carries
/// no look: the banding and the ink are the material's shade program (the standard's <c>celMaterial</c> writes both),
/// and the live <c>world.toon off</c> shades every styled material continuously and uninked. Presentation only; no
/// field enters simulation state or the bake.</summary>
/// <param name="Enabled">Whether the style is on; absent is on. False on a palette row keeps that material unstyled
/// whatever the world's default is; false on <c>render.toon</c> leaves the world's default off.</param>
public sealed record ToonStyleDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null
) {
    /// <summary>Gets whether the style is on: <see cref="Enabled"/>, or true when it is absent.</summary>
    [JsonIgnore]
    public bool IsOn => (Enabled ?? true);

    /// <summary>Returns the style's presentation mark.</summary>
    /// <returns>The style.</returns>
    public SdfToon ToToon() => new() { Enabled = IsOn };
    /// <summary>Resolves a world's default style and the live lever; neither affects simulation state.</summary>
    /// <param name="toon">The document's <c>render.toon</c>, or <see langword="null"/> when the world authors none.</param>
    /// <param name="shown">The live lever: false suppresses every style, a material's own included; true or
    /// <see langword="null"/> shows every style as authored.</param>
    /// <returns>The world's default style: on when the document turns it on, the default <see cref="SdfToon"/> when the
    /// world authors none; <see cref="SdfToon.Suppressed"/> when <paramref name="shown"/> is false.</returns>
    public static SdfToon ResolveDefault(ToonStyleDocument? toon, bool? shown = null) =>
        ((toon?.ToToon() ?? default) with { Suppressed = (shown == false) });
}
