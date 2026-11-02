namespace Puck.World.Authoring;

/// <summary>A palette slot's cel style chosen live by a state cell (<see cref="PaletteEntryDocument.ToonSelect"/>): the
/// cell's whole-number value, an enum member's ordinal or an integer, names the case the slot shades with, so a command
/// that writes the cell swaps the slot's look as a binding layout or a camera case is swapped. A value with no case
/// shades the slot with its <see cref="PaletteEntryDocument.Toon"/>. A swap changes only the material's style rows, never
/// its geometry, so it bakes nothing. Presentation only.</summary>
/// <param name="State">The <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding naming the cell.</param>
/// <param name="Cases">The styles, the first for the value zero; each is a whole style (<see cref="ToonStyleDocument"/>),
/// <c>enabled: false</c> keeping the slot continuous.</param>
public sealed record PaletteToonSelectDocument(string State, IReadOnlyList<ToonStyleDocument> Cases) {
    /// <summary>Returns the case a cell's value names.</summary>
    /// <param name="value">The cell's value, or <see langword="null"/> when the cell holds none.</param>
    /// <returns>The case, or <see langword="null"/> when the value names none.</returns>
    public ToonStyleDocument? CaseOf(long? value) => (((value is { } index) && (index >= 0) && (index < Cases.Count)) ? Cases[(int)index] : null);
    /// <summary>Lists what a document refuses in the selection, each named under <paramref name="path"/>.</summary>
    /// <param name="path">The selection's document path, such as <c>palette[2].toonSelect</c>.</param>
    /// <returns>One message per refusal; empty when the selection is admissible.</returns>
    public IEnumerable<string> Refusals(string path) {
        if (!HexColor.IsStateBinding(value: State)) {
            yield return $"{path}.state must be a state.<row>[.<key>] binding";
        }
        if ((Cases is null) || (Cases.Count == 0)) {
            yield return $"{path}.cases must name at least one style";
            yield break;
        }
        for (var index = 0; (index < Cases.Count); index++) {
            foreach (var refusal in Cases[index].Refusals(path: $"{path}.cases[{index}]")) {
                yield return refusal;
            }
        }
    }
}
