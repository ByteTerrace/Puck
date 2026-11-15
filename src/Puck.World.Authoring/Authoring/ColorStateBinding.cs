using Puck.Assets.Documents;

namespace Puck.World.Authoring;

/// <summary>A state-bound colour: <c>state.&lt;row&gt;</c> or <c>state.&lt;row&gt;.&lt;key&gt;</c> naming a world Text cell
/// that holds a colour in <see cref="Puck.Maths.HexColor"/>'s grammar. A creation document carries the token verbatim
/// and the hosting world resolves it (<c>Puck.World.WorldColor</c>), so a creation on its own can only admit the
/// syntax.</summary>
public static class ColorStateBinding {
    /// <summary>Returns whether a value has the state-binding shape: <see cref="DocumentIdentifier.ReferencePrefix"/>
    /// followed by at least one character.</summary>
    /// <param name="value">The candidate string.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> names a state cell rather than a literal colour.</returns>
    public static bool IsStateBinding(string? value) =>
        ((value is not null) &&
        (value.Length > DocumentIdentifier.ReferencePrefix.Length) &&
        value.StartsWith(comparisonType: StringComparison.Ordinal, value: DocumentIdentifier.ReferencePrefix));
}
