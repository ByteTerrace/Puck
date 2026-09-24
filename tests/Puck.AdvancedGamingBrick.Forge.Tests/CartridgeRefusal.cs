using Puck.GamingBricks.Forge;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Names one document validation must refuse and says where and what the refusal is. A table of them backs one
/// theory, and a refusal inside a wider claim holds on its own, so every failure names its case, and the
/// diagnostic's location is part of the claim rather than only its wording.
/// </summary>
/// <param name="Name">The case's name, which is the theory row's display name and is unique within its table.</param>
/// <param name="Document">The document validation must refuse.</param>
/// <param name="Path">The validation error's document path, in the validator's dotted and indexed spelling.</param>
/// <param name="Fragment">Ordinal text the error's message contains.</param>
public sealed record CartridgeRefusal(string Name, CartridgeDocument Document, string Path, string Fragment) {
    /// <summary>Validates the named case of a table.</summary>
    /// <param name="table">The class's refusal table.</param>
    /// <param name="name">The case to validate.</param>
    public static void Holds(IEnumerable<CartridgeRefusal> table, string name) => table.Single(predicate: candidate => (candidate.Name == name)).Holds();
    /// <summary>Validates the document and fails, naming this case, unless one error sits at its path and carries its fragment.</summary>
    public void Holds() {
        var errors = CartridgeDocuments.Validate(document: Document);

        if (!errors.Any(predicate: error => ((error.Path == Path) && error.Message.Contains(
            comparisonType: StringComparison.Ordinal,
            value: Fragment
        )))) {
            var reported = ((errors.Count == 0)
                ? "no error"
                : string.Join(
                    separator: "; ",
                    values: errors.Select(selector: static error => $"{error.Path}: {error.Message}")
                )
            );

            Assert.Fail(message: $"Refusal '{Name}' expected an error at '{Path}' containing \"{Fragment}\"; validation reported: {reported}");
        }
    }
    /// <summary>Returns a table's case names as theory rows.</summary>
    /// <param name="table">The class's refusal table.</param>
    /// <returns>One row per case.</returns>
    /// <exception cref="ArgumentException">Two cases in <paramref name="table"/> share a name.</exception>
    public static TheoryData<string> Names(IEnumerable<CartridgeRefusal> table) {
        var names = table.Select(selector: static refusal => refusal.Name).ToArray();

        if (names.Distinct(comparer: StringComparer.Ordinal).Count() != names.Length) {
            throw new ArgumentException(
                message: "Each refusal in a table needs its own name.",
                paramName: nameof(table)
            );
        }

        return new TheoryData<string>(values: names);
    }
    /// <inheritdoc/>
    public override string ToString() => Name;
}
