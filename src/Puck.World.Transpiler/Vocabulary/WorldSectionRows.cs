using System.Text.Json.Serialization.Metadata;

namespace Puck.World.Transpiler.Vocabulary;

/// <summary>The sections whose model holds a list of rows under <c>rows</c> beside optional policy members
/// (<c>kits</c>, <c>looks</c>, <c>placements</c>, <c>captures</c>, …). A document writes such a section that holds
/// only its rows as the array of them, <c>kits [ … ]</c>, and lowers it to <c>{ "rows": [ … ] }</c>; a section that
/// also authors a policy member keeps its block.</summary>
internal static class WorldSectionRows {
    /// <summary>The model member a section's rows live under.</summary>
    public const string Member = "rows";

    /// <summary>Returns the section type a member of a model type holds, when that member is one object holding a
    /// list of rows under <see cref="Member"/>.</summary>
    /// <param name="holder">The model type declaring the member.</param>
    /// <param name="member">The member's JSON name.</param>
    /// <returns>The section type, or <see langword="null"/> when the member is no such section (a list, a scalar, or
    /// an object without a rows list).</returns>
    public static Type? SectionOf(Type holder, string member) {
        if (
            (WorldModelShape.Of(type: holder) is not { } shape) ||
            (shape.Members.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Name, b: member, comparisonType: StringComparison.Ordinal)) is not { } declared) ||
            (WorldModelShape.Of(type: Nullable.GetUnderlyingType(nullableType: declared.Type) ?? declared.Type) is not { Kind: JsonTypeInfoKind.Object } section)
        ) {
            return null;
        }

        return (HoldsRows(section: section.Type) ? section.Type : null);
    }

    private static bool HoldsRows(Type section) => (
        (WorldModelShape.Of(type: section) is { } shape) &&
        shape.Members.Any(predicate: static candidate => (
            string.Equals(a: candidate.Name, b: Member, comparisonType: StringComparison.Ordinal) &&
            (WorldModelShape.Of(type: candidate.Type) is { Kind: JsonTypeInfoKind.Enumerable })
        ))
    );
}
