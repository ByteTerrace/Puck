using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>A palette slot's look chosen live by a state cell (<see cref="PaletteEntryDocument.ToonSelect"/>): the cell's
/// whole-number value, an enum member's ordinal or an integer, names the case the slot shades with, so a command that
/// writes the cell swaps the slot's look as a binding layout or a camera case is swapped. A value with no case shades the
/// slot with its own <see cref="PaletteEntryDocument.Toon"/> and <see cref="PaletteEntryDocument.Shade"/>. Every case's
/// program is declared by the document, so it is compiled and specialized before a swap; a swap changes only the
/// material's style rows and program reference, never its geometry, so it bakes nothing. Presentation only.</summary>
/// <param name="State">The <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding naming the cell.</param>
/// <param name="Cases">The looks, the first for the value zero (<see cref="PaletteToonCaseDocument"/>).</param>
public sealed record PaletteToonSelectDocument(string State, IReadOnlyList<PaletteToonCaseDocument> Cases) {
    /// <summary>Returns the case a cell's value names.</summary>
    /// <param name="value">The cell's value, or <see langword="null"/> when the cell holds none.</param>
    /// <returns>The case, or <see langword="null"/> when the value names none.</returns>
    public PaletteToonCaseDocument? CaseOf(long? value) => (((value is { } index) && (index >= 0) && (index < Cases.Count)) ? Cases[(int)index] : null);
    /// <summary>Lists what a document refuses in the selection, each named under <paramref name="path"/>.</summary>
    /// <param name="path">The selection's document path, such as <c>palette[2].toonSelect</c>.</param>
    /// <returns>One message per refusal; empty when the selection is admissible.</returns>
    public IEnumerable<string> Refusals(string path) {
        if (!DocumentIdentifier.IsCellReference(value: State)) {
            yield return $"{path}.state must be a state.<row>[.<key>] binding";
        }
        if ((Cases is null) || (Cases.Count == 0)) {
            yield return $"{path}.cases must name at least one look";
            yield break;
        }
        for (var index = 0; (index < Cases.Count); index++) {
            foreach (var refusal in (Cases[index].Toon?.Refusals(path: $"{path}.cases[{index}].toon") ?? [])) {
                yield return refusal;
            }
        }
    }
}

/// <summary>One look a <see cref="PaletteToonSelectDocument"/> chooses: the slot's cel style and its shade program while
/// the case is selected. A case that leaves either out shades the slot without it: no style of its own (the world's
/// default style) and no program of its own (the world's <c>render.shade</c>).</summary>
/// <param name="Toon">The case's style (<see cref="ToonStyleDocument"/>), or <see langword="null"/> for none.</param>
/// <param name="Shade">The case's program, or <see langword="null"/> for none.</param>
public sealed record PaletteToonCaseDocument(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToonStyleDocument? Toon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfShadeDefinition? Shade = null
);
