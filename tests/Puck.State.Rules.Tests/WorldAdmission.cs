using Puck.State;
using Puck.World;

namespace Puck.Testing;

/// <summary>Validates an engine state fixture as the world document that would author it, so a law written over a
/// trait shape cannot drift onto one the world validator refuses.</summary>
internal static class WorldAdmission {
    /// <summary>Returns why the world validator refuses a document holding <paramref name="section"/>.</summary>
    /// <param name="section">The engine state section the law's arena is built from.</param>
    /// <param name="patterns">The pattern rows the law compiles against, or <see langword="null"/> for none.</param>
    /// <returns>The validator's refusal, or empty when the document is admissible.</returns>
    public static string Refusal(StateSection section, IReadOnlyList<PatternRow>? patterns = null) {
        ArgumentNullException.ThrowIfNull(section);

        var definition = new WorldDefinition(
            PatternsRaw: patterns,
            StateRaw: new WorldStateSection(
                Families: section.Families,
                Lattices: section.Lattices,
                World: [.. (section.Rows ?? []).Select(selector: static row => new WorldStateRow(
                    field: null,
                    gatesDrive: false,
                    row: row
                ))]
            )
        );

        return (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );
    }
}
